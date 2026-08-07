using System.Buffers.Binary;

namespace Comote.Input;

public enum RemoteChannelKind : byte
{
    Input = 1,
    File = 2,
}

public enum RemoteInputMode : byte
{
    SendInput = 1,
    VirtualHid = 2,
}

public readonly record struct RemoteAuthHello(
    RemoteChannelKind Channel,
    byte[] Token,
    Guid ClientSessionId);

public readonly record struct RemoteAuthAccepted(
    RemoteChannelKind Channel,
    RemoteInputMode InputMode,
    uint Capabilities);

public static class RemoteControlProtocol
{
    public const byte AuthHelloMessage = 0x40;
    public const byte AuthAcceptedMessage = 0x41;
    public const byte AuthRejectedMessage = 0x42;
    public const byte SecureEnvelopeMessage = 0x43;

    public const byte Version = 1;
    public const int TokenSize = 32;
    public const int ClientSessionIdSize = 16;
    public const int AuthHelloSize =
        4 + TokenSize + ClientSessionIdSize;
    public const int AuthAcceptedSize = 8;
    public const int SecureEnvelopeHeaderSize = 11;
    public const int MaximumPayloadSize = 48 * 1024;

    public static byte[] CreateAuthHello(
        RemoteChannelKind channel,
        ReadOnlySpan<byte> token,
        Guid clientSessionId)
    {
        ValidateChannel(channel);
        if (token.Length != TokenSize)
        {
            throw new ArgumentOutOfRangeException(nameof(token));
        }

        var message = new byte[AuthHelloSize];
        message[0] = AuthHelloMessage;
        message[1] = Version;
        message[2] = (byte)channel;
        message[3] = TokenSize;
        token.CopyTo(message.AsSpan(4, TokenSize));
        if (!clientSessionId.TryWriteBytes(
                message.AsSpan(4 + TokenSize, ClientSessionIdSize)))
        {
            throw new InvalidOperationException(
                "Could not encode the client session identifier.");
        }
        return message;
    }

    public static bool TryParseAuthHello(
        ReadOnlySpan<byte> message,
        out RemoteAuthHello hello)
    {
        hello = default;
        if (message.Length != AuthHelloSize ||
            message[0] != AuthHelloMessage ||
            message[1] != Version ||
            message[3] != TokenSize ||
            !Enum.IsDefined(typeof(RemoteChannelKind), message[2]))
        {
            return false;
        }

        var token = message.Slice(4, TokenSize).ToArray();
        var sessionId = new Guid(
            message.Slice(4 + TokenSize, ClientSessionIdSize));
        if (sessionId == Guid.Empty)
        {
            System.Security.Cryptography.CryptographicOperations
                .ZeroMemory(token);
            return false;
        }

        hello = new RemoteAuthHello(
            (RemoteChannelKind)message[2],
            token,
            sessionId);
        return true;
    }

    public static byte[] CreateAuthAccepted(
        RemoteChannelKind channel,
        RemoteInputMode inputMode,
        uint capabilities)
    {
        ValidateChannel(channel);
        if (!Enum.IsDefined(typeof(RemoteInputMode), inputMode))
        {
            throw new ArgumentOutOfRangeException(nameof(inputMode));
        }

        var message = new byte[AuthAcceptedSize];
        message[0] = AuthAcceptedMessage;
        message[1] = Version;
        message[2] = (byte)channel;
        message[3] = (byte)inputMode;
        BinaryPrimitives.WriteUInt32LittleEndian(
            message.AsSpan(4),
            capabilities);
        return message;
    }

    public static bool TryParseAuthAccepted(
        ReadOnlySpan<byte> message,
        out RemoteAuthAccepted accepted)
    {
        accepted = default;
        if (message.Length != AuthAcceptedSize ||
            message[0] != AuthAcceptedMessage ||
            message[1] != Version ||
            !Enum.IsDefined(typeof(RemoteChannelKind), message[2]) ||
            !Enum.IsDefined(typeof(RemoteInputMode), message[3]))
        {
            return false;
        }

        accepted = new RemoteAuthAccepted(
            (RemoteChannelKind)message[2],
            (RemoteInputMode)message[3],
            BinaryPrimitives.ReadUInt32LittleEndian(message.Slice(4)));
        return true;
    }

    public static byte[] CreateAuthRejected(byte reasonCode)
    {
        return
        [
            AuthRejectedMessage,
            Version,
            reasonCode,
        ];
    }

    public static byte[] WrapPayload(
        ulong sequence,
        ReadOnlySpan<byte> payload)
    {
        if (sequence == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }
        if (payload.Length is < 1 or > MaximumPayloadSize)
        {
            throw new ArgumentOutOfRangeException(nameof(payload));
        }

        var message = new byte[
            SecureEnvelopeHeaderSize + payload.Length];
        message[0] = SecureEnvelopeMessage;
        BinaryPrimitives.WriteUInt64LittleEndian(
            message.AsSpan(1),
            sequence);
        BinaryPrimitives.WriteUInt16LittleEndian(
            message.AsSpan(9),
            checked((ushort)payload.Length));
        payload.CopyTo(message.AsSpan(SecureEnvelopeHeaderSize));
        return message;
    }

    public static bool TryUnwrapPayload(
        ReadOnlySpan<byte> message,
        ulong expectedSequence,
        out byte[] payload)
    {
        payload = [];
        if (expectedSequence == 0 ||
            message.Length < SecureEnvelopeHeaderSize + 1 ||
            message[0] != SecureEnvelopeMessage)
        {
            return false;
        }

        var sequence = BinaryPrimitives.ReadUInt64LittleEndian(
            message.Slice(1));
        var payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(
            message.Slice(9));
        if (sequence != expectedSequence ||
            payloadLength is < 1 or > MaximumPayloadSize ||
            message.Length != SecureEnvelopeHeaderSize + payloadLength)
        {
            return false;
        }

        payload = message.Slice(
            SecureEnvelopeHeaderSize,
            payloadLength).ToArray();
        return true;
    }

    private static void ValidateChannel(RemoteChannelKind channel)
    {
        if (!Enum.IsDefined(typeof(RemoteChannelKind), channel))
        {
            throw new ArgumentOutOfRangeException(nameof(channel));
        }
    }
}
