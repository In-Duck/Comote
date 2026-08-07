using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Comote.Input;

namespace Viewer;

internal enum RemoteFileAcknowledgement
{
    Accepted,
    HashMismatch,
    Rejected,
}

internal sealed class RemoteFileSender : IDisposable
{
    private const ulong HighWatermark = 1024 * 1024;
    private const ulong LowWatermark = 256 * 1024;

    private readonly SemaphoreSlim _transferGate = new(1, 1);
    private readonly Func<ReadOnlyMemory<byte>, bool> _send;
    private readonly Func<(bool IsOpen, ulong BufferedAmount)> _channelState;
    private readonly Action<Exception>? _abortChannel;
    private readonly object _ackGate = new();
    private TaskCompletionSource<RemoteFileAcknowledgement>? _acknowledgement;
    private Guid _activeTransferId;
    private int _disposed;

    public RemoteFileSender(
        Func<ReadOnlyMemory<byte>, bool> send,
        Func<(bool IsOpen, ulong BufferedAmount)> channelState,
        Action<Exception>? abortChannel = null)
    {
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _channelState = channelState ??
            throw new ArgumentNullException(nameof(channelState));
        _abortChannel = abortChannel;
    }

    public event Action<int>? ProgressChanged;

    public async Task SendAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
        await _transferGate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        var transferStarted = false;
        try
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
            var file = new FileInfo(filePath);
            if (!file.Exists)
            {
                throw new FileNotFoundException(
                    "File not found.",
                    filePath);
            }
            if (file.Length > RemoteFileTransferProtocol.MaximumFileSize)
            {
                throw new InvalidOperationException(
                    "Files larger than 256 MiB are not supported.");
            }

            var nameBytes = Encoding.UTF8.GetBytes(file.Name);
            if (nameBytes.Length is < 1 or
                > RemoteFileTransferProtocol.MaximumFileNameSize)
            {
                throw new InvalidOperationException(
                    "The UTF-8 file name must be 1 to 255 bytes.");
            }

            using var stream = new FileStream(
                file.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var fileLength = stream.Length;
            if (fileLength > RemoteFileTransferProtocol.MaximumFileSize)
            {
                throw new InvalidOperationException(
                    "Files larger than 256 MiB are not supported.");
            }

            var hash = await SHA256.HashDataAsync(
                    stream,
                    cancellationToken)
                .ConfigureAwait(false);
            stream.Position = 0;

            var transferId = Guid.NewGuid();
            var completion =
                new TaskCompletionSource<RemoteFileAcknowledgement>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_ackGate)
            {
                _activeTransferId = transferId;
                _acknowledgement = completion;
            }

            transferStarted = true;
            SendOrThrow(RemoteFileTransferProtocol.CreateStart(
                transferId,
                checked((uint)fileLength),
                nameBytes,
                hash));

            var buffer = new byte[
                RemoteFileTransferProtocol.MaximumChunkSize];
            uint offset = 0;
            while (true)
            {
                var read = await stream.ReadAsync(
                        buffer.AsMemory(),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await WaitForBackpressureAsync(cancellationToken)
                    .ConfigureAwait(false);
                SendOrThrow(RemoteFileTransferProtocol.CreateChunk(
                    transferId,
                    offset,
                    buffer.AsSpan(0, read)));
                offset = checked(offset + (uint)read);
                ReportProgress(
                    fileLength == 0
                        ? 100
                        : (int)(offset * 100L / fileLength));
            }

            if (offset != fileLength)
            {
                throw new IOException(
                    "The source file changed during transfer.");
            }

            await WaitForBackpressureAsync(cancellationToken)
                .ConfigureAwait(false);
            SendOrThrow(
                RemoteFileTransferProtocol.CreateEnd(transferId));

            var acknowledgement = await completion.Task.WaitAsync(
                    TimeSpan.FromSeconds(60),
                    cancellationToken)
                .ConfigureAwait(false);
            switch (acknowledgement)
            {
                case RemoteFileAcknowledgement.Accepted:
                    ReportProgress(100);
                    return;
                case RemoteFileAcknowledgement.HashMismatch:
                    throw new InvalidDataException(
                        "The host reported a file hash mismatch.");
                default:
                    throw new InvalidDataException(
                        "The host rejected the file transfer.");
            }
        }
        catch (Exception ex)
        {
            if (transferStarted)
            {
                try
                {
                    _abortChannel?.Invoke(ex);
                }
                catch
                {
                    // Preserve the original transfer failure.
                }
            }
            throw;
        }
        finally
        {
            lock (_ackGate)
            {
                _activeTransferId = Guid.Empty;
                _acknowledgement = null;
            }
            _transferGate.Release();
        }
    }

    private void ReportProgress(int progress)
    {
        try
        {
            ProgressChanged?.Invoke(progress);
        }
        catch
        {
            // UI callbacks cannot corrupt the wire transaction.
        }
    }

    public bool TryProcessResponse(ReadOnlySpan<byte> payload)
    {
        Guid transferId;
        RemoteFileAcknowledgement acknowledgement;
        if (RemoteFileTransferProtocol.TryParseTransferOnly(
                payload,
                RemoteFileTransferProtocol.AcknowledgedMessage,
                out transferId))
        {
            acknowledgement = RemoteFileAcknowledgement.Accepted;
        }
        else if (RemoteFileTransferProtocol.TryParseTransferOnly(
                     payload,
                     RemoteFileTransferProtocol.HashMismatchMessage,
                     out transferId))
        {
            acknowledgement = RemoteFileAcknowledgement.HashMismatch;
        }
        else if (RemoteFileTransferProtocol.TryParseRejected(
                     payload,
                     out transferId,
                     out _))
        {
            acknowledgement = RemoteFileAcknowledgement.Rejected;
        }
        else
        {
            return false;
        }

        lock (_ackGate)
        {
            if (transferId != _activeTransferId ||
                _acknowledgement == null)
            {
                return true;
            }

            _acknowledgement.TrySetResult(acknowledgement);
            return true;
        }
    }

    public void Abort(Exception? error = null)
    {
        lock (_ackGate)
        {
            _acknowledgement?.TrySetException(
                error ?? new IOException(
                    "The file channel closed during transfer."));
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        Abort(new ObjectDisposedException(nameof(RemoteFileSender)));
    }

    private void SendOrThrow(byte[] payload)
    {
        if (!_send(payload))
        {
            throw new IOException(
                "The file channel closed during transfer.");
        }
    }

    private async Task WaitForBackpressureAsync(
        CancellationToken cancellationToken)
    {
        var state = _channelState();
        if (!state.IsOpen)
        {
            throw new IOException("The file channel is closed.");
        }
        if (state.BufferedAmount <= HighWatermark)
        {
            return;
        }

        var wait = Stopwatch.StartNew();
        while (state.BufferedAmount > LowWatermark)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!state.IsOpen)
            {
                throw new IOException("The file channel is closed.");
            }
            if (wait.Elapsed >= TimeSpan.FromSeconds(30))
            {
                throw new TimeoutException(
                    "The file channel did not drain its send buffer.");
            }

            await Task.Delay(10, cancellationToken)
                .ConfigureAwait(false);
            state = _channelState();
        }
    }
}
