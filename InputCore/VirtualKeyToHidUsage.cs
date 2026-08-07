namespace Comote.Input;

public static class VirtualKeyToHidUsage
{
    private const uint LlkhfExtended = 0x01;
    private const uint LlkhfInjected = 0x10;
    private const uint LlkhfLowerIlInjected = 0x02;

    public static bool IsInjected(uint hookFlags) =>
        (hookFlags & (LlkhfInjected | LlkhfLowerIlInjected)) != 0;

    public static bool TryMap(
        ushort virtualKey,
        ushort scanCode,
        uint hookFlags,
        out HidKey key)
    {
        var extended = (hookFlags & LlkhfExtended) != 0;
        if (TryMapModifier(virtualKey, scanCode, extended, out key))
        {
            return true;
        }

        if (scanCode != 0 &&
            TryMapScanCode((byte)(scanCode & 0xFF), extended, out var usage))
        {
            key = new HidKey(usage, 0);
            return true;
        }

        if (TryMapVirtualKey(virtualKey, out usage))
        {
            key = new HidKey(usage, 0);
            return true;
        }

        key = default;
        return false;
    }

    private static bool TryMapModifier(
        ushort virtualKey,
        ushort scanCode,
        bool extended,
        out HidKey key)
    {
        var mask = virtualKey switch
        {
            0xA2 => (byte)0x01,
            0xA0 => (byte)0x02,
            0xA4 => (byte)0x04,
            0x5B => (byte)0x08,
            0xA3 => (byte)0x10,
            0xA1 => (byte)0x20,
            0xA5 => (byte)0x40,
            0x5C => (byte)0x80,
            0x11 => extended ? (byte)0x10 : (byte)0x01,
            0x10 => scanCode == 0x36 ? (byte)0x20 : (byte)0x02,
            0x12 => extended ? (byte)0x40 : (byte)0x04,
            _ => (byte)0,
        };
        key = new HidKey(0, mask);
        return mask != 0;
    }

    private static bool TryMapScanCode(
        byte scanCode,
        bool extended,
        out byte usage)
    {
        if (extended)
        {
            usage = scanCode switch
            {
                0x1C => 0x58,
                0x35 => 0x54,
                0x37 => 0x46,
                0x47 => 0x4A,
                0x48 => 0x52,
                0x49 => 0x4B,
                0x4B => 0x50,
                0x4D => 0x4F,
                0x4F => 0x4D,
                0x50 => 0x51,
                0x51 => 0x4E,
                0x52 => 0x49,
                0x53 => 0x4C,
                0x5D => 0x65,
                _ => (byte)0,
            };
            return usage != 0;
        }

        usage = scanCode switch
        {
            0x01 => 0x29,
            >= 0x02 and <= 0x0A => (byte)(0x1E + scanCode - 0x02),
            0x0B => 0x27,
            0x0C => 0x2D,
            0x0D => 0x2E,
            0x0E => 0x2A,
            0x0F => 0x2B,
            0x10 => 0x14,
            0x11 => 0x1A,
            0x12 => 0x08,
            0x13 => 0x15,
            0x14 => 0x17,
            0x15 => 0x1C,
            0x16 => 0x18,
            0x17 => 0x0C,
            0x18 => 0x12,
            0x19 => 0x13,
            0x1A => 0x2F,
            0x1B => 0x30,
            0x1C => 0x28,
            0x1E => 0x04,
            0x1F => 0x16,
            0x20 => 0x07,
            0x21 => 0x09,
            0x22 => 0x0A,
            0x23 => 0x0B,
            0x24 => 0x0D,
            0x25 => 0x0E,
            0x26 => 0x0F,
            0x27 => 0x33,
            0x28 => 0x34,
            0x29 => 0x35,
            0x2B => 0x31,
            0x2C => 0x1D,
            0x2D => 0x1B,
            0x2E => 0x06,
            0x2F => 0x19,
            0x30 => 0x05,
            0x31 => 0x11,
            0x32 => 0x10,
            0x33 => 0x36,
            0x34 => 0x37,
            0x35 => 0x38,
            0x37 => 0x55,
            0x39 => 0x2C,
            0x3A => 0x39,
            >= 0x3B and <= 0x44 => (byte)(0x3A + scanCode - 0x3B),
            0x45 => 0x53,
            0x46 => 0x47,
            0x47 => 0x5F,
            0x48 => 0x60,
            0x49 => 0x61,
            0x4A => 0x56,
            0x4B => 0x5C,
            0x4C => 0x5D,
            0x4D => 0x5E,
            0x4E => 0x57,
            0x4F => 0x59,
            0x50 => 0x5A,
            0x51 => 0x5B,
            0x52 => 0x62,
            0x53 => 0x63,
            0x56 => 0x64,
            0x57 => 0x44,
            0x58 => 0x45,
            _ => (byte)0,
        };
        return usage != 0;
    }

    private static bool TryMapVirtualKey(
        ushort virtualKey,
        out byte usage)
    {
        if (virtualKey is >= 0x41 and <= 0x5A)
        {
            usage = (byte)(0x04 + virtualKey - 0x41);
            return true;
        }
        if (virtualKey is >= 0x31 and <= 0x39)
        {
            usage = (byte)(0x1E + virtualKey - 0x31);
            return true;
        }
        if (virtualKey is >= 0x70 and <= 0x7B)
        {
            usage = (byte)(0x3A + virtualKey - 0x70);
            return true;
        }
        if (virtualKey is >= 0x60 and <= 0x69)
        {
            usage = virtualKey == 0x60
                ? (byte)0x62
                : (byte)(0x59 + virtualKey - 0x61);
            return true;
        }

        usage = virtualKey switch
        {
            0x30 => 0x27,
            0x08 => 0x2A,
            0x09 => 0x2B,
            0x0D => 0x28,
            0x1B => 0x29,
            0x20 => 0x2C,
            0x14 => 0x39,
            0x21 => 0x4B,
            0x22 => 0x4E,
            0x23 => 0x4D,
            0x24 => 0x4A,
            0x25 => 0x50,
            0x26 => 0x52,
            0x27 => 0x4F,
            0x28 => 0x51,
            0x2C => 0x46,
            0x2D => 0x49,
            0x2E => 0x4C,
            0x5D => 0x65,
            0x6A => 0x55,
            0x6B => 0x57,
            0x6D => 0x56,
            0x6E => 0x63,
            0x6F => 0x54,
            0x90 => 0x53,
            0x91 => 0x47,
            0x15 => 0x90,
            0x19 => 0x91,
            0xBA => 0x33,
            0xBB => 0x2E,
            0xBC => 0x36,
            0xBD => 0x2D,
            0xBE => 0x37,
            0xBF => 0x38,
            0xC0 => 0x35,
            0xDB => 0x2F,
            0xDC => 0x31,
            0xDD => 0x30,
            0xDE => 0x34,
            0xE2 => 0x64,
            _ => (byte)0,
        };
        return usage != 0;
    }
}
