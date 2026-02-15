using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.FFmpeg;
using SIPSorcery.Media;
using System.Runtime.InteropServices;
using Newtonsoft.Json.Linq;
using NAudio.Wave;
using Concentus.Structs;
using Concentus.Enums;

namespace Host
{
    public class WebRTCManager : IDisposable
    {
        private RTCPeerConnection? _peerConnection;
        private FFmpegVideoEncoder? _videoEncoder;
        private OpusEncoder? _opusEncoder;
        private WasapiLoopbackCapture? _audioCapture;
        private ScreenCapture _capture;
        private InputSimulator _inputSimulator;
        private byte[]? _lastRawFrame;
        private bool _isStreaming;
        private string? _remoteSocketId;
        private uint _timestamp = 0;
        private int _frameCount = 0;
        private RTCDataChannel? _viewerInputChannel;
        private RTCDataChannel? _viewerFileChannel;

        // 프로토콜 상수 (Stats/Ping/Pong/Clipboard/MonitorSwitch)
        // 주의: 0x01~0x04=마우스, 0x10~0x12=키보드 (InputSimulator)
        //       → 그 외 프로토콜은 0x05~0x06, 0x20 이상 사용 (충돌 방지)
        private const byte MSG_MONITOR_SWITCH  = 0x06;
        private const byte MSG_STATS           = 0x20;
        private const byte MSG_PING            = 0x21;
        private const byte MSG_PONG            = 0x22;
        private const byte MSG_CLIPBOARD       = 0x23;

        // 파일 전송 프로토콜
        private const byte MSG_FILE_START = 0x30;
        private const byte MSG_FILE_CHUNK = 0x31;
        private const byte MSG_FILE_END   = 0x32;
        private const byte MSG_FILE_ACK   = 0x33;

        // 적응형 비트레이트 상태
        private int _viewerFps = 0;
        private string? _password;

        // 모니터 전환 상태
        private int _currentAdapterIndex;
        private int _currentOutputIndex;

        // 클립보드 공유 상태
        private string? _lastClipboardText;
        private System.Threading.Timer? _clipboardTimer;

        // 파일 수신 상태
        private System.IO.MemoryStream? _fileReceiveStream;
        private string? _fileReceiveName;
        private uint _fileReceiveSize;

        public event Action<string, object>? OnSignalReady;

        public WebRTCManager(ScreenCapture capture, string? password = null)
        {
            _capture = capture;
            _inputSimulator = new InputSimulator(capture.Width, capture.Height);
            _password = password;
            _currentAdapterIndex = capture.AdapterIndex;
            _currentOutputIndex = capture.OutputIndex;
        }

        private void EnsureInitialized(string remoteSocketId)
        {
            // 이전 연결이 남아있으면 정리 후 재생성 (Viewer 재연결 지원)
            if (_peerConnection != null)
            {
                Console.WriteLine("[WebRTC] Cleaning up previous connection for reconnect...");
                _isStreaming = false;
                _peerConnection.close();
                _peerConnection.Dispose();
                _peerConnection = null;
                _videoEncoder = null;
            }

            _remoteSocketId = remoteSocketId;

            var config = new RTCConfiguration
            {
                iceServers = new System.Collections.Generic.List<RTCIceServer>
                {
                    new RTCIceServer { urls = "stun:stun.l.google.com:19302" }
                }
            };

            _peerConnection = new RTCPeerConnection(config);
            _videoEncoder = new FFmpegVideoEncoder();
            
            // 오디오 트랙 설정 (Opus 48kHz)
            var audioFormat = new AudioFormat(111, "opus", 48000);
            var audioTrack = new MediaStreamTrack(audioFormat, MediaStreamStatusEnum.SendOnly);
            _peerConnection.addTrack(audioTrack);

            // 최적의 비디오 인코더 선택 (GPU 가속 우선)
            var (encoderName, encoderOpts) = SelectBestVideoEncoder();
            Console.WriteLine($"[Video] Selected Encoder: {encoderName}");

            try 
            {
                _videoEncoder.SetCodec(FFmpeg.AutoGen.AVCodecID.AV_CODEC_ID_H264, encoderName, encoderOpts);
                _videoEncoder.InitialiseEncoder(FFmpeg.AutoGen.AVCodecID.AV_CODEC_ID_H264, _capture.Width, _capture.Height, 30);
                _videoEncoder.SetBitrate(20000000, null, null, null); // 20Mbps
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Video] Failed to init {encoderName}: {ex.Message}. Falling back to libx264.");
                var fallbackOpts = new System.Collections.Generic.Dictionary<string, string> { 
                    { "preset", "ultrafast" }, 
                    { "tune", "zerolatency" },
                    { "crf", "18" } // 품질 확보
                };
                _videoEncoder.SetCodec(FFmpeg.AutoGen.AVCodecID.AV_CODEC_ID_H264, "libx264", fallbackOpts);
                _videoEncoder.InitialiseEncoder(FFmpeg.AutoGen.AVCodecID.AV_CODEC_ID_H264, _capture.Width, _capture.Height, 30);
            }

            // 비트레이트 상향: 50Mbps (HQ)
            _videoEncoder.SetBitrate(50_000_000, null, null, null); 

            var h264Format = new VideoFormat(VideoCodecsEnum.H264, 96, 90000, null);
            var videoTrack = new MediaStreamTrack(h264Format, MediaStreamStatusEnum.SendOnly);
            _peerConnection.addTrack(videoTrack);

            _peerConnection.onicecandidate += (candidate) =>
            {
                if (candidate != null && _remoteSocketId != null)
                {
                    OnSignalReady?.Invoke(_remoteSocketId, new { 
                        ice = new { 
                            candidate = candidate.candidate, 
                            sdpMid = candidate.sdpMid, 
                            sdpMLineIndex = candidate.sdpMLineIndex 
                        } 
                    });
                }
            };

            _peerConnection.onconnectionstatechange += (state) =>
            {
                Console.WriteLine($"[WebRTC] Connection state: {state}");
                if (state == RTCPeerConnectionState.connected && !_isStreaming)
                {
                    _isStreaming = true;
                    _ = Task.Run(SendVideoLoop);
                    StartClipboardMonitoring();
                    StartAudioCapture();
                }
                else if (state == RTCPeerConnectionState.failed ||
                         state == RTCPeerConnectionState.closed)
                {
                    // failed/closed는 복구 불가 → 즉시 정리
                    _isStreaming = false;
                    StopAudioCapture();
                    _peerConnection?.close();
                }
                else if (state == RTCPeerConnectionState.disconnected)
                {
                    // disconnected는 일시적 상태 (ICE 재연결 가능)
                    // 파일 전송 등 진행 중인 작업이 있을 수 있으므로
                    // 5초 대기 후에도 복구 안 되면 정리
                    Console.WriteLine("[WebRTC] Disconnected (waiting 5s for recovery...)");
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(5000);
                        if (_peerConnection?.connectionState == RTCPeerConnectionState.disconnected)
                        {
                            Console.WriteLine("[WebRTC] Not recovered, closing connection");
                            _isStreaming = false;
                            StopAudioCapture();
                            _peerConnection?.close();
                        }
                    });
                }
            };

            // DataChannel 수신 (Viewer에서 생성)
            _peerConnection.ondatachannel += (channel) =>
            {
                Console.WriteLine($"[WebRTC] DataChannel received: {channel.label}");
                if (channel.label == "input")
                {
                    _viewerInputChannel = channel;
                    channel.onmessage += (dc, protocol, data) => HandleInputMessage(dc, data);
                    Console.WriteLine("[WebRTC] Input DataChannel ready");
                }
                else if (channel.label == "file")
                {
                    _viewerFileChannel = channel;
                    channel.onmessage += (dc, protocol, data) => HandleFileMessage(dc, data);
                    Console.WriteLine("[WebRTC] File DataChannel ready");
                }
            };

            Console.WriteLine($"[WebRTC] PeerConnection initialized for {remoteSocketId}");
        }

        private void HandleInputMessage(RTCDataChannel dc, byte[] data)
        {
            if (data == null || data.Length < 1) return;

            switch (data[0])
            {
                case MSG_STATS:
                    if (data.Length >= 3)
                    {
                        _viewerFps = BitConverter.ToUInt16(data, 1);
                        Console.WriteLine($"[WebRTC] Viewer FPS: {_viewerFps}");
                    }
                    break;
                case MSG_PING:
                    if (dc.readyState == RTCDataChannelState.open)
                    {
                        dc.send(new byte[] { MSG_PONG });
                    }
                    break;
                case MSG_PONG:
                    break;
                case MSG_MONITOR_SWITCH:
                    if (data.Length >= 3)
                    {
                        int ai = data[1];
                        int oi = data[2];
                        SwitchMonitor(ai, oi);
                    }
                    else
                    {
                        // 인계값이 없으면 다음 모니터로 자동 순환
                        CycleMonitor();
                    }
                    break;
                case MSG_CLIPBOARD:
                    if (data.Length > 1)
                    {
                        string text = Encoding.UTF8.GetString(data, 1, data.Length - 1);
                        _lastClipboardText = text;
                        SetClipboardOnSta(text);
                        Console.WriteLine($"[Clipboard] Set from Viewer ({text.Length} chars)");
                    }
                    break;
                // [하위 호환성] 구버전 Viewer가 Input 채널로 파일을 보내는 경우 처리
                case 0x30: // MSG_FILE_START
                case 0x31: // MSG_FILE_CHUNK
                case 0x32: // MSG_FILE_END
                    HandleFileMessage(dc, data);
                    break;
                default:
                    // 일반 입력 메시지 (마우스/키보드)
                    _inputSimulator.ProcessMessage(data);
                    break;
            }
        }

        private void HandleFileMessage(RTCDataChannel dc, byte[] data)
        {
            try
            {
                if (data == null || data.Length < 1) return;

                if (data[0] == MSG_FILE_START && data.Length >= 5)
                {
                    _fileReceiveSize = BitConverter.ToUInt32(data, 1);
                    // 인코딩 디버깅: 원본 바이트 출력
                    string hex = BitConverter.ToString(data, 5, Math.Min(data.Length - 5, 20));
                    Console.WriteLine($"[FileTransfer] Name Bytes: {hex}...");
                    
                    _fileReceiveName = Encoding.UTF8.GetString(data, 5, data.Length - 5);
                    _fileReceiveStream = new System.IO.MemoryStream();
                    Console.WriteLine($"[FileTransfer] Start Receiving: {_fileReceiveName} ({_fileReceiveSize} bytes)");
                }
                else if (data[0] == MSG_FILE_CHUNK && _fileReceiveStream != null)
                {
                    _fileReceiveStream.Write(data, 1, data.Length - 1);
                    // 진행 상황 로그 (너무 자주는 말고)
                    if (_fileReceiveStream.Length % (1024 * 1024) < 16000) // 약 1MB 마다
                    {
                         Console.WriteLine($"[FileTransfer] Received {_fileReceiveStream.Length} / {_fileReceiveSize}");
                    }
                }
                else if (data[0] == MSG_FILE_END && _fileReceiveStream != null)
                {
                    Console.WriteLine($"[FileTransfer] File End Received. Processing save...");
                    SaveReceivedFile(dc);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FileTransfer] CRITICAL ERROR: {ex}");
            }
        }

        private void SaveReceivedFile(RTCDataChannel dc)
        {
            try
            {
                string rawFileName = _fileReceiveName ?? "received_file";
                string safeFileName = System.IO.Path.GetFileName(rawFileName);
                
                // 유효하지 않은 문자 제거
                foreach (char c in System.IO.Path.GetInvalidFileNameChars())
                {
                    safeFileName = safeFileName.Replace(c, '_');
                }

                string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                // string downloadFolder = System.IO.Path.Combine(desktopPath, "Comote_Downloads");
                
                // if (!System.IO.Directory.Exists(downloadFolder))
                // {
                //     System.IO.Directory.CreateDirectory(downloadFolder);
                //     Console.WriteLine($"[FileTransfer] Created directory: {downloadFolder}");
                // }

                // 바탕화면에 직접 저장
                string savePath = System.IO.Path.Combine(desktopPath, safeFileName);
                
                // 중복 처리
                if (System.IO.File.Exists(savePath))
                {
                    string nameOnly = System.IO.Path.GetFileNameWithoutExtension(safeFileName);
                    string extension = System.IO.Path.GetExtension(safeFileName);
                    int count = 1;
                    while (System.IO.File.Exists(savePath))
                    {
                        savePath = System.IO.Path.Combine(desktopPath, $"{nameOnly} ({count++}){extension}");
                    }
                }

                Console.WriteLine($"[FileTransfer] Writing to {savePath}...");
                System.IO.File.WriteAllBytes(savePath, _fileReceiveStream.ToArray());
                Console.WriteLine($"[FileTransfer] Success: Saved to {savePath} ({_fileReceiveStream.Length} bytes)");

                _fileReceiveStream.Dispose();
                _fileReceiveStream = null;
                _fileReceiveName = null;

                if (dc.readyState == RTCDataChannelState.open)
                {
                    dc.send(new byte[] { MSG_FILE_ACK });
                    Console.WriteLine("[FileTransfer] ACK sent");
                }
                else
                {
                    Console.WriteLine($"[FileTransfer] Cannot send ACK, state: {dc.readyState}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FileTransfer] Save failed: {ex.Message}");
            }
        }

        private void CycleMonitor()
        {
            var monitors = ScreenCapture.GetMonitors();
            if (monitors.Count <= 1) return;

            int currentIndex = monitors.FindIndex(m => m.AdapterIndex == _currentAdapterIndex && m.OutputIndex == _currentOutputIndex);
            int nextIndex = (currentIndex + 1) % monitors.Count;
            var next = monitors[nextIndex];
            SwitchMonitor(next.AdapterIndex, next.OutputIndex);
        }

        private void SwitchMonitor(int adapterIndex, int outputIndex)
        {
            if (_currentAdapterIndex == adapterIndex && _currentOutputIndex == outputIndex) return;

            Console.WriteLine($"[WebRTC] Switching monitor to Adapter:{adapterIndex}, Output:{outputIndex}");
            
            try 
            {
                var newCapture = new ScreenCapture(adapterIndex, outputIndex);
                var oldCapture = _capture;
                
                // 락을 걸거나 캡처 루프에서 교체되도록 안전하게 교체
                lock (this)
                {
                    _capture = newCapture;
                    _currentAdapterIndex = adapterIndex;
                    _currentOutputIndex = outputIndex;
                    _inputSimulator.UpdateScreenSize(_capture.Width, _capture.Height); // InputSimulator도 업데이트
                }

                // 기존 캡처 리소스 해제
                Task.Run(() => {
                    Thread.Sleep(500); // 전송 중인 프레임이 있을 수 있으므로 약간 지연 후 해제
                    oldCapture.Dispose();
                });

                Console.WriteLine($"[WebRTC] Monitor switched. New size: {newCapture.Width}x{newCapture.Height}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WebRTC] Monitor switch failed: {ex.Message}");
            }
        }

        private unsafe (string name, System.Collections.Generic.Dictionary<string, string> opts) SelectBestVideoEncoder()
        {
            // 1. NVIDIA NVENC (Performance -> Quality Balance)
            if (FFmpeg.AutoGen.ffmpeg.avcodec_find_encoder_by_name("h264_nvenc") != null)
            {
                return ("h264_nvenc", new System.Collections.Generic.Dictionary<string, string> {
                    { "preset", "p3" },      // p1(fastest) -> p3(fast/medium)
                    { "tune", "ll" },        // Low Latency
                    { "rc", "cbr" },         // CBR로 변경하여 일정한 품질 유도
                    { "zerolatency", "1" },
                    { "delay", "0" }
                });
            }
            // 2. Intel QSV (Balanced)
            if (FFmpeg.AutoGen.ffmpeg.avcodec_find_encoder_by_name("h264_qsv") != null)
            {
                return ("h264_qsv", new System.Collections.Generic.Dictionary<string, string> {
                    { "preset", "medium" },  // veryfast -> medium
                    { "look_ahead", "0" },
                    { "low_power", "0" }     // 품질 위해 low_power 끔
                });
            }
            // 3. AMD AMF
            if (FFmpeg.AutoGen.ffmpeg.avcodec_find_encoder_by_name("h264_amf") != null)
            {
                return ("h264_amf", new System.Collections.Generic.Dictionary<string, string> {
                    { "usage", "lowlatency" },
                    { "quality", "balanced" } // speed -> balanced
                });
            }
            // 4. Default Software (libx264)
            return ("libx264", new System.Collections.Generic.Dictionary<string, string> {
                { "preset", "ultrafast" },
                { "tune", "zerolatency" },
                { "crf", "18" },            // CRF 20 -> 18 (Better Quality)
                { "rc-lookahead", "0" }
            });
        }

        private List<short> _audioAccumulator = new List<short>();

        private void StartAudioCapture()
        {
            try
            {
                _audioCapture = new WasapiLoopbackCapture();
                var waveFormat = _audioCapture.WaveFormat;
                Console.WriteLine($"[Audio] Capture Format: {waveFormat.SampleRate}Hz, {waveFormat.BitsPerSample}bit, {waveFormat.Channels}ch, Encoding={waveFormat.Encoding}");

                // Concentus Opus 인코더 초기화 (캡처 포맷에 맞춤)
                _opusEncoder = new OpusEncoder(waveFormat.SampleRate, waveFormat.Channels, OpusApplication.OPUS_APPLICATION_AUDIO);
                _opusEncoder.Bitrate = 128000; // 128kbps 고품질
                Console.WriteLine($"[Audio] Opus encoder initialized: {waveFormat.SampleRate}Hz, {waveFormat.Channels}ch, 128kbps");

                _audioAccumulator.Clear();

                _audioCapture.DataAvailable += (s, e) =>
                {
                    if (!_isStreaming || _peerConnection == null || _peerConnection.connectionState != RTCPeerConnectionState.connected) return;

                    try
                    {
                        var buffer = e.Buffer;
                        int bytesRecorded = e.BytesRecorded;

                        // 1. 형식 변환 (Float -> Short PCM 16bit)
                        if (waveFormat.Encoding == WaveFormatEncoding.IeeeFloat)
                        {
                            for (int i = 0; i < bytesRecorded; i += 4)
                            {
                                float sample = BitConverter.ToSingle(buffer, i);
                                short pcm = (short)(Math.Max(-1.0f, Math.Min(1.0f, sample)) * 32767);
                                _audioAccumulator.Add(pcm);
                            }
                        }
                        else if (waveFormat.BitsPerSample == 16)
                        {
                            for (int i = 0; i < bytesRecorded; i += 2)
                            {
                                _audioAccumulator.Add(BitConverter.ToInt16(buffer, i));
                            }
                        }

                        // 안전장치: 버퍼가 너무 쌓이면(1초 이상) 초기화 (지연 방지)
                        if (_audioAccumulator.Count > waveFormat.SampleRate * waveFormat.Channels)
                        {
                            _audioAccumulator.Clear();
                        }

                        // 2. 프레임 버퍼링 (20ms = SampleRate/50 samples per channel)
                        int frameSizePerChannel = waveFormat.SampleRate / 50; // 48000/50=960
                        int samplesPer20ms = frameSizePerChannel * waveFormat.Channels; // 960*2=1920
                        byte[] opusOutput = new byte[4000]; // Opus 인코딩 출력 버퍼

                        int _audioSendCount = 0;
                        while (_audioAccumulator.Count >= samplesPer20ms)
                        {
                            var frame = new short[samplesPer20ms];
                            _audioAccumulator.CopyTo(0, frame, 0, samplesPer20ms);
                            _audioAccumulator.RemoveRange(0, samplesPer20ms);

                            // Concentus로 Opus 인코딩
                            int encodedLen = _opusEncoder!.Encode(frame, 0, frameSizePerChannel, opusOutput, 0, opusOutput.Length);
                            if (encodedLen > 0)
                            {
                                var encoded = new byte[encodedLen];
                                Array.Copy(opusOutput, encoded, encodedLen);
                                _peerConnection.SendAudio((uint)frameSizePerChannel, encoded);
                                _audioSendCount++;
                                if (_audioSendCount <= 5 || _audioSendCount % 500 == 0)
                                {
                                    Console.WriteLine($"[Audio] Sent frame #{_audioSendCount}: {encodedLen} bytes encoded from {samplesPer20ms} samples");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Audio] Capture Loop Error: {ex.Message}");
                    }
                };

                _audioCapture.StartRecording();
                Console.WriteLine("[WebRTC] WASAPI Audio Loopback capture started");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WebRTC] Failed to start audio capture: {ex.Message}");
            }
        }

        private void StopAudioCapture()
        {
            _audioCapture?.StopRecording();
            _audioCapture?.Dispose();
            _audioCapture = null;
        }

        private async Task SendVideoLoop()
        {
            Console.WriteLine("[WebRTC] Streaming loop started");
            _timestamp = 0;
            var loopWatch = System.Diagnostics.Stopwatch.StartNew();
            int encodeFailCount = 0; // 연속 인코딩 실패 카운터
            try
            {
                while (_isStreaming && _peerConnection != null && _videoEncoder != null)
                {
                    var rawFrame = _capture.Capture();
                    if (rawFrame != null)
                    {
                        _lastRawFrame = rawFrame;
                    }

                    if (_lastRawFrame != null)
                    {
                        try 
                        {
                            // 1) BGRA 프레임을 H.264로 인코딩
                            if (_frameCount % 30 == 0) _videoEncoder.ForceKeyFrame();

                            var encodedFrame = _videoEncoder.EncodeVideo(
                                _capture.Width, _capture.Height,
                                _lastRawFrame,
                                VideoPixelFormatsEnum.Bgra,
                                VideoCodecsEnum.H264);

                            if (encodedFrame != null && encodedFrame.Length > 0)
                            {
                                encodeFailCount = 0; // 성공 시 카운터 리셋
                                // 2) 인코딩된 데이터를 WebRTC로 전송
                                uint duration = 3000;
                                try
                                {
                                    _peerConnection.SendVideo(duration, encodedFrame);
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"[WebRTC] SendVideo error: {ex.Message}");
                                }
                                _timestamp += duration;
                                _frameCount++;

                                // 초기 10프레임 또는 1초마다 상세 로그
                                if (_frameCount <= 10 || _frameCount % 30 == 0)
                                {
                                    string hex = BitConverter.ToString(encodedFrame, 0, Math.Min(10, encodedFrame.Length)).Replace("-", " ");
                                    Console.WriteLine($"[WebRTC] Sent Frame #{_frameCount}: {encodedFrame.Length} bytes | Header: {hex}");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            encodeFailCount++;
                            if (encodeFailCount <= 3)
                            {
                                Console.WriteLine($"[WebRTC] Encoding/Sending Error ({encodeFailCount}/3): {ex.Message}");
                            }

                            // 3회 연속 실패 → 소프트웨어 인코더(libx264)로 자동 전환
                            if (encodeFailCount == 3)
                            {
                                Console.WriteLine("[Video] GPU encoder failed 3 times. Switching to libx264 (software)...");
                                try
                                {
                                    _videoEncoder = new FFmpegVideoEncoder();
                                    var fallbackOpts = new System.Collections.Generic.Dictionary<string, string>
                                    {
                                        { "preset", "ultrafast" },
                                        { "tune", "zerolatency" },
                                        { "rc-lookahead", "0" },
                                        { "bf", "0" }
                                    };
                                    _videoEncoder.SetCodec(FFmpeg.AutoGen.AVCodecID.AV_CODEC_ID_H264, "libx264", fallbackOpts);
                                    _videoEncoder.InitialiseEncoder(FFmpeg.AutoGen.AVCodecID.AV_CODEC_ID_H264, _capture.Width, _capture.Height, 30);
                                    Console.WriteLine("[Video] Successfully switched to libx264!");
                                    encodeFailCount = 0;
                                }
                                catch (Exception fallbackEx)
                                {
                                    Console.WriteLine($"[Video] Fallback to libx264 also failed: {fallbackEx.Message}");
                                }
                            }
                        }
                    }
                    else
                    {
                        // 아직 한 번도 캡처 성공 못함
                        Console.WriteLine("[WebRTC] Waiting for first valid screen capture...");
                    }

                    // Frame Pacing: 60 FPS (16.6ms) 목표
                    // 인코딩+전송에 걸린 시간을 제외한 남은 시간만 대기
                    long elapsedMs = loopWatch.ElapsedMilliseconds;
                    int targetInterval = 16; // 60 FPS
                    
                    int delay = targetInterval - (int)elapsedMs;
                    if (delay > 0)
                    {
                        await Task.Delay(delay);
                    }
                    loopWatch.Restart();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WebRTC] Streaming Loop Fatal Error: {ex.Message}");
            }
            Console.WriteLine("[WebRTC] Streaming loop ended");
        }

        public async Task HandleSignalAsync(string from, object signal)
        {
            try
            {
                var jobj = JObject.FromObject(signal);
                if (jobj.ContainsKey("sdp"))
                {
                    var type = (RTCSdpType)Enum.Parse(typeof(RTCSdpType), jobj["sdp"]!["type"]!.ToString());

                    // offer 수신 시에만 PeerConnection 재초기화 (재연결 지원)
                    if (type == RTCSdpType.offer)
                    {
                        // 비밀번호 검증
                        if (_password != null)
                        {
                            string? receivedPwd = jobj["password"]?.ToString();
                            if (receivedPwd != _password)
                            {
                                Console.WriteLine($"[WebRTC] Password rejected from {from}");
                                OnSignalReady?.Invoke(from, new { rejected = true, reason = "invalid_password" });
                                return;
                            }
                            Console.WriteLine($"[WebRTC] Password accepted from {from}");
                        }

                        EnsureInitialized(from);
                    }

                    var sdpStr = jobj["sdp"]!["sdp"]!.ToString();
                    var result = _peerConnection!.setRemoteDescription(new RTCSessionDescriptionInit
                    {
                        type = type,
                        sdp = sdpStr
                    });

                    if (result != SetDescriptionResultEnum.OK)
                    {
                        Console.WriteLine($"[WebRTC] Failed to set remote description: {result}");
                        return;
                    }

                    Console.WriteLine($"[WebRTC] Remote description set ({type})");

                    if (type == RTCSdpType.offer)
                    {
                        var answer = _peerConnection.createAnswer();
                        await _peerConnection.setLocalDescription(answer);
                        OnSignalReady?.Invoke(from, new { 
                            sdp = new { 
                                sdp = answer.sdp, 
                                type = answer.type.ToString().ToLower() 
                            } 
                        });
                        Console.WriteLine("[WebRTC] Answer sent");
                    }
                }
                else if (jobj.ContainsKey("ice"))
                {
                    // ICE candidate는 기존 연결에 추가만 (연결 재초기화 안 함)
                    if (_peerConnection == null)
                    {
                        Console.WriteLine("[WebRTC] ICE candidate received but no PeerConnection, ignoring.");
                        return;
                    }

                    var candidate = jobj["ice"]!["candidate"]!.ToString();
                    var sdpMid = jobj["ice"]!["sdpMid"]?.ToString();
                    var sdpMLineIndex = (ushort)(jobj["ice"]!["sdpMLineIndex"] ?? 0);

                    _peerConnection.addIceCandidate(new RTCIceCandidateInit
                    {
                        candidate = candidate,
                        sdpMid = sdpMid,
                        sdpMLineIndex = sdpMLineIndex
                    });
                    Console.WriteLine($"[WebRTC] ICE candidate added");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WebRTC] Signal Handle Error: {ex.Message}");
            }
        }
        /// <summary>
        /// STA 스레드에서 클립보드에 텍스트를 설정합니다.
        /// (Host는 콘솔 앱이므로 Clipboard.SetText는 STA 필요)
        /// </summary>
        private void SetClipboardOnSta(string text)
        {
            var thread = new Thread(() =>
            {
                try { System.Windows.Forms.Clipboard.SetText(text); }
                catch (Exception ex) { Console.WriteLine($"[Clipboard] Set failed: {ex.Message}"); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        /// <summary>
        /// STA 스레드에서 클립보드 텍스트를 읽어 변경됐으면 Viewer로 전송합니다.
        /// </summary>
        private void PollClipboard(object? _)
        {
            if (_viewerInputChannel == null || _viewerInputChannel.readyState != RTCDataChannelState.open)
                return;

            var thread = new Thread(() =>
            {
                try
                {
                    if (!System.Windows.Forms.Clipboard.ContainsText()) return;
                    string text = System.Windows.Forms.Clipboard.GetText();
                    if (string.IsNullOrEmpty(text) || text == _lastClipboardText) return;

                    _lastClipboardText = text;
                    byte[] textBytes = Encoding.UTF8.GetBytes(text);
                    byte[] msg = new byte[1 + textBytes.Length];
                    msg[0] = MSG_CLIPBOARD;
                    textBytes.CopyTo(msg, 1);
                    _viewerInputChannel.send(msg);
                    Console.WriteLine($"[Clipboard] Sent to Viewer ({text.Length} chars)");
                }
                catch { /* 클립보드 액세스 실패 무시 */ }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        /// <summary>
        /// 클립보드 모니터링 시작 (2초 간격 폴링)
        /// </summary>
        private void StartClipboardMonitoring()
        {
            _clipboardTimer?.Dispose();
            _clipboardTimer = new System.Threading.Timer(PollClipboard, null, 2000, 2000);
            Console.WriteLine("[Clipboard] Monitoring started");
        }

        public void Dispose()
        {
            _isStreaming = false;
            _clipboardTimer?.Dispose();
            _videoEncoder?.Dispose();
            _peerConnection?.Close("disposed");
        }
    }
}
