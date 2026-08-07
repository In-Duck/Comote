using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Comote.Input;

namespace Host;

public enum InboundFileTransferStatus
{
    Continue,
    Completed,
    HashMismatch,
    ProtocolViolation,
}

public readonly record struct InboundFileTransferResult(
    InboundFileTransferStatus Status,
    Guid TransferId = default,
    string? Detail = null,
    string? SavedPath = null);

public sealed class InboundFileTransferReceiver : IDisposable
{
    public const byte FileStartMessage =
        RemoteFileTransferProtocol.StartMessage;
    public const byte FileChunkMessage =
        RemoteFileTransferProtocol.ChunkMessage;
    public const byte FileEndMessage =
        RemoteFileTransferProtocol.EndMessage;
    public const long MaximumFileSize =
        RemoteFileTransferProtocol.MaximumFileSize;
    public const int MaximumChunkSize =
        RemoteFileTransferProtocol.MaximumChunkSize;

    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly object _gate = new();
    private readonly string _temporaryDirectory;
    private readonly string _destinationDirectory;
    private FileStream? _stream;
    private string? _temporaryPath;
    private string? _fileName;
    private byte[]? _expectedHash;
    private Guid _transferId;
    private long _expectedLength;
    private long _receivedLength;
    private bool _disposed;

    public InboundFileTransferReceiver(
        string temporaryDirectory,
        string destinationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        _temporaryDirectory = Path.GetFullPath(temporaryDirectory);
        _destinationDirectory = Path.GetFullPath(destinationDirectory);
    }

    public bool IsReceiving
    {
        get
        {
            lock (_gate)
            {
                return _stream != null;
            }
        }
    }

    public InboundFileTransferResult Process(ReadOnlySpan<byte> message)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (message.Length < 1)
            {
                return Violation("The file message is empty.");
            }

            try
            {
                return message[0] switch
                {
                    FileStartMessage => Start(message),
                    FileChunkMessage => Append(message),
                    FileEndMessage => Complete(message),
                    _ => Violation("The file message type is unsupported."),
                };
            }
            catch (Exception ex) when (
                ex is IOException or
                      UnauthorizedAccessException or
                      CryptographicException)
            {
                AbortCore();
                return new InboundFileTransferResult(
                    InboundFileTransferStatus.ProtocolViolation,
                    Detail: $"File transfer I/O failed: {ex.Message}");
            }
        }
    }

    public void Abort()
    {
        lock (_gate)
        {
            AbortCore();
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

            AbortCore();
            _disposed = true;
        }
    }

    private InboundFileTransferResult Start(ReadOnlySpan<byte> message)
    {
        if (_stream != null)
        {
            return Violation(
                "A second file was started before the first completed.");
        }
        if (!RemoteFileTransferProtocol.TryParseStart(
                message,
                out var transferId,
                out var fileLength,
                out var nameBytes,
                out var expectedHash))
        {
            return Violation("The file-start header is invalid.");
        }

        string fileName;
        try
        {
            fileName = StrictUtf8.GetString(nameBytes);
        }
        catch (DecoderFallbackException)
        {
            return Violation("The file name is not valid UTF-8.");
        }

        if (!IsSafeFileName(fileName))
        {
            return Violation("The file name is unsafe.");
        }

        Directory.CreateDirectory(_temporaryDirectory);
        var temporaryPath = Path.Combine(
            _temporaryDirectory,
            $"incoming-{Guid.NewGuid():N}.part");
        var stream = new FileStream(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan |
            FileOptions.WriteThrough |
            FileOptions.DeleteOnClose);

        _stream = stream;
        _temporaryPath = temporaryPath;
        _fileName = fileName;
        _transferId = transferId;
        _expectedLength = fileLength;
        _receivedLength = 0;
        _expectedHash = expectedHash;

        return new InboundFileTransferResult(
            InboundFileTransferStatus.Continue,
            transferId);
    }
    private InboundFileTransferResult Append(ReadOnlySpan<byte> message)
    {
        if (_stream == null ||
            !RemoteFileTransferProtocol.TryParseChunk(
                message,
                out var transferId,
                out var offset,
                out var data) ||
            transferId != _transferId ||
            offset != _receivedLength)
        {
            return Violation("The file chunk is invalid.");
        }

        if (_receivedLength + data.Length > _expectedLength)
        {
            return Violation(
                "The received data exceeds the declared file size.");
        }

        _stream.Write(data);
        _receivedLength += data.Length;
        return new InboundFileTransferResult(
            InboundFileTransferStatus.Continue,
            transferId);
    }
    private InboundFileTransferResult Complete(ReadOnlySpan<byte> message)
    {
        if (_stream == null ||
            _temporaryPath == null ||
            _fileName == null ||
            _expectedHash == null ||
            !RemoteFileTransferProtocol.TryParseTransferOnly(
                message,
                RemoteFileTransferProtocol.EndMessage,
                out var transferId) ||
            transferId != _transferId ||
            _receivedLength != _expectedLength)
        {
            return Violation("The file-end message is invalid or premature.");
        }

        _stream.Flush(flushToDisk: true);
        _stream.Position = 0;
        var actualHash = SHA256.HashData(_stream);

        if (!CryptographicOperations.FixedTimeEquals(
                actualHash,
                _expectedHash))
        {
            _stream.Dispose();
            _stream = null;
            var failedTransferId = _transferId;
            AbortCore();
            return new InboundFileTransferResult(
                InboundFileTransferStatus.HashMismatch,
                failedTransferId,
                "The received file hash did not match.");
        }

        Directory.CreateDirectory(_destinationDirectory);
        var finalPath = GetUniqueDestinationPath(
            _destinationDirectory,
            _fileName);
        var destinationStagingPath = Path.Combine(
            _destinationDirectory,
            $".comote-{Guid.NewGuid():N}.part");
        try
        {
            _stream.Position = 0;
            using (var destinationStream = new FileStream(
                       destinationStagingPath,
                       FileMode.CreateNew,
                       FileAccess.ReadWrite,
                       FileShare.None,
                       bufferSize: 64 * 1024,
                       FileOptions.SequentialScan |
                       FileOptions.WriteThrough))
            {
                _stream.CopyTo(destinationStream);
                destinationStream.Flush(flushToDisk: true);
                destinationStream.Position = 0;
                var stagedHash = SHA256.HashData(destinationStream);
                if (!CryptographicOperations.FixedTimeEquals(
                        stagedHash,
                        _expectedHash))
                {
                    throw new CryptographicException(
                        "The staged file hash did not match.");
                }
            }

            _stream.Dispose();
            _stream = null;
            File.Move(destinationStagingPath, finalPath);
        }
        catch
        {
            TryDelete(destinationStagingPath);
            throw;
        }

        var completedTransferId = _transferId;
        ResetCore();
        return new InboundFileTransferResult(
            InboundFileTransferStatus.Completed,
            completedTransferId,
            SavedPath: finalPath);
    }

    private InboundFileTransferResult Violation(string detail)
    {
        var failedTransferId = _transferId;
        AbortCore();
        return new InboundFileTransferResult(
            InboundFileTransferStatus.ProtocolViolation,
            failedTransferId,
            detail);
    }

    private void AbortCore()
    {
        try
        {
            _stream?.Dispose();
        }
        finally
        {
            _stream = null;
            if (_temporaryPath != null)
            {
                TryDelete(_temporaryPath);
            }
            ResetCore();
        }
    }

    private void ResetCore()
    {
        _temporaryPath = null;
        _fileName = null;
        _transferId = Guid.Empty;
        _expectedLength = 0;
        _receivedLength = 0;
        if (_expectedHash != null)
        {
            CryptographicOperations.ZeroMemory(_expectedHash);
            _expectedHash = null;
        }
    }

    private static bool IsSafeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            fileName.Length > 255 ||
            fileName.EndsWith(' ') ||
            fileName.EndsWith('.') ||
            !string.Equals(
                Path.GetFileName(fileName),
                fileName,
                StringComparison.Ordinal) ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return false;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName)
            .ToUpperInvariant();
        return stem is not "CON" and not "PRN" and not "AUX" and not "NUL" &&
            !(stem.Length == 4 &&
              (stem.StartsWith("COM", StringComparison.Ordinal) ||
               stem.StartsWith("LPT", StringComparison.Ordinal)) &&
              stem[3] is >= '1' and <= '9');
    }

    private static string GetUniqueDestinationPath(
        string directory,
        string fileName)
    {
        var candidate = Path.Combine(directory, fileName);
        if (!File.Exists(candidate) && !Directory.Exists(candidate))
        {
            return candidate;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var suffix = 1; suffix <= 10_000; suffix++)
        {
            candidate = Path.Combine(
                directory,
                $"{stem} ({suffix}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException(
            "Could not allocate a unique destination file name.");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Cleanup is retried when the session is disposed.
        }
    }
}
