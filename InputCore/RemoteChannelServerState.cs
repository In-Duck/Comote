using System.Security.Cryptography;

namespace Comote.Input;

public enum RemoteServerReceiveResult
{
    Authenticated,
    Rejected,
    Payload,
    ProtocolViolation,
}

public sealed class RemoteControlSession : IDisposable
{
    private readonly object _gate = new();
    private readonly byte[] _token;
    private Guid? _clientSessionId;
    private bool _revoked;

    public RemoteControlSession()
        : this(RandomNumberGenerator.GetBytes(
            RemoteControlProtocol.TokenSize))
    {
    }

    public RemoteControlSession(ReadOnlySpan<byte> token)
    {
        if (token.Length != RemoteControlProtocol.TokenSize)
        {
            throw new ArgumentOutOfRangeException(nameof(token));
        }
        _token = token.ToArray();
    }

    public string ExportTokenBase64()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_revoked, this);
            return Convert.ToBase64String(_token);
        }
    }

    public bool IsActive
    {
        get
        {
            lock (_gate)
            {
                return !_revoked;
            }
        }
    }

    internal bool TryAuthenticate(RemoteAuthHello hello)
    {
        lock (_gate)
        {
            if (_revoked ||
                !CryptographicOperations.FixedTimeEquals(
                    _token,
                    hello.Token))
            {
                return false;
            }

            if (_clientSessionId is { } existing &&
                existing != hello.ClientSessionId)
            {
                return false;
            }

            _clientSessionId = hello.ClientSessionId;
            return true;
        }
    }

    public void Revoke()
    {
        lock (_gate)
        {
            if (_revoked)
            {
                return;
            }

            CryptographicOperations.ZeroMemory(_token);
            _clientSessionId = null;
            _revoked = true;
        }
    }

    public void Dispose()
    {
        Revoke();
    }
}

public sealed class RemoteChannelServerState : IDisposable
{
    private readonly object _gate = new();
    private readonly RemoteControlSession _session;
    private readonly RemoteChannelKind _channel;
    private readonly RemoteInputMode _inputMode;
    private readonly uint _capabilities;
    private bool _authenticated;
    private bool _revoked;
    private ulong _nextReceiveSequence = 1;
    private ulong _nextSendSequence = 1;

    public RemoteChannelServerState(
        RemoteControlSession session,
        RemoteChannelKind channel,
        RemoteInputMode inputMode,
        uint capabilities)
    {
        _session = session ??
            throw new ArgumentNullException(nameof(session));
        if (!Enum.IsDefined(typeof(RemoteChannelKind), channel))
        {
            throw new ArgumentOutOfRangeException(nameof(channel));
        }
        if (!Enum.IsDefined(typeof(RemoteInputMode), inputMode))
        {
            throw new ArgumentOutOfRangeException(nameof(inputMode));
        }

        _channel = channel;
        _inputMode = inputMode;
        _capabilities = capabilities;
    }

    public bool IsAuthenticated
    {
        get
        {
            lock (_gate)
            {
                return _authenticated &&
                       !_revoked &&
                       _session.IsActive;
            }
        }
    }

    public RemoteServerReceiveResult ProcessClientMessage(
        ReadOnlySpan<byte> message,
        out byte[] response,
        out byte[] payload)
    {
        response = [];
        payload = [];
        lock (_gate)
        {
            if (_revoked || !_session.IsActive)
            {
                _authenticated = false;
                return RemoteServerReceiveResult.ProtocolViolation;
            }

            if (!_authenticated)
            {
                if (!RemoteControlProtocol.TryParseAuthHello(
                        message,
                        out var hello))
                {
                    response =
                        RemoteControlProtocol.CreateAuthRejected(1);
                    return RemoteServerReceiveResult.Rejected;
                }

                var authenticated = false;
                try
                {
                    authenticated =
                        hello.Channel == _channel &&
                        _session.TryAuthenticate(hello);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(hello.Token);
                }

                if (!authenticated || !_session.IsActive)
                {
                    response =
                        RemoteControlProtocol.CreateAuthRejected(1);
                    return RemoteServerReceiveResult.Rejected;
                }

                _authenticated = true;
                _nextReceiveSequence = 1;
                _nextSendSequence = 1;
                response = RemoteControlProtocol.CreateAuthAccepted(
                    _channel,
                    _inputMode,
                    _capabilities);
                return RemoteServerReceiveResult.Authenticated;
            }

            if (!RemoteControlProtocol.TryUnwrapPayload(
                    message,
                    _nextReceiveSequence,
                    out payload) ||
                !_session.IsActive)
            {
                if (payload.Length != 0)
                {
                    CryptographicOperations.ZeroMemory(payload);
                    payload = [];
                }
                return RemoteServerReceiveResult.ProtocolViolation;
            }

            _nextReceiveSequence = checked(_nextReceiveSequence + 1);
            return RemoteServerReceiveResult.Payload;
        }
    }

    public byte[] WrapPayload(ReadOnlySpan<byte> payload)
    {
        lock (_gate)
        {
            if (!_authenticated ||
                _revoked ||
                !_session.IsActive)
            {
                throw new InvalidOperationException(
                    "The remote channel is not authenticated.");
            }

            var message = RemoteControlProtocol.WrapPayload(
                _nextSendSequence,
                payload);
            _nextSendSequence = checked(_nextSendSequence + 1);
            return message;
        }
    }

    public void Revoke()
    {
        lock (_gate)
        {
            _authenticated = false;
            _revoked = true;
            _nextReceiveSequence = 0;
            _nextSendSequence = 0;
        }
    }

    public void Dispose()
    {
        Revoke();
    }
}