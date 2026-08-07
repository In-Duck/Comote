using System.IO.Pipes;
using System.Security.Principal;

namespace Comote.Input;

public sealed class InputBrokerClient : IDisposable
{
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private readonly object _stateLock = new();
    private readonly Timer _heartbeatTimer;
    private NamedPipeClientStream? _pipe;
    private uint _nextRequestId;
    private int _disposed;

    public InputBrokerClient()
    {
        _heartbeatTimer = new Timer(
            static state => ((InputBrokerClient)state!).SendHeartbeat(),
            this,
            Timeout.Infinite,
            Timeout.Infinite);
    }

    public BrokerResponse GetStatus() =>
        Send(BrokerCommand.GetStatus, []);

    public BrokerResponse SetKeyboardState(
        byte modifiers,
        ReadOnlySpan<byte> keys) =>
        Send(
            BrokerCommand.SetKeyboardState,
            BrokerProtocol.CreateKeyboardPayload(modifiers, keys));

    public BrokerResponse MouseRelative(
        byte buttons,
        short deltaX,
        short deltaY,
        sbyte verticalWheel,
        sbyte horizontalWheel) =>
        Send(
            BrokerCommand.MouseRelative,
            BrokerProtocol.CreateRelativeMousePayload(
                buttons,
                deltaX,
                deltaY,
                verticalWheel,
                horizontalWheel));

    public BrokerResponse MouseAbsolute(
        byte buttons,
        ushort x,
        ushort y,
        sbyte verticalWheel,
        sbyte horizontalWheel) =>
        Send(
            BrokerCommand.MouseAbsolute,
            BrokerProtocol.CreateAbsoluteMousePayload(
                buttons,
                x,
                y,
                verticalWheel,
                horizontalWheel));

    public BrokerResponse ReleaseAll() =>
        Send(BrokerCommand.ReleaseAll, []);

    public BrokerResponse Send(
        BrokerCommand command,
        byte[] payload)
    {
        try
        {
            return SendAsync(
                command,
                payload,
                CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (
            ex is IOException or
            TimeoutException or
            UnauthorizedAccessException or
            InvalidDataException or
            OperationCanceledException)
        {
            Disconnect();
            return new BrokerResponse(
                BrokerStatus.DriverUnavailable,
                ex.Message);
        }
    }

    public async Task<BrokerResponse> SendAsync(
        BrokerCommand command,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        if (payload.Length > BrokerProtocol.MaximumPayloadSize)
        {
            throw new ArgumentOutOfRangeException(nameof(payload));
        }

        using var requestTimeout =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        requestTimeout.CancelAfter(TimeSpan.FromSeconds(3));
        await _requestLock.WaitAsync(requestTimeout.Token)
            .ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
            var pipe = await EnsureConnectedAsync(
                requestTimeout.Token).ConfigureAwait(false);
            var requestId = NextRequestId();
            await BrokerProtocol.WriteRequestAsync(
                pipe,
                new BrokerFrame(requestId, command, payload),
                requestTimeout.Token).ConfigureAwait(false);
            var response = await BrokerProtocol.ReadResponseAsync(
                pipe,
                requestId,
                command,
                requestTimeout.Token).ConfigureAwait(false);
            ArmHeartbeat();
            return response;
        }
        catch
        {
            Disconnect();
            throw;
        }
        finally
        {
            _requestLock.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _heartbeatTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _requestLock.Wait();
        try
        {
            var pipe = _pipe;
            if (pipe is { IsConnected: true })
            {
                try
                {
                    var requestId = NextRequestId();
                    using var timeout = new CancellationTokenSource(
                        TimeSpan.FromSeconds(1));
                    BrokerProtocol.WriteRequestAsync(
                        pipe,
                        new BrokerFrame(
                            requestId,
                            BrokerCommand.ReleaseAll,
                            []),
                        timeout.Token).AsTask().GetAwaiter().GetResult();
                    _ = BrokerProtocol.ReadResponseAsync(
                        pipe,
                        requestId,
                        BrokerCommand.ReleaseAll,
                        timeout.Token).AsTask().GetAwaiter().GetResult();
                }
                catch
                {
                }
            }
            Disconnect();
        }
        finally
        {
            _requestLock.Release();
            _heartbeatTimer.Dispose();
        }
    }

    private async Task<NamedPipeClientStream> EnsureConnectedAsync(
        CancellationToken cancellationToken)
    {
        lock (_stateLock)
        {
            if (_pipe is { IsConnected: true })
            {
                return _pipe;
            }
        }

        var candidate = new NamedPipeClientStream(
            ".",
            BrokerProtocol.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            await candidate.ConnectAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested)
        {
            candidate.Dispose();
            throw new TimeoutException(
                "Timed out connecting to the Comote input broker.");
        }

        try
        {
            RequireLocalSystemPipeOwner(candidate);
        }
        catch
        {
            candidate.Dispose();
            throw;
        }

        candidate.ReadMode = PipeTransmissionMode.Byte;
        lock (_stateLock)
        {
            if (_pipe is { IsConnected: true })
            {
                candidate.Dispose();
                return _pipe;
            }
            _pipe?.Dispose();
            _pipe = candidate;
            return candidate;
        }
    }

    /// <summary>
    /// Rejects a pipe that the LocalSystem broker did not create.
    /// </summary>
    /// <remarks>
    /// The server creates its pipe with <c>FirstPipeInstance</c>, but the name
    /// is free before the service starts and between connections. A local
    /// process could claim it and receive every keystroke and mouse
    /// coordinate this client sends, so the owner is checked before the first
    /// request leaves.
    /// </remarks>
    private static void RequireLocalSystemPipeOwner(
        NamedPipeClientStream pipe)
    {
        var localSystem = new SecurityIdentifier(
            WellKnownSidType.LocalSystemSid,
            null);
        IdentityReference? owner;
        try
        {
            owner = pipe.GetAccessControl()
                .GetOwner(typeof(SecurityIdentifier));
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException or
            InvalidOperationException or
            IOException)
        {
            throw new UnauthorizedAccessException(
                "The Comote input broker pipe owner could not be read.",
                ex);
        }

        if (owner is not SecurityIdentifier ownerSid ||
            !ownerSid.Equals(localSystem))
        {
            throw new UnauthorizedAccessException(
                "The Comote input broker pipe is not owned by LocalSystem.");
        }
    }

    private uint NextRequestId()
    {
        _nextRequestId++;
        if (_nextRequestId == 0)
        {
            _nextRequestId = 1;
        }
        return _nextRequestId;
    }

    private void ArmHeartbeat()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }
        try
        {
            _heartbeatTimer.Change(
                BrokerProtocol.HeartbeatIntervalMilliseconds,
                BrokerProtocol.HeartbeatIntervalMilliseconds);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void SendHeartbeat()
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            !_requestLock.Wait(0))
        {
            return;
        }

        try
        {
            var pipe = _pipe;
            if (pipe is not { IsConnected: true })
            {
                return;
            }

            var requestId = NextRequestId();
            using var timeout = new CancellationTokenSource(
                TimeSpan.FromMilliseconds(
                    BrokerProtocol.HeartbeatIntervalMilliseconds));
            BrokerProtocol.WriteRequestAsync(
                pipe,
                new BrokerFrame(
                    requestId,
                    BrokerCommand.Heartbeat,
                    []),
                timeout.Token).AsTask().GetAwaiter().GetResult();
            var response = BrokerProtocol.ReadResponseAsync(
                pipe,
                requestId,
                BrokerCommand.Heartbeat,
                timeout.Token).AsTask().GetAwaiter().GetResult();
            if (!response.IsSuccess)
            {
                Disconnect();
            }
        }
        catch
        {
            Disconnect();
        }
        finally
        {
            _requestLock.Release();
        }
    }

    private void Disconnect()
    {
        lock (_stateLock)
        {
            try
            {
                _heartbeatTimer.Change(Timeout.Infinite, Timeout.Infinite);
            }
            catch (ObjectDisposedException)
            {
            }
            _pipe?.Dispose();
            _pipe = null;
        }
    }
}
