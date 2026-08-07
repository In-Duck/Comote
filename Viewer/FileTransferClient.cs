using System.Collections.Generic;
using System.Security.Cryptography;
using Comote.Input;
using Newtonsoft.Json.Linq;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace Viewer;

public sealed class FileTransferClient : IDisposable
{
    private readonly SignalingClient _signaling;
    private readonly string _targetHostId;
    private readonly List<RTCIceCandidateInit> _iceQueue = [];
    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _connectGate = new(1, 1);

    private RTCPeerConnection? _peerConnection;
    private RTCDataChannel? _dataChannel;
    private RemoteChannelClientState? _security;
    private RemoteFileSender? _fileSender;
    private TaskCompletionSource<bool>? _connectionCompletion;
    private long _connectionGeneration;
    private bool _remoteDescriptionSet;
    private bool _answerApplied;
    private bool _disposed;

    public Action<int>? OnProgress;
    public Action<string>? OnStatus;

    public FileTransferClient(
        SignalingClient signaling,
        string targetHostId)
    {
        _signaling = signaling ??
            throw new ArgumentNullException(nameof(signaling));
        _targetHostId = string.IsNullOrWhiteSpace(targetHostId)
            ? throw new ArgumentException(
                "Target host ID is required.",
                nameof(targetHostId))
            : targetHostId;
        _signaling.OnSignalReceived += OnSignalReceivedAsync;
    }

    public async Task<bool> ConnectAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _connectGate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            DisposePeerConnection();
            var generation = Random.Shared.NextInt64(
                1,
                long.MaxValue);
            var security = new RemoteChannelClientState(
                RemoteChannelKind.File);
            security.Reset(Guid.NewGuid());
            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var config = new RTCConfiguration
            {
                iceServers =
                [
                    new RTCIceServer
                    {
                        urls = "stun:stun.l.google.com:19302",
                    },
                ],
            };
            var connection = new RTCPeerConnection(config);
            RTCDataChannel channel;
            try
            {
                channel = await connection.createDataChannel("file")
                    .ConfigureAwait(false);
            }
            catch
            {
                security.Dispose();
                try { connection.Dispose(); } catch { }
                throw;
            }
            var sender = new RemoteFileSender(
                payload => SendSecure(
                    generation,
                    connection,
                    channel,
                    security,
                    payload.Span),
                () =>
                {
                    var isOpen = IsCurrent(
                            generation,
                            connection,
                            channel,
                            security) &&
                        channel.readyState == RTCDataChannelState.open;
                    return (
                        isOpen,
                        isOpen ? checked((ulong)channel.bufferedAmount) : 0);
                },
                _ =>
                {
                    if (!IsCurrent(
                            generation,
                            connection,
                            channel,
                            security))
                    {
                        return;
                    }

                    completion.TrySetResult(false);
                    security.Revoke();
                    try { channel.close(); } catch { }
                    try { connection.close(); } catch { }
                });
            sender.ProgressChanged += progress =>
                OnProgress?.Invoke(progress);

            bool published;
            lock (_stateGate)
            {
                published = !_disposed;
                if (published)
                {
                    _connectionGeneration = generation;
                    _peerConnection = connection;
                    _dataChannel = channel;
                    _security = security;
                    _fileSender = sender;
                    _connectionCompletion = completion;
                    _remoteDescriptionSet = false;
                    _answerApplied = false;
                    _iceQueue.Clear();
                }
            }
            if (!published)
            {
                sender.Dispose();
                security.Dispose();
                try { channel.close(); } catch { }
                try { connection.Close("disposed"); } catch { }
                connection.Dispose();
                throw new ObjectDisposedException(nameof(FileTransferClient));
            }

            channel.onopen += () =>
                TrySendAuthenticationHello(
                    generation,
                    connection,
                    channel,
                    security);
            channel.onmessage += (_, _, data) =>
                HandleSecuredMessage(
                    generation,
                    connection,
                    channel,
                    security,
                    sender,
                    completion,
                    data);
            channel.onclose += () =>
            {
                if (IsCurrent(
                        generation,
                        connection,
                        channel,
                        security))
                {
                    sender.Abort();
                    completion.TrySetResult(false);
                    security.Revoke();
                }
            };

            connection.onconnectionstatechange += state =>
            {
                if (!IsCurrent(
                        generation,
                        connection,
                        channel,
                        security))
                {
                    return;
                }

                if (state is RTCPeerConnectionState.disconnected or
                             RTCPeerConnectionState.failed or
                             RTCPeerConnectionState.closed)
                {
                    sender.Abort();
                    completion.TrySetResult(false);
                    security.Revoke();
                    try { channel.close(); } catch { }
                }
            };
            connection.onicecandidate += candidate =>
            {
                if (candidate == null ||
                    !IsCurrent(
                        generation,
                        connection,
                        channel,
                        security))
                {
                    return;
                }

                _ = _signaling.SendSignalAsync(
                    _targetHostId,
                    new
                    {
                        ice = new
                        {
                            candidate = candidate.candidate,
                            sdpMid = candidate.sdpMid,
                            sdpMLineIndex = candidate.sdpMLineIndex,
                        },
                        connectionGeneration = generation,
                    });
            };

            var h264 = new VideoFormat(
                VideoCodecsEnum.H264,
                96,
                90000,
                null);
            connection.addTrack(new MediaStreamTrack(
                h264,
                MediaStreamStatusEnum.RecvOnly));

            if (!IsCurrent(
                    generation,
                    connection,
                    channel,
                    security))
            {
                return false;
            }

            var offer = connection.createOffer(null);
            await connection.setLocalDescription(offer)
                .ConfigureAwait(false);
            if (!IsCurrent(
                    generation,
                    connection,
                    channel,
                    security))
            {
                return false;
            }
            await _signaling.SendSignalAsync(
                    _targetHostId,
                    new
                    {
                        sdp = new
                        {
                            type = "offer",
                            sdp = offer.sdp,
                        },
                        connectionGeneration = generation,
                    })
                .ConfigureAwait(false);

            try
            {
                bool connected = await completion.Task.WaitAsync(
                        TimeSpan.FromSeconds(30),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!connected)
                {
                    DisposePeerConnection();
                }
                return connected;
            }
            catch (TimeoutException)
            {
                DisposePeerConnection();
                return false;
            }
        }
        catch
        {
            DisposePeerConnection();
            throw;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    public async Task SendFileAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        RemoteFileSender sender;
        lock (_stateGate)
        {
            sender = _fileSender ??
                throw new InvalidOperationException(
                    "File channel is not connected.");
        }

        ReportStatus("파일을 안전하게 전송하는 중입니다.");
        await sender.SendAsync(filePath, cancellationToken)
            .ConfigureAwait(false);
        ReportStatus("파일 전송 및 무결성 검증이 완료되었습니다.");
    }

    private void ReportStatus(string status)
    {
        try
        {
            OnStatus?.Invoke(status);
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"[FileTransfer] Status callback failed: {ex.GetType().Name}");
        }
    }

    private Task OnSignalReceivedAsync(string from, object signal)
    {
        OnSignalReceived(from, signal);
        return Task.CompletedTask;
    }

    private void OnSignalReceived(string from, object signal)
    {
        if (!string.Equals(
                from,
                _targetHostId,
                StringComparison.Ordinal))
        {
            return;
        }

        RTCPeerConnection? connection;
        RTCDataChannel? channel;
        RemoteChannelClientState? security;
        TaskCompletionSource<bool>? completion;
        long generation;
        lock (_stateGate)
        {
            connection = _peerConnection;
            channel = _dataChannel;
            security = _security;
            completion = _connectionCompletion;
            generation = _connectionGeneration;
        }
        if (connection == null ||
            channel == null ||
            security == null ||
            completion == null)
        {
            return;
        }

        try
        {
            var json = signal as JObject ?? JObject.FromObject(signal);
            if (json["connectionGeneration"]?.Type !=
                    JTokenType.Integer ||
                json.Value<long>("connectionGeneration") != generation)
            {
                return;
            }

            int signalKindCount =
                (json.ContainsKey("rejected") ? 1 : 0) +
                (json.ContainsKey("sdp") ? 1 : 0) +
                (json.ContainsKey("ice") ? 1 : 0);
            if (signalKindCount != 1)
            {
                throw new InvalidDataException(
                    "A signal must contain exactly one body.");
            }

            if (json.ContainsKey("rejected"))
            {
                if (json["rejected"]?.Type != JTokenType.Boolean ||
                    json.Value<bool>("rejected") != true)
                {
                    throw new InvalidDataException(
                        "The rejection body is malformed.");
                }

                security.Revoke();
                completion.TrySetResult(false);
                try { channel.close(); } catch { }
                return;
            }

            if (json["sdp"] is JObject sdp)
            {
                if (sdp["type"]?.Type != JTokenType.String ||
                    !string.Equals(
                        sdp.Value<string>("type"),
                        "answer",
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Only an exact SDP answer is accepted.");
                }

                var answerText = sdp["sdp"]?.Type == JTokenType.String
                    ? sdp.Value<string>("sdp")
                    : null;
                if (string.IsNullOrWhiteSpace(answerText) ||
                    answerText.Length > 1024 * 1024)
                {
                    throw new InvalidDataException(
                        "The SDP answer is empty or too large.");
                }

                lock (_stateGate)
                {
                    if (!IsCurrentLocked(
                            generation,
                            connection,
                            channel,
                            security) ||
                        _answerApplied)
                    {
                        throw new InvalidDataException(
                            "Only one SDP answer is accepted per connection.");
                    }
                    _answerApplied = true;
                }

                ApplyControlToken(json, security);
                if (connection.setRemoteDescription(
                        new RTCSessionDescriptionInit
                        {
                            type = RTCSdpType.answer,
                            sdp = answerText,
                        }) != SetDescriptionResultEnum.OK)
                {
                    throw new InvalidDataException(
                        "The remote SDP answer was rejected.");
                }

                List<RTCIceCandidateInit> queued;
                lock (_stateGate)
                {
                    if (!IsCurrentLocked(
                            generation,
                            connection,
                            channel,
                            security))
                    {
                        return;
                    }
                    _remoteDescriptionSet = true;
                    queued = [.. _iceQueue];
                    _iceQueue.Clear();
                }
                foreach (var queuedCandidate in queued)
                {
                    if (!IsCurrent(
                            generation,
                            connection,
                            channel,
                            security))
                    {
                        return;
                    }
                    connection.addIceCandidate(queuedCandidate);
                }
                TrySendAuthenticationHello(
                    generation,
                    connection,
                    channel,
                    security);
                return;
            }

            if (json["ice"] is not JObject ice)
            {
                throw new InvalidDataException(
                    "The signal body is malformed.");
            }

            var candidateText =
                ice["candidate"]?.Type == JTokenType.String
                    ? ice.Value<string>("candidate")
                    : null;
            var sdpMid = ice["sdpMid"]?.Type == JTokenType.String
                ? ice.Value<string>("sdpMid")
                : null;
            var lineIndex =
                ice["sdpMLineIndex"]?.Type == JTokenType.Integer
                    ? ice.Value<int>("sdpMLineIndex")
                    : -1;
            if (string.IsNullOrWhiteSpace(candidateText) ||
                candidateText.Length > 8192 ||
                (sdpMid?.Length ?? 0) > 64 ||
                lineIndex is < 0 or > ushort.MaxValue)
            {
                throw new InvalidDataException(
                    "The ICE candidate is out of range.");
            }

            var candidate = new RTCIceCandidateInit
            {
                candidate = candidateText,
                sdpMid = sdpMid,
                sdpMLineIndex = checked((ushort)lineIndex),
            };
            bool addImmediately;
            lock (_stateGate)
            {
                if (!IsCurrentLocked(
                        generation,
                        connection,
                        channel,
                        security))
                {
                    return;
                }

                addImmediately = _remoteDescriptionSet;
                if (!addImmediately)
                {
                    if (_iceQueue.Count >= 64)
                    {
                        throw new InvalidDataException(
                            "Too many queued ICE candidates.");
                    }
                    _iceQueue.Add(candidate);
                }
            }
            if (addImmediately &&
                IsCurrent(
                    generation,
                    connection,
                    channel,
                    security))
            {
                connection.addIceCandidate(candidate);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"[FileTransfer] Signaling failed: {ex.GetType().Name}");
            security.Revoke();
            completion.TrySetResult(false);
            try { channel.close(); } catch { }
        }
    }

    private static void ApplyControlToken(
        JObject signal,
        RemoteChannelClientState security)
    {
        if (signal["control"] is not JObject control ||
            control["version"]?.Type != JTokenType.Integer ||
            control.Value<int>("version") !=
                RemoteControlProtocol.Version ||
            control["token"]?.Type != JTokenType.String)
        {
            throw new InvalidDataException(
                "The host did not provide a supported control handshake.");
        }

        byte[] token;
        try
        {
            token = Convert.FromBase64String(
                control.Value<string>("token") ?? "");
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException(
                "The host control token is malformed.",
                ex);
        }
        if (token.Length != RemoteControlProtocol.TokenSize)
        {
            CryptographicOperations.ZeroMemory(token);
            throw new InvalidDataException(
                "The host control token has the wrong size.");
        }

        try
        {
            security.SetToken(token);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(token);
        }
    }
    private void TrySendAuthenticationHello(
        long generation,
        RTCPeerConnection connection,
        RTCDataChannel channel,
        RemoteChannelClientState security)
    {
        if (!IsCurrent(
                generation,
                connection,
                channel,
                security) ||
            channel.readyState != RTCDataChannelState.open)
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
            try { channel.close(); } catch { }
        }
    }

    private void HandleSecuredMessage(
        long generation,
        RTCPeerConnection connection,
        RTCDataChannel channel,
        RemoteChannelClientState security,
        RemoteFileSender sender,
        TaskCompletionSource<bool> completion,
        byte[] data)
    {
        if (!IsCurrent(
                generation,
                connection,
                channel,
                security))
        {
            return;
        }

        var result = security.ProcessServerMessage(
            data,
            out var payload,
            out _);
        switch (result)
        {
            case RemoteChannelReceiveResult.Authenticated:
                completion.TrySetResult(true);
                return;
            case RemoteChannelReceiveResult.Payload:
                if (!sender.TryProcessResponse(payload))
                {
                    sender.Abort(new InvalidDataException(
                        "The host sent an invalid file response."));
                    channel.close();
                }
                return;
            case RemoteChannelReceiveResult.Rejected:
            case RemoteChannelReceiveResult.ProtocolViolation:
                sender.Abort(new InvalidDataException(
                    "The secure file channel was rejected."));
                completion.TrySetResult(false);
                channel.close();
                return;
        }
    }

    private bool SendSecure(
        long generation,
        RTCPeerConnection connection,
        RTCDataChannel channel,
        RemoteChannelClientState security,
        ReadOnlySpan<byte> payload)
    {
        lock (_stateGate)
        {
            if (!IsCurrentLocked(
                    generation,
                    connection,
                    channel,
                    security) ||
                channel.readyState != RTCDataChannelState.open ||
                !security.IsAuthenticated)
            {
                return false;
            }

            channel.send(security.WrapPayload(payload));
            return true;
        }
    }

    private bool IsCurrent(
        long generation,
        RTCPeerConnection connection,
        RTCDataChannel channel,
        RemoteChannelClientState security)
    {
        lock (_stateGate)
        {
            return IsCurrentLocked(
                generation,
                connection,
                channel,
                security);
        }
    }

    private bool IsCurrentLocked(
        long generation,
        RTCPeerConnection connection,
        RTCDataChannel channel,
        RemoteChannelClientState security) =>
        !_disposed &&
        generation == _connectionGeneration &&
        ReferenceEquals(connection, _peerConnection) &&
        ReferenceEquals(channel, _dataChannel) &&
        ReferenceEquals(security, _security);

    private void DisposePeerConnection()
    {
        RTCPeerConnection? connection;
        RTCDataChannel? channel;
        RemoteChannelClientState? security;
        RemoteFileSender? sender;
        TaskCompletionSource<bool>? completion;
        lock (_stateGate)
        {
            connection = _peerConnection;
            channel = _dataChannel;
            security = _security;
            sender = _fileSender;
            completion = _connectionCompletion;
            _connectionGeneration = 0;
            _peerConnection = null;
            _dataChannel = null;
            _security = null;
            _fileSender = null;
            _connectionCompletion = null;
            _remoteDescriptionSet = false;
            _answerApplied = false;
            _iceQueue.Clear();
        }

        sender?.Dispose();
        security?.Dispose();
        completion?.TrySetResult(false);
        try { channel?.close(); } catch { }
        try { connection?.Close("disposed"); } catch { }
        connection?.Dispose();
    }

    public void Dispose()
    {
        lock (_stateGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _signaling.OnSignalReceived -= OnSignalReceivedAsync;
        DisposePeerConnection();
    }
}