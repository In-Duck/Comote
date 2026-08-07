using System.Buffers.Binary;

namespace Comote.Input;

public readonly record struct RemoteFileStart(
    Guid TransferId,
    uint FileSize,
    string FileName,
    byte[] Sha256);

public readonly record struct RemoteFileChunk(
    Guid TransferId,
    uint Offset,
    byte[] Data);

public static class RemoteFileTransferProtocol
{
    public const byte StartMessage = 0x30;
    public const byte ChunkMessage = 0x31;
    public const byte EndMessage = 0x32;
    public const byte AcknowledgedMessage = 0x33;
    public const byte HashMismatchMessage = 0x34;
    public const byte RejectedMessage = 0x35;

    public const int TransferIdSize = 16;
    public const int Sha256Size = 32;
    public const int MaximumFileNameSize = 255;
    public const int MaximumChunkSize = 14 * 1024;
    public const long MaximumFileSize = 256L * 1024 * 1024;

    public static byte[] CreateStart(
        Guid transferId,
        uint fileSize,
        ReadOnlySpan<byte> fileNameUtf8,
        ReadOnlySpan<byte> sha256)
    {
        ValidateTransferId(transferId);
        if (fileSize > MaximumFileSize ||
            fileNameUtf8.Length is < 1 or > MaximumFileNameSize ||
            sha256.Length != Sha256Size)
        {
            throw new ArgumentOutOfRangeException(nameof(fileNameUtf8));
        }

        var message = new byte[
            1 + TransferIdSize + sizeof(uint) + sizeof(ushort) +
            fileNameUtf8.Length + Sha256Size];
        message[0] = StartMessage;
        WriteTransferId(message.AsSpan(1), transferId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            message.AsSpan(1 + TransferIdSize),
            fileSize);
        BinaryPrimitives.WriteUInt16LittleEndian(
            message.AsSpan(1 + TransferIdSize + sizeof(uint)),
            checked((ushort)fileNameUtf8.Length));
        fileNameUtf8.CopyTo(
            message.AsSpan(
                1 + TransferIdSize + sizeof(uint) + sizeof(ushort)));
        sha256.CopyTo(message.AsSpan(message.Length - Sha256Size));
        return message;
    }

    public static bool TryParseStart(
        ReadOnlySpan<byte> message,
        out Guid transferId,
        out uint fileSize,
        out byte[] fileNameUtf8,
        out byte[] sha256)
    {
        transferId = Guid.Empty;
        fileSize = 0;
        fileNameUtf8 = [];
        sha256 = [];
        var minimumSize =
            1 + TransferIdSize + sizeof(uint) + sizeof(ushort) +
            1 + Sha256Size;
        if (message.Length < minimumSize ||
            message[0] != StartMessage ||
            !TryReadTransferId(message.Slice(1), out transferId))
        {
            return false;
        }

        fileSize = BinaryPrimitives.ReadUInt32LittleEndian(
            message.Slice(1 + TransferIdSize, sizeof(uint)));
        var nameSize = BinaryPrimitives.ReadUInt16LittleEndian(
            message.Slice(
                1 + TransferIdSize + sizeof(uint),
                sizeof(ushort)));
        var expectedSize =
            1 + TransferIdSize + sizeof(uint) + sizeof(ushort) +
            nameSize + Sha256Size;
        if (fileSize > MaximumFileSize ||
            nameSize is < 1 or > MaximumFileNameSize ||
            message.Length != expectedSize)
        {
            transferId = Guid.Empty;
            return false;
        }

        fileNameUtf8 = message.Slice(
            1 + TransferIdSize + sizeof(uint) + sizeof(ushort),
            nameSize).ToArray();
        sha256 = message.Slice(
            message.Length - Sha256Size,
            Sha256Size).ToArray();
        return true;
    }

    public static byte[] CreateChunk(
        Guid transferId,
        uint offset,
        ReadOnlySpan<byte> data)
    {
        ValidateTransferId(transferId);
        if (data.Length is < 1 or > MaximumChunkSize)
        {
            throw new ArgumentOutOfRangeException(nameof(data));
        }

        var message = new byte[
            1 + TransferIdSize + sizeof(uint) + sizeof(ushort) +
            data.Length];
        message[0] = ChunkMessage;
        WriteTransferId(message.AsSpan(1), transferId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            message.AsSpan(1 + TransferIdSize),
            offset);
        BinaryPrimitives.WriteUInt16LittleEndian(
            message.AsSpan(1 + TransferIdSize + sizeof(uint)),
            checked((ushort)data.Length));
        data.CopyTo(
            message.AsSpan(
                1 + TransferIdSize + sizeof(uint) + sizeof(ushort)));
        return message;
    }

    public static bool TryParseChunk(
        ReadOnlySpan<byte> message,
        out Guid transferId,
        out uint offset,
        out byte[] data)
    {
        transferId = Guid.Empty;
        offset = 0;
        data = [];
        var headerSize =
            1 + TransferIdSize + sizeof(uint) + sizeof(ushort);
        if (message.Length < headerSize + 1 ||
            message[0] != ChunkMessage ||
            !TryReadTransferId(message.Slice(1), out transferId))
        {
            return false;
        }

        offset = BinaryPrimitives.ReadUInt32LittleEndian(
            message.Slice(1 + TransferIdSize, sizeof(uint)));
        var dataSize = BinaryPrimitives.ReadUInt16LittleEndian(
            message.Slice(
                1 + TransferIdSize + sizeof(uint),
                sizeof(ushort)));
        if (dataSize is < 1 or > MaximumChunkSize ||
            message.Length != headerSize + dataSize)
        {
            transferId = Guid.Empty;
            return false;
        }

        data = message.Slice(headerSize, dataSize).ToArray();
        return true;
    }

    public static byte[] CreateEnd(Guid transferId) =>
        CreateTransferOnlyMessage(EndMessage, transferId);

    public static byte[] CreateAcknowledged(Guid transferId) =>
        CreateTransferOnlyMessage(AcknowledgedMessage, transferId);

    public static byte[] CreateHashMismatch(Guid transferId) =>
        CreateTransferOnlyMessage(HashMismatchMessage, transferId);

    public static byte[] CreateRejected(Guid transferId, byte reason)
    {
        ValidateTransferId(transferId);
        var message = new byte[2 + TransferIdSize];
        message[0] = RejectedMessage;
        WriteTransferId(message.AsSpan(1), transferId);
        message[^1] = reason;
        return message;
    }

    public static bool TryParseTransferOnly(
        ReadOnlySpan<byte> message,
        byte expectedType,
        out Guid transferId)
    {
        transferId = Guid.Empty;
        return message.Length == 1 + TransferIdSize &&
            message[0] == expectedType &&
            TryReadTransferId(message.Slice(1), out transferId);
    }

    public static bool TryParseRejected(
        ReadOnlySpan<byte> message,
        out Guid transferId,
        out byte reason)
    {
        transferId = Guid.Empty;
        reason = 0;
        if (message.Length != 2 + TransferIdSize ||
            message[0] != RejectedMessage ||
            !TryReadTransferId(message.Slice(1), out transferId))
        {
            return false;
        }

        reason = message[^1];
        return true;
    }

    private static byte[] CreateTransferOnlyMessage(
        byte type,
        Guid transferId)
    {
        ValidateTransferId(transferId);
        var message = new byte[1 + TransferIdSize];
        message[0] = type;
        WriteTransferId(message.AsSpan(1), transferId);
        return message;
    }

    private static bool TryReadTransferId(
        ReadOnlySpan<byte> source,
        out Guid transferId)
    {
        transferId = Guid.Empty;
        if (source.Length < TransferIdSize)
        {
            return false;
        }

        transferId = new Guid(source.Slice(0, TransferIdSize));
        return transferId != Guid.Empty;
    }

    private static void WriteTransferId(
        Span<byte> destination,
        Guid transferId)
    {
        if (!transferId.TryWriteBytes(
                destination.Slice(0, TransferIdSize)))
        {
            throw new InvalidOperationException(
                "Could not encode the transfer identifier.");
        }
    }

    private static void ValidateTransferId(Guid transferId)
    {
        if (transferId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(transferId));
        }
    }
}
