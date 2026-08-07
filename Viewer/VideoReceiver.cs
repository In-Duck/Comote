using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
using Concentus;
using Concentus.Structs;
using Concentus.Enums;
using Comote.Input;

namespace Viewer
{
    public class VideoReceiver : IDisposable
    {
        private RTCPeerConnection? _peerConnection;
        private RTCDataChannel? _inputChannel;
        private RTCDataChannel? _fileChannel; // File Transfer ONLY
        private readonly RemoteChannelClientState _inputSecurity =
            new(RemoteChannelKind.Input);
        private readonly RemoteChannelClientState _fileSecurity =
            new(RemoteChannelKind.File);
        private readonly object _inputSendGate = new();
        private readonly object _fileSendGate = new();
        private readonly object _lifecycleGate = new();
        private readonly List<RTCIceCandidateInit> _iceQueue = [];
        private readonly ConnectionGenerationState _generationState = new();
        private readonly string _expectedHostId;
        private RemoteFileSender? _fileSender;
        private Guid _controlSessionId = Guid.NewGuid();
        private bool _disposed;
        private bool _remoteDescriptionSet;
        private readonly ClipboardConsentState _clipboardConsent = new();
        private long _offerStartedGeneration;
        private FFmpegVideoEncoder? _decoder;
        private IOpusDecoder? _opusDecoder;
        private IWavePlayer? _waveOut;
        private BufferedWaveProvider? _waveProvider;
        public WriteableBitmap? VideoBitmap { get; private set; }
        private int _frameCount = 0;

        // --- FPS/RTT 측정용 ---
        private int _fpsCounter = 0;
        private readonly Stopwatch _fpsStopwatch = new();
        private readonly Stopwatch _pingStopwatch = new();
        private Timer? _statsTimer;

        /// <summary>최근 1초간 디코딩된 FPS</summary>
        public int CurrentFps { get; private set; }

        /// <summary>DataChannel ping/pong으로 측정된 RTT (ms)</summary>
        public int RttMs { get; private set; } = -1;
        public bool InputChannelReady
        {
            get
            {
                lock (_lifecycleGate)
                {
                    return IsInputChannelReadyLocked();
                }
            }
        }

        private bool IsInputChannelReadyLocked()
        {
            long generation = _generationState.Current;
            return !_disposed &&
                   _generationState.IsConnected(generation) &&
                   _peerConnection?.connectionState ==
                       RTCPeerConnectionState.connected &&
                   _inputChannel?.readyState ==
                       RTCDataChannelState.open &&
                   _inputSecurity.IsAuthenticated;
        }
        public DateTime LastInputAcknowledgedAt { get; private set; } = DateTime.MinValue;
        public bool ClipboardSessionEnabled
        {
            get
            {
                lock (_lifecycleGate)
                {
                    return !_disposed &&
                        _clipboardConsent.Requested &&
                        _clipboardConsent.Enabled &&
                        _inputSecurity.IsAuthenticated;
                }
            }
        }

        public ClipboardConsentSnapshot ClipboardConsent
        {
            get
            {
                lock (_lifecycleGate)
                {
                    return _clipboardConsent.Snapshot;
                }
            }
        }

        public RemoteInputMode? NegotiatedInputMode { get; private set; }
        public uint NegotiatedInputCapabilities { get; private set; }

        // --- 프로토콜 상수 ---
        private const byte MSG_STATS     = 0x20;
        private const byte MSG_PING      = 0x21;
        private const byte MSG_PONG      = 0x22;
        private const byte MSG_CLIPBOARD = 0x23;
        private const byte MSG_INPUT_ACK = 0x14;

        // --- 이벤트 ---
        public event Func<object, Task>? OnSignalReady;
        public event Action? OnFrameReady;
        public event Action<RTCPeerConnectionState>? OnConnectionStateChanged;
        public event Action<bool>? OnInputAuthorizationChanged;
        public event Action<string>? OnRejected;
        public event Action<string, long>? OnClipboardReceived;
        public event Action<ClipboardConsentSnapshot>? OnClipboardConsentChanged;
        public event Action<int>? OnFileProgress;     // 0~100%
        public event Action<string>? OnFileComplete;   // 완료 메시지

        public RTCPeerConnectionState ConnectionState
        {
            get
            {
                lock (_lifecycleGate)
                {
                    return _peerConnection?.connectionState ??
                        RTCPeerConnectionState.closed;
                }
            }
        }

        public VideoReceiver(string expectedHostId)
        {
            if (string.IsNullOrWhiteSpace(expectedHostId))
            {
                throw new ArgumentException(
                    "Expected Host ID is required.",
                    nameof(expectedHostId));
            }

            _expectedHostId = expectedHostId;
            lock (_lifecycleGate)
            {
                _generationState.BeginNew();
                InitializePeerConnectionLocked();
            }
        }

        /// <summary>
        /// PeerConnection 초기화 (신규 생성 또는 재연결 시 호출)
        /// </summary>
        private void InitializePeerConnectionLocked()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            long generation = _generationState.Current;
            _remoteDescriptionSet = false;
            _iceQueue.Clear();

            var config = new RTCConfiguration
            {
                iceServers = new List<RTCIceServer>
                {
                    new RTCIceServer { urls = "stun:stun.l.google.com:19302" }
                }
            };

            var peerConnection = new RTCPeerConnection(config);
            _peerConnection = peerConnection;

            // FFmpegVideoEncoder를 디코더로도 사용 (DecodeVideo 메서드 보유)
            var decoder = new FFmpegVideoEncoder();
            _decoder = decoder;

            // Concentus Opus 디코더 초기화 (48kHz, 스테레오)
            var opusDecoder =
                OpusCodecFactory.CreateDecoder(48000, 2, null);
            _opusDecoder = opusDecoder;
            Console.WriteLine("[Audio] Opus decoder initialized: 48000Hz, 2ch");

            // 오디오 재생기 초기화 (Opus 48kHz, 스테레오)
            // 오디오 재생기 초기화 (Opus 48kHz, 스테레오)
            var waveProvider = new BufferedWaveProvider(
                new WaveFormat(48000, 16, 2))
            {
                BufferDuration = TimeSpan.FromSeconds(3),
                DiscardOnBufferOverflow = true
            };
            _waveProvider = waveProvider;
            
            // [Stability] Smart Buffering Logic (Drift Correction)
            // _waveProvider의 버퍼가 너무 쌓이면(지연 발생) 일부를 버려서 최신 상태 유지
            _ = Task.Run(async () =>
            {
                while (IsGenerationCurrent(generation, peerConnection))
                {
                    if (peerConnection.connectionState ==
                            RTCPeerConnectionState.connected &&
                        waveProvider.BufferedDuration.TotalMilliseconds > 1000)
                    {
                        waveProvider.ClearBuffer();
                        Console.WriteLine(
                            "[Audio] Buffer too large; cleared to catch up.");
                    }

                    await Task.Delay(1000).ConfigureAwait(false);
                }
            });

            // [Fix] WaveOutEvent 대신 WasapiOut 사용 (지연 시간/안정성 개선)
            // 지연 시간 50ms, Shared 모드
            try 
            {
                var wasapiOut = new NAudio.Wave.WasapiOut(
                    NAudio.CoreAudioApi.AudioClientShareMode.Shared, 50);
                wasapiOut.Init(_waveProvider);
                wasapiOut.Play();
                _waveOut = wasapiOut;
                Console.WriteLine("[Audio] Initialized WasapiOut (50ms latency)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Audio] WasapiOut failed ({ex.Message}), falling back to WaveOutEvent");
                var waveOut = new WaveOutEvent { DesiredLatency = 100 }; // 100ms
                waveOut.Init(_waveProvider);
                waveOut.Play();
                _waveOut = waveOut;
            }

            // H.264 수신 전용 트랙 추가
            var supportedFormats = new List<VideoFormat>
            {
                new VideoFormat(VideoCodecsEnum.H264, 96, 90000, null)
            };
            var videoTrack = new MediaStreamTrack(supportedFormats, MediaStreamStatusEnum.RecvOnly);
            peerConnection.addTrack(videoTrack);

            // Opus 오디오 수신 전용 트랙 추가 (SDP에 오디오 라인 포함 필수)
            var opusFormat = new AudioFormat(111, "opus", 48000);
            var audioTrack = new MediaStreamTrack(opusFormat, MediaStreamStatusEnum.RecvOnly);
            peerConnection.addTrack(audioTrack);

            // RTP 수신 → 직접 디코딩
            peerConnection.OnVideoFrameReceived += (rep, timestamp, frame, format) =>
            {
                if (!IsGenerationCurrent(generation, peerConnection))
                    return;

                _frameCount++;
                _fpsCounter++;

                if (_frameCount <= 10 || _frameCount % 30 == 0)
                {
                    Console.WriteLine($"[VideoReceiver] RTP #{_frameCount}: {frame.Length} bytes");
                }

                try
                {
                    var decodedFrames = decoder.DecodeVideo(
                        frame,
                        VideoPixelFormatsEnum.Bgra,
                        VideoCodecsEnum.H264);

                    foreach (var decoded in decodedFrames)
                    {
                        RenderFrame(decoded, generation, peerConnection);
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

            peerConnection.onicecandidate += candidate =>
            {
                if (candidate == null ||
                    !IsGenerationCurrent(generation, peerConnection))
                {
                    return;
                }

                RaiseSignalReady(generation, new
                {
                    connectionGeneration = generation,
                    ice = new
                    {
                        candidate = candidate.candidate,
                        sdpMid = candidate.sdpMid,
                        sdpMLineIndex = candidate.sdpMLineIndex
                    }
                });
            };

            peerConnection.onconnectionstatechange += state =>
                HandleConnectionStateChanged(
                    generation,
                    peerConnection,
                    state);

            // 오디오 수신 처리 (Concentus Opus 디코딩)
            int _audioFrameCount = 0;
            peerConnection.OnRtpPacketReceived += (rep, media, rtpPacket) =>
            {
                if (!IsGenerationCurrent(generation, peerConnection))
                    return;

                if (media == SDPMediaTypesEnum.audio)
                {
                    try
                    {
                        if (_audioFrameCount++ < 5)
                        {
                            Console.WriteLine($"[Audio] RTP audio packet #{_audioFrameCount}: {rtpPacket.Payload.Length} bytes");
                        }

                        // Concentus로 Opus 디코딩 (20ms = 960 samples per channel, 스테레오 = 1920)
                        short[] pcmOutput = new short[960 * 2];
                        int decodedSamples = opusDecoder.Decode(
                            rtpPacket.Payload.AsSpan(),
                            pcmOutput.AsSpan(),
                            960,
                            false);

                        if (decodedSamples > 0)
                        {
                            // short[] -> byte[] 변환 후 WaveProvider에 추가
                            int totalSamples = decodedSamples * 2; // 스테레오
                            byte[] byteData = new byte[totalSamples * 2]; // 16bit = 2bytes per sample
                            Buffer.BlockCopy(pcmOutput, 0, byteData, 0, byteData.Length);
                            
                            // [Stability] Jitter Buffer 처리 (너무 적으면 무시? 아니면 Silence 삽입?)
                            // 여기서는 단순 추가하지만, 위쪽 Task에서 과다 버퍼링 제어
                            waveProvider.AddSamples(byteData, 0, byteData.Length);
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

        private bool IsGenerationCurrent(
            long generation,
            RTCPeerConnection peerConnection)
        {
            lock (_lifecycleGate)
            {
                return !_disposed &&
                       ReferenceEquals(_peerConnection, peerConnection) &&
                       _generationState.IsCurrent(generation);
            }
        }

        private bool IsSameGeneration(
            long generation,
            RTCPeerConnection peerConnection)
        {
            lock (_lifecycleGate)
            {
                return !_disposed &&
                       ReferenceEquals(_peerConnection, peerConnection) &&
                       _generationState.Current == generation;
            }
        }

        private bool IsConnectedGeneration(
            long generation,
            RTCPeerConnection peerConnection)
        {
            lock (_lifecycleGate)
            {
                return IsGenerationCurrent(generation, peerConnection) &&
                       _generationState.IsConnected(generation);
            }
        }
        private bool IsCurrentInputChannel(
            long generation,
            RTCPeerConnection peerConnection,
            RTCDataChannel channel)
        {
            lock (_lifecycleGate)
            {
                return IsGenerationCurrent(generation, peerConnection) &&
                       ReferenceEquals(_inputChannel, channel);
            }
        }

        private bool IsCurrentFileChannel(
            long generation,
            RTCPeerConnection peerConnection,
            RTCDataChannel channel)
        {
            lock (_lifecycleGate)
            {
                return IsGenerationCurrent(generation, peerConnection) &&
                       ReferenceEquals(_fileChannel, channel);
            }
        }
        private void RaiseSignalReady(long generation, object signal)
        {
            lock (_lifecycleGate)
            {
                if (_disposed || !_generationState.IsCurrent(generation))
                    return;
            }

            var handlers = OnSignalReady;
            if (handlers == null)
                return;

            foreach (Func<object, Task> handler in
                     handlers.GetInvocationList())
            {
                _ = InvokeSignalHandlerAsync(handler, signal);
            }
        }

        private static async Task InvokeSignalHandlerAsync(
            Func<object, Task> handler,
            object signal)
        {
            try
            {
                await handler(signal).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[VideoReceiver] Signal callback failed: {ex.GetType().Name}");
            }
        }

        private void HandleConnectionStateChanged(
            long generation,
            RTCPeerConnection peerConnection,
            RTCPeerConnectionState state)
        {
            bool connected = false;
            bool terminal = false;

            lock (_lifecycleGate)
            {
                if (_disposed ||
                    !ReferenceEquals(_peerConnection, peerConnection) ||
                    !_generationState.IsCurrent(generation))
                {
                    return;
                }

                if (state == RTCPeerConnectionState.connected)
                {
                    connected = _generationState.MarkConnected(generation);
                }
                else if (state == RTCPeerConnectionState.disconnected ||
                         state == RTCPeerConnectionState.failed ||
                         state == RTCPeerConnectionState.closed)
                {
                    terminal = _generationState.Terminate(generation);
                    if (terminal)
                    {
                        RevokeControlLocked(closeChannels: true);
                    }
                }
            }

            if ((connected &&
                 !IsConnectedGeneration(generation, peerConnection)) ||
                (terminal &&
                 !IsSameGeneration(generation, peerConnection)) ||
                (!connected && !terminal &&
                 !IsGenerationCurrent(generation, peerConnection)))
            {
                return;
            }

            Console.WriteLine($"[VideoReceiver] Connection state: {state}");
            if (connected)
            {
                StartStatsReporting(generation, peerConnection);
                bool inputReady = InputChannelReady;
                NotifyInputAuthorization(inputReady);
                if (inputReady)
                    SendInitialSettings();
            }
            else
            {
                StopStatsReporting();
                NotifyInputAuthorization(false);
            }

            try
            {
                OnConnectionStateChanged?.Invoke(state);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[VideoReceiver] Connection-state callback failed: {ex.Message}");
            }
        }

        private void RevokeControlLocked(bool closeChannels)
        {
            TrySendReleaseAllLocked();
            _inputSecurity.Revoke();
            _fileSecurity.Revoke();
            _fileSender?.Abort(
                new IOException("The control connection was revoked."));
            NegotiatedInputMode = null;
            NegotiatedInputCapabilities = 0;
            _clipboardConsent.Revoke();
            _remoteDescriptionSet = false;
            _iceQueue.Clear();

            if (!closeChannels)
                return;

            var inputChannel = _inputChannel;
            var fileChannel = _fileChannel;
            var fileSender = _fileSender;
            _inputChannel = null;
            _fileChannel = null;
            _fileSender = null;
            SafeClose(inputChannel);
            SafeClose(fileChannel);
            fileSender?.Dispose();
        }

        private void TrySendReleaseAllLocked()
        {
            try
            {
                if (_inputChannel?.readyState != RTCDataChannelState.open ||
                    !_inputSecurity.IsAuthenticated)
                {
                    return;
                }

                lock (_inputSendGate)
                {
                    _inputChannel.send(
                        _inputSecurity.WrapPayload(new byte[] { 0x13 }));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[Control] RELEASE_ALL best effort failed: {ex.Message}");
            }
        }

        private static void SafeClose(RTCDataChannel? channel)
        {
            if (channel == null)
                return;

            try
            {
                channel.close();
            }
            catch
            {
            }
        }

        private void NotifyInputAuthorization(bool isAuthorized)
        {
            try
            {
                OnInputAuthorizationChanged?.Invoke(isAuthorized);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[Control] Authorization callback failed: {ex.Message}");
            }
        }
        private void RenderFrame(
            VideoSample decoded,
            long generation,
            RTCPeerConnection peerConnection)
        {
            if (!IsGenerationCurrent(generation, peerConnection))
                return;

            try
            {
                int width = (int)decoded.Width;
                int height = (int)decoded.Height;
                byte[] sample = decoded.Sample;
                int stride = width * 3;

                if (_frameCount % 30 == 1)
                {
                    Console.WriteLine($"[VideoReceiver] SUCCESS: Decoded {width}x{height}, sample={sample.Length} bytes");
                }

                Application.Current?.Dispatcher.Invoke(() =>
                {
                    if (!IsGenerationCurrent(generation, peerConnection))
                        return;

                    if (VideoBitmap == null ||
                        VideoBitmap.PixelWidth != width ||
                        VideoBitmap.PixelHeight != height)
                    {
                        Console.WriteLine($"[VideoReceiver] Creating bitmap: {width}x{height}");
                        VideoBitmap = new WriteableBitmap(
                            width, height, 96, 96,
                            PixelFormats.Rgb24, null);
                    }

                    VideoBitmap.Lock();
                    try
                    {
                        int bmpStride = VideoBitmap.BackBufferStride;
                        int copyStride = Math.Min(stride, bmpStride);

                        for (int y = 0; y < height; y++)
                        {
                            int srcOffset = y * stride;
                            IntPtr dstPtr = VideoBitmap.BackBuffer + (y * bmpStride);

                            if (srcOffset + copyStride <= sample.Length)
                            {
                                Marshal.Copy(sample, srcOffset, dstPtr, copyStride);
                            }
                        }

                        VideoBitmap.AddDirtyRect(new Int32Rect(0, 0, width, height));
                    }
                    finally
                    {
                        VideoBitmap.Unlock();
                    }

                    OnFrameReady?.Invoke();
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VideoReceiver] Frame render error: {ex}");
            }
        }

        // ===========================================
        // FPS/RTT 통계 보고
        // ===========================================

        private sealed record StatsTimerContext(
            long Generation,
            RTCPeerConnection PeerConnection);

        private void StartStatsReporting(
            long generation,
            RTCPeerConnection peerConnection)
        {
            lock (_lifecycleGate)
            {
                if (!IsConnectedGeneration(generation, peerConnection))
                    return;

                _fpsStopwatch.Restart();
                _fpsCounter = 0;
                _statsTimer?.Dispose();
                _statsTimer = new Timer(
                    StatsTimerCallback,
                    new StatsTimerContext(generation, peerConnection),
                    1000,
                    2000);
            }
        }
        private void StopStatsReporting()
        {
            lock (_lifecycleGate)
            {
                _statsTimer?.Dispose();
                _statsTimer = null;
            }
        }

        private void StatsTimerCallback(object? state)
        {
            if (state is not StatsTimerContext context ||
                !IsConnectedGeneration(
                    context.Generation,
                    context.PeerConnection))
            {
                return;
            }

            // FPS 계산
            double elapsed = _fpsStopwatch.Elapsed.TotalSeconds;
            if (elapsed > 0)
            {
                CurrentFps = (int)(_fpsCounter / elapsed);
            }
            _fpsCounter = 0;
            _fpsStopwatch.Restart();

            // Host에 FPS 통계 전송 (적응형 비트레이트용)
            var data = new byte[3];
            data[0] = MSG_STATS;
            BitConverter.GetBytes((ushort)CurrentFps).CopyTo(data, 1);
            SendSecuredInputPayload(
                data,
                context.Generation,
                context.PeerConnection);

            // Ping 전송 (RTT 측정용)
            SendPing(context);
        }

        private void SendPing(StatsTimerContext context)
        {
            _pingStopwatch.Restart();
            SendSecuredInputPayload(
                new byte[] { MSG_PING },
                context.Generation,
                context.PeerConnection);
        }

        /// <summary>
        /// Host로부터 Pong 수신 시 호출 (RTT 계산)
        /// </summary>
        private void HandlePong()
        {
            RttMs = (int)_pingStopwatch.ElapsedMilliseconds;
        }

        private static bool TryReadControlToken(
            JObject signal,
            out byte[] token)
        {
            token = [];
            if (signal["control"] is not JObject control ||
                control["version"]?.Type != JTokenType.Integer ||
                control.Value<int>("version") !=
                    RemoteControlProtocol.Version ||
                control["token"]?.Type != JTokenType.String)
            {
                return false;
            }

            string? tokenText = control.Value<string>("token");
            if (string.IsNullOrWhiteSpace(tokenText))
                return false;

            try
            {
                token = Convert.FromBase64String(tokenText);
            }
            catch (FormatException)
            {
                return false;
            }

            if (token.Length == RemoteControlProtocol.TokenSize)
                return true;

            CryptographicOperations.ZeroMemory(token);
            token = [];
            return false;
        }

        private void AssignControlTokenLocked(
            long generation,
            RTCPeerConnection peerConnection,
            byte[] token)
        {
            try
            {
                if (!IsGenerationCurrent(generation, peerConnection))
                    return;

                _inputSecurity.SetToken(token);
                _fileSecurity.SetToken(token);
                TrySendAuthenticationHelloLocked(
                    generation,
                    peerConnection,
                    _inputChannel,
                    _inputSecurity);
                TrySendAuthenticationHelloLocked(
                    generation,
                    peerConnection,
                    _fileChannel,
                    _fileSecurity);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(token);
            }
        }

        private void TrySendAuthenticationHelloLocked(
            long generation,
            RTCPeerConnection peerConnection,
            RTCDataChannel? channel,
            RemoteChannelClientState security)
        {
            if (!IsGenerationCurrent(generation, peerConnection) ||
                channel?.readyState != RTCDataChannelState.open)
            {
                return;
            }

            try
            {
                var hello = security.TryCreateHello();
                if (hello != null)
                {
                    channel.send(hello);
                }
            }
            catch
            {
                security.Revoke();
                SafeClose(channel);
            }
        }

        private void HandleSecuredInputMessage(
            long generation,
            RTCPeerConnection peerConnection,
            RTCDataChannel channel,
            byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                CloseInputChannelForProtocolViolation(
                    generation,
                    peerConnection,
                    channel,
                    "control_protocol_violation");
                return;
            }

            RemoteChannelReceiveResult result;
            byte[] payload;
            RemoteAuthAccepted accepted;

            lock (_lifecycleGate)
            {
                if (!IsGenerationCurrent(generation, peerConnection) ||
                    !ReferenceEquals(_inputChannel, channel))
                {
                    return;
                }

                result = _inputSecurity.ProcessServerMessage(
                    data,
                    out payload,
                    out accepted);
                if (result == RemoteChannelReceiveResult.Authenticated)
                {
                    NegotiatedInputMode = accepted.InputMode;
                    NegotiatedInputCapabilities = accepted.Capabilities;
                }
            }

            switch (result)
            {
                case RemoteChannelReceiveResult.Authenticated:
                    Console.WriteLine(
                        $"[Control] Input authenticated ({accepted.InputMode}).");
                    bool ready =
                        IsCurrentInputChannel(
                            generation,
                            peerConnection,
                            channel) &&
                        InputChannelReady;
                    NotifyInputAuthorization(ready);
                    if (ready)
                    {
                        SendInitialSettings();
                        SendRequestedClipboardConsent();
                    }
                    break;
                case RemoteChannelReceiveResult.Payload:
                    if (!IsCurrentInputChannel(
                            generation,
                            peerConnection,
                            channel))
                    {
                        return;
                    }
                    if (!HandleInputResponsePayload(payload))
                    {
                        CloseInputChannelForProtocolViolation(
                            generation,
                            peerConnection,
                            channel,
                            "control_protocol_violation");
                    }
                    break;
                case RemoteChannelReceiveResult.Rejected:
                    CloseInputChannelForProtocolViolation(
                        generation,
                        peerConnection,
                        channel,
                        "control_auth_rejected");
                    break;
                case RemoteChannelReceiveResult.ProtocolViolation:
                    CloseInputChannelForProtocolViolation(
                        generation,
                        peerConnection,
                        channel,
                        "control_protocol_violation");
                    break;
            }
        }

        private void HandleSecuredFileMessage(
            long generation,
            RTCPeerConnection peerConnection,
            RTCDataChannel channel,
            byte[] data)
        {
            RemoteChannelReceiveResult result;
            byte[] payload;
            RemoteFileSender? fileSender;
            lock (_lifecycleGate)
            {
                if (!IsGenerationCurrent(generation, peerConnection) ||
                    !ReferenceEquals(_fileChannel, channel) ||
                    data == null || data.Length == 0)
                {
                    if (data == null || data.Length == 0)
                    {
                        CloseFileChannelLocked(
                            generation,
                            peerConnection,
                            channel);
                    }
                    return;
                }

                result = _fileSecurity.ProcessServerMessage(
                    data,
                    out payload,
                    out _);
                fileSender = _fileSender;
            }

            switch (result)
            {
                case RemoteChannelReceiveResult.Authenticated:
                    Console.WriteLine("[Control] File channel authenticated.");
                    break;
                case RemoteChannelReceiveResult.Payload
                    when fileSender != null &&
                         fileSender.TryProcessResponse(payload):
                    break;
                default:
                    lock (_lifecycleGate)
                    {
                        CloseFileChannelLocked(
                            generation,
                            peerConnection,
                            channel);
                    }
                    break;
            }
        }
        private bool HandleInputResponsePayload(byte[] data)
        {
            if (data.Length == 0)
                return false;

            switch (data[0])
            {
                case MSG_INPUT_ACK when data.Length == 2:
                    LastInputAcknowledgedAt = DateTime.UtcNow;
                    return true;
                case MSG_PONG when data.Length == 1:
                    HandlePong();
                    return true;
                case RemoteClipboardConsentProtocol.MessageType:
                    if (!RemoteClipboardConsentProtocol.TryParse(
                            data,
                            out var clipboardEnabled))
                    {
                        return false;
                    }

                    ClipboardConsentSnapshot snapshot;
                    lock (_lifecycleGate)
                    {
                        bool revokeUnexpectedEnable =
                            clipboardEnabled && !_clipboardConsent.Requested;
                        snapshot = _clipboardConsent.ApplyAcknowledgement(
                            clipboardEnabled);
                        if (revokeUnexpectedEnable)
                        {
                            _ = TrySendClipboardConsentMessageLocked(false);
                        }
                    }

                    NotifyClipboardConsentChanged(snapshot);
                    return true;
                case MSG_CLIPBOARD
                    when data.Length is > 1 and <= 1 + 32 * 1024:
                    long clipboardEpoch;
                    lock (_lifecycleGate)
                    {
                        if (!_clipboardConsent.TryCaptureActiveEpoch(
                                out clipboardEpoch) ||
                            !IsInputChannelReadyLocked())
                        {
                            // A frame already in flight when consent was revoked
                            // is benign and must not close the control channel.
                            return true;
                        }
                    }
                    try
                    {
                        string text = new System.Text.UTF8Encoding(
                            encoderShouldEmitUTF8Identifier: false,
                            throwOnInvalidBytes: true).GetString(
                                data,
                                1,
                                data.Length - 1);
                        if (!IsClipboardConsentEpochActive(clipboardEpoch))
                        {
                            return true;
                        }

                        try
                        {
                            OnClipboardReceived?.Invoke(text, clipboardEpoch);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine(
                                $"[Clipboard] Callback failed: {ex.GetType().Name}");
                        }
                        return true;
                    }
                    catch (System.Text.DecoderFallbackException)
                    {
                        return false;
                    }
                default:
                    return false;
            }
        }
        private void SendInitialSettings()
        {
            try
            {
                var settings = AppSettings.Load();
                int qualityLevel = settings.Quality switch
                {
                    "최상" => 3,
                    "상" => 2,
                    "중" => 1,
                    "하" => 0,
                    _ => 2,
                };
                SendSettings(settings.GetTargetFps(), qualityLevel);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[Control] Initial settings failed: {ex.Message}");
            }
        }

        private void CloseInputChannelForProtocolViolation(
            long generation,
            RTCPeerConnection peerConnection,
            RTCDataChannel channel,
            string rejectionReason)
        {
            bool closed;
            lock (_lifecycleGate)
            {
                closed = CloseInputChannelForProtocolViolationLocked(
                    generation,
                    peerConnection,
                    channel);
            }

            if (!closed)
                return;

            StopStatsReporting();
            NotifyInputAuthorization(false);
            try
            {
                OnRejected?.Invoke(rejectionReason);
            }
            catch
            {
            }
        }

        private bool CloseInputChannelForProtocolViolationLocked(
            long generation,
            RTCPeerConnection peerConnection,
            RTCDataChannel channel)
        {
            if (!IsGenerationCurrent(generation, peerConnection) ||
                !ReferenceEquals(_inputChannel, channel))
            {
                return false;
            }

            TrySendReleaseAllLocked();
            _inputSecurity.Revoke();
            _inputChannel = null;
            NegotiatedInputMode = null;
            NegotiatedInputCapabilities = 0;
            _clipboardConsent.Revoke();
            SafeClose(channel);
            return true;
        }

        private bool CloseFileChannelLocked(
            long generation,
            RTCPeerConnection peerConnection,
            RTCDataChannel channel)
        {
            if (!IsGenerationCurrent(generation, peerConnection) ||
                !ReferenceEquals(_fileChannel, channel))
            {
                return false;
            }

            _fileSecurity.Revoke();
            var fileSender = _fileSender;
            _fileChannel = null;
            _fileSender = null;
            fileSender?.Abort(
                new IOException("The file channel was closed."));
            SafeClose(channel);
            fileSender?.Dispose();
            return true;
        }

        private bool SendSecuredInputPayload(
            ReadOnlySpan<byte> payload,
            long? expectedGeneration = null,
            RTCPeerConnection? expectedPeerConnection = null)
        {
            lock (_lifecycleGate)
            {
                long generation = _generationState.Current;
                if ((expectedGeneration.HasValue &&
                     expectedGeneration.Value != generation) ||
                    (expectedPeerConnection != null &&
                     !ReferenceEquals(
                         _peerConnection,
                         expectedPeerConnection)) ||
                    !InputChannelReady ||
                    !_generationState.IsConnected(generation) ||
                    _inputChannel == null)
                {
                    return false;
                }

                lock (_inputSendGate)
                {
                    _inputChannel.send(
                        _inputSecurity.WrapPayload(payload));
                    return true;
                }
            }
        }

        private bool SendSecuredFilePayload(
            ReadOnlySpan<byte> payload,
            long expectedGeneration,
            RTCPeerConnection expectedPeerConnection,
            RTCDataChannel expectedChannel)
        {
            lock (_lifecycleGate)
            {
                long generation = _generationState.Current;
                if (_disposed ||
                    expectedGeneration != generation ||
                    !ReferenceEquals(
                        _peerConnection,
                        expectedPeerConnection) ||
                    !ReferenceEquals(_fileChannel, expectedChannel) ||
                    !_generationState.IsConnected(generation) ||
                    _peerConnection?.connectionState !=
                        RTCPeerConnectionState.connected ||
                    _fileChannel?.readyState != RTCDataChannelState.open ||
                    !_fileSecurity.IsAuthenticated)
                {
                    return false;
                }

                lock (_fileSendGate)
                {
                    _fileChannel.send(
                        _fileSecurity.WrapPayload(payload));
                    return true;
                }
            }
        }
        // ===========================================
        // Signaling / connection / reconnect
        // ===========================================
        /// <summary>
        /// PeerConnection 정리 후 새로 생성 (재연결용)
        /// </summary>
        public void Reset()
        {
            Console.WriteLine("[VideoReceiver] Resetting for reconnect...");
            ClipboardConsentSnapshot clipboardSnapshot;
            lock (_lifecycleGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);

                TrySendReleaseAllLocked();
                _inputSecurity.Revoke();
                _fileSecurity.Revoke();
                _generationState.BeginNew();

                var inputChannel = _inputChannel;
                var fileChannel = _fileChannel;
                var fileSender = _fileSender;
                var peerConnection = _peerConnection;
                _inputChannel = null;
                _fileChannel = null;
                _fileSender = null;
                _peerConnection = null;
                _offerStartedGeneration = 0;

                SafeClose(inputChannel);
                SafeClose(fileChannel);
                fileSender?.Dispose();
                try
                {
                    peerConnection?.Close("reset");
                    peerConnection?.Dispose();
                }
                catch
                {
                }

                DisposeMediaLocked();
                _controlSessionId = Guid.NewGuid();
                _inputSecurity.Reset(_controlSessionId);
                _fileSecurity.Reset(_controlSessionId);
                NegotiatedInputMode = null;
                NegotiatedInputCapabilities = 0;
                clipboardSnapshot = _clipboardConsent.Revoke();
                LastInputAcknowledgedAt = DateTime.MinValue;
                _frameCount = 0;
                CurrentFps = 0;
                RttMs = -1;
                InitializePeerConnectionLocked();
            }

            StopStatsReporting();
            NotifyInputAuthorization(false);
            NotifyClipboardConsentChanged(clipboardSnapshot);
        }

        public async Task StartAsync(string? password = null)
        {
            RTCPeerConnection peerConnection;
            long generation;
            ClipboardConsentSnapshot clipboardSnapshot;
            lock (_lifecycleGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                peerConnection = _peerConnection ??
                    throw new InvalidOperationException(
                        "Peer connection is not initialized.");
                generation = _generationState.Current;
                if (!_generationState.IsCurrent(generation) ||
                    _offerStartedGeneration == generation)
                {
                    throw new InvalidOperationException(
                        "This connection generation already started or ended.");
                }

                _offerStartedGeneration = generation;
                _controlSessionId = Guid.NewGuid();
                _inputSecurity.Reset(_controlSessionId);
                _fileSecurity.Reset(_controlSessionId);
                clipboardSnapshot = _clipboardConsent.Revoke();
            }

            NotifyClipboardConsentChanged(clipboardSnapshot);
            RTCDataChannel inputChannel =
                await peerConnection.createDataChannel("input")
                    .ConfigureAwait(false);
            lock (_lifecycleGate)
            {
                if (!IsGenerationCurrent(generation, peerConnection))
                {
                    SafeClose(inputChannel);
                    return;
                }

                _inputChannel = inputChannel;
                inputChannel.onopen += () =>
                {
                    lock (_lifecycleGate)
                    {
                        if (!IsGenerationCurrent(
                                generation,
                                peerConnection) ||
                            !ReferenceEquals(_inputChannel, inputChannel))
                        {
                            return;
                        }

                        TrySendAuthenticationHelloLocked(
                            generation,
                            peerConnection,
                            inputChannel,
                            _inputSecurity);
                    }
                };
                inputChannel.onclose += () =>
                    HandleInputChannelClosed(
                        generation,
                        peerConnection,
                        inputChannel);
                inputChannel.onmessage += (_, _, data) =>
                    HandleSecuredInputMessage(
                        generation,
                        peerConnection,
                        inputChannel,
                        data);
            }

            RTCDataChannel fileChannel =
                await peerConnection.createDataChannel("file")
                    .ConfigureAwait(false);
            var fileSender = new RemoteFileSender(
                payload => SendSecuredFilePayload(
                    payload.Span,
                    generation,
                    peerConnection,
                    fileChannel),
                () =>
                {
                    lock (_lifecycleGate)
                    {
                        bool isOpen =
                            IsGenerationCurrent(
                                generation,
                                peerConnection) &&
                            ReferenceEquals(_fileChannel, fileChannel) &&
                            _generationState.IsConnected(generation) &&
                            _fileSecurity.IsAuthenticated &&
                            fileChannel.readyState ==
                                RTCDataChannelState.open;
                        return (
                            isOpen,
                            isOpen
                                ? checked((ulong)fileChannel.bufferedAmount)
                                : 0);
                    }
                },
                _ =>
                {
                    lock (_lifecycleGate)
                    {
                        CloseFileChannelLocked(
                            generation,
                            peerConnection,
                            fileChannel);
                    }
                });
            fileSender.ProgressChanged += progress =>
                OnFileProgress?.Invoke(progress);
            lock (_lifecycleGate)
            {
                if (!IsGenerationCurrent(generation, peerConnection))
                {
                    SafeClose(fileChannel);
                    fileSender.Dispose();
                    return;
                }

                _fileChannel = fileChannel;
                _fileSender = fileSender;
                fileChannel.onopen += () =>
                {
                    lock (_lifecycleGate)
                    {
                        if (!IsGenerationCurrent(
                                generation,
                                peerConnection) ||
                            !ReferenceEquals(_fileChannel, fileChannel))
                        {
                            return;
                        }

                        TrySendAuthenticationHelloLocked(
                            generation,
                            peerConnection,
                            fileChannel,
                            _fileSecurity);
                    }
                };
                fileChannel.onclose += () =>
                {
                    lock (_lifecycleGate)
                    {
                        CloseFileChannelLocked(
                            generation,
                            peerConnection,
                            fileChannel);
                    }
                };
                fileChannel.onmessage += (_, _, data) =>
                    HandleSecuredFileMessage(
                        generation,
                        peerConnection,
                        fileChannel,
                        data);
            }

            var offer = peerConnection.createOffer();
            await peerConnection.setLocalDescription(offer)
                .ConfigureAwait(false);
            if (!IsGenerationCurrent(generation, peerConnection))
                return;

            object signalData = password != null
                ? new
                {
                    connectionGeneration = generation,
                    sdp = new { sdp = offer.sdp, type = "offer" },
                    password
                }
                : new
                {
                    connectionGeneration = generation,
                    sdp = new { sdp = offer.sdp, type = "offer" }
                };
            RaiseSignalReady(generation, signalData);
            Console.WriteLine(
                $"[VideoReceiver] Offer sent for generation {generation}.");
        }

        private void HandleInputChannelClosed(
            long generation,
            RTCPeerConnection peerConnection,
            RTCDataChannel channel)
        {
            bool closed;
            lock (_lifecycleGate)
            {
                closed = CloseInputChannelForProtocolViolationLocked(
                    generation,
                    peerConnection,
                    channel);
            }

            if (closed)
            {
                StopStatsReporting();
                NotifyInputAuthorization(false);
            }
        }

        private void DisposeMediaLocked()
        {
            try
            {
                _waveOut?.Stop();
            }
            catch
            {
            }

            _waveOut?.Dispose();
            _waveOut = null;
            _waveProvider = null;
            _opusDecoder = null;
            _decoder?.Dispose();
            _decoder = null;
        }
        /// <summary>
        /// 바이너리 입력 데이터를 DataChannel로 전송합니다.
        /// </summary>
        public bool SendInput(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);
            if (data.Length == 0 ||
                data.Length > RemoteControlProtocol.MaximumPayloadSize)
            {
                return false;
            }

            try
            {
                return SendSecuredInputPayload(data);
            }
            catch (Exception ex) when (
                ex is InvalidOperationException or
                      ArgumentOutOfRangeException or
                      OverflowException)
            {
                Console.WriteLine($"[Input] Secure send failed: {ex.Message}");
                return false;
            }
        }
        /// <summary>
        /// 클립보드 텍스트를 DataChannel로 Host에 전송합니다.
        /// </summary>
        public bool SetClipboardConsentForSession(bool enabled)
        {
            bool sent = true;
            ClipboardConsentSnapshot snapshot;
            lock (_lifecycleGate)
            {
                if (_disposed)
                {
                    return false;
                }

                snapshot = _clipboardConsent.Request(enabled);
                if (IsInputChannelReadyLocked())
                {
                    sent = TrySendClipboardConsentMessageLocked(enabled);
                    if (!sent)
                    {
                        snapshot = _clipboardConsent.Revoke();
                    }
                }
            }

            NotifyClipboardConsentChanged(snapshot);
            return sent;
        }

        private void SendRequestedClipboardConsent()
        {
            ClipboardConsentSnapshot? disabledSnapshot = null;
            lock (_lifecycleGate)
            {
                if (!_clipboardConsent.Requested)
                {
                    return;
                }

                if (!TrySendClipboardConsentMessageLocked(true))
                {
                    disabledSnapshot = _clipboardConsent.Revoke();
                }
            }

            if (disabledSnapshot is { } snapshot)
            {
                NotifyClipboardConsentChanged(snapshot);
            }
        }

        private bool TrySendClipboardConsentMessageLocked(bool enabled)
        {
            try
            {
                return SendSecuredInputPayload(
                    RemoteClipboardConsentProtocol.Create(enabled));
            }
            catch (Exception ex) when (
                ex is InvalidOperationException or
                      ArgumentOutOfRangeException or
                      OverflowException or
                      ObjectDisposedException)
            {
                Console.WriteLine(
                    $"[Clipboard] Consent send failed: {ex.GetType().Name}");
                return false;
            }
        }

        private void NotifyClipboardConsentChanged(
            ClipboardConsentSnapshot snapshot)
        {
            try
            {
                OnClipboardConsentChanged?.Invoke(snapshot);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[Clipboard] Consent callback failed: {ex.GetType().Name}");
            }
        }

        public bool IsClipboardConsentEpochActive(long consentEpoch)
        {
            lock (_lifecycleGate)
            {
                return _clipboardConsent.IsActive(consentEpoch) &&
                    IsInputChannelReadyLocked();
            }
        }

        public bool TryApplyClipboardWithConsentLease(
            long consentEpoch,
            Func<bool> isExpectedTarget,
            Action applyClipboard)
        {
            ArgumentNullException.ThrowIfNull(isExpectedTarget);
            ArgumentNullException.ThrowIfNull(applyClipboard);

            ClipboardConsentState.ClipboardConsentLease? lease;
            lock (_lifecycleGate)
            {
                if (_disposed ||
                    !IsInputChannelReadyLocked() ||
                    !_clipboardConsent.TryAcquireActiveLease(
                        consentEpoch,
                        out lease) ||
                    lease == null)
                {
                    return false;
                }
            }

            using (lease)
            {
                if (!isExpectedTarget())
                {
                    return false;
                }

                applyClipboard();
                return true;
            }
        }
        public bool SendClipboard(string text)
        {
            ArgumentNullException.ThrowIfNull(text);
            byte[] textBytes = System.Text.Encoding.UTF8.GetBytes(text);
            if (textBytes.Length > 32 * 1024)
            {
                throw new ArgumentOutOfRangeException(nameof(text));
            }

            byte[] msg = new byte[1 + textBytes.Length];
            msg[0] = MSG_CLIPBOARD;
            textBytes.CopyTo(msg, 1);

            lock (_lifecycleGate)
            {
                if (!_clipboardConsent.Enabled ||
                    !_clipboardConsent.Requested ||
                    !IsInputChannelReadyLocked())
                {
                    return false;
                }

                bool sent;
                try
                {
                    sent = SendSecuredInputPayload(msg);
                }
                catch (Exception ex) when (
                    ex is InvalidOperationException or
                          ArgumentOutOfRangeException or
                          OverflowException or
                          ObjectDisposedException)
                {
                    sent = false;
                }

                if (!sent)
                {
                    return false;
                }
            }

            Console.WriteLine($"[Clipboard] Sent to Host ({text.Length} chars)");
            return true;
        }
        public void SendMonitorSwitch()
        {
            SendInput(new byte[] { 0x06 }); // MSG_MONITOR_SWITCH
        }

        public void SendSettings(int fps, int qualityLevel)
        {
            if (fps is < 1 or > 120 || qualityLevel is < 0 or > 3)
            {
                throw new ArgumentOutOfRangeException(
                    fps is < 1 or > 120 ? nameof(fps) : nameof(qualityLevel));
            }

            byte[] msg = new byte[] { 0x07, (byte)fps, (byte)qualityLevel };
            SendInput(msg);
        }

        /// <summary>
        /// Sends one authenticated, framed file transfer and waits for
        /// the Host's matching transfer acknowledgement.
        /// </summary>
        public async Task SendFileAsync(
            string filePath,
            CancellationToken cancellationToken = default)
        {
            RemoteFileSender fileSender;
            long generation;
            lock (_lifecycleGate)
            {
                generation = _generationState.Current;
                if (_disposed ||
                    !_generationState.IsConnected(generation) ||
                    !_fileSecurity.IsAuthenticated ||
                    _fileChannel?.readyState !=
                        RTCDataChannelState.open ||
                    _fileSender == null)
                {
                    throw new InvalidOperationException(
                        "File channel is not authenticated.");
                }

                fileSender = _fileSender;
            }

            await fileSender.SendAsync(filePath, cancellationToken)
                .ConfigureAwait(false);

            lock (_lifecycleGate)
            {
                if (_disposed ||
                    generation != _generationState.Current ||
                    !ReferenceEquals(fileSender, _fileSender))
                {
                    throw new IOException(
                        "The connection changed before the transfer completed.");
                }
            }

            try
            {
                OnFileComplete?.Invoke("File transfer verified.");
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[FileTransfer] Completion callback failed: {ex.GetType().Name}");
            }
        }
        public void Dispose()
        {
            lock (_lifecycleGate)
            {
                if (_disposed)
                    return;

                TrySendReleaseAllLocked();
                _inputSecurity.Revoke();
                _fileSecurity.Revoke();
                _generationState.BeginNew();
                _disposed = true;

                var inputChannel = _inputChannel;
                var fileChannel = _fileChannel;
                var fileSender = _fileSender;
                var peerConnection = _peerConnection;
                _inputChannel = null;
                _fileChannel = null;
                _fileSender = null;
                _peerConnection = null;
                _clipboardConsent.Revoke();

                SafeClose(inputChannel);
                SafeClose(fileChannel);
                fileSender?.Dispose();
                try
                {
                    peerConnection?.Close("disposed");
                    peerConnection?.Dispose();
                }
                catch
                {
                }

                DisposeMediaLocked();
                _inputSecurity.Dispose();
                _fileSecurity.Dispose();
            }

            StopStatsReporting();
            NotifyInputAuthorization(false);
            GC.SuppressFinalize(this);
        }

        public Task HandleSignalAsync(string from, object signal)
        {
            ArgumentNullException.ThrowIfNull(signal);
            if (!string.Equals(
                    from,
                    _expectedHostId,
                    StringComparison.Ordinal))
            {
                Console.WriteLine(
                    "[VideoReceiver] Ignored signal from an unexpected Host.");
                return Task.CompletedTask;
            }

            JObject json;
            try
            {
                json = signal as JObject ?? JObject.FromObject(signal);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[VideoReceiver] Invalid signal object: {ex.Message}");
                return Task.CompletedTask;
            }

            if (json["connectionGeneration"]?.Type != JTokenType.Integer)
            {
                Console.WriteLine(
                    "[VideoReceiver] Ignored signal without a valid generation.");
                return Task.CompletedTask;
            }

            long generation;
            try
            {
                generation = json.Value<long>("connectionGeneration");
            }
            catch (Exception)
            {
                return Task.CompletedTask;
            }

            string? rejection = null;
            bool authorizationRevoked = false;
            lock (_lifecycleGate)
            {
                var peerConnection = _peerConnection;
                if (peerConnection == null ||
                    _offerStartedGeneration != generation ||
                    !IsGenerationCurrent(generation, peerConnection))
                {
                    Console.WriteLine(
                        $"[VideoReceiver] Ignored stale signal generation {generation}.");
                    return Task.CompletedTask;
                }

                try
                {
                    int signalKindCount =
                        (json.ContainsKey("rejected") ? 1 : 0) +
                        (json.ContainsKey("sdp") ? 1 : 0) +
                        (json.ContainsKey("ice") ? 1 : 0);
                    if (signalKindCount != 1)
                    {
                        throw new InvalidOperationException(
                            "A signal must contain exactly one body.");
                    }

                    if (json.ContainsKey("rejected"))
                    {
                        if (json["rejected"]?.Type != JTokenType.Boolean ||
                            json.Value<bool>("rejected") != true)
                        {
                            throw new InvalidOperationException(
                                "The rejection body is malformed.");
                        }

                        rejection = json["reason"]?.Type == JTokenType.String
                            ? json.Value<string>("reason")
                            : "connection_rejected";
                        if (string.IsNullOrWhiteSpace(rejection) ||
                            rejection.Length > 128)
                        {
                            rejection = "connection_rejected";
                        }

                        authorizationRevoked =
                            _generationState.Terminate(generation);
                        if (authorizationRevoked)
                            RevokeControlLocked(closeChannels: true);
                    }
                    else if (json["sdp"] is JObject sdpObject)
                    {
                        if (sdpObject["type"]?.Type != JTokenType.String ||
                            !string.Equals(
                                sdpObject.Value<string>("type"),
                                "answer",
                                StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException(
                                "Only an exact SDP answer is accepted.");
                        }

                        string? sdp = sdpObject["sdp"]?.Type ==
                                JTokenType.String
                            ? sdpObject.Value<string>("sdp")
                            : null;
                        if (string.IsNullOrWhiteSpace(sdp) ||
                            sdp.Length > 1024 * 1024)
                        {
                            throw new InvalidOperationException(
                                "The SDP answer is empty or too large.");
                        }

                        if (!TryReadControlToken(json, out var token))
                        {
                            throw new InvalidOperationException(
                                "The control handshake is missing or invalid.");
                        }

                        if (!_generationState.TryAcceptAnswer(generation))
                        {
                            CryptographicOperations.ZeroMemory(token);
                            throw new InvalidOperationException(
                                "Only one SDP answer is accepted per generation.");
                        }

                        var result = peerConnection.setRemoteDescription(
                            new RTCSessionDescriptionInit
                            {
                                type = RTCSdpType.answer,
                                sdp = sdp
                            });
                        if (result != SetDescriptionResultEnum.OK)
                        {
                            CryptographicOperations.ZeroMemory(token);
                            throw new InvalidOperationException(
                                $"Remote SDP was rejected: {result}");
                        }

                        _remoteDescriptionSet = true;
                        foreach (var queuedCandidate in _iceQueue)
                        {
                            peerConnection.addIceCandidate(queuedCandidate);
                        }
                        _iceQueue.Clear();

                        AssignControlTokenLocked(
                            generation,
                            peerConnection,
                            token);
                    }
                    else if (json["ice"] is JObject iceObject)
                    {
                        if (iceObject["candidate"]?.Type !=
                                JTokenType.String ||
                            iceObject["sdpMLineIndex"]?.Type !=
                                JTokenType.Integer)
                        {
                            throw new InvalidOperationException(
                                "The ICE candidate is malformed.");
                        }

                        string candidate =
                            iceObject.Value<string>("candidate") ?? "";
                        string? sdpMid = iceObject["sdpMid"]?.Type ==
                                JTokenType.String
                            ? iceObject.Value<string>("sdpMid")
                            : null;
                        int lineIndex =
                            iceObject.Value<int>("sdpMLineIndex");
                        if (candidate.Length is < 1 or > 8192 ||
                            (sdpMid?.Length ?? 0) > 64 ||
                            lineIndex is < 0 or > ushort.MaxValue)
                        {
                            throw new InvalidOperationException(
                                "The ICE candidate is out of range.");
                        }

                        var parsedCandidate = new RTCIceCandidateInit
                        {
                            candidate = candidate,
                            sdpMid = sdpMid,
                            sdpMLineIndex = checked((ushort)lineIndex)
                        };
                        if (_remoteDescriptionSet)
                        {
                            peerConnection.addIceCandidate(parsedCandidate);
                        }
                        else
                        {
                            if (_iceQueue.Count >= 64)
                            {
                                throw new InvalidOperationException(
                                    "Too many queued ICE candidates.");
                            }
                            _iceQueue.Add(parsedCandidate);
                        }
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            "The signal body is malformed.");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"[VideoReceiver] Signal protocol violation: {ex.Message}");
                    rejection ??= "signal_protocol_violation";
                    authorizationRevoked =
                        _generationState.Terminate(generation);
                    if (authorizationRevoked)
                        RevokeControlLocked(closeChannels: true);
                }
            }

            lock (_lifecycleGate)
            {
                if (_disposed ||
                    _generationState.Current != generation)
                {
                    return Task.CompletedTask;
                }
            }

            if (authorizationRevoked)
            {
                StopStatsReporting();
                NotifyInputAuthorization(false);
            }

            if (rejection != null)
            {
                try
                {
                    OnRejected?.Invoke(rejection);
                }
                catch
                {
                }
            }

            return Task.CompletedTask;
        }
    }
}
