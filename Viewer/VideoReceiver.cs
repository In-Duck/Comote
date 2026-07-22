using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.FFmpeg;
using Newtonsoft.Json.Linq;
using NAudio.Wave;
using Concentus.Structs;
using Concentus.Enums;

using Comote.Shared;
namespace Viewer
{
    public class VideoReceiver : IDisposable
    {
        private RTCPeerConnection? _peerConnection;
        private RTCDataChannel? _inputChannel;
        private RTCDataChannel? _fileChannel; // File Transfer ONLY
        private readonly ConcurrentQueue<RTCIceCandidateInit> _pendingIceCandidates = new();
        private bool _remoteDescriptionSet;
        private FFmpegVideoEncoder? _decoder;
        private OpusDecoder? _opusDecoder;
        private IWavePlayer? _waveOut;
        private BufferedWaveProvider? _waveProvider;
        public WriteableBitmap? VideoBitmap { get; private set; }
        private int _frameCount = 0;

        // --- FPS/RTT 측정용 ---
        private int _fpsCounter = 0;
        private readonly Stopwatch _fpsStopwatch = new();
        private readonly Stopwatch _pingStopwatch = new();
        private Timer? _statsTimer;
        private volatile bool _presentationActive;
        private volatile bool _thumbnailStreaming;
        private MonitoringProfile _thumbnailProfile =
            MonitoringProfile.CreateAutomatic(100);
        private readonly object _audioSync = new();
        private PendingVideoFrame? _pendingThumbnailFrame;
        private int _thumbnailRenderScheduled;

        /// <summary>최근 1초간 디코딩된 FPS</summary>
        public int CurrentFps { get; private set; }

        /// <summary>DataChannel ping/pong으로 측정된 RTT (ms)</summary>
        public int RttMs { get; private set; } = -1;
        public bool InputChannelReady => _inputChannel?.readyState == RTCDataChannelState.open;
        public DateTime LastInputAcknowledgedAt { get; private set; } = DateTime.MinValue;

        // --- 프로토콜 상수 ---
        private const byte MSG_STATS     = 0x20;
        private const byte MSG_PING      = 0x21;
        private const byte MSG_PONG      = 0x22;
        private const byte MSG_CLIPBOARD = 0x23;
        private const byte MSG_INPUT_ACK = 0x14;

        // --- 파일 전송 프로토콜 ---
        private const byte MSG_FILE_START = 0x30;
        private const byte MSG_FILE_CHUNK = 0x31;
        private const byte MSG_FILE_END   = 0x32;
        private const byte MSG_FILE_ACK   = 0x33;

        // --- 이벤트 ---
        public event Action<object>? OnSignalReady;
        public event Action? OnFrameReady;
        public event Action<RTCPeerConnectionState>? OnConnectionStateChanged;
        public event Action<bool>? OnInputReadyChanged;
        public event Action<string>? OnRejected;
        public event Action<string>? OnClipboardReceived;
        public event Action<int>? OnFileProgress;     // 0~100%
        public event Action<string>? OnFileComplete;   // 완료 메시지

        public RTCPeerConnectionState ConnectionState =>
            _peerConnection?.connectionState ?? RTCPeerConnectionState.closed;

        public VideoReceiver()
        {
            InitializePeerConnection();
        }

        /// <summary>
        /// PeerConnection 초기화 (신규 생성 또는 재연결 시 호출)
        /// </summary>
        private void InitializePeerConnection()
        {
            _remoteDescriptionSet = false;
            _pendingIceCandidates.Clear();
            var config = new RTCConfiguration
            {
                iceServers = IceServerConfiguration.Create()
            };

            _peerConnection = new RTCPeerConnection(config);

            // FFmpegVideoEncoder를 디코더로도 사용 (DecodeVideo 메서드 보유)
            _decoder = new FFmpegVideoEncoder();

            // Concentus Opus 디코더 초기화 (48kHz, 스테레오)
            var supportedFormats = new List<VideoFormat>
            {
                new VideoFormat(VideoCodecsEnum.H265, 100, 90000, null),
                // new VideoFormat(VideoCodecsEnum.AV1, 101, 90000, null), // AV1 미지원
                new VideoFormat(VideoCodecsEnum.H264, 96, 90000, null)
            };
            var videoTrack = new MediaStreamTrack(supportedFormats, MediaStreamStatusEnum.RecvOnly);
            _peerConnection.addTrack(videoTrack);

            // Opus 오디오 수신 전용 트랙 추가 (SDP에 오디오 라인 포함 필수)
            var opusFormat = new AudioFormat(111, "opus", 48000);
            var audioTrack = new MediaStreamTrack(opusFormat, MediaStreamStatusEnum.RecvOnly);
            _peerConnection.addTrack(audioTrack);

            // RTP 수신 → 직접 디코딩
            _peerConnection.OnVideoFrameReceived += (rep, timestamp, frame, format) =>
            {
                _frameCount++;
                _fpsCounter++;

                if (_frameCount <= 10 || _frameCount % 30 == 0)
                {
                    Console.WriteLine($"[VideoReceiver] RTP #{_frameCount}: {frame.Length} bytes");
                }

                try
                {
                    var decodedFrames = _decoder.DecodeVideo(
                        frame,
                        VideoPixelFormatsEnum.Bgra,
                        VideoCodecsEnum.H264);

                    foreach (var decoded in decodedFrames)
                    {
                        RenderFrame(decoded);
                    }
                }
                catch (Exception ex)
                {
                    if (_frameCount <= 5)
                    {
                        Console.WriteLine($"[VideoReceiver] Decode Error: {ex.Message}");
                    }
                }
            };

            _peerConnection.onicecandidate += (candidate) =>
            {
                if (candidate != null) OnSignalReady?.Invoke(new {
                    ice = new {
                        candidate = candidate.candidate,
                        sdpMid = candidate.sdpMid,
                        sdpMLineIndex = candidate.sdpMLineIndex
                    }
                });
            };

            _peerConnection.onconnectionstatechange += (state) =>
            {
                Console.WriteLine($"[VideoReceiver] Connection state: {state}");
                OnConnectionStateChanged?.Invoke(state);

                if (state == RTCPeerConnectionState.connected)
                {
                    StartStatsReporting();
                }
                else if (state == RTCPeerConnectionState.disconnected ||
                         state == RTCPeerConnectionState.failed ||
                         state == RTCPeerConnectionState.closed)
                {
                    StopStatsReporting();
                }
            };

            // 오디오 수신 처리 (Concentus Opus 디코딩)
            int _audioFrameCount = 0;
            _peerConnection.OnRtpPacketReceived += (rep, media, rtpPacket) =>
            {
                if (media == SDPMediaTypesEnum.audio && _presentationActive)
                {
                    try
                    {
                        if (_audioFrameCount++ < 5)
                        {
                            Console.WriteLine($"[Audio] RTP audio packet #{_audioFrameCount}: {rtpPacket.Payload.Length} bytes");
                        }

                        // Concentus로 Opus 디코딩 (20ms = 960 samples per channel, 스테레오 = 1920)
                        short[] pcmOutput = new short[960 * 2];
                        var decoder = _opusDecoder;
                        if (decoder == null) return;
                        int decodedSamples = decoder.Decode(rtpPacket.Payload, 0, rtpPacket.Payload.Length, pcmOutput, 0, 960, false);

                        if (decodedSamples > 0 && _waveProvider != null)
                        {
                            // short[] -> byte[] 변환 후 WaveProvider에 추가
                            int totalSamples = decodedSamples * 2; // 스테레오
                            byte[] byteData = new byte[totalSamples * 2]; // 16bit = 2bytes per sample
                            Buffer.BlockCopy(pcmOutput, 0, byteData, 0, byteData.Length);

                            // [Stability] Jitter Buffer 처리 (너무 적으면 무시? 아니면 Silence 삽입?)
                            // 여기서는 단순 추가하지만, 위쪽 Task에서 과다 버퍼링 제어
                            _waveProvider.AddSamples(byteData, 0, byteData.Length);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (_audioFrameCount <= 5)
                        {
                            Console.WriteLine($"[Audio] Decode Error: {ex.Message}");
                        }
                    }
                }
            };
        }

        private void RenderFrame(VideoSample decoded)
        {
            var width = (int)decoded.Width;
            var height = (int)decoded.Height;
            if (_presentationActive)
            {
                RenderFrameOnUi(width, height, decoded.Sample);
                return;
            }

            // Ignore the initial full-resolution frames while the Host is
            // switching into its negotiated monitoring profile.
            if (width > 640 || height > 360) return;
            var sample = decoded.Sample.ToArray();
            Interlocked.Exchange(
                ref _pendingThumbnailFrame,
                new PendingVideoFrame(width, height, sample));
            if (Interlocked.CompareExchange(
                    ref _thumbnailRenderScheduled, 1, 0) != 0)
                return;

            Application.Current?.Dispatcher.BeginInvoke(
                RenderLatestThumbnailFrame);
        }

        private void RenderLatestThumbnailFrame()
        {
            while (true)
            {
                var frame = Interlocked.Exchange(
                    ref _pendingThumbnailFrame, null);
                if (frame != null)
                    RenderFrameOnUi(
                        frame.Width,
                        frame.Height,
                        frame.Sample);

                Interlocked.Exchange(ref _thumbnailRenderScheduled, 0);
                if (Volatile.Read(ref _pendingThumbnailFrame) == null ||
                    Interlocked.CompareExchange(
                        ref _thumbnailRenderScheduled, 1, 0) != 0)
                    return;
            }
        }

        private void RenderFrameOnUi(int width, int height, byte[] sample)
        {
            try
            {
                void Render()
                {
                    var stride = width * 3;
                    if (VideoBitmap == null ||
                        VideoBitmap.PixelWidth != width ||
                        VideoBitmap.PixelHeight != height)
                    {
                        VideoBitmap = new WriteableBitmap(
                            width, height, 96, 96,
                            PixelFormats.Rgb24, null);
                    }

                    VideoBitmap.Lock();
                    try
                    {
                        var copyStride = Math.Min(
                            stride, VideoBitmap.BackBufferStride);
                        for (var y = 0; y < height; y++)
                        {
                            var sourceOffset = y * stride;
                            if (sourceOffset + copyStride > sample.Length)
                                break;
                            Marshal.Copy(
                                sample,
                                sourceOffset,
                                VideoBitmap.BackBuffer +
                                    y * VideoBitmap.BackBufferStride,
                                copyStride);
                        }
                        VideoBitmap.AddDirtyRect(
                            new Int32Rect(0, 0, width, height));
                    }
                    finally
                    {
                        VideoBitmap.Unlock();
                    }
                    OnFrameReady?.Invoke();
                }

                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher == null || dispatcher.CheckAccess()) Render();
                else dispatcher.Invoke(Render);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[VideoReceiver] Frame render error: {ex.Message}");
            }
        }

        private sealed record PendingVideoFrame(
            int Width,
            int Height,
            byte[] Sample);

        // ===========================================
        // FPS/RTT 통계 보고
        // ===========================================

        private void StartStatsReporting()
        {
            _fpsStopwatch.Restart();
            _fpsCounter = 0;

            // 2초마다 FPS를 Host에 전송 + ping
            _statsTimer = new Timer(StatsTimerCallback, null, 1000, 2000);
        }

        private void StopStatsReporting()
        {
            _statsTimer?.Dispose();
            _statsTimer = null;
        }

        private void StatsTimerCallback(object? state)
        {
            // FPS 계산
            double elapsed = _fpsStopwatch.Elapsed.TotalSeconds;
            if (elapsed > 0)
            {
                CurrentFps = (int)(_fpsCounter / elapsed);
            }
            _fpsCounter = 0;
            _fpsStopwatch.Restart();

            // Host에 FPS 통계 전송 (적응형 비트레이트용)
            if (_inputChannel?.readyState == RTCDataChannelState.open)
            {
                var data = new byte[3];
                data[0] = MSG_STATS;
                BitConverter.GetBytes((ushort)CurrentFps).CopyTo(data, 1);
                _inputChannel.send(data);
            }

            // Ping 전송 (RTT 측정용)
            SendPing();
        }

        private void SendPing()
        {
            if (_inputChannel?.readyState == RTCDataChannelState.open)
            {
                _pingStopwatch.Restart();
                _inputChannel.send(new byte[] { MSG_PING });
            }
        }

        /// <summary>
        /// Host로부터 Pong 수신 시 호출 (RTT 계산)
        /// </summary>
        private void HandlePong()
        {
            RttMs = (int)_pingStopwatch.ElapsedMilliseconds;
        }

        // ===========================================
        // 시그널 / 연결 / 재연결
        // ===========================================

        public async Task HandleSignalAsync(object signal)
        {
            if (_peerConnection == null) return;

            try
            {
                var jobj = JObject.FromObject(signal);

                // Host가 비밀번호 불일치로 거절한 경우
                if (jobj.ContainsKey("rejected"))
                {
                    string reason = jobj["reason"]?.ToString() ?? "unknown";
                    Console.WriteLine($"[VideoReceiver] Connection rejected: {reason}");
                    OnRejected?.Invoke(reason);
                    return;
                }

                if (jobj.ContainsKey("sdp"))
                {
                    var sdpStr = jobj["sdp"]!["sdp"]!.ToString();
                    var type = (RTCSdpType)Enum.Parse(typeof(RTCSdpType), jobj["sdp"]!["type"]!.ToString());

                    var result = _peerConnection.setRemoteDescription(new RTCSessionDescriptionInit {
                        type = type,
                        sdp = sdpStr
                    });
                    Console.WriteLine($"[VideoReceiver] Remote SDP set ({type}): {result}");
                    if (result == SetDescriptionResultEnum.OK)
                    {
                        _remoteDescriptionSet = true;
                        while (_pendingIceCandidates.TryDequeue(out var pending))
                            _peerConnection.addIceCandidate(pending);
                    }

                    if (type == RTCSdpType.offer)
                    {
                        var answer = _peerConnection.createAnswer();
                        await _peerConnection.setLocalDescription(answer);
                        OnSignalReady?.Invoke(new {
                            sdp = new {
                                sdp = answer.sdp,
                                type = answer.type.ToString().ToLower()
                            }
                        });
                        Console.WriteLine("[VideoReceiver] Answer sent");
                    }
                }
                else if (jobj.ContainsKey("ice"))
                {
                    var iceCandidate = new RTCIceCandidateInit
                    {
                        candidate = jobj["ice"]!["candidate"]!.ToString(),
                        sdpMid = jobj["ice"]!["sdpMid"]?.ToString(),
                        sdpMLineIndex = (ushort)(jobj["ice"]!["sdpMLineIndex"] ?? 0)
                    };

                    if (!_remoteDescriptionSet)
                    {
                        _pendingIceCandidates.Enqueue(iceCandidate);
                        Console.WriteLine("[VideoReceiver] ICE candidate queued");
                    }
                    else
                    {
                        _peerConnection.addIceCandidate(iceCandidate);
                        Console.WriteLine("[VideoReceiver] ICE candidate added");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VideoReceiver] Signal Handle Error: {ex.Message}");
            }
        }

        /// <summary>
        /// PeerConnection 정리 후 새로 생성 (재연결용)
        /// </summary>
        public void Reset()
        {
            Console.WriteLine("[VideoReceiver] Resetting for reconnect...");
            StopStatsReporting();
            _inputChannel = null;
            OnInputReadyChanged?.Invoke(false);
            _peerConnection?.Close("reset");
            _peerConnection?.Dispose();
            _peerConnection = null;
            _decoder?.Dispose();
            _decoder = null;
            _frameCount = 0;
            CurrentFps = 0;
            RttMs = -1;

            InitializePeerConnection();
        }

        public async Task StartAsync(string? password = null)
        {
            if (_peerConnection == null) return;

            // DataChannel 생성 (Viewer → Host 입력 전송용)
            _inputChannel = await _peerConnection.createDataChannel("input");
            _inputChannel.onopen += () =>
            {
                Console.WriteLine("[VideoReceiver] Input DataChannel opened");
                OnInputReadyChanged?.Invoke(true);
                ApplyPresentationSettings();
            };

            // DataChannel 생성 (파일 전송용)
            _fileChannel = await _peerConnection.createDataChannel("file");
            _fileChannel.onopen += () => Console.WriteLine("[VideoReceiver] File DataChannel opened");
            _fileChannel.onmessage += (dc, protocol, data) =>
            {
                if (data.Length > 0 && data[0] == MSG_FILE_ACK)
                {
                    Console.WriteLine("[FileTransfer] Host acknowledged file receipt");
                    OnFileComplete?.Invoke("파일 전송 및 검증 완료");
                }
            };
            _inputChannel.onclose += () =>
            {
                Console.WriteLine("[VideoReceiver] Input DataChannel closed");
                OnInputReadyChanged?.Invoke(false);
            };
            // Host에서 보낸 데이터 수신 (Pong, Clipboard 등)
            _inputChannel.onmessage += (dc, protocol, data) =>
            {
                if (data == null || data.Length < 1) return;
                switch (data[0])
                {
                    case MSG_INPUT_ACK:
                        LastInputAcknowledgedAt = DateTime.UtcNow;
                        break;
                    case MSG_PONG:
                        HandlePong();
                        break;
                    case MSG_CLIPBOARD:
                        if (data.Length > 1)
                        {
                            string text = System.Text.Encoding.UTF8.GetString(data, 1, data.Length - 1);
                            Console.WriteLine($"[Clipboard] Received from Host ({text.Length} chars)");
                            OnClipboardReceived?.Invoke(text);
                        }
                        break;
                    case MSG_FILE_ACK:
                        Console.WriteLine("[FileTransfer] Host acknowledged file receipt");
                        OnFileComplete?.Invoke("파일 전송 및 검증 완료");
                        break;
                }
            };

            var offer = _peerConnection.createOffer();
            await _peerConnection.setLocalDescription(offer);

            // offer 시그널에 비밀번호 포함 (설정된 경우)
            object signalData;
            if (password != null)
            {
                signalData = new {
                    sdp = new {
                        sdp = offer.sdp,
                        type = offer.type.ToString().ToLower()
                    },
                    password = password
                };
            }
            else
            {
                signalData = new {
                    sdp = new {
                        sdp = offer.sdp,
                        type = offer.type.ToString().ToLower()
                    }
                };
            }
            OnSignalReady?.Invoke(signalData);
            Console.WriteLine("[VideoReceiver] Offer sent");
        }

        /// <summary>
        /// 바이너리 입력 데이터를 DataChannel로 전송합니다.
        /// </summary>
        public bool SendInput(byte[] data)
        {
            if (_inputChannel == null || _inputChannel.readyState != RTCDataChannelState.open)
            {
                Console.WriteLine("[Input] Send skipped: input DataChannel is not open.");
                return false;
            }
            _inputChannel.send(data);
            return true;
        }

        /// <summary>
        /// 클립보드 텍스트를 DataChannel로 Host에 전송합니다.
        /// </summary>
        public void SendClipboard(string text)
        {
            if (_inputChannel == null || _inputChannel.readyState != RTCDataChannelState.open) return;
            byte[] textBytes = System.Text.Encoding.UTF8.GetBytes(text);
            byte[] msg = new byte[1 + textBytes.Length];
            msg[0] = MSG_CLIPBOARD;
            textBytes.CopyTo(msg, 1);
            _inputChannel.send(msg);
            Console.WriteLine($"[Clipboard] Sent to Host ({text.Length} chars)");
        }

        public void SendMonitorSwitch()
        {
            if (_inputChannel?.readyState == RTCDataChannelState.open)
            {
                _inputChannel.send(new byte[] { 0x06 }); // MSG_MONITOR_SWITCH
            }
        }

        public void SetPresentationActive(bool active)
        {
            _presentationActive = active;
            if (active)
                Interlocked.Exchange(ref _pendingThumbnailFrame, null);
            try
            {
                if (active)
                {
                    EnsureAudioPlayback();
                    _waveOut?.Play();
                }
                else
                {
                    ReleaseAudioPlayback();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Audio] Presentation state warning: {ex.Message}");
            }

            ApplyPresentationSettings();
        }

        public void SetThumbnailStreaming(
            bool active,
            MonitoringProfile? profile = null)
        {
            var nextProfile = profile ?? _thumbnailProfile;
            if (_thumbnailStreaming == active &&
                _thumbnailProfile.Equals(nextProfile)) return;
            _thumbnailStreaming = active;
            _thumbnailProfile = nextProfile;
            ApplyPresentationSettings();
        }

        private void ApplyPresentationSettings()
        {
            if (!InputChannelReady) return;
            if (!_presentationActive)
            {
                if (_thumbnailStreaming)
                    SendSettings(
                        _thumbnailProfile.FramesPerSecond,
                        0,
                        _thumbnailProfile);
                else
                    SendSettings(0, 0);
                return;
            }

            try
            {
                var settings = AppSettings.Load();
                var qualityLevel = settings.Quality switch
                {
                    "최상" => 3,
                    "중" => 1,
                    "하" => 0,
                    _ => 2,
                };
                SendSettings(settings.GetTargetFps(), qualityLevel);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VideoReceiver] Failed to apply presentation settings: {ex.Message}");
            }
        }
        public void SendSettings(
            int fps,
            int qualityLevel,
            MonitoringProfile? profile = null)
        {
            if (_inputChannel?.readyState == RTCDataChannelState.open)
            {
                byte[] msg = profile == null ? new byte[3] : new byte[11];
                msg[0] = 0x07;
                msg[1] = (byte)Math.Clamp(fps, 0, 60);
                msg[2] = (byte)Math.Clamp(qualityLevel, 0, 3);
                if (profile is { } monitoring)
                {
                    BinaryPrimitives.WriteUInt16BigEndian(
                        msg.AsSpan(3, 2), monitoring.Width);
                    BinaryPrimitives.WriteUInt16BigEndian(
                        msg.AsSpan(5, 2), monitoring.Height);
                    BinaryPrimitives.WriteInt32BigEndian(
                        msg.AsSpan(7, 4),
                        monitoring.TargetBitrateBitsPerSecond);
                }
                _inputChannel.send(msg);
                Console.WriteLine(
                    $"[Settings] FPS={fps}, Quality={qualityLevel}, " +
                    $"Size={profile?.Width ?? 0}x{profile?.Height ?? 0}");
            }
        }

        private void EnsureAudioPlayback()
        {
            lock (_audioSync)
            {
                if (_waveOut != null) return;
                _opusDecoder = new OpusDecoder(48000, 2);
                _waveProvider = new BufferedWaveProvider(
                    new WaveFormat(48000, 16, 2))
                {
                    BufferDuration = TimeSpan.FromSeconds(1),
                    DiscardOnBufferOverflow = true,
                };
                try
                {
                    var wasapiOut = new NAudio.Wave.WasapiOut(
                        NAudio.CoreAudioApi.AudioClientShareMode.Shared, 50);
                    wasapiOut.Init(_waveProvider);
                    _waveOut = wasapiOut;
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"[Audio] WASAPI unavailable ({ex.Message}); using WaveOut.");
                    var waveOut = new WaveOutEvent { DesiredLatency = 100 };
                    waveOut.Init(_waveProvider);
                    _waveOut = waveOut;
                }
            }
        }

        private void ReleaseAudioPlayback()
        {
            lock (_audioSync)
            {
                try { _waveOut?.Stop(); } catch { }
                _waveOut?.Dispose();
                _waveOut = null;
                _waveProvider = null;
                _opusDecoder = null;
            }
        }

        /// <summary>
        /// 파일을 DataChannel로 청크 분할 전송합니다.
        /// 프로토콜: FILE_START(파일이름+크기) → FILE_CHUNK(데이터)×N → FILE_END
        /// </summary>
        public async Task SendFileAsync(string filePath)
        {
            if (_fileChannel == null || _fileChannel.readyState != RTCDataChannelState.open)
            {
                Console.WriteLine("[FileTransfer] File DataChannel not open");
                throw new InvalidOperationException("File connection not ready");
            }

            var fileInfo = new System.IO.FileInfo(filePath);
            if (!fileInfo.Exists)
            {
                Console.WriteLine($"[FileTransfer] File not found: {filePath}");
                return;
            }

            string fileName = fileInfo.Name;
            long fileSize = fileInfo.Length;
            const long maxFileSize = 256L * 1024 * 1024;
            if (fileSize <= 0 || fileSize > maxFileSize)
                throw new InvalidOperationException("Files must be between 1 byte and 256 MB.");
            Console.WriteLine($"[FileTransfer] Sending: {fileName} ({fileSize} bytes)");

            // 1) FILE_START: [type][uint32 size][UTF-8 name][SHA-256]
            byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(fileName);
            if (nameBytes.Length is < 1 or > 1024)
                throw new InvalidOperationException("File name is too long.");
            byte[] hashBytes;
            using (var hashStream = fileInfo.OpenRead())
                hashBytes = await System.Security.Cryptography.SHA256.HashDataAsync(hashStream);
            byte[] startMsg = new byte[1 + 4 + nameBytes.Length + hashBytes.Length];
            startMsg[0] = MSG_FILE_START;
            BitConverter.GetBytes((uint)fileSize).CopyTo(startMsg, 1);
            nameBytes.CopyTo(startMsg, 5);
            hashBytes.CopyTo(startMsg, 5 + nameBytes.Length);
            _fileChannel.send(startMsg);

            // 2) FILE_CHUNK: 14KB 단위로 전송 (DataChannel SCTP 제한 고려)
            const int chunkSize = 14 * 1024;
            byte[] buffer = new byte[chunkSize];
            long sent = 0;

            using var fs = fileInfo.OpenRead();
            int bytesRead;
            while ((bytesRead = await fs.ReadAsync(buffer, 0, chunkSize)) > 0)
            {
                byte[] chunkMsg = new byte[1 + bytesRead];
                chunkMsg[0] = MSG_FILE_CHUNK;
                Array.Copy(buffer, 0, chunkMsg, 1, bytesRead);
                _fileChannel.send(chunkMsg);

                sent += bytesRead;
                int progress = (int)(sent * 100 / fileSize);
                OnFileProgress?.Invoke(progress);

                // SCTP 버퍼 오버플로우 방지: 충분한 딜레이 (20ms)
                await Task.Delay(20);
            }

            // 3) FILE_END
            _fileChannel.send(new byte[] { MSG_FILE_END });
            Console.WriteLine($"[FileTransfer] Send complete: {fileName}");
        }

        public void Dispose()
        {
            StopStatsReporting();
            _inputChannel?.close();
            _peerConnection?.Close("disposed");
            _decoder?.Dispose();
            ReleaseAudioPlayback();
        }

    }
}
