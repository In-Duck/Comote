using System.Runtime.InteropServices;

namespace Host
{
    /// <summary>
    /// Implements the current Comote input protocol with the Windows SendInput API.
    /// This backend must run in the interactive user's Windows session.
    /// </summary>
    public sealed class SendInputBackend : IInputBackend
    {
        public const byte MsgMouseMove = 0x01;
        public const byte MsgMouseDown = 0x02;
        public const byte MsgMouseUp = 0x03;
        public const byte MsgMouseWheel = 0x04;
        public const byte MsgKeyDown = 0x10;
        public const byte MsgKeyUp = 0x11;
        public const byte MsgTextInput = 0x12;

        public const byte ButtonLeft = 0;
        public const byte ButtonRight = 1;
        public const byte ButtonMiddle = 2;

        private const ushort VkHangul = 0x15;
        private const ushort VkHanja = 0x19;

        private readonly object _sync = new();
        private readonly HashSet<ushort> _pressedKeys = new();
        private readonly HashSet<byte> _pressedMouseButtons = new();
        private int _screenWidth;
        private int _screenLeft;
        private int _screenTop;
        private int _screenHeight;
        private bool _disposed;

        public SendInputBackend(int screenWidth, int screenHeight)
        {
            UpdateScreenSize(screenWidth, screenHeight);
        }

        public InputBackendMode Mode => InputBackendMode.SendInput;

        public string Name => "Windows SendInput";

        public InputBackendStatus GetStatus()
        {
            lock (_sync)
            {
                return new InputBackendStatus(
                    Mode,
                    Name,
                    !_disposed,
                    _disposed
                        ? "Disposed"
                        : $"Screen={_screenLeft},{_screenTop} {_screenWidth}x{_screenHeight}");
            }
        }

        public void UpdateScreenSize(int width, int height)
        {
            UpdateScreenBounds(0, 0, width, height);
        }

        public void UpdateScreenBounds(
            int left,
            int top,
            int width,
            int height)
        {
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

            lock (_sync)
            {
                ThrowIfDisposed();
                _screenLeft = left;
                _screenTop = top;
                _screenWidth = width;
                _screenHeight = height;
            }

            Console.WriteLine($"[Input] Backend screen bounds updated: {left},{top} {width}x{height}");
        }

        public void ProcessMessage(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);
            if (data.Length == 0) return;

            lock (_sync)
            {
                ThrowIfDisposed();

                try
                {
                    switch (data[0])
                    {
                        case MsgReleaseAll:
                            ReleaseAllInputs();
                            break;

                        case MsgMouseMove when data.Length >= 9:
                            MoveMouse(
                                BitConverter.ToSingle(data, 1),
                                BitConverter.ToSingle(data, 5));
                            break;

                        case MsgMouseDown when data.Length >= 10:
                            SetMouseButton(
                                data[1],
                                true,
                                BitConverter.ToSingle(data, 2),
                                BitConverter.ToSingle(data, 6));
                            break;

                        case MsgMouseUp when data.Length >= 10:
                            SetMouseButton(
                                data[1],
                                false,
                                BitConverter.ToSingle(data, 2),
                                BitConverter.ToSingle(data, 6));
                            break;

                        case MsgMouseWheel when data.Length >= 5:
                            MouseWheel(BitConverter.ToInt32(data, 1));
                            break;

                        case MsgKeyDown when data.Length >= 3:
                            SetKey(BitConverter.ToUInt16(data, 1), true);
                            break;

                        case MsgKeyUp when data.Length >= 3:
                            SetKey(BitConverter.ToUInt16(data, 1), false);
                            break;

                        case MsgTextInput when data.Length >= 3:
                            SendUnicodeCharacter(BitConverter.ToUInt16(data, 1));
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Input] ProcessMessage failed: {ex.Message}");
                }
            }
        }

        public void ReleaseAllInputs()
        {
            lock (_sync)
            {
                if (_disposed) return;

                var keys = _pressedKeys.ToArray();
                var buttons = _pressedMouseButtons.ToArray();

                foreach (var key in keys)
                {
                    SetKey(key, false);
                }

                foreach (var button in buttons)
                {
                    SetMouseButton(button, false, null, null);
                }

                _pressedKeys.Clear();
                _pressedMouseButtons.Clear();

                if (keys.Length > 0 || buttons.Length > 0)
                {
                    Console.WriteLine(
                        $"[Input] Released {keys.Length} key(s) and " +
                        $"{buttons.Length} mouse button(s).");
                }
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                ReleaseAllInputs();
                _disposed = true;
            }
        }

        private void MoveMouse(float ratioX, float ratioY)
        {
            if (!float.IsFinite(ratioX) || !float.IsFinite(ratioY)) return;

            var normalizedX = Math.Clamp(ratioX, 0f, 1f);
            var normalizedY = Math.Clamp(ratioY, 0f, 1f);
            var virtualLeft = GetSystemMetrics(SmXVirtualScreen);
            var virtualTop = GetSystemMetrics(SmYVirtualScreen);
            var virtualWidth = Math.Max(
                1, GetSystemMetrics(SmCxVirtualScreen));
            var virtualHeight = Math.Max(
                1, GetSystemMetrics(SmCyVirtualScreen));
            var pixelX = _screenLeft +
                normalizedX * Math.Max(1, _screenWidth - 1);
            var pixelY = _screenTop +
                normalizedY * Math.Max(1, _screenHeight - 1);
            var absoluteX = (int)Math.Round(
                (pixelX - virtualLeft) * 65535d /
                Math.Max(1, virtualWidth - 1));
            var absoluteY = (int)Math.Round(
                (pixelY - virtualTop) * 65535d /
                Math.Max(1, virtualHeight - 1));

            Send(
                new INPUT
                {
                    type = InputMouse,
                    u = new InputUnion
                    {
                        mi = new MOUSEINPUT
                        {
                            dx = absoluteX,
                            dy = absoluteY,
                            dwFlags = MouseEventMove | MouseEventAbsolute |
                                MouseEventVirtualDesk,
                        },
                    },
                },
                "mouse move");
        }

        private void SetMouseButton(
            byte button,
            bool isDown,
            float? ratioX,
            float? ratioY)
        {
            if (ratioX.HasValue && ratioY.HasValue)
            {
                MoveMouse(ratioX.Value, ratioY.Value);
            }

            var flags = button switch
            {
                ButtonLeft => isDown ? MouseEventLeftDown : MouseEventLeftUp,
                ButtonRight => isDown ? MouseEventRightDown : MouseEventRightUp,
                ButtonMiddle => isDown ? MouseEventMiddleDown : MouseEventMiddleUp,
                _ => 0u,
            };

            if (flags == 0) return;

            if (!Send(
                    new INPUT
                    {
                        type = InputMouse,
                        u = new InputUnion
                        {
                            mi = new MOUSEINPUT { dwFlags = flags },
                        },
                    },
                    $"mouse button {button} {(isDown ? "down" : "up")}"))
            {
                return;
            }

            if (isDown)
            {
                _pressedMouseButtons.Add(button);
            }
            else
            {
                _pressedMouseButtons.Remove(button);
            }
        }

        private void MouseWheel(int delta)
        {
            Send(
                new INPUT
                {
                    type = InputMouse,
                    u = new InputUnion
                    {
                        mi = new MOUSEINPUT
                        {
                            mouseData = unchecked((uint)delta),
                            dwFlags = MouseEventWheel,
                        },
                    },
                },
                "mouse wheel");
        }

        private void SetKey(ushort virtualKeyCode, bool isDown)
        {
            if (virtualKeyCode == VkHangul)
            {
                if (isDown) ToggleImeMode();
                return;
            }

            if (virtualKeyCode == VkHanja)
            {
                if (PostHanjaKey(virtualKeyCode, isDown))
                {
                    TrackKey(virtualKeyCode, isDown);
                }

                return;
            }

            var scanCode = (ushort)MapVirtualKey(
                virtualKeyCode,
                MapVkVirtualKeyToScanCode);

            var succeeded = Send(
                new INPUT
                {
                    type = InputKeyboard,
                    u = new InputUnion
                    {
                        ki = new KEYBDINPUT
                        {
                            wVk = virtualKeyCode,
                            wScan = scanCode,
                            dwFlags = isDown ? 0u : KeyEventKeyUp,
                        },
                    },
                },
                $"key 0x{virtualKeyCode:X2} {(isDown ? "down" : "up")}");

            if (succeeded)
            {
                TrackKey(virtualKeyCode, isDown);
            }
        }

        private void TrackKey(ushort virtualKeyCode, bool isDown)
        {
            if (isDown)
            {
                _pressedKeys.Add(virtualKeyCode);
            }
            else
            {
                _pressedKeys.Remove(virtualKeyCode);
            }
        }

        private static bool PostHanjaKey(ushort virtualKeyCode, bool isDown)
        {
            var window = GetForegroundWindow();
            if (window == IntPtr.Zero) return false;

            var scanCode = MapVirtualKey(
                virtualKeyCode,
                MapVkVirtualKeyToScanCode);
            var lParam = isDown
                ? (IntPtr)(1 | (scanCode << 16))
                : (IntPtr)unchecked(
                    (int)0xC0000001 | (int)(scanCode << 16));

            return PostMessage(
                window,
                isDown ? WindowMessageKeyDown : WindowMessageKeyUp,
                (IntPtr)virtualKeyCode,
                lParam);
        }

        private static void ToggleImeMode()
        {
            var window = GetForegroundWindow();
            if (window == IntPtr.Zero) return;

            var imeWindow = ImmGetDefaultIMEWnd(window);
            if (imeWindow == IntPtr.Zero) return;

            var currentMode = SendMessage(
                imeWindow,
                WindowMessageImeControl,
                (IntPtr)ImeGetConversionMode,
                IntPtr.Zero);
            var newMode = (uint)(int)currentMode ^ ImeModeNative;

            SendMessage(
                imeWindow,
                WindowMessageImeControl,
                (IntPtr)ImeSetConversionMode,
                (IntPtr)newMode);
        }

        private static void SendUnicodeCharacter(ushort character)
        {
            var inputs = new[]
            {
                new INPUT
                {
                    type = InputKeyboard,
                    u = new InputUnion
                    {
                        ki = new KEYBDINPUT
                        {
                            wScan = character,
                            dwFlags = KeyEventUnicode,
                        },
                    },
                },
                new INPUT
                {
                    type = InputKeyboard,
                    u = new InputUnion
                    {
                        ki = new KEYBDINPUT
                        {
                            wScan = character,
                            dwFlags = KeyEventUnicode | KeyEventKeyUp,
                        },
                    },
                },
            };

            SendInputs((uint)inputs.Length, inputs, "unicode character");
        }

        private static bool Send(INPUT input, string operation)
        {
            return SendInputs(1, new[] { input }, operation);
        }

        private static bool SendInputs(
            uint count,
            INPUT[] inputs,
            string operation)
        {
            var sent = SendInput(count, inputs, Marshal.SizeOf<INPUT>());
            if (sent == count) return true;

            var error = Marshal.GetLastWin32Error();
            Console.WriteLine(
                $"[Input] SendInput failed during {operation}: " +
                $"sent={sent}/{count}, win32={error}");
            return false;
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        private const uint InputMouse = 0;
        private const uint InputKeyboard = 1;

        private const uint MouseEventMove = 0x0001;
        private const uint MouseEventLeftDown = 0x0002;
        private const uint MouseEventLeftUp = 0x0004;
        private const uint MouseEventRightDown = 0x0008;
        private const uint MouseEventRightUp = 0x0010;
        private const uint MouseEventMiddleDown = 0x0020;
        private const uint MouseEventMiddleUp = 0x0040;
        private const uint MouseEventWheel = 0x0800;
        private const uint MouseEventAbsolute = 0x8000;
        private const uint MouseEventVirtualDesk = 0x4000;
        private const int SmXVirtualScreen = 76;
        private const int SmYVirtualScreen = 77;
        private const int SmCxVirtualScreen = 78;
        private const int SmCyVirtualScreen = 79;
        private const byte MsgReleaseAll = 0x13;


        private const uint KeyEventKeyUp = 0x0002;
        private const uint KeyEventUnicode = 0x0004;

        private const uint MapVkVirtualKeyToScanCode = 0;
        private const uint WindowMessageKeyDown = 0x0100;
        private const uint WindowMessageKeyUp = 0x0101;
        private const uint WindowMessageImeControl = 0x0283;
        private const uint ImeModeNative = 0x0001;
        private const int ImeGetConversionMode = 0x0001;
        private const int ImeSetConversionMode = 0x0002;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(
            uint nInputs,
            INPUT[] pInputs,
            int cbSize);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(
            uint code,
            uint mapType);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessage(
            IntPtr window,
            uint message,
            IntPtr wParam,
            IntPtr lParam);

        [DllImport("imm32.dll")]
        private static extern IntPtr ImmGetDefaultIMEWnd(IntPtr window);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(
            IntPtr window,
            uint message,
            IntPtr wParam,
            IntPtr lParam);

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
