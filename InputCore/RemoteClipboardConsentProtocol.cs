namespace Comote.Input;

public static class RemoteClipboardConsentProtocol
{
    public const byte MessageType = 0x24;
    public const byte Disabled = 0;
    public const byte Enabled = 1;
    public const int MessageSize = 2;

    public static byte[] Create(bool enabled) =>
        [MessageType, enabled ? Enabled : Disabled];

    public static bool TryParse(
        ReadOnlySpan<byte> payload,
        out bool enabled)
    {
        enabled = false;
        if (payload.Length != MessageSize ||
            payload[0] != MessageType ||
            payload[1] is not Disabled and not Enabled)
        {
            return false;
        }

        enabled = payload[1] == Enabled;
        return true;
    }
}
