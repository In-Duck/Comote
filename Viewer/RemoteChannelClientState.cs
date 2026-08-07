using System;
using System.Security.Cryptography;
using Comote.Input;

namespace Viewer;

internal enum RemoteChannelReceiveResult
{
    Ignored,
    Authenticated,
    Rejected,
    Payload,
    ProtocolViolation,
}

internal sealed class RemoteChannelClientState : IDisposable
{
    private readonly object _gate = new();
    private readonly RemoteChannelKind _channel;
    private byte[]? _token;
    private Guid _clientSessionId;
    private bool _helloSent;
    private bool _authenticated;
    private bool _tokenAssigned;
    private bool _revoked;
    private bool _disposed;
    private ulong _nextSendSequence;
    private ulong _nextReceiveSequence;

    public RemoteChannelClientState(RemoteChannelKind channel)
    {
        _channel = channel;
        Reset(Guid.NewGuid());
    }

    public bool IsAuthenticated
    {
        get
        {
            lock (_gate)
            {
                return _authenticated &&
                       !_revoked &&
                       !_disposed;
            }
        }
    }

    public void Reset(Guid clientSessionId)
    {
        if (clientSessionId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(
                nameof(clientSessionId));
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ClearTokenLocked();
            _clientSessionId = clientSessionId;
            _helloSent = false;
            _authenticated = false;
            _tokenAssigned = false;
            _revoked = false;
            _nextSendSequence = 1;
            _nextReceiveSequence = 1;
        }
    }

    public void SetToken(ReadOnlySpan<byte> token)
    {
        if (token.Length != RemoteControlProtocol.TokenSize)
        {
            throw new ArgumentOutOfRangeException(nameof(token));
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_revoked || _tokenAssigned)
            {
                throw new InvalidOperationException(
                    "A control token can only be set once per connection.");
            }

            _token = token.ToArray();
            _tokenAssigned = true;
        }
    }

    public byte[]? TryCreateHello()
    {
        lock (_gate)
        {
            if (_disposed ||
                _revoked ||
                _helloSent ||
                _token == null)
            {
                return null;
            }

            _helloSent = true;
            return RemoteControlProtocol.CreateAuthHello(
                _channel,
                _token,
                _clientSessionId);
        }
    }

    public RemoteChannelReceiveResult ProcessServerMessage(
        ReadOnlySpan<byte> message,
        out byte[] payload,
        out RemoteAuthAccepted accepted)
    {
        payload = [];
        accepted = default;
        lock (_gate)
        {
            if (_disposed || _revoked)
            {
                return RemoteChannelReceiveResult.ProtocolViolation;
            }

            if (!_authenticated)
            {
                if (!_helloSent || _token == null)
                {
                    RevokeLocked();
                    return RemoteChannelReceiveResult.ProtocolViolation;
                }

                if (RemoteControlProtocol.TryParseAuthAccepted(
                        message,
                        out accepted) &&
                    accepted.Channel == _channel)
                {
                    _authenticated = true;
                    _nextSendSequence = 1;
                    _nextReceiveSequence = 1;
                    ClearTokenLocked();
                    return RemoteChannelReceiveResult.Authenticated;
                }

                if (message.Length == 3 &&
                    message[0] ==
                        RemoteControlProtocol.AuthRejectedMessage &&
                    message[1] == RemoteControlProtocol.Version)
                {
                    RevokeLocked();
                    return RemoteChannelReceiveResult.Rejected;
                }

                RevokeLocked();
                return RemoteChannelReceiveResult.ProtocolViolation;
            }

            if (!RemoteControlProtocol.TryUnwrapPayload(
                    message,
                    _nextReceiveSequence,
                    out payload))
            {
                RevokeLocked();
                return RemoteChannelReceiveResult.ProtocolViolation;
            }

            _nextReceiveSequence = checked(_nextReceiveSequence + 1);
            return RemoteChannelReceiveResult.Payload;
        }
    }

    public byte[] WrapPayload(ReadOnlySpan<byte> payload)
    {
        lock (_gate)
        {
            if (!_authenticated || _revoked || _disposed)
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
            RevokeLocked();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            RevokeLocked();
            _disposed = true;
        }
    }

    private void RevokeLocked()
    {
        ClearTokenLocked();
        _authenticated = false;
        _revoked = true;
        _nextSendSequence = 0;
        _nextReceiveSequence = 0;
    }

    private void ClearTokenLocked()
    {
        if (_token == null)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_token);
        _token = null;
    }
}