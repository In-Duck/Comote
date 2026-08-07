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
using Concentus;
using Concentus.Structs;
using Concentus.Enums;
using Comote.Input;

namespace Host
{
    public class WebRTCManager : IDisposable
    {
        private const string SoftwareVideoEncoderName = "h264_mf";
        private RTCPeerConnection? _peerConnection;
        private FFmpegVideoEncoder? _videoEncoder;
        private string? _activeVideoEncoderName;
        private IOpusEncoder? _opusEncoder;
        private WasapiLoopbackCapture? _audioCapture;
        private ScreenCapture _capture;
        private readonly IInputBackend _inputBackend;
        private byte[]? _lastRawFrame;
        private bool _isStreaming;
        public bool IsSessionActive =>
            _isStreaming ||
            _peerConnection?.connectionState is
                RTCPeerConnectionState.connecting or
                RTCPeerConnectionState.connected or
                RTCPeerConnectionState.disconnected;
        private string? _remoteSocketId;
        private long _connectionGeneration;
        private uint _timestamp = 0;
        private int _frameCount = 0;
        private long _totalAudioFramesSent = 0;
        private RTCDataChannel? _viewerInputChannel;
        private RTCDataChannel? _viewerFileChannel;
        private readonly object _sessionGate = new();
        private readonly SemaphoreSlim _signalGate = new(1, 1);
        private readonly Dictionary<
            (string RemoteSocketId, long Generation),
            List<RTCIceCandidateInit>> _pendingRemoteIce = [];
        private RemoteControlSession? _controlSession;
        private RemoteChannelServerState? _inputSecurity;
        private RemoteChannelServerState? _fileSecurity;
        private InboundFileTransferReceiver? _fileReceiver;
        private bool _disposed;

        // 프로토콜 상수 (Stats/Ping/Pong/Clipboard/MonitorSwitch)
        // 주의: 0x01~0x05=마우스, 0x10~0x13=키보드/입력 해제
        //       → 그 외 프로토콜은 0x06, 0x20 이상 사용 (충돌 방지)
        private const byte MSG_MONITOR_SWITCH  = 0x06;
        private const byte MSG_STATS           = 0x20;
        private const byte MSG_PING            = 0x21;
        private const byte MSG_PONG            = 0x22;
        private const byte MSG_CLIPBOARD       = 0x23;
        private const byte MSG_INPUT_ACK       = 0x14;

        // 파일 전송 프로토콜
        private const byte MSG_FILE_START = 0x30;
        private const byte MSG_FILE_CHUNK = 0x31;
        private const byte MSG_FILE_END   = 0x32;
        private const byte MSG_FILE_ACK   = 0x33;
        private const byte MSG_FILE_HASH_MISMATCH = 0x34;

        // 적응형 비트레이트 상태
        private int _viewerFps = 0;
        private int _targetFps = EncoderConfig.DefaultFps;
        private string? _password;
        

        // Encoder Configuration Class
        private static class EncoderConfig
        {
            public const int DefaultFps = 30;
            public const long BitrateUltra = 20_000_000;
            public const long BitrateHigh  = 10_000_000;
            public const long BitrateMedium = 5_000_000;
            public const long BitrateLow    = 2_000_000;
        }

        // 모니터 전환 상태
        private int _currentAdapterIndex;
        private int _currentOutputIndex;

        // 클립보드 공유 상태
        private string? _lastClipboardText;
        private System.Threading.Timer? _clipboardTimer;
        private readonly ClipboardStaWorker _clipboardWorker;
        private bool _clipboardSessionEnabled;
        private long _clipboardConsentEpoch;
        private int _activeClipboardLeases;

        private const uint REMOTE_CAP_KEYBOARD = 1u << 0;
        private const uint REMOTE_CAP_ABSOLUTE_MOUSE = 1u << 1;
        private const uint REMOTE_CAP_VERTICAL_WHEEL = 1u << 2;
        private const uint REMOTE_CAP_HORIZONTAL_WHEEL = 1u << 3;
        private const uint REMOTE_CAP_FIVE_MOUSE_BUTTONS = 1u << 4;
        private const uint REMOTE_CAP_RELEASE_ALL = 1u << 5;
        private const uint REMOTE_CAP_CLIPBOARD = 1u << 6;
        private const uint REMOTE_CAP_FILE_TRANSFER = 1u << 7;
        private const uint REMOTE_CAP_SCAN_CODE_KEYS = 1u << 8;
        private const uint REMOTE_CAPABILITIES =
            REMOTE_CAP_KEYBOARD |
            REMOTE_CAP_ABSOLUTE_MOUSE |
            REMOTE_CAP_VERTICAL_WHEEL |
            REMOTE_CAP_HORIZONTAL_WHEEL |
            REMOTE_CAP_FIVE_MOUSE_BUTTONS |
            REMOTE_CAP_RELEASE_ALL |
            REMOTE_CAP_CLIPBOARD |
            REMOTE_CAP_FILE_TRANSFER |
            REMOTE_CAP_SCAN_CODE_KEYS;

        public event Func<string, object, Task>? OnSignalReady;

        public InputBackendStatus InputBackendStatus =>
            _inputBackend.GetStatus();

        public WebRTCManager(
            ScreenCapture capture,
            string? password = null,
            IInputBackend? inputBackend = null)
        {
            _capture = capture;
            _inputBackend = inputBackend ??
                new SendInputBackend(capture.Width, capture.Height);
            _inputBackend.UpdateScreenBounds(
                capture.Left,
                capture.Top,
                capture.Width,
                capture.Height);
            _password = password;
            _currentAdapterIndex = capture.AdapterIndex;
            _currentOutputIndex = capture.OutputIndex;
            _clipboardWorker = new ClipboardStaWorker();
        }

        private void EnsureInitialized(string remoteSocketId, long generation)
        {
            ClosePeerConnectionForReplacement();
            lock (_sessionGate)
            {
                _remoteSocketId = remoteSocketId;
                _connectionGeneration = generation;
            var inputMode = _inputBackend.Mode == InputBackendMode.VirtualHid
                ? RemoteInputMode.VirtualHid
                : RemoteInputMode.SendInput;
            _controlSession = new RemoteControlSession();
            _inputSecurity = new RemoteChannelServerState(
                _controlSession,
                RemoteChannelKind.Input,
                inputMode,
                REMOTE_CAPABILITIES);
            _fileSecurity = new RemoteChannelServerState(
                _controlSession,
                RemoteChannelKind.File,
                inputMode,
                REMOTE_CAPABILITIES);
            _fileReceiver = new InboundFileTransferReceiver(
                Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData),
                    "Comote",
                    "IncomingTemp"),
                Environment.GetFolderPath(
                    Environment.SpecialFolder.DesktopDirectory));

            var config = new RTCConfiguration
            {
                iceServers = new System.Collections.Generic.List<RTCIceServer>
                {
                    new RTCIceServer { urls = "stun:stun.l.google.com:19302" }
                }
            };

                _peerConnection = new RTCPeerConnection(config);
            }
            // 오디오 트랙 설정 (Opus 48kHz)
            var audioFormat = new AudioFormat(111, "opus", 48000);
            var audioTrack = new MediaStreamTrack(audioFormat, MediaStreamStatusEnum.SendOnly);
            _peerConnection.addTrack(audioTrack);

            // 최적의 비디오 인코더 선택 (GPU 가속 우선)
            // Select an H.264 encoder, preferring available hardware.
            var (codecId, encoderName, encoderOpts) = SelectBestVideoEncoder();
            Console.WriteLine($"[Video] Selected Encoder: {encoderName} (CodecID: {codecId})");

            try
            {
                _videoEncoder = CreateInitializedVideoEncoder(
                    codecId,
                    encoderName,
                    encoderOpts,
                    _capture.Width,
                    _capture.Height,
                    20,
                    6_000_000);
                _activeVideoEncoderName = encoderName;
            }
            catch (Exception ex)
            {
                if (string.Equals(
                        encoderName,
                        SoftwareVideoEncoderName,
                        StringComparison.Ordinal))
                {
                    _videoEncoder?.Dispose();
                    _videoEncoder = null;
                    throw new InvalidOperationException(
                        "The Media Foundation H.264 encoder could not be initialized.",
                        ex);
                }

                Console.WriteLine(
                    $"[Video] Failed to init {encoderName}: {ex.Message}. " +
                    $"Falling back to {SoftwareVideoEncoderName}.");
                try
                {
                    _videoEncoder = CreateInitializedVideoEncoder(
                        FFmpeg.AutoGen.AVCodecID.AV_CODEC_ID_H264,
                        SoftwareVideoEncoderName,
                        CreateMediaFoundationFallbackOptions(),
                        _capture.Width,
                        _capture.Height,
                        20,
                        6_000_000);
                    _activeVideoEncoderName =
                        SoftwareVideoEncoderName;
                }
                catch (Exception fallbackEx)
                {
                    _videoEncoder?.Dispose();
                    _videoEncoder = null;
                    throw new InvalidOperationException(
                        "The Media Foundation H.264 fallback encoder could not be initialized.",
                        fallbackEx);
                }
            }

            // Signalling and packetisation are intentionally H.264-only.
            var videoFormat = new VideoFormat(
                VideoCodecsEnum.H264,
                96,
                90000,
                null);
            var videoTrack = new MediaStreamTrack(
                videoFormat,
                MediaStreamStatusEnum.SendOnly);
            _peerConnection.addTrack(videoTrack);

            var connection = _peerConnection;
            connection.onicecandidate += candidate =>
            {
                if (candidate == null)
                {
                    return;
                }

                string? remoteSocketId;
                long generation;
                lock (_sessionGate)
                {
                    if (!ReferenceEquals(_peerConnection, connection))
                    {
                        return;
                    }

                    remoteSocketId = _remoteSocketId;
                    generation = _connectionGeneration;
                }
                if (remoteSocketId == null || generation <= 0)
                {
                    return;
                }

                QueueSignalReady(remoteSocketId, new
                {
                    ice = new
                    {
                        candidate = candidate.candidate,
                        sdpMid = candidate.sdpMid,
                        sdpMLineIndex = candidate.sdpMLineIndex
                    },
                    connectionGeneration = generation
                });
            };
            connection.onconnectionstatechange += (state) =>
            {
                if (!ReferenceEquals(_peerConnection, connection))
                {
                    return;
                }

                Console.WriteLine($"[WebRTC] Connection state: {state}");
                if (state == RTCPeerConnectionState.connected && !_isStreaming)
                {
                    _isStreaming = true;
                    _ = Task.Run(SendVideoLoop);
                    StartAudioCapture();
                    return;
                }

                if (state is RTCPeerConnectionState.disconnected or
                             RTCPeerConnectionState.failed or
                             RTCPeerConnectionState.closed)
                {
                    // Input authority is revoked immediately. A recovered ICE
                    // transport must negotiate a fresh application session.
                    if (!FailControlSession(
                            connection,
                            "peer connection became terminal"))
                    {
                        return;
                    }
                    _isStreaming = false;
                    StopClipboardMonitoring();
                    StopAudioCapture();
                    if (state != RTCPeerConnectionState.closed)
                    {
                        connection.close();
                    }
                }
            };

            connection.ondatachannel += (channel) =>
            {
                if (!ReferenceEquals(_peerConnection, connection))
                {
                    channel.close();
                    return;
                }

                if (channel.label == "input")
                {
                    RemoteChannelServerState? security;
                    lock (_sessionGate)
                    {
                        if (!ReferenceEquals(_peerConnection, connection) ||
                            _viewerInputChannel != null ||
                            _inputSecurity == null)
                        {
                            channel.close();
                            return;
                        }

                        _viewerInputChannel = channel;
                        security = _inputSecurity;
                    }

                    channel.onmessage += (_, _, data) =>
                        HandleSecuredInputMessage(
                            connection,
                            channel,
                            security,
                            data);
                    channel.onclose += () =>
                        HandleInputChannelClosed(connection, channel);
                    Console.WriteLine("[WebRTC] Input channel awaiting authentication");
                    return;
                }

                if (channel.label == "file")
                {
                    RemoteChannelServerState? security;
                    lock (_sessionGate)
                    {
                        if (!ReferenceEquals(_peerConnection, connection) ||
                            _viewerFileChannel != null ||
                            _fileSecurity == null)
                        {
                            channel.close();
                            return;
                        }

                        _viewerFileChannel = channel;
                        security = _fileSecurity;
                    }

                    channel.onmessage += (_, _, data) =>
                        HandleSecuredFileMessage(
                            connection,
                            channel,
                            security,
                            data);
                    channel.onclose += () =>
                        HandleFileChannelClosed(connection, channel);
                    Console.WriteLine("[WebRTC] File channel awaiting authentication");
                    return;
                }

                Console.WriteLine("[WebRTC] Rejected unknown DataChannel label");
                channel.close();
            };
            Console.WriteLine($"[WebRTC] PeerConnection initialized for {remoteSocketId}");
        }



        private void HandleSecuredInputMessage(
            RTCPeerConnection connection,
            RTCDataChannel channel,
            RemoteChannelServerState security,
            byte[] data)
        {
            if (!IsCurrentInputChannel(connection, channel, security))
            {
                return;
            }
            if (data == null || data.Length == 0)
            {
                FailControlSession(connection, "stale or empty input message");
                return;
            }

            var result = security.ProcessClientMessage(
                data,
                out var response,
                out var payload);
            switch (result)
            {
                case RemoteServerReceiveResult.Authenticated:
                    if (!TrySendRaw(channel, response))
                    {
                        FailControlSession(connection, "input authentication response failed");
                    }
                    else
                    {
                        Console.WriteLine("[Control] Input channel authenticated");
                    }
                    return;

                case RemoteServerReceiveResult.Rejected:
                    _ = TrySendRaw(channel, response);
                    FailControlSession(connection, "input authentication rejected");
                    return;

                case RemoteServerReceiveResult.ProtocolViolation:
                    FailControlSession(connection, "input sequence or envelope violation");
                    return;

                case RemoteServerReceiveResult.Payload:
                    bool accepted;
                    lock (_sessionGate)
                    {
                        if (!ReferenceEquals(_peerConnection, connection) ||
                            !ReferenceEquals(_viewerInputChannel, channel) ||
                            !ReferenceEquals(_inputSecurity, security))
                        {
                            return;
                        }

                        accepted = HandleInputPayload(
                            channel,
                            security,
                            payload);
                    }
                    if (!accepted)
                    {
                        FailControlSession(connection, "invalid input payload");
                    }
                    return;
            }
        }

        private bool HandleInputPayload(
            RTCDataChannel channel,
            RemoteChannelServerState security,
            ReadOnlySpan<byte> data)
        {
            if (data.Length < 1)
            {
                return false;
            }

            switch (data[0])
            {
                case MSG_STATS:
                    if (data.Length != 3)
                    {
                        return false;
                    }
                    _viewerFps = BitConverter.ToUInt16(data.Slice(1, 2));
                    return _viewerFps <= 240;

                case 0x07: // settings update
                    if (data.Length != 3 ||
                        data[1] is < 1 or > 120 ||
                        data[2] > 3)
                    {
                        return false;
                    }
                    UpdateEncoderSettings(data[1], data[2]);
                    return true;

                case MSG_PING:
                    return data.Length == 1 &&
                        TrySendSecured(
                            channel,
                            security,
                            [MSG_PONG]);

                case MSG_PONG:
                    return data.Length == 1;

                case MSG_MONITOR_SWITCH:
                    if (data.Length == 1)
                    {
                        CycleMonitor();
                        return true;
                    }
                    if (data.Length == 3)
                    {
                        SwitchMonitor(data[1], data[2]);
                        return true;
                    }
                    return false;

                case RemoteClipboardConsentProtocol.MessageType:
                    if (!RemoteClipboardConsentProtocol.TryParse(
                            data,
                            out var clipboardEnabled))
                    {
                        return false;
                    }
                    return ApplyClipboardConsent(
                        channel,
                        security,
                        clipboardEnabled);
                case MSG_CLIPBOARD:
                    if (!TryGetClipboardConsentEpoch(
                            channel,
                            security,
                            out var clipboardEpoch) ||
                        data.Length is < 2 or > 1 + 32 * 1024)
                    {
                        return false;
                    }
                    try
                    {
                        var text = new UTF8Encoding(
                            encoderShouldEmitUTF8Identifier: false,
                            throwOnInvalidBytes: true).GetString(data.Slice(1));
                        SetClipboardOnSta(
                            text,
                            channel,
                            security,
                            clipboardEpoch);
                        return true;
                    }
                    catch (DecoderFallbackException)
                    {
                        return false;
                    }

                default:
                    var dispatch = _inputBackend.ProcessMessage(data);
                    if (!dispatch.IsAccepted)
                    {
                        Console.WriteLine(
                            $"[Input] Rejected message: {dispatch.Code}");
                        return dispatch.Code is
                            InputDispatchCode.Unsupported or
                            InputDispatchCode.BackendRejected;
                    }

                    return TrySendSecured(
                        channel,
                        security,
                        [MSG_INPUT_ACK, data[0]]);
            }
        }
        private void UpdateEncoderSettings(int fps, int qualityLevel)
        {
            try
            {
                // Quality Multiplier
                // 3: 20Mbps (Ultra), 2: 10Mbps (High/Default), 1: 5Mbps (Medium), 0: 2Mbps (Low)
                long baseBitrate = 10_000_000;
                long targetBitrate = baseBitrate;
                
                switch (qualityLevel)
                {
                    case 3: targetBitrate = EncoderConfig.BitrateUltra; break; 
                    case 2: targetBitrate = EncoderConfig.BitrateHigh; break; 
                    case 1: targetBitrate = EncoderConfig.BitrateMedium; break;  
                    case 0: targetBitrate = EncoderConfig.BitrateLow; break;  
                }

                Console.WriteLine($"[WebRTC] Updating Encoder Settings -> FPS: {fps}, Bitrate: {targetBitrate / 1_000_000.0:F1}Mbps");
                
                // 비트레이트 적용
                var encoder = _videoEncoder ??
                    throw new InvalidOperationException(
                        "The video encoder is unavailable.");
                encoder.SetBitrate(targetBitrate, null, null, null);
                
                // FPS 적용 (ChangeFps 메서드가 인코더 래퍼에 있다고 가정하거나, 캡처 루프 딜레이 조절 필요)
                // 만약 ChangeFps가 없다면 InitialiseEncoder 재호출은 너무 무거움.
                // 여기서는 비트레이트 변경에 집중하고 FPS는 인코더 내부 제어 혹은 캡처 딜레이 변수(_targetFps)를 둬야 함.
                // 일단 _targetFps 변수를 필드로 만들고 캡처 루프에서 참조하도록 수정 필요.
                _targetFps = fps > 0 ? fps : 30; // 0이면 기본값 30
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WebRTC] Failed to update settings: {ex.Message}");
            }

        }

        private void HandleSecuredFileMessage(
            RTCPeerConnection connection,
            RTCDataChannel channel,
            RemoteChannelServerState security,
            byte[] data)
        {
            if (!IsCurrentFileChannel(connection, channel, security))
            {
                return;
            }
            if (data == null || data.Length == 0)
            {
                FailControlSession(connection, "stale or empty file message");
                return;
            }

            var receiveResult = security.ProcessClientMessage(
                data,
                out var response,
                out var payload);
            switch (receiveResult)
            {
                case RemoteServerReceiveResult.Authenticated:
                    if (!TrySendRaw(channel, response))
                    {
                        FailControlSession(connection, "file authentication response failed");
                    }
                    else
                    {
                        Console.WriteLine("[Control] File channel authenticated");
                    }
                    return;

                case RemoteServerReceiveResult.Rejected:
                    _ = TrySendRaw(channel, response);
                    FailControlSession(connection, "file authentication rejected");
                    return;

                case RemoteServerReceiveResult.ProtocolViolation:
                    FailControlSession(connection, "file sequence or envelope violation");
                    return;

                case RemoteServerReceiveResult.Payload:
                    InboundFileTransferReceiver receiver;
                    lock (_sessionGate)
                    {
                        if (!ReferenceEquals(_peerConnection, connection) ||
                            !ReferenceEquals(_viewerFileChannel, channel) ||
                            !ReferenceEquals(_fileSecurity, security) ||
                            _fileReceiver == null)
                        {
                            return;
                        }

                        receiver = _fileReceiver;
                    }

                    InboundFileTransferResult result;
                    try
                    {
                        result = receiver.Process(payload);
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }

                    if (!IsCurrentFileChannel(connection, channel, security))
                    {
                        return;
                    }
                    switch (result.Status)
                    {
                        case InboundFileTransferStatus.Continue:
                            return;
                        case InboundFileTransferStatus.Completed:
                            Console.WriteLine("[FileTransfer] File verified and saved");
                            if (!TrySendSecured(
                                    channel,
                                    security,
                                    RemoteFileTransferProtocol.CreateAcknowledged(
                                        result.TransferId)))
                            {
                                FailControlSession(connection, "file acknowledgement failed");
                            }
                            return;
                        case InboundFileTransferStatus.HashMismatch:
                            Console.WriteLine("[FileTransfer] File hash mismatch");
                            if (!TrySendSecured(
                                    channel,
                                    security,
                                    RemoteFileTransferProtocol.CreateHashMismatch(
                                        result.TransferId)))
                            {
                                FailControlSession(connection, "file rejection response failed");
                            }
                            return;
                        case InboundFileTransferStatus.ProtocolViolation:
                            Console.WriteLine("[FileTransfer] Protocol violation");
                            FailControlSession(connection, "invalid file payload");
                            return;
                    }
                    return;
            }
        }
        private bool IsCurrentInputChannel(
            RTCPeerConnection connection,
            RTCDataChannel channel,
            RemoteChannelServerState security)
        {
            lock (_sessionGate)
            {
                return ReferenceEquals(_peerConnection, connection) &&
                    ReferenceEquals(_viewerInputChannel, channel) &&
                    ReferenceEquals(_inputSecurity, security);
            }
        }

        private bool IsCurrentFileChannel(
            RTCPeerConnection connection,
            RTCDataChannel channel,
            RemoteChannelServerState security)
        {
            lock (_sessionGate)
            {
                return ReferenceEquals(_peerConnection, connection) &&
                    ReferenceEquals(_viewerFileChannel, channel) &&
                    ReferenceEquals(_fileSecurity, security);
            }
        }

        private static bool TrySendRaw(
            RTCDataChannel channel,
            byte[] message)
        {
            if (message.Length == 0 ||
                channel.readyState != RTCDataChannelState.open)
            {
                return false;
            }

            try
            {
                channel.send(message);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TrySendSecured(
            RTCDataChannel channel,
            RemoteChannelServerState security,
            ReadOnlySpan<byte> payload)
        {
            try
            {
                lock (security)
                {
                    return TrySendRaw(
                        channel,
                        security.WrapPayload(payload));
                }
            }
            catch (Exception ex) when (
                ex is InvalidOperationException or
                      ArgumentOutOfRangeException or
                      OverflowException or
                      ObjectDisposedException)
            {
                return false;
            }
        }

        private void HandleInputChannelClosed(
            RTCPeerConnection connection,
            RTCDataChannel channel)
        {
            lock (_sessionGate)
            {
                if (!ReferenceEquals(_peerConnection, connection) ||
                    !ReferenceEquals(_viewerInputChannel, channel))
                {
                    return;
                }
            }

            FailControlSession(connection, "input channel closed");
        }

        private void HandleFileChannelClosed(
            RTCPeerConnection connection,
            RTCDataChannel channel)
        {
            RemoteChannelServerState? security;
            InboundFileTransferReceiver? receiver;
            lock (_sessionGate)
            {
                if (!ReferenceEquals(_peerConnection, connection) ||
                    !ReferenceEquals(_viewerFileChannel, channel))
                {
                    return;
                }

                _viewerFileChannel = null;
                security = _fileSecurity;
                receiver = _fileReceiver;
                _fileSecurity = null;
                _fileReceiver = null;
            }

            security?.Dispose();
            receiver?.Dispose();
        }

        private bool FailControlSession(
            RTCPeerConnection connection,
            string reason)
        {
            lock (_sessionGate)
            {
                if (!ReferenceEquals(_peerConnection, connection))
                {
                    return false;
                }

                _inputBackend.ReleaseAllInputs();
            }

            if (!RevokeControlSession(
                    closeChannels: true,
                    expectedConnection: connection))
            {
                return false;
            }

            Console.WriteLine($"[Control] Session revoked: {reason}");
            return true;
        }

        private bool RevokeControlSession(
            bool closeChannels,
            RTCPeerConnection? expectedConnection = null)
        {
            RTCDataChannel? inputChannel;
            RTCDataChannel? fileChannel;
            RemoteControlSession? session;
            RemoteChannelServerState? inputSecurity;
            RemoteChannelServerState? fileSecurity;
            InboundFileTransferReceiver? fileReceiver;
            lock (_sessionGate)
            {
                if (expectedConnection != null &&
                    !ReferenceEquals(_peerConnection, expectedConnection))
                {
                    return false;
                }

                inputChannel = _viewerInputChannel;
                fileChannel = _viewerFileChannel;
                session = _controlSession;
                inputSecurity = _inputSecurity;
                fileSecurity = _fileSecurity;
                fileReceiver = _fileReceiver;
                _viewerInputChannel = null;
                _viewerFileChannel = null;
                _inputSecurity = null;
                _fileSecurity = null;
                _controlSession = null;
                _fileReceiver = null;
                _clipboardSessionEnabled = false;
                unchecked { _clipboardConsentEpoch++; }
                _lastClipboardText = null;
            }

            StopClipboardMonitoring();
            inputSecurity?.Dispose();
            fileSecurity?.Dispose();
            fileReceiver?.Dispose();
            session?.Dispose();
            if (closeChannels)
            {
                try { inputChannel?.close(); } catch { }
                try { fileChannel?.close(); } catch { }
            }

            return true;
        }

        private void ClosePeerConnectionForReplacement()
        {
            _inputBackend.ReleaseAllInputs();
            _isStreaming = false;
            StopClipboardMonitoring();
            StopAudioCapture();
            RevokeControlSession(closeChannels: true);

            RTCPeerConnection? connection;
            lock (_sessionGate)
            {
                connection = _peerConnection;
                _peerConnection = null;
                _remoteSocketId = null;
                _connectionGeneration = 0;
            }
            try { connection?.close(); } catch { }
            connection?.Dispose();

            _videoEncoder?.Dispose();
            _videoEncoder = null;
            _activeVideoEncoderName = null;
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
                    _inputBackend.UpdateScreenBounds(
                        _capture.Left,
                        _capture.Top,
                        _capture.Width,
                        _capture.Height);
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

        private unsafe (FFmpeg.AutoGen.AVCodecID, string, System.Collections.Generic.Dictionary<string, string>) SelectBestVideoEncoder()
        {
            // H.264-only hardware preference: NVENC > QSV > AMF.

            var commonOpts = new System.Collections.Generic.Dictionary<string, string>
            {
                { "preset", "llhq" }, // Low Latency High Quality
                { "rc", "cbr_ld_hq" },
                { "zerolatency", "1" },
                { "delay", "0" }
            };

            // 1. NVIDIA NVENC
            // H264
            if (IsEncoderAvailable("h264_nvenc")) return (FFmpeg.AutoGen.AVCodecID.AV_CODEC_ID_H264, "h264_nvenc", commonOpts);

            // 2. Intel QSV
            if (IsEncoderAvailable("h264_qsv"))
            {
                var qsvOpts = new System.Collections.Generic.Dictionary<string, string> { { "profile", "baseline" }, { "preset", "veryfast" } };
                return (FFmpeg.AutoGen.AVCodecID.AV_CODEC_ID_H264, "h264_qsv", qsvOpts);
            }

            // 3. AMD AMF
            if (IsEncoderAvailable("h264_amf")) return (FFmpeg.AutoGen.AVCodecID.AV_CODEC_ID_H264, "h264_amf", new System.Collections.Generic.Dictionary<string, string> { { "usage", "lowlatency" } });

            // 4. Windows Media Foundation software fallback.
            if (IsEncoderAvailable(SoftwareVideoEncoderName))
            {
                return (
                    FFmpeg.AutoGen.AVCodecID.AV_CODEC_ID_H264,
                    SoftwareVideoEncoderName,
                    CreateMediaFoundationFallbackOptions());
            }

            throw new InvalidOperationException(
                "No supported H.264 encoder is available.");
        }

        private static System.Collections.Generic.Dictionary<string, string>
            CreateMediaFoundationFallbackOptions()
        {
            return new System.Collections.Generic.Dictionary<string, string>
            {
                { "rate_control", "cbr" },
                { "scenario", "display_remoting" },
                { "hw_encoding", "0" }
            };
        }

        private static FFmpegVideoEncoder CreateInitializedVideoEncoder(
            FFmpeg.AutoGen.AVCodecID codecId,
            string encoderName,
            System.Collections.Generic.Dictionary<string, string>
                encoderOptions,
            int width,
            int height,
            int fps,
            long bitrate)
        {
            var encoder = new FFmpegVideoEncoder(
                new System.Collections.Generic.Dictionary<string, string>(
                    encoderOptions,
                    StringComparer.Ordinal));
            try
            {
                if (!encoder.SetCodec(codecId, encoderName))
                {
                    throw new InvalidOperationException(
                        $"FFmpeg encoder '{encoderName}' is unavailable.");
                }

                encoder.SetBitrate(bitrate, null, null, null);
                encoder.InitialiseEncoder(
                    codecId,
                    width,
                    height,
                    fps);
                return encoder;
            }
            catch
            {
                encoder.Dispose();
                throw;
            }
        }

        private unsafe bool IsEncoderAvailable(string encoderName)
        {
            try 
            {
                var codec = FFmpeg.AutoGen.ffmpeg.avcodec_find_encoder_by_name(encoderName);
                return codec != null;
            }
            catch
            {
                return false;
            }
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
                _opusEncoder = OpusCodecFactory.CreateEncoder(
                    waveFormat.SampleRate,
                    waveFormat.Channels,
                    OpusApplication.OPUS_APPLICATION_AUDIO,
                    null);
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

                        while (_audioAccumulator.Count >= samplesPer20ms)
                        {
                            var frame = new short[samplesPer20ms];
                            _audioAccumulator.CopyTo(0, frame, 0, samplesPer20ms);
                            _audioAccumulator.RemoveRange(0, samplesPer20ms);

                            // Concentus로 Opus 인코딩
                            int encodedLen = _opusEncoder!.Encode(
                                frame.AsSpan(),
                                frameSizePerChannel,
                                opusOutput.AsSpan(),
                                opusOutput.Length);
                            if (encodedLen > 0)
                            {
                                var encoded = new byte[encodedLen];
                                Array.Copy(opusOutput, encoded, encodedLen);
                                _peerConnection.SendAudio((uint)frameSizePerChannel, encoded);
                                
                                _totalAudioFramesSent++;
                                if (_totalAudioFramesSent <= 10 || _totalAudioFramesSent % 500 == 0)
                                {
                                    Console.WriteLine($"[Audio] Sent frame #{_totalAudioFramesSent}: {encodedLen} bytes encoded from {samplesPer20ms} samples");
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

                            // 3회 연속 실패 시 Media Foundation H.264로 자동 전환
                            if (encodeFailCount == 3)
                            {
                                if (string.Equals(
                                        _activeVideoEncoderName,
                                        SoftwareVideoEncoderName,
                                        StringComparison.Ordinal))
                                {
                                    Console.WriteLine(
                                        "[Video] Media Foundation H.264 encoding " +
                                        "failed repeatedly; no further fallback is available.");
                                    _isStreaming = false;
                                    break;
                                }

                                Console.WriteLine(
                                    $"[Video] GPU encoder failed 3 times. " +
                                    $"Switching to {SoftwareVideoEncoderName} (software)...");
                                FFmpegVideoEncoder? replacementEncoder = null;
                                try
                                {
                                    replacementEncoder =
                                        CreateInitializedVideoEncoder(
                                            FFmpeg.AutoGen.AVCodecID.AV_CODEC_ID_H264,
                                            SoftwareVideoEncoderName,
                                            CreateMediaFoundationFallbackOptions(),
                                            _capture.Width,
                                            _capture.Height,
                                            20,
                                            6_000_000);

                                    var previousEncoder = _videoEncoder;
                                    _videoEncoder = replacementEncoder;
                                    replacementEncoder = null;
                                    _activeVideoEncoderName =
                                        SoftwareVideoEncoderName;
                                    previousEncoder?.Dispose();
                                    Console.WriteLine(
                                        $"[Video] Successfully switched to " +
                                        $"{SoftwareVideoEncoderName}.");
                                    encodeFailCount = 0;
                                }
                                catch (Exception fallbackEx)
                                {
                                    replacementEncoder?.Dispose();
                                    Console.WriteLine(
                                        $"[Video] Fallback to " +
                                        $"{SoftwareVideoEncoderName} failed: " +
                                        fallbackEx.Message);
                                    _isStreaming = false;
                                    break;
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
                    int targetInterval = _targetFps > 0 ? 1000 / _targetFps : 33; // Dynamic FPS
                    
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
            ArgumentException.ThrowIfNullOrWhiteSpace(from);
            ArgumentNullException.ThrowIfNull(signal);
            if (from.Length > 512)
            {
                return;
            }

            await _signalGate.WaitAsync().ConfigureAwait(false);
            bool createdConnection = false;
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var root = signal as JObject ?? JObject.FromObject(signal);
                int signalKindCount =
                    (root.ContainsKey("sdp") ? 1 : 0) +
                    (root.ContainsKey("ice") ? 1 : 0);
                if (signalKindCount != 1)
                {
                    Console.WriteLine(
                        "[WebRTC] Ignored signal without exactly one body");
                    return;
                }

                if (root["sdp"] is JObject sdp)
                {
                    long generation =
                        root["connectionGeneration"]?.Type ==
                            JTokenType.Integer
                            ? root.Value<long>("connectionGeneration")
                            : 0;
                    if (generation <= 0)
                    {
                        Console.WriteLine(
                            "[WebRTC] Ignored offer without a valid generation");
                        return;
                    }

                    if (sdp["type"]?.Type != JTokenType.String ||
                        !string.Equals(
                            sdp.Value<string>("type"),
                            "offer",
                            StringComparison.Ordinal))
                    {
                        RejectSignal(
                            from,
                            "invalid_sdp_type",
                            generation);
                        return;
                    }

                    var offerSdp =
                        sdp["sdp"]?.Type == JTokenType.String
                            ? sdp.Value<string>("sdp")
                            : null;
                    if (string.IsNullOrWhiteSpace(offerSdp) ||
                        offerSdp.Length > 1024 * 1024)
                    {
                        RejectSignal(from, "invalid_offer", generation);
                        return;
                    }

                    if (_peerConnection?.connectionState is
                            RTCPeerConnectionState.connecting or
                            RTCPeerConnectionState.connected or
                            RTCPeerConnectionState.disconnected)
                    {
                        RejectSignal(from, "control_busy", generation);
                        return;
                    }

                    string? suppliedPassword =
                        root["password"]?.Type == JTokenType.String
                            ? root.Value<string>("password")
                            : null;
                    if (_password != null &&
                        (suppliedPassword == null ||
                         suppliedPassword.Length > 1024 ||
                         !FixedTimeEquals(
                             _password,
                             suppliedPassword)))
                    {
                        RejectSignal(
                            from,
                            "invalid_password",
                            generation);
                        return;
                    }

                    var backendStatus = _inputBackend.GetStatus();
                    if (!backendStatus.IsAvailable)
                    {
                        RejectSignal(
                            from,
                            "input_backend_unavailable",
                            generation);
                        return;
                    }

                    createdConnection = true;
                    EnsureInitialized(from, generation);
                    var connection = _peerConnection ??
                        throw new InvalidOperationException(
                            "Peer connection initialization failed.");
                    var descriptionResult = connection.setRemoteDescription(
                        new RTCSessionDescriptionInit
                        {
                            type = RTCSdpType.offer,
                            sdp = offerSdp,
                        });
                    if (descriptionResult != SetDescriptionResultEnum.OK)
                    {
                        ClosePeerConnectionForReplacement();
                        createdConnection = false;
                        RejectSignal(
                            from,
                            "invalid_remote_description",
                            generation);
                        return;
                    }

                    if (_pendingRemoteIce.Remove(
                            (from, generation),
                            out var pendingCandidates))
                    {
                        foreach (var pendingCandidate in pendingCandidates)
                        {
                            connection.addIceCandidate(pendingCandidate);
                        }
                    }
                    _pendingRemoteIce.Clear();

                    var answer = connection.createAnswer();
                    await connection.setLocalDescription(answer)
                        .ConfigureAwait(false);
                    var session = _controlSession ??
                        throw new InvalidOperationException(
                            "Control session initialization failed.");
                    QueueSignalReady(from, new
                    {
                        sdp = new
                        {
                            sdp = answer.sdp,
                            type = "answer",
                        },
                        connectionGeneration = generation,
                        control = new
                        {
                            version = RemoteControlProtocol.Version,
                            token = session.ExportTokenBase64(),
                        },
                    });
                    Console.WriteLine(
                        "[WebRTC] Authenticated control offer accepted");
                    return;
                }

                if (root["ice"] is JObject ice)
                {
                    long generation =
                        root["connectionGeneration"]?.Type ==
                            JTokenType.Integer
                            ? root.Value<long>("connectionGeneration")
                            : 0;
                    var candidateText =
                        ice["candidate"]?.Type == JTokenType.String
                            ? ice.Value<string>("candidate")
                            : null;
                    var sdpMid =
                        ice["sdpMid"]?.Type == JTokenType.String
                            ? ice.Value<string>("sdpMid")
                            : null;
                    var lineIndex =
                        ice["sdpMLineIndex"]?.Type == JTokenType.Integer
                            ? ice.Value<int>("sdpMLineIndex")
                            : -1;
                    if (generation <= 0 ||
                        string.IsNullOrWhiteSpace(candidateText) ||
                        candidateText.Length > 8192 ||
                        (sdpMid?.Length ?? 0) > 64 ||
                        lineIndex is < 0 or > ushort.MaxValue)
                    {
                        Console.WriteLine(
                            "[WebRTC] Rejected malformed ICE candidate");
                        return;
                    }

                    var candidate = new RTCIceCandidateInit
                    {
                        candidate = candidateText,
                        sdpMid = sdpMid,
                        sdpMLineIndex = checked((ushort)lineIndex),
                    };
                    if (_peerConnection == null ||
                        _peerConnection.connectionState is
                            RTCPeerConnectionState.failed or
                            RTCPeerConnectionState.closed)
                    {
                        QueuePendingRemoteIce(from, generation, candidate);
                        return;
                    }
                    if (generation != _connectionGeneration ||
                        !string.Equals(
                            from,
                            _remoteSocketId,
                            StringComparison.Ordinal))
                    {
                        Console.WriteLine(
                            "[WebRTC] Ignored stale ICE candidate");
                        return;
                    }

                    _peerConnection.addIceCandidate(candidate);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[WebRTC] Signal handling failed: {ex.GetType().Name}");
                if (createdConnection)
                {
                    ClosePeerConnectionForReplacement();
                }
            }
            finally
            {
                _signalGate.Release();
            }
        }

        private void QueuePendingRemoteIce(
            string remoteSocketId,
            long generation,
            RTCIceCandidateInit candidate)
        {
            var key = (remoteSocketId, generation);
            if (!_pendingRemoteIce.TryGetValue(key, out var candidates))
            {
                if (_pendingRemoteIce.Count >= 8)
                {
                    Console.WriteLine(
                        "[WebRTC] Dropped early ICE candidate: queue is full");
                    return;
                }

                candidates = [];
                _pendingRemoteIce.Add(key, candidates);
            }
            if (candidates.Count >= 64)
            {
                Console.WriteLine(
                    "[WebRTC] Dropped early ICE candidate: generation limit reached");
                return;
            }

            candidates.Add(candidate);
            Console.WriteLine("[WebRTC] Queued ICE candidate before offer");
        }

        private void RejectSignal(
            string remoteSocketId,
            string reason,
            long connectionGeneration)
        {
            QueueSignalReady(
                remoteSocketId,
                new
                {
                    rejected = true,
                    reason,
                    connectionGeneration,
                });
        }
        private void QueueSignalReady(
            string remoteSocketId,
            object signal)
        {
            var handlers = OnSignalReady;
            if (handlers == null)
            {
                return;
            }

            foreach (Func<string, object, Task> handler in
                     handlers.GetInvocationList())
            {
                _ = InvokeSignalHandlerAsync(
                    handler,
                    remoteSocketId,
                    signal);
            }
        }

        private static async Task InvokeSignalHandlerAsync(
            Func<string, object, Task> handler,
            string remoteSocketId,
            object signal)
        {
            try
            {
                await handler(remoteSocketId, signal)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[WebRTC] Signal delivery failed: {ex.GetType().Name}");
            }
        }

        private static bool FixedTimeEquals(
            string expected,
            string? actual)
        {
            if (actual == null)
            {
                return false;
            }

            var expectedBytes = Encoding.UTF8.GetBytes(expected);
            var actualBytes = Encoding.UTF8.GetBytes(actual);
            try
            {
                return expectedBytes.Length == actualBytes.Length &&
                    System.Security.Cryptography.CryptographicOperations
                        .FixedTimeEquals(expectedBytes, actualBytes);
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations
                    .ZeroMemory(expectedBytes);
                System.Security.Cryptography.CryptographicOperations
                    .ZeroMemory(actualBytes);
            }
        }
        private bool ApplyClipboardConsent(
            RTCDataChannel channel,
            RemoteChannelServerState security,
            bool enabled)
        {
            bool acknowledgementSent;
            ClipboardMonitoringTransition monitoringTransition;
            lock (_sessionGate)
            {
                if (!ReferenceEquals(_viewerInputChannel, channel) ||
                    !ReferenceEquals(_inputSecurity, security) ||
                    !security.IsAuthenticated)
                {
                    return false;
                }

                _clipboardSessionEnabled = enabled;
                unchecked { _clipboardConsentEpoch++; }
                _lastClipboardText = null;

                acknowledgementSent = TrySendSecured(
                    channel,
                    security,
                    RemoteClipboardConsentProtocol.Create(enabled));
                if (!acknowledgementSent)
                {
                    _clipboardSessionEnabled = false;
                    unchecked { _clipboardConsentEpoch++; }
                    _lastClipboardText = null;
                }

                monitoringTransition = new ClipboardMonitoringTransition(
                    _clipboardConsentEpoch,
                    _clipboardSessionEnabled);
            }

            ApplyClipboardMonitoringTransition(monitoringTransition);
            if (!acknowledgementSent)
            {
                return false;
            }

            Console.WriteLine(
                enabled
                    ? "[Clipboard] Bidirectional session consent enabled."
                    : "[Clipboard] Session consent revoked.");
            return true;
        }


        private bool TryGetClipboardConsentEpoch(
            RTCDataChannel channel,
            RemoteChannelServerState security,
            out long epoch)
        {
            lock (_sessionGate)
            {
                epoch = _clipboardConsentEpoch;
                return _clipboardSessionEnabled &&
                    ReferenceEquals(_viewerInputChannel, channel) &&
                    ReferenceEquals(_inputSecurity, security) &&
                    security.IsAuthenticated;
            }
        }

        private bool IsClipboardSessionEnabled(
            RTCDataChannel channel,
            RemoteChannelServerState security,
            long expectedEpoch)
        {
            lock (_sessionGate)
            {
                return _clipboardSessionEnabled &&
                    _clipboardConsentEpoch == expectedEpoch &&
                    ReferenceEquals(_viewerInputChannel, channel) &&
                    ReferenceEquals(_inputSecurity, security) &&
                    security.IsAuthenticated;
            }
        }

        private bool IsClipboardSessionEnabled(
            RTCPeerConnection connection,
            RTCDataChannel channel,
            RemoteChannelServerState security,
            long expectedEpoch)
        {
            lock (_sessionGate)
            {
                return _clipboardSessionEnabled &&
                    _clipboardConsentEpoch == expectedEpoch &&
                    ReferenceEquals(_peerConnection, connection) &&
                    ReferenceEquals(_viewerInputChannel, channel) &&
                    ReferenceEquals(_inputSecurity, security) &&
                    security.IsAuthenticated;
            }
        }
        private bool TryAcquireClipboardLease(
            RTCDataChannel channel,
            RemoteChannelServerState security,
            long expectedEpoch,
            out ClipboardSessionLease? lease)
        {
            lock (_sessionGate)
            {
                if (_disposed ||
                    !_clipboardSessionEnabled ||
                    _clipboardConsentEpoch != expectedEpoch ||
                    !ReferenceEquals(_viewerInputChannel, channel) ||
                    !ReferenceEquals(_inputSecurity, security) ||
                    !security.IsAuthenticated)
                {
                    lease = null;
                    return false;
                }

                _activeClipboardLeases = checked(_activeClipboardLeases + 1);
                lease = new ClipboardSessionLease(this, expectedEpoch);
                return true;
            }
        }

        private bool TryAcquireClipboardLease(
            RTCPeerConnection connection,
            RTCDataChannel channel,
            RemoteChannelServerState security,
            long expectedEpoch,
            out ClipboardSessionLease? lease)
        {
            lock (_sessionGate)
            {
                if (_disposed ||
                    !_clipboardSessionEnabled ||
                    _clipboardConsentEpoch != expectedEpoch ||
                    !ReferenceEquals(_peerConnection, connection) ||
                    !ReferenceEquals(_viewerInputChannel, channel) ||
                    !ReferenceEquals(_inputSecurity, security) ||
                    !security.IsAuthenticated)
                {
                    lease = null;
                    return false;
                }

                _activeClipboardLeases = checked(_activeClipboardLeases + 1);
                lease = new ClipboardSessionLease(this, expectedEpoch);
                return true;
            }
        }

        private void ReleaseClipboardLease(long leaseEpoch)
        {
            lock (_sessionGate)
            {
                if (_activeClipboardLeases <= 0)
                {
                    throw new InvalidOperationException(
                        "Clipboard session lease accounting underflowed.");
                }

                _activeClipboardLeases--;
                _ = leaseEpoch;
            }
        }

        private sealed class ClipboardSessionLease : IDisposable
        {
            private WebRTCManager? _owner;

            public ClipboardSessionLease(WebRTCManager owner, long epoch)
            {
                _owner = owner;
                Epoch = epoch;
            }

            public long Epoch { get; }

            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                owner?.ReleaseClipboardLease(Epoch);
            }
        }

        private void SetClipboardOnSta(
            string text,
            RTCDataChannel channel,
            RemoteChannelServerState security,
            long consentEpoch)
        {
            if (!_clipboardWorker.TryQueueClipboardSet(() =>
                    ApplyClipboardOnStaWorker(
                        text,
                        channel,
                        security,
                        consentEpoch)))
            {
                Console.WriteLine("[Clipboard] STA worker is stopping.");
            }
        }

        private void ApplyClipboardOnStaWorker(
            string text,
            RTCDataChannel channel,
            RemoteChannelServerState security,
            long consentEpoch)
        {
            if (!TryAcquireClipboardLease(
                    channel,
                    security,
                    consentEpoch,
                    out var lease) ||
                lease == null)
            {
                return;
            }

            // A lease linearizes this already-accepted operation before a
            // concurrent revoke without making the transport thread wait for
            // a potentially slow OS clipboard call. Revocation immediately
            // prevents every later lease acquisition.
            using (lease)
            {
                try
                {
                    System.Windows.Forms.Clipboard.SetText(text);
                    lock (_sessionGate)
                    {
                        if (IsClipboardSessionEnabled(
                                channel,
                                security,
                                consentEpoch))
                        {
                            _lastClipboardText = text;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"[Clipboard] Set failed: {ex.GetType().Name}");
                }
            }
        }

        /// <summary>
        /// Coalesces timer ticks onto the one background STA clipboard worker.
        /// </summary>
        private void PollClipboard(object? _)
        {
            _ = _clipboardWorker.TryQueuePoll(PollClipboardOnStaWorker);
        }

        private void PollClipboardOnStaWorker()
        {
            RTCDataChannel? channel;
            RemoteChannelServerState? security;
            RTCPeerConnection? connection;
            long consentEpoch;
            lock (_sessionGate)
            {
                channel = _viewerInputChannel;
                security = _inputSecurity;
                connection = _peerConnection;
                consentEpoch = _clipboardConsentEpoch;
                if (!_clipboardSessionEnabled)
                {
                    return;
                }
            }

            if (connection == null ||
                channel == null ||
                security == null ||
                !TryAcquireClipboardLease(
                    connection,
                    channel,
                    security,
                    consentEpoch,
                    out var lease) ||
                lease == null)
            {
                return;
            }

            using (lease)
            {
                try
                {
                    if (!System.Windows.Forms.Clipboard.ContainsText())
                    {
                        return;
                    }

                    var text = System.Windows.Forms.Clipboard.GetText();
                    if (string.IsNullOrEmpty(text))
                    {
                        return;
                    }

                    var textBytes = Encoding.UTF8.GetBytes(text);
                    if (textBytes.Length > 32 * 1024)
                    {
                        return;
                    }

                    var message = new byte[1 + textBytes.Length];
                    message[0] = MSG_CLIPBOARD;
                    textBytes.CopyTo(message, 1);
                    lock (_sessionGate)
                    {
                        if (!IsClipboardSessionEnabled(
                                connection,
                                channel,
                                security,
                                consentEpoch) ||
                            text == _lastClipboardText)
                        {
                            return;
                        }

                        if (TrySendSecured(channel, security, message))
                        {
                            _lastClipboardText = text;
                        }
                    }
                }
                catch
                {
                    // Clipboard contention and disconnect races are non-fatal.
                }
            }
        }

        private void ApplyClipboardMonitoringTransition(
            ClipboardMonitoringTransition transition)
        {
            lock (_sessionGate)
            {
                // Consent callbacks may complete out of order. Only the
                // transition matching the current epoch may touch the timer.
                if (!ClipboardMonitoringTransitionPolicy.IsCurrent(
                        _clipboardConsentEpoch,
                        _clipboardSessionEnabled,
                        transition))
                {
                    return;
                }

                if (transition.Enabled)
                {
                    StartClipboardMonitoringLocked();
                }
                else
                {
                    StopClipboardMonitoringLocked();
                }
            }
        }

        private void StartClipboardMonitoringLocked()
        {
            _clipboardTimer?.Dispose();
            _clipboardTimer = new System.Threading.Timer(
                PollClipboard,
                null,
                2000,
                2000);
            Console.WriteLine("[Clipboard] Monitoring started");
        }

        private void StopClipboardMonitoring()
        {
            lock (_sessionGate)
            {
                StopClipboardMonitoringLocked();
            }
        }

        private void StopClipboardMonitoringLocked()
        {
            _clipboardTimer?.Dispose();
            _clipboardTimer = null;
            _clipboardWorker.CancelPending();
        }
        public void Dispose()
        {
            _signalGate.Wait();
            try
            {
                if (_disposed)
                {
                    // A previous bounded worker join may have timed out.
                    _clipboardWorker.Dispose();
                    return;
                }

                _disposed = true;
                try
                {
                    ClosePeerConnectionForReplacement();
                    _inputBackend.Dispose();
                }
                finally
                {
                    _clipboardWorker.Dispose();
                }
            }
            finally
            {
                _signalGate.Release();
            }
        }
    }
}
