using System.Buffers.Binary;
using System.Text;

namespace Comote.Shared;

public enum MonitoringMessageType : byte
{
    VideoFrame = 1,
    ClientStatus = 2,
    StreamSettings = 3,
    Subscribe = 4,
    KeyframeRequest = 5,
    Error = 6,
}

public enum MonitoringCodec : byte
{
    None = 0,
    H264AnnexB = 1,
    Jpeg = 2,
    WebP = 3,
}

[Flags]
public enum MonitoringPacketFlags : byte
{
    None = 0,
    Keyframe = 1,
    EndOfStream = 2,
}

public readonly record struct MonitoringPacketHeader(
    MonitoringMessageType MessageType,
    MonitoringPacketFlags Flags,
    MonitoringCodec Codec,
    byte FramesPerSecond,
    ushort Width,
    ushort Height,
    string DeviceId,
    long Sequence,
    long CapturedAtUnixMilliseconds,
    int PayloadLength);

public static class MonitoringProtocol
{
    public const uint Magic = 0x434D5448; // CMTH
    public const ushort Version = 1;
    public const int FixedHeaderSize = 36;
    public const int MaxDeviceIdBytes = 128;
    public const int MaxPayloadBytes = 2 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static int GetPacketSize(string deviceId, int payloadLength)
    {
        var idLength = Encoding.UTF8.GetByteCount(deviceId);
        ValidateLengths(idLength, payloadLength);
        return checked(FixedHeaderSize + idLength + payloadLength);
    }

    public static int WritePacket(
        Span<byte> destination,
        in MonitoringPacketHeader header,
        ReadOnlySpan<byte> payload)
    {
        var idLength = Encoding.UTF8.GetByteCount(header.DeviceId);
        ValidateLengths(idLength, payload.Length);
        if (header.PayloadLength != payload.Length)
            throw new ArgumentException("Header payload length does not match the payload.");

        var packetLength = checked(FixedHeaderSize + idLength + payload.Length);
        if (destination.Length < packetLength)
            throw new ArgumentException("Destination buffer is too small.");

        BinaryPrimitives.WriteUInt32BigEndian(destination, Magic);
        BinaryPrimitives.WriteUInt16BigEndian(destination[4..], Version);
        destination[6] = (byte)header.MessageType;
        destination[7] = (byte)header.Flags;
        destination[8] = (byte)header.Codec;
        destination[9] = header.FramesPerSecond;
        BinaryPrimitives.WriteUInt16BigEndian(destination[10..], header.Width);
        BinaryPrimitives.WriteUInt16BigEndian(destination[12..], header.Height);
        BinaryPrimitives.WriteUInt16BigEndian(destination[14..], (ushort)idLength);
        BinaryPrimitives.WriteInt32BigEndian(destination[16..], payload.Length);
        BinaryPrimitives.WriteInt64BigEndian(destination[20..], header.Sequence);
        BinaryPrimitives.WriteInt64BigEndian(
            destination[28..], header.CapturedAtUnixMilliseconds);

        Encoding.UTF8.GetBytes(
            header.DeviceId,
            destination.Slice(FixedHeaderSize, idLength));
        payload.CopyTo(destination[(FixedHeaderSize + idLength)..]);
        return packetLength;
    }

    public static bool TryReadHeader(
        ReadOnlySpan<byte> source,
        out MonitoringPacketHeader header,
        out int payloadOffset,
        out int packetLength,
        out string? error)
    {
        header = default;
        payloadOffset = 0;
        packetLength = 0;
        error = null;

        if (source.Length < FixedHeaderSize)
        {
            error = "Packet header is incomplete.";
            return false;
        }
        if (BinaryPrimitives.ReadUInt32BigEndian(source) != Magic)
        {
            error = "Packet magic is invalid.";
            return false;
        }
        if (BinaryPrimitives.ReadUInt16BigEndian(source[4..]) != Version)
        {
            error = "Monitoring protocol version is not supported.";
            return false;
        }

        var messageType = (MonitoringMessageType)source[6];
        var flags = (MonitoringPacketFlags)source[7];
        var codec = (MonitoringCodec)source[8];
        var framesPerSecond = source[9];
        var width = BinaryPrimitives.ReadUInt16BigEndian(source[10..]);
        var height = BinaryPrimitives.ReadUInt16BigEndian(source[12..]);
        if (!Enum.IsDefined(messageType) || !Enum.IsDefined(codec))
        {
            error = "Packet type or codec is invalid.";
            return false;
        }
        if ((flags & ~(MonitoringPacketFlags.Keyframe |
                       MonitoringPacketFlags.EndOfStream)) != 0)
        {
            error = "Packet flags are invalid.";
            return false;
        }
        if (messageType == MonitoringMessageType.VideoFrame &&
            (framesPerSecond is 0 or > 60 || width is 0 or > 7680 ||
             height is 0 or > 4320 || codec == MonitoringCodec.None))
        {
            error = "Video frame metadata is invalid.";
            return false;
        }

        var idLength = BinaryPrimitives.ReadUInt16BigEndian(source[14..]);
        var payloadLength = BinaryPrimitives.ReadInt32BigEndian(source[16..]);
        if (idLength == 0 || idLength > MaxDeviceIdBytes)
        {
            error = "Device identifier length is invalid.";
            return false;
        }
        if (payloadLength < 0 || payloadLength > MaxPayloadBytes)
        {
            error = "Payload length is invalid.";
            return false;
        }

        packetLength = checked(FixedHeaderSize + idLength + payloadLength);
        if (source.Length < packetLength)
        {
            error = "Packet payload is incomplete.";
            return false;
        }

        string deviceId;
        try
        {
            deviceId = StrictUtf8.GetString(
                source.Slice(FixedHeaderSize, idLength));
        }
        catch (DecoderFallbackException)
        {
            error = "Device identifier is not valid UTF-8.";
            return false;
        }
        payloadOffset = FixedHeaderSize + idLength;
        header = new MonitoringPacketHeader(
            messageType,
            flags,
            codec,
            framesPerSecond,
            width,
            height,
            deviceId,
            BinaryPrimitives.ReadInt64BigEndian(source[20..]),
            BinaryPrimitives.ReadInt64BigEndian(source[28..]),
            payloadLength);
        return true;
    }

    private static void ValidateLengths(int idLength, int payloadLength)
    {
        if (idLength is <= 0 or > MaxDeviceIdBytes)
            throw new ArgumentOutOfRangeException(nameof(idLength));
        if (payloadLength is < 0 or > MaxPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(payloadLength));
    }
}

public readonly record struct MonitoringProfile(
    ushort Width,
    ushort Height,
    byte FramesPerSecond,
    int TargetBitrateBitsPerSecond)
{
    public static MonitoringProfile CreateAutomatic(int hostCount)
    {
        if (hostCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(hostCount));
        if (hostCount <= 25) return new(320, 180, 15, 150_000);
        if (hostCount <= 50) return new(240, 135, 15, 100_000);
        if (hostCount <= 100) return new(160, 90, 15, 80_000);
        return new(160, 90, 10, 60_000);
    }
}
