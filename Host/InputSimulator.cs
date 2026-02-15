using System;
using System.Runtime.InteropServices;

namespace Host
{
    /// <summary>
    /// Windows SendInput API를 사용하여 키보드/마우스 입력을 재현합니다.
    /// Viewer에서 바이너리 프로토콜로 수신된 입력을 시스템 레벨에서 실행합니다.
    /// 
    /// 바이너리 프로토콜 형식:
    ///   [0]    = 메시지 타입
    ///   [1..N] = 페이로드 (타입별 고정 크기)
    /// 
    /// 메시지 타입:
    ///   0x01 = 마우스 이동     (9바이트: type + float x + float y)
    ///   0x02 = 마우스 버튼 다운 (10바이트: type + byte button + float x + float y)
    ///   0x03 = 마우스 버튼 업   (10바이트: type + byte button + float x + float y)
    ///   0x04 = 마우스 휠       (5바이트: type + int delta)
    ///   0x10 = 키보드 다운     (3바이트: type + ushort keyCode)
    ///   0x11 = 키보드 업       (3바이트: type + ushort keyCode)
    ///   0x12 = Unicode 문자   (3바이트: type + ushort charCode)
    /// </summary>
    public class InputSimulator
    {
        // --- 메시지 타입 상수 ---
        public const byte MSG_MOUSE_MOVE = 0x01;
        public const byte MSG_MOUSE_DOWN = 0x02;
        public const byte MSG_MOUSE_UP   = 0x03;
        public const byte MSG_MOUSE_WHEEL = 0x04;
        public const byte MSG_KEY_DOWN   = 0x10;
        public const byte MSG_KEY_UP     = 0x11;
        public const byte MSG_TEXT_INPUT = 0x12;

        // --- 마우스 버튼 상수 ---
        public const byte BUTTON_LEFT   = 0;
        public const byte BUTTON_RIGHT  = 1;
        public const byte BUTTON_MIDDLE = 2;

        // --- 화면 해상도 (마우스 좌표 변환용) ---
        private int _screenWidth;
        private int _screenHeight;

        public InputSimulator(int screenWidth, int screenHeight)
        {
            _screenWidth = screenWidth;
            _screenHeight = screenHeight;
        }

        public void UpdateScreenSize(int w, int h)
        {
            _screenWidth = w;
            _screenHeight = h;
            Console.WriteLine($"[Input] Screen size updated: {w}x{h}");
        }

        /// <summary>
        /// 바이너리 메시지를 파싱하여 해당 입력을 실행합니다.
        /// </summary>
        public void ProcessMessage(byte[] data)
        {
            try 
            {
                if (data == null || data.Length < 1) return;

                byte msgType = data[0];
                // 키보드 이벤트 디버그 로그
                if (msgType == MSG_KEY_DOWN || msgType == MSG_KEY_UP)
                {
                    ushort vk = BitConverter.ToUInt16(data, 1);
                    Console.WriteLine($"[Input] Key {(msgType == MSG_KEY_DOWN ? "DOWN" : "UP")}: VK=0x{vk:X2} ({vk})");
                }
                switch (msgType)
                {
                    case MSG_MOUSE_MOVE:
                        if (data.Length >= 9)
                        {
                            float mx = BitConverter.ToSingle(data, 1);
                            float my = BitConverter.ToSingle(data, 5);
                            MoveMouse(mx, my);
                        }
                        break;

                    case MSG_MOUSE_DOWN:
                        if (data.Length >= 10)
                        {
                            byte button = data[1];
                            float dx = BitConverter.ToSingle(data, 2);
                            float dy = BitConverter.ToSingle(data, 6);
                            MouseButton(button, true, dx, dy);
                        }
                        break;

                    case MSG_MOUSE_UP:
                        if (data.Length >= 10)
                        {
                            byte button = data[1];
                            float ux = BitConverter.ToSingle(data, 2);
                            float uy = BitConverter.ToSingle(data, 6);
                            MouseButton(button, false, ux, uy);
                        }
                        break;

                    case MSG_MOUSE_WHEEL:
                        if (data.Length >= 5)
                        {
                            int delta = BitConverter.ToInt32(data, 1);
                            MouseWheel(delta);
                        }
                        break;

                    case MSG_KEY_DOWN:
                        if (data.Length >= 3)
                        {
                            ushort keyCode = BitConverter.ToUInt16(data, 1);
                            KeyPress(keyCode, true);
                        }
                        break;

                    case MSG_KEY_UP:
                        if (data.Length >= 3)
                        {
                            ushort keyCode = BitConverter.ToUInt16(data, 1);
                            KeyPress(keyCode, false);
                        }
                        break;

                    case MSG_TEXT_INPUT:
                        if (data.Length >= 3)
                        {
                            ushort charCode = BitConverter.ToUInt16(data, 1);
                            UnicodeChar(charCode);
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Input] ProcessMessage Error: {ex.Message}");
            }
        }

        private void MoveMouse(float ratioX, float ratioY)
        {
            // 비율(0~1) → 절대 좌표(0~65535) 변환 (MOUSEEVENTF_ABSOLUTE 기준)
            int absX = (int)(ratioX * 65535);
            int absY = (int)(ratioY * 65535);

            var input = new INPUT
            {
                type = INPUT_MOUSE,
                u = new InputUnion
                {
                    mi = new MOUSEINPUT
                    {
                        dx = absX,
                        dy = absY,
                        dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE
                    }
                }
            };
            SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        }

        private void MouseButton(byte button, bool isDown, float ratioX, float ratioY)
        {
            // 먼저 마우스 위치 이동
            MoveMouse(ratioX, ratioY);

            uint flags = 0;
            switch (button)
            {
                case BUTTON_LEFT:
                    flags = isDown ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP;
                    break;
                case BUTTON_RIGHT:
                    flags = isDown ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP;
                    break;
                case BUTTON_MIDDLE:
                    flags = isDown ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP;
                    break;
            }

            var input = new INPUT
            {
                type = INPUT_MOUSE,
                u = new InputUnion
                {
                    mi = new MOUSEINPUT
                    {
                        dwFlags = flags
                    }
                }
            };
            SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        }

        private void MouseWheel(int delta)
        {
            var input = new INPUT
            {
                type = INPUT_MOUSE,
                u = new InputUnion
                {
                    mi = new MOUSEINPUT
                    {
                        mouseData = (uint)delta,
                        dwFlags = MOUSEEVENTF_WHEEL
                    }
                }
            };
            SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        }

        private const ushort VK_HANGUL = 0x15;
        private const ushort VK_HANJA  = 0x19;

        private void KeyPress(ushort virtualKeyCode, bool isDown)
        {
            // VK_HANGUL: SendInput/PostMessage로는 IME 전환 불가 → IMM32 API로 직접 토글
            if (virtualKeyCode == VK_HANGUL && isDown)
            {
                ToggleImeMode();
                return;
            }
            // VK_HANGUL key up은 무시 (토글은 down에서만)
            if (virtualKeyCode == VK_HANGUL && !isDown) return;

            // VK_HANJA: PostMessage로 전달
            if (virtualKeyCode == VK_HANJA)
            {
                IntPtr hwnd = GetForegroundWindow();
                if (hwnd != IntPtr.Zero)
                {
                    uint scanCode = MapVirtualKey(virtualKeyCode, MAPVK_VK_TO_VSC);
                    IntPtr lParam = (IntPtr)(1 | (scanCode << 16));
                    if (!isDown)
                    {
                        lParam = (IntPtr)unchecked((int)0xC0000001 | (int)(scanCode << 16));
                    }
                    uint msg = isDown ? WM_KEYDOWN : WM_KEYUP;
                    PostMessage(hwnd, msg, (IntPtr)virtualKeyCode, lParam);
                }
                return;
            }

            // 일반 키: SendInput 사용
            ushort sc = (ushort)MapVirtualKey(virtualKeyCode, MAPVK_VK_TO_VSC);

            var input = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = virtualKeyCode,
                        wScan = sc,
                        dwFlags = isDown ? 0u : KEYEVENTF_KEYUP
                    }
                }
            };
            SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        }

        /// <summary>
        /// IME 기본 창에 WM_IME_CONTROL 메시지를 보내 한/영 모드를 토글합니다.
        /// ImmGetContext는 다른 프로세스에서 NULL을 반환하므로,
        /// ImmGetDefaultIMEWnd로 IME 창을 찾아 메시지를 보내는 방식을 사용합니다.
        /// </summary>
        private void ToggleImeMode()
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                Console.WriteLine("[Input] ToggleImeMode: No foreground window");
                return;
            }

            IntPtr imeWnd = ImmGetDefaultIMEWnd(hwnd);
            if (imeWnd != IntPtr.Zero)
            {
                // WM_IME_CONTROL(0x283)로 현재 변환 모드 조회
                IntPtr currentMode = SendMessage(imeWnd, WM_IME_CONTROL,
                    (IntPtr)IMC_GETCONVERSIONMODE, IntPtr.Zero);

                // IME_CMODE_NATIVE (0x01) 토글: 영문 ↔ 한글
                uint newMode = (uint)(int)currentMode ^ IME_CMODE_NATIVE;
                SendMessage(imeWnd, WM_IME_CONTROL,
                    (IntPtr)IMC_SETCONVERSIONMODE, (IntPtr)newMode);

                string mode = (newMode & IME_CMODE_NATIVE) != 0 ? "한글" : "영문";
                Console.WriteLine($"[Input] IME toggled to: {mode} (imeWnd=0x{imeWnd:X})");
            }
            else
            {
                Console.WriteLine("[Input] ToggleImeMode: No IME window found");
            }
        }

        /// <summary>
        /// Unicode 문자를 직접 입력합니다 (한글 등 IME 조합 문자).
        /// KEYEVENTF_UNICODE 플래그를 사용하여 wScan에 문자 코드를 넣습니다.
        /// </summary>
        private void UnicodeChar(ushort charCode)
        {
            // Key Down
            var down = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = charCode,
                        dwFlags = KEYEVENTF_UNICODE
                    }
                }
            };
            // Key Up
            var up = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = charCode,
                        dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP
                    }
                }
            };
            SendInput(2, new[] { down, up }, Marshal.SizeOf<INPUT>());
        }

        // =========================================================
        // Windows API (P/Invoke)
        // =========================================================

        private const uint INPUT_MOUSE = 0;
        private const uint INPUT_KEYBOARD = 1;

        private const uint MOUSEEVENTF_MOVE       = 0x0001;
        private const uint MOUSEEVENTF_LEFTDOWN   = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP     = 0x0004;
        private const uint MOUSEEVENTF_RIGHTDOWN  = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP    = 0x0010;
        private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        private const uint MOUSEEVENTF_MIDDLEUP   = 0x0040;
        private const uint MOUSEEVENTF_WHEEL      = 0x0800;
        private const uint MOUSEEVENTF_ABSOLUTE   = 0x8000;

        private const uint KEYEVENTF_KEYUP   = 0x0002;
        private const uint KEYEVENTF_UNICODE = 0x0004;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        private const uint MAPVK_VK_TO_VSC = 0;
        private const uint WM_KEYDOWN = 0x0100;
        private const uint WM_KEYUP   = 0x0101;

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        // IMM32 API (한/영 전환용)
        private const uint IME_CMODE_NATIVE = 0x0001;
        private const uint WM_IME_CONTROL = 0x0283;
        private const int IMC_GETCONVERSIONMODE = 0x0001;
        private const int IMC_SETCONVERSIONMODE = 0x0002;

        [DllImport("imm32.dll")]
        private static extern IntPtr ImmGetDefaultIMEWnd(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion u;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }
    }
}
