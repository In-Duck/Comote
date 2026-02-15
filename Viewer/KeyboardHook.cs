using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Viewer
{
    /// <summary>
    /// WH_KEYBOARD_LL 저수준 키보드 훅으로 모든 키 입력을 캡처합니다.
    /// OS 레벨에서 동작하므로 한/영, Win, Alt+Tab 등 시스템 키도 캡처 가능합니다.
    /// </summary>
    public class KeyboardHook : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;  // Alt 조합 키
        private const int WM_SYSKEYUP = 0x0105;

        private IntPtr _hookId = IntPtr.Zero;
        private LowLevelKeyboardProc _proc;
        private bool _isCapturing;

        /// <summary>
        /// 키 이벤트 콜백: (ushort virtualKeyCode, bool isDown)
        /// </summary>
        public event Action<ushort, bool>? OnKeyEvent;

        public KeyboardHook()
        {
            _proc = HookCallback;
        }

        /// <summary>
        /// 캡처 시작. Viewer 창이 활성화된 동안만 키를 포워딩합니다.
        /// </summary>
        public void Start()
        {
            if (_hookId != IntPtr.Zero) return;

            using var curProcess = Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule!;
            _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc,
                GetModuleHandle(curModule.ModuleName!), 0);

            if (_hookId == IntPtr.Zero)
            {
                Console.WriteLine("[KeyboardHook] Failed to set hook!");
            }
            else
            {
                Console.WriteLine("[KeyboardHook] Low-level keyboard hook installed");
            }
        }

        /// <summary>
        /// 캡처 활성화/비활성화 (Viewer 창 포커스 여부에 따라)
        /// </summary>
        public bool IsCapturing
        {
            get => _isCapturing;
            set
            {
                _isCapturing = value;
                Console.WriteLine($"[KeyboardHook] Capturing: {value}");
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && _isCapturing)
            {
                int msg = (int)wParam;
                var hookStruct = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                ushort vk = (ushort)hookStruct.vkCode;

                bool isDown = (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN);
                bool isUp = (msg == WM_KEYUP || msg == WM_SYSKEYUP);

                if (isDown || isUp)
                {
                    OnKeyEvent?.Invoke(vk, isDown);

                    // 시스템 키(Alt+Tab, Win, 한/영 등)를 로컬에서 처리하지 않도록 차단
                    // Viewer 창이 포커스 중일 때만 차단하므로 안전
                    return (IntPtr)1;
                }
            }

            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
                Console.WriteLine("[KeyboardHook] Hook removed");
            }
        }

        // =========================================================
        // P/Invoke
        // =========================================================

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn,
            IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode,
            IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);
    }
}
