using System.Runtime.InteropServices;

namespace Host;

public static class SessionManager
{
    private const uint DesktopReadObjects = 0x0001;
    private const uint DesktopCreateWindow = 0x0002;
    private const uint DesktopWriteObjects = 0x0080;
    private const uint DesktopSwitchDesktop = 0x0100;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(
        uint flags, bool inherit, uint desiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetThreadDesktop(IntPtr desktop);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetUserObjectInformation(
        IntPtr handle, int index, IntPtr information,
        uint length, out uint needed);

    public static string? GetInputDesktopName()
    {
        var desktop = OpenInputDesktop(0, false, DesktopReadObjects);
        if (desktop == IntPtr.Zero) return null;
        try
        {
            GetUserObjectInformation(desktop, 2, IntPtr.Zero, 0, out var needed);
            if (needed == 0) return null;
            var buffer = Marshal.AllocHGlobal((int)needed);
            try
            {
                return GetUserObjectInformation(
                    desktop, 2, buffer, needed, out _)
                    ? Marshal.PtrToStringUni(buffer)
                    : null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseDesktop(desktop);
        }
    }
    /// <summary>
    /// Attaches a newly-created worker thread to the desktop currently receiving
    /// input. A LocalSystem process in the interactive session can attach to both
    /// the normal Default desktop and the Winlogon secure desktop.
    /// </summary>
    public static bool SwitchToInputDesktop()
    {
        var desktop = OpenInputDesktop(
            0,
            false,
            DesktopReadObjects | DesktopCreateWindow |
            DesktopWriteObjects | DesktopSwitchDesktop);
        if (desktop == IntPtr.Zero)
        {
            Console.WriteLine(
                $"[Desktop] OpenInputDesktop failed: {Marshal.GetLastWin32Error()}");
            return false;
        }

        if (SetThreadDesktop(desktop)) return true;

        Console.WriteLine(
            $"[Desktop] SetThreadDesktop failed: {Marshal.GetLastWin32Error()}");
        return false;
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern bool WTSRegisterSessionNotification(
        IntPtr window, uint flags);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern bool WTSUnRegisterSessionNotification(IntPtr window);

    public const uint NOTIFY_FOR_ALL_SESSIONS = 1;
    public const int WM_WTSSESSION_CHANGE = 0x02B1;
    public const int WTS_SESSION_LOGON = 0x5;
    public const int WTS_SESSION_LOGOFF = 0x6;
    public const int WTS_SESSION_LOCK = 0x7;
    public const int WTS_SESSION_UNLOCK = 0x8;
    public const int WTS_SESSION_REMOTE_CONNECT = 0x1;
    public const int WTS_SESSION_REMOTE_DISCONNECT = 0x2;
}
