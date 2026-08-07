using System.Runtime.InteropServices;
using System.Text;

namespace Comote.VirtualHidE2E;

internal static class NativeMethods
{
    public const int WmInput = 0x00FF;
    public const uint RidInput = 0x10000003;
    public const uint RidiDeviceName = 0x20000007;
    public const uint RimTypeMouse = 0;
    public const uint RimTypeKeyboard = 1;
    public const uint RidevRemove = 0x00000001;
    public const uint RidevNoLegacy = 0x00000030;
    public const uint RidevInputSink = 0x00000100;
    public const ushort RiKeyBreak = 0x0001;
    public const ushort MouseMoveAbsolute = 0x0001;
    public const ushort RiMouseLeftButtonDown = 0x0001;
    public const ushort RiMouseLeftButtonUp = 0x0002;
    public const ushort RiMouseRightButtonDown = 0x0004;
    public const ushort RiMouseRightButtonUp = 0x0008;
    public const ushort RiMouseMiddleButtonDown = 0x0010;
    public const ushort RiMouseMiddleButtonUp = 0x0020;
    public const ushort RiMouseButton4Down = 0x0040;
    public const ushort RiMouseButton4Up = 0x0080;
    public const ushort RiMouseButton5Down = 0x0100;
    public const ushort RiMouseButton5Up = 0x0200;
    public const ushort RiMouseWheel = 0x0400;
    public const ushort RiMouseHorizontalWheel = 0x0800;

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RawInputHeader
    {
        public uint Type;
        public uint Size;
        public IntPtr Device;
        public IntPtr WParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RawKeyboard
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VirtualKey;
        public uint Message;
        public uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    public struct RawMouse
    {
        [FieldOffset(0)]
        public ushort Flags;

        [FieldOffset(4)]
        public uint Buttons;

        [FieldOffset(8)]
        public uint RawButtons;

        [FieldOffset(12)]
        public int LastX;

        [FieldOffset(16)]
        public int LastY;

        [FieldOffset(20)]
        public uint ExtraInformation;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterRawInputDevices(
        [In] RawInputDevice[] devices,
        uint deviceCount,
        uint structureSize);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetRawInputData(
        IntPtr rawInput,
        uint command,
        IntPtr data,
        ref uint size,
        uint headerSize);

    [DllImport(
        "user32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    public static extern uint GetRawInputDeviceInfo(
        IntPtr device,
        uint command,
        StringBuilder? data,
        ref uint size);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(
        IntPtr window,
        out uint processId);
}
