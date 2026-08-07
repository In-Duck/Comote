using System.Runtime.InteropServices;

namespace Host
{
    /// <summary>
    /// Implements the Comote input protocol with the Windows SendInput API.
    /// This backend must run in the interactive user's Windows session.
    /// </summary>
    public sealed class SendInputBackend : IInputBackend
    {
        private const ushort VkHangul = 0x15;
        private const ushort VkHanja = 0x19;
        private const uint LlkhfExtended = 0x01;

        private readonly object _sync = new();
        private readonly Dictionary<ushort, PressedKey> _pressedKeys = [];
        private readonly HashSet<byte> _pressedMouseButtons = [];
        private int _screenWidth;
        private int _screenLeft;
        private int _screenTop;
        private int _screenHeight;
        private bool _disposed;

        public SendInputBackend(int screenWidth, int screenHeight)
        {
            UpdateScreenBounds(0, 0, screenWidth, screenHeight);
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
                        : $"Screen={_screenLeft},{_screenTop} " +
                            $"{_screenWidth}x{_screenHeight}");
            }
        }

        public void UpdateScreenBounds(
            int left,
            int top,
            int width,
            int height)
        {
            if (width <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(width));
            }
            if (height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(height));
            }

            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _screenLeft = left;
                _screenTop = top;
                _screenWidth = width;
                _screenHeight = height;
            }

            Console.WriteLine(
                $"[Input] Backend screen bounds updated: " +
                $"{left},{top} {width}x{height}");
        }

        public InputDispatchResult ProcessMessage(ReadOnlySpan<byte> data)
        {
            var parsed = HostInputProtocol.Parse(data, out var message);
            if (!parsed.IsAccepted)
            {
                return parsed;
            }

            lock (_sync)
            {
                if (_disposed)
                {
                    return InputDispatchResult.Unavailable(
                        "The SendInput backend is disposed.");
                }

                try
                {
                    var succeeded = message.Kind switch
                    {
                        HostInputMessageKind.MouseMove =>
                            MoveMouse(
                                message.NormalizedX,
                                message.NormalizedY),
                        HostInputMessageKind.MouseDown =>
                            SetMouseButton(
                                message.Button,
                                true,
                                message.NormalizedX,
                                message.NormalizedY),
                        HostInputMessageKind.MouseUp =>
                            SetMouseButton(
                                message.Button,
                                false,
                                message.NormalizedX,
                                message.NormalizedY),
                        HostInputMessageKind.MouseWheel =>
                            MouseWheel(message.WheelDelta, horizontal: false),
                        HostInputMessageKind.MouseHorizontalWheel =>
                            MouseWheel(message.WheelDelta, horizontal: true),
                        HostInputMessageKind.KeyDown =>
                            SetKey(message, true),
                        HostInputMessageKind.KeyUp =>
                            SetKey(message, false),
                        HostInputMessageKind.TextInput =>
                            SendUnicodeCharacter(message.Character),
                        HostInputMessageKind.ReleaseAll =>
                            ReleaseAllCore(),
                        _ => false,
                    };

                    return succeeded
                        ? InputDispatchResult.Success()
                        : InputDispatchResult.Rejected(
                            "Windows did not accept the input operation.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"[Input] ProcessMessage failed: {ex.Message}");
                    return InputDispatchResult.Rejected(ex.Message);
                }
            }
        }

        public void ReleaseAllInputs()
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _ = ReleaseAllCore();
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _ = ReleaseAllCore();
                _disposed = true;
            }
        }

        private bool ReleaseAllCore()
        {
            var keys = _pressedKeys.ToArray();
            var buttons = _pressedMouseButtons.ToArray();
            var succeeded = true;

            foreach (var (virtualKey, pressedKey) in keys)
            {
                var message = new HostInputMessage(
                    HostInputMessageKind.KeyUp,
                    0,
                    0,
                    0,
                    0,
                    virtualKey,
                    pressedKey.ScanCode,
                    pressedKey.Flags,
                    0,
                    pressedKey.HasExtendedData);
                succeeded &= SetKey(message, false);
            }

            foreach (var button in buttons)
            {
                succeeded &= SetMouseButton(
                    button,
                    false,
                    null,
                    null);
            }

            _pressedKeys.Clear();
            _pressedMouseButtons.Clear();

            if (keys.Length > 0 || buttons.Length > 0)
            {
                Console.WriteLine(
                    $"[Input] Released {keys.Length} key(s) and " +
                    $"{buttons.Length} mouse button(s).");
            }

            return succeeded;
        }

        private bool MoveMouse(float normalizedX, float normalizedY)
        {
            if (!float.IsFinite(normalizedX) ||
                !float.IsFinite(normalizedY) ||
                normalizedX is < 0 or > 1 ||
                normalizedY is < 0 or > 1)
            {
                return false;
            }

            var virtualLeft = GetSystemMetrics(SmXVirtualScreen);
            var virtualTop = GetSystemMetrics(SmYVirtualScreen);
            var virtualWidth = Math.Max(
                1,
                GetSystemMetrics(SmCxVirtualScreen));
            var virtualHeight = Math.Max(
                1,
                GetSystemMetrics(SmCyVirtualScreen));
            var pixelX = _screenLeft +
                normalizedX * Math.Max(1, _screenWidth - 1);
            var pixelY = _screenTop +
                normalizedY * Math.Max(1, _screenHeight - 1);
            var absoluteX = Math.Clamp(
                (int)Math.Round(
                    (pixelX - virtualLeft) * 65535d /
                    Math.Max(1, virtualWidth - 1)),
                0,
                65535);
            var absoluteY = Math.Clamp(
                (int)Math.Round(
                    (pixelY - virtualTop) * 65535d /
                    Math.Max(1, virtualHeight - 1)),
                0,
                65535);

            return Send(
                new INPUT
                {
                    type = InputMouse,
                    u = new InputUnion
                    {
                        mi = new MOUSEINPUT
                        {
                            dx = absoluteX,
                            dy = absoluteY,
                            dwFlags = MouseEventMove |
                                MouseEventAbsolute |
                                MouseEventVirtualDesk,
                        },
                    },
                },
                "mouse move");
        }

        private bool SetMouseButton(
            byte button,
            bool isDown,
            float? normalizedX,
            float? normalizedY)
        {
            if (normalizedX.HasValue && normalizedY.HasValue &&
                !MoveMouse(normalizedX.Value, normalizedY.Value))
            {
                return false;
            }

            var (flags, mouseData) = button switch
            {
                HostInputProtocol.LeftButton =>
                    (isDown ? MouseEventLeftDown : MouseEventLeftUp, 0u),
                HostInputProtocol.RightButton =>
                    (isDown ? MouseEventRightDown : MouseEventRightUp, 0u),
                HostInputProtocol.MiddleButton =>
                    (isDown ? MouseEventMiddleDown : MouseEventMiddleUp, 0u),
                HostInputProtocol.XButton1 =>
                    (isDown ? MouseEventXDown : MouseEventXUp, XButton1Data),
                HostInputProtocol.XButton2 =>
                    (isDown ? MouseEventXDown : MouseEventXUp, XButton2Data),
                _ => (0u, 0u),
            };

            if (flags == 0)
            {
                return false;
            }

            if (!Send(
                    new INPUT
                    {
                        type = InputMouse,
                        u = new InputUnion
                        {
                            mi = new MOUSEINPUT
                            {
                                mouseData = mouseData,
                                dwFlags = flags,
                            },
                        },
                    },
                    $"mouse button {button} " +
                    (isDown ? "down" : "up")))
            {
                return false;
            }

            if (isDown)
            {
                _pressedMouseButtons.Add(button);
            }
            else
            {
                _pressedMouseButtons.Remove(button);
            }

            return true;
        }

        private static bool MouseWheel(int delta, bool horizontal)
        {
            return Send(
                new INPUT
                {
                    type = InputMouse,
                    u = new InputUnion
                    {
                        mi = new MOUSEINPUT
                        {
                            mouseData = unchecked((uint)delta),
                            dwFlags = horizontal
                                ? MouseEventHorizontalWheel
                                : MouseEventWheel,
                        },
                    },
                },
                "mouse wheel");
        }

        private bool SetKey(HostInputMessage message, bool isDown)
        {
            var virtualKeyCode = message.VirtualKey;
            if (virtualKeyCode == VkHangul)
            {
                return !isDown || ToggleImeMode();
            }

            if (virtualKeyCode == VkHanja)
            {
                var posted = PostHanjaKey(virtualKeyCode, isDown);
                if (posted)
                {
                    TrackKey(message, isDown);
                }
                return posted;
            }

            var scanCode = message.HasExtendedKeyData
                ? message.ScanCode
                : (ushort)MapVirtualKey(
                    virtualKeyCode,
                    MapVkVirtualKeyToScanCode);
            var useScanCode = message.HasExtendedKeyData && scanCode != 0;
            var flags = isDown ? 0u : KeyEventKeyUp;
            if (useScanCode)
            {
                flags |= KeyEventScanCode;
                if ((message.KeyFlags & LlkhfExtended) != 0)
                {
                    flags |= KeyEventExtendedKey;
                }
            }

            var succeeded = Send(
                new INPUT
                {
                    type = InputKeyboard,
                    u = new InputUnion
                    {
                        ki = new KEYBDINPUT
                        {
                            wVk = useScanCode ? (ushort)0 : virtualKeyCode,
                            wScan = scanCode,
                            dwFlags = flags,
                        },
                    },
                },
                $"key 0x{virtualKeyCode:X2} " +
                (isDown ? "down" : "up"));

            if (succeeded)
            {
                TrackKey(message, isDown);
            }
            return succeeded;
        }

        private void TrackKey(HostInputMessage message, bool isDown)
        {
            if (isDown)
            {
                _pressedKeys[message.VirtualKey] = new PressedKey(
                    message.ScanCode,
                    message.KeyFlags,
                    message.HasExtendedKeyData);
            }
            else
            {
                _pressedKeys.Remove(message.VirtualKey);
            }
        }

        private static bool PostHanjaKey(
            ushort virtualKeyCode,
            bool isDown)
        {
            var window = GetForegroundWindow();
            if (window == IntPtr.Zero)
            {
                return false;
            }

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

        private static bool ToggleImeMode()
        {
            var window = GetForegroundWindow();
            if (window == IntPtr.Zero)
            {
                return false;
            }

            var imeWindow = ImmGetDefaultIMEWnd(window);
            if (imeWindow == IntPtr.Zero)
            {
                return false;
            }

            var currentMode = SendMessage(
                imeWindow,
                WindowMessageImeControl,
                (IntPtr)ImeGetConversionMode,
                IntPtr.Zero);
            var newMode = (uint)(int)currentMode ^ ImeModeNative;

            _ = SendMessage(
                imeWindow,
                WindowMessageImeControl,
                (IntPtr)ImeSetConversionMode,
                (IntPtr)newMode);
            return true;
        }

        private static bool SendUnicodeCharacter(ushort character)
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

            return SendInputs(
                checked((uint)inputs.Length),
                inputs,
                "unicode character");
        }

        private static bool Send(INPUT input, string operation) =>
            SendInputs(1, [input], operation);

        private static bool SendInputs(
            uint count,
            INPUT[] inputs,
            string operation)
        {
            var sent = SendInput(count, inputs, Marshal.SizeOf<INPUT>());
            if (sent == count)
            {
                return true;
            }

            var error = Marshal.GetLastWin32Error();
            Console.WriteLine(
                $"[Input] SendInput failed during {operation}: " +
                $"sent={sent}/{count}, win32={error}");
            return false;
        }

        private readonly record struct PressedKey(
            ushort ScanCode,
            uint Flags,
            bool HasExtendedData);

        private const uint InputMouse = 0;
        private const uint InputKeyboard = 1;

        private const uint MouseEventMove = 0x0001;
        private const uint MouseEventLeftDown = 0x0002;
        private const uint MouseEventLeftUp = 0x0004;
        private const uint MouseEventRightDown = 0x0008;
        private const uint MouseEventRightUp = 0x0010;
        private const uint MouseEventMiddleDown = 0x0020;
        private const uint MouseEventMiddleUp = 0x0040;
        private const uint MouseEventXDown = 0x0080;
        private const uint MouseEventXUp = 0x0100;
        private const uint MouseEventWheel = 0x0800;
        private const uint MouseEventHorizontalWheel = 0x1000;
        private const uint MouseEventVirtualDesk = 0x4000;
        private const uint MouseEventAbsolute = 0x8000;
        private const uint XButton1Data = 1;
        private const uint XButton2Data = 2;

        private const uint KeyEventExtendedKey = 0x0001;
        private const uint KeyEventKeyUp = 0x0002;
        private const uint KeyEventUnicode = 0x0004;
        private const uint KeyEventScanCode = 0x0008;

        private const int SmXVirtualScreen = 76;
        private const int SmYVirtualScreen = 77;
        private const int SmCxVirtualScreen = 78;
        private const int SmCyVirtualScreen = 79;
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
