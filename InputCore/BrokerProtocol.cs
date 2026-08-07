using System.Buffers.Binary;
using System.Text;

namespace Comote.Input;

public enum BrokerCommand : ushort
{
    GetStatus = 1,
    Heartbeat = 2,
    SetKeyboardState = 3,
    MouseRelative = 4,
    MouseAbsolute = 5,
    ReleaseAll = 6,
}

public enum BrokerStatus : int
{
    Success = 0,
    InvalidRequest = 1,
    Unsupported = 2,
    DriverUnavailable = 3,
    DriverRejected = 4,
    LeaseRejected = 5,
    InternalError = 6,
}

public readonly record struct BrokerResponse(
    BrokerStatus Status,
    string Detail)
{
    public bool IsSuccess => Status == BrokerStatus.Success;

    public static BrokerResponse Success(string detail = "OK") =>
        new(BrokerStatus.Success, detail);
}

public readonly record struct BrokerFrame(
    uint RequestId,
    BrokerCommand Command,
    byte[] Payload);

public static class BrokerProtocol
{
    public const string PipeName = "Comote.InputBroker.v1";
    public const string ControllerGroupName = "Comote Input Controllers";
    public const uint Magic = 0x31424943;
    public const ushort Version = 1;
    public const int HeaderSize = 16;
    public const int MaximumPayloadSize = 512;
    public const int KeyboardPayloadSize = 8;
    public const int RelativeMousePayloadSize = 8;
    public const int AbsoluteMousePayloadSize = 8;
    public const int HeartbeatIntervalMilliseconds = 500;
    public const int WatchdogTimeoutMilliseconds = 1500;

    private const ushort ResponseFlag = 0x8000;

    public static async ValueTask<BrokerFrame?> ReadFrameAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var header = new byte[HeaderSize];
        var firstRead = await ReadExactlyOrEofAsync(
            stream,
            header,
            cancellationToken).ConfigureAwait(false);
        if (!firstRead)
        {
            return null;
        }

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
        var version = BinaryPrimitives.ReadUInt16LittleEndian(
            header.AsSpan(4));
        var commandValue = BinaryPrimitives.ReadUInt16LittleEndian(
            header.AsSpan(6));
        var requestId = BinaryPrimitives.ReadUInt32LittleEndian(
            header.AsSpan(8));
        var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(
            header.AsSpan(12));

        if (magic != Magic ||
            version != Version ||
            (commandValue & ResponseFlag) != 0 ||
            payloadLength > MaximumPayloadSize)
        {
            throw new InvalidDataException("Invalid broker frame header.");
        }

        var payload = new byte[payloadLength];
        if (payload.Length > 0)
        {
            await ReadExactlyAsync(
                stream,
                payload,
                cancellationToken).ConfigureAwait(false);
        }

        return new BrokerFrame(
            requestId,
            (BrokerCommand)commandValue,
            payload);
    }

    public static async ValueTask WriteRequestAsync(
        Stream stream,
        BrokerFrame frame,
        CancellationToken cancellationToken)
    {
        if (frame.Payload.Length > MaximumPayloadSize)
        {
            throw new ArgumentOutOfRangeException(nameof(frame));
        }

        await WriteFrameAsync(
            stream,
            frame.RequestId,
            (ushort)frame.Command,
            frame.Payload,
            cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask WriteResponseAsync(
        Stream stream,
        uint requestId,
        BrokerCommand command,
        BrokerResponse response,
        CancellationToken cancellationToken)
    {
        var detailBytes = Encoding.UTF8.GetBytes(response.Detail ?? "");
        if (detailBytes.Length > MaximumPayloadSize - sizeof(int))
        {
            Array.Resize(
                ref detailBytes,
                MaximumPayloadSize - sizeof(int));
        }

        var payload = new byte[sizeof(int) + detailBytes.Length];
        BinaryPrimitives.WriteInt32LittleEndian(
            payload,
            (int)response.Status);
        detailBytes.CopyTo(payload.AsSpan(sizeof(int)));

        await WriteFrameAsync(
            stream,
            requestId,
            (ushort)((ushort)command | ResponseFlag),
            payload,
            cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<BrokerResponse> ReadResponseAsync(
        Stream stream,
        uint expectedRequestId,
        BrokerCommand expectedCommand,
        CancellationToken cancellationToken)
    {
        var header = new byte[HeaderSize];
        await ReadExactlyAsync(
            stream,
            header,
            cancellationToken).ConfigureAwait(false);

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
        var version = BinaryPrimitives.ReadUInt16LittleEndian(
            header.AsSpan(4));
        var commandValue = BinaryPrimitives.ReadUInt16LittleEndian(
            header.AsSpan(6));
        var requestId = BinaryPrimitives.ReadUInt32LittleEndian(
            header.AsSpan(8));
        var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(
            header.AsSpan(12));

        var expectedCommandValue =
            (ushort)((ushort)expectedCommand | ResponseFlag);
        if (magic != Magic ||
            version != Version ||
            commandValue != expectedCommandValue ||
            requestId != expectedRequestId ||
            payloadLength < sizeof(int) ||
            payloadLength > MaximumPayloadSize)
        {
            throw new InvalidDataException("Invalid broker response header.");
        }

        var payload = new byte[payloadLength];
        await ReadExactlyAsync(
            stream,
            payload,
            cancellationToken).ConfigureAwait(false);

        var statusValue = BinaryPrimitives.ReadInt32LittleEndian(payload);
        if (!Enum.IsDefined(typeof(BrokerStatus), statusValue))
        {
            throw new InvalidDataException("Invalid broker response status.");
        }

        var detail = Encoding.UTF8.GetString(payload.AsSpan(sizeof(int)));
        return new BrokerResponse((BrokerStatus)statusValue, detail);
    }

    public static byte[] CreateKeyboardPayload(
        byte modifiers,
        ReadOnlySpan<byte> keys)
    {
        if (keys.Length > 6)
        {
            throw new ArgumentOutOfRangeException(nameof(keys));
        }

        var payload = new byte[KeyboardPayloadSize];
        payload[0] = modifiers;
        payload[1] = 0;
        keys.CopyTo(payload.AsSpan(2));
        return payload;
    }

    public static byte[] CreateRelativeMousePayload(
        byte buttons,
        short deltaX,
        short deltaY,
        sbyte verticalWheel,
        sbyte horizontalWheel)
    {
        var payload = new byte[RelativeMousePayloadSize];
        payload[0] = buttons;
        payload[1] = 0;
        BinaryPrimitives.WriteInt16LittleEndian(
            payload.AsSpan(2),
            deltaX);
        BinaryPrimitives.WriteInt16LittleEndian(
            payload.AsSpan(4),
            deltaY);
        payload[6] = unchecked((byte)verticalWheel);
        payload[7] = unchecked((byte)horizontalWheel);
        return payload;
    }

    public static byte[] CreateAbsoluteMousePayload(
        byte buttons,
        ushort x,
        ushort y,
        sbyte verticalWheel,
        sbyte horizontalWheel)
    {
        if (x > 32767 || y > 32767)
        {
            throw new ArgumentOutOfRangeException(
                x > 32767 ? nameof(x) : nameof(y));
        }

        var payload = new byte[AbsoluteMousePayloadSize];
        payload[0] = buttons;
        payload[1] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2), x);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4), y);
        payload[6] = unchecked((byte)verticalWheel);
        payload[7] = unchecked((byte)horizontalWheel);
        return payload;
    }

    private static async ValueTask WriteFrameAsync(
        Stream stream,
        uint requestId,
        ushort command,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        var header = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(
            header.AsSpan(4),
            Version);
        BinaryPrimitives.WriteUInt16LittleEndian(
            header.AsSpan(6),
            command);
        BinaryPrimitives.WriteUInt32LittleEndian(
            header.AsSpan(8),
            requestId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            header.AsSpan(12),
            checked((uint)payload.Length));

        await stream.WriteAsync(
            header,
            cancellationToken).ConfigureAwait(false);
        if (payload.Length > 0)
        {
            await stream.WriteAsync(
                payload,
                cancellationToken).ConfigureAwait(false);
        }
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<bool> ReadExactlyOrEofAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(
                buffer.AsMemory(offset),
                cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (offset == 0)
                {
                    return false;
                }
                throw new EndOfStreamException(
                    "Broker frame ended unexpectedly.");
            }
            offset += read;
        }
        return true;
    }

    private static async ValueTask ReadExactlyAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        if (!await ReadExactlyOrEofAsync(
                stream,
                buffer,
                cancellationToken).ConfigureAwait(false))
        {
            throw new EndOfStreamException(
                "Broker stream closed unexpectedly.");
        }
    }
}
