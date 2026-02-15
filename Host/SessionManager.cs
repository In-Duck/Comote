using System;
using System.Runtime.InteropServices;

namespace Host
{
    public static class SessionManager
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetThreadDesktop(IntPtr hDesktop);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseDesktop(IntPtr hDesktop);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr OpenDesktop(string lpszDesktop, uint dwFlags, bool fInherit, uint dwDesiredAccess);

        private const uint DESKTOP_ALL_ACCESS = 0x01FF;

        /// <summary>
        /// 현재 서비스 스레드를 "활성 입력 데스크톱"(사용자 바탕화면 또는 로그인 화면)으로 전환합니다.
        /// DXGI 캡처 전에 이 함수를 호출해야 세션 0 격리를 우회할 수 있습니다.
        /// </summary>
        public static bool SwitchToInputDesktop()
        {
            try
            {
                IntPtr hDesktop = OpenInputDesktop(0, false, DESKTOP_ALL_ACCESS);
                if (hDesktop == IntPtr.Zero)
                {
                    // OpenInputDesktop이 실패하면 Winlogon 데스크톱 직접 시도
                    hDesktop = OpenDesktop("Winlogon", 0, false, DESKTOP_ALL_ACCESS);
                }

                if (hDesktop != IntPtr.Zero)
                {
                    bool result = SetThreadDesktop(hDesktop);
                    // CloseDesktop(hDesktop); // 주의: SetThreadDesktop 이후 바로 닫으면 안 됨
                    return result;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Session] SwitchToInputDesktop error: {ex.Message}");
            }
            return false;
        }

        [DllImport("wtsapi32.dll", SetLastError = true)]
        public static extern bool WTSRegisterSessionNotification(IntPtr hWnd, uint dwFlags);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        public static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);

        public const uint NOTIFY_FOR_ALL_SESSIONS = 1;

        // 세션 변경 통지 메시지
        public const int WM_WTSSESSION_CHANGE = 0x02B1;
        public const int WTS_SESSION_LOGON = 0x5;
        public const int WTS_SESSION_LOGOFF = 0x6;
        public const int WTS_SESSION_LOCK = 0x7;
        public const int WTS_SESSION_UNLOCK = 0x8;
        public const int WTS_SESSION_REMOTE_CONNECT = 0x1;
        public const int WTS_SESSION_REMOTE_DISCONNECT = 0x2;
    }
}
