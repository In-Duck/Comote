using System.Runtime.InteropServices;
using Comote.Input;

namespace Host
{
    public sealed class VirtualHidInputBackend : IInputBackend
    {
        private readonly object _sync = new();
        private readonly InputBrokerClient _broker;
        private readonly HidKeyboardState _keyboardState = new();
        private int _screenLeft;
        private int _screenTop;
        private int _screenWidth;
        private int _screenHeight;
        private byte _mouseButtons;
        private ushort _lastAbsoluteX;
        private ushort _lastAbsoluteY;
        private bool _hasLastAbsolutePosition;
        private bool _disposed;

        public VirtualHidInputBackend(
            int screenLeft,
            int screenTop,
            int screenWidth,
            int screenHeight)
        {
            _broker = new InputBrokerClient();
            UpdateScreenBounds(
                screenLeft,
                screenTop,
                screenWidth,
                screenHeight);
        }

        public InputBackendMode Mode => InputBackendMode.VirtualHid;

        public string Name => "Comote Virtual HID (brokered)";

        public InputBackendStatus GetStatus()
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return new InputBackendStatus(
                        Mode,
                        Name,
                        false,
                        "Disposed");
                }

                try
                {
                    var response = _broker.GetStatus();
                    return new InputBackendStatus(
                        Mode,
                        Name,
                        response.IsSuccess,
                        response.Detail);
                }
                catch (Exception ex)
                {
                    return new InputBackendStatus(
                        Mode,
                        Name,
                        false,
                        ex.Message);
                }
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
                _hasLastAbsolutePosition = false;
            }

            Console.WriteLine(
                $"[Input] Virtual HID screen bounds updated: " +
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
                        "The Virtual HID backend is disposed.");
                }

                try
                {
                    return message.Kind switch
                    {
                        HostInputMessageKind.MouseMove =>
                            DispatchMousePosition(message),
                        HostInputMessageKind.MouseDown =>
                            DispatchMouseButton(message, true),
                        HostInputMessageKind.MouseUp =>
                            DispatchMouseButton(message, false),
                        HostInputMessageKind.MouseWheel =>
                            DispatchMouseWheel(
                                message.WheelDelta,
                                horizontal: false),
                        HostInputMessageKind.MouseHorizontalWheel =>
                            DispatchMouseWheel(
                                message.WheelDelta,
                                horizontal: true),
                        HostInputMessageKind.KeyDown =>
                            DispatchKey(message, true),
                        HostInputMessageKind.KeyUp =>
                            DispatchKey(message, false),
                        HostInputMessageKind.TextInput =>
                            InputDispatchResult.NotSupported(
                                "Unicode text packets are not supported " +
                                "by the Virtual HID backend."),
                        HostInputMessageKind.ReleaseAll =>
                            DispatchReleaseAll(),
                        _ => InputDispatchResult.NotSupported(
                            "The input operation is unsupported."),
                    };
                }
                catch (ObjectDisposedException ex)
                {
                    return InputDispatchResult.Unavailable(ex.Message);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"[Input] Virtual HID dispatch failed: " +
                        ex.Message);
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

                _ = DispatchReleaseAll();
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

                _ = DispatchReleaseAll();
                _disposed = true;
                _broker.Dispose();
            }
        }

        private InputDispatchResult DispatchKey(
            HostInputMessage message,
            bool isDown)
        {
            if (message.HasExtendedKeyData &&
                VirtualKeyToHidUsage.IsInjected(message.KeyFlags))
            {
                return InputDispatchResult.Invalid(
                    "Injected keyboard-hook packets are not forwarded.");
            }
            if (!VirtualKeyToHidUsage.TryMap(
                    message.VirtualKey,
                    message.ScanCode,
                    message.KeyFlags,
                    out var hidKey))
            {
                return InputDispatchResult.NotSupported(
                    $"Virtual key 0x{message.VirtualKey:X4} has no " +
                    "supported HID usage.");
            }

            var snapshot = _keyboardState.Capture();
            if (!_keyboardState.TrySetKey(
                    hidKey,
                    isDown,
                    out var error))
            {
                return InputDispatchResult.Rejected(
                    error ?? "The keyboard state was rejected.");
            }

            var response = _broker.SetKeyboardState(
                _keyboardState.Modifiers,
                _keyboardState.GetOrderedKeys());
            if (!response.IsSuccess)
            {
                _keyboardState.Restore(snapshot);
            }
            return FromBrokerResponse(response);
        }

        private InputDispatchResult DispatchMousePosition(
            HostInputMessage message)
        {
            if (!TryMapScreenPoint(
                    message.NormalizedX,
                    message.NormalizedY,
                    out var absoluteX,
                    out var absoluteY))
            {
                return InputDispatchResult.Unavailable(
                    "The Windows virtual desktop metrics are unavailable.");
            }

            var response = _broker.MouseAbsolute(
                _mouseButtons,
                absoluteX,
                absoluteY,
                0,
                0);
            if (response.IsSuccess)
            {
                RecordAbsolutePosition(absoluteX, absoluteY);
            }
            return FromBrokerResponse(response);
        }

        private InputDispatchResult DispatchMouseButton(
            HostInputMessage message,
            bool isDown)
        {
            if (!TryMapScreenPoint(
                    message.NormalizedX,
                    message.NormalizedY,
                    out var absoluteX,
                    out var absoluteY))
            {
                return InputDispatchResult.Unavailable(
                    "The Windows virtual desktop metrics are unavailable.");
            }

            var previousButtons = _mouseButtons;
            var mask = checked((byte)(1 << message.Button));
            _mouseButtons = isDown
                ? (byte)(_mouseButtons | mask)
                : (byte)(_mouseButtons & ~mask);

            var response = _broker.MouseAbsolute(
                _mouseButtons,
                absoluteX,
                absoluteY,
                0,
                0);
            if (response.IsSuccess)
            {
                RecordAbsolutePosition(absoluteX, absoluteY);
            }
            else
            {
                _mouseButtons = previousButtons;
            }
            return FromBrokerResponse(response);
        }

        private InputDispatchResult DispatchMouseWheel(
            int delta,
            bool horizontal)
        {
            if (delta == 0)
            {
                return InputDispatchResult.Success();
            }
            if (!_hasLastAbsolutePosition &&
                !TryGetCurrentPointerPosition(
                    out _lastAbsoluteX,
                    out _lastAbsoluteY))
            {
                return InputDispatchResult.Unavailable(
                    "The current mouse position is unavailable.");
            }
            _hasLastAbsolutePosition = true;

            var remaining = delta;
            while (remaining != 0)
            {
                var chunk = Math.Clamp(remaining, -127, 127);
                var response = _broker.MouseAbsolute(
                    _mouseButtons,
                    _lastAbsoluteX,
                    _lastAbsoluteY,
                    horizontal ? (sbyte)0 : checked((sbyte)chunk),
                    horizontal ? checked((sbyte)chunk) : (sbyte)0);
                if (!response.IsSuccess)
                {
                    return FromBrokerResponse(response);
                }
                remaining -= chunk;
            }

            return InputDispatchResult.Success();
        }

        private InputDispatchResult DispatchReleaseAll()
        {
            BrokerResponse response;
            try
            {
                response = _broker.ReleaseAll();
            }
            finally
            {
                _keyboardState.Clear();
                _mouseButtons = 0;
                _hasLastAbsolutePosition = false;
            }
            return FromBrokerResponse(response);
        }

        private bool TryMapScreenPoint(
            float normalizedX,
            float normalizedY,
            out ushort absoluteX,
            out ushort absoluteY)
        {
            return VirtualHidCoordinateMapper.TryMapNormalizedPoint(
                _screenLeft,
                _screenTop,
                _screenWidth,
                _screenHeight,
                GetSystemMetrics(SmXVirtualScreen),
                GetSystemMetrics(SmYVirtualScreen),
                GetSystemMetrics(SmCxVirtualScreen),
                GetSystemMetrics(SmCyVirtualScreen),
                normalizedX,
                normalizedY,
                out absoluteX,
                out absoluteY);
        }

        private static bool TryGetCurrentPointerPosition(
            out ushort absoluteX,
            out ushort absoluteY)
        {
            absoluteX = 0;
            absoluteY = 0;
            if (!GetCursorPos(out var point))
            {
                return false;
            }

            return VirtualHidCoordinateMapper.TryMapPixelPoint(
                point.X,
                point.Y,
                GetSystemMetrics(SmXVirtualScreen),
                GetSystemMetrics(SmYVirtualScreen),
                GetSystemMetrics(SmCxVirtualScreen),
                GetSystemMetrics(SmCyVirtualScreen),
                out absoluteX,
                out absoluteY);
        }

        private void RecordAbsolutePosition(ushort x, ushort y)
        {
            _lastAbsoluteX = x;
            _lastAbsoluteY = y;
            _hasLastAbsolutePosition = true;
        }

        private static InputDispatchResult FromBrokerResponse(
            BrokerResponse response)
        {
            return response.Status switch
            {
                BrokerStatus.Success => InputDispatchResult.Success(),
                BrokerStatus.InvalidRequest =>
                    InputDispatchResult.Invalid(response.Detail),
                BrokerStatus.Unsupported =>
                    InputDispatchResult.NotSupported(response.Detail),
                BrokerStatus.DriverUnavailable =>
                    InputDispatchResult.Unavailable(response.Detail),
                _ => InputDispatchResult.Rejected(response.Detail),
            };
        }

        private const int SmXVirtualScreen = 76;
        private const int SmYVirtualScreen = 77;
        private const int SmCxVirtualScreen = 78;
        private const int SmCyVirtualScreen = 79;

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT point);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }
    }

    public static class VirtualHidCoordinateMapper
    {
        public const ushort MaximumCoordinate = 32767;

        public static bool TryMapNormalizedPoint(
            int screenLeft,
            int screenTop,
            int screenWidth,
            int screenHeight,
            int virtualLeft,
            int virtualTop,
            int virtualWidth,
            int virtualHeight,
            float normalizedX,
            float normalizedY,
            out ushort absoluteX,
            out ushort absoluteY)
        {
            absoluteX = 0;
            absoluteY = 0;
            if (screenWidth <= 0 ||
                screenHeight <= 0 ||
                !float.IsFinite(normalizedX) ||
                !float.IsFinite(normalizedY) ||
                normalizedX is < 0 or > 1 ||
                normalizedY is < 0 or > 1)
            {
                return false;
            }

            var pixelX = screenLeft +
                normalizedX * Math.Max(1, screenWidth - 1);
            var pixelY = screenTop +
                normalizedY * Math.Max(1, screenHeight - 1);
            return TryMapPixelPoint(
                pixelX,
                pixelY,
                virtualLeft,
                virtualTop,
                virtualWidth,
                virtualHeight,
                out absoluteX,
                out absoluteY);
        }

        public static bool TryMapPixelPoint(
            double pixelX,
            double pixelY,
            int virtualLeft,
            int virtualTop,
            int virtualWidth,
            int virtualHeight,
            out ushort absoluteX,
            out ushort absoluteY)
        {
            absoluteX = 0;
            absoluteY = 0;
            if (virtualWidth <= 0 ||
                virtualHeight <= 0 ||
                !double.IsFinite(pixelX) ||
                !double.IsFinite(pixelY))
            {
                return false;
            }

            absoluteX = MapAxis(
                pixelX,
                virtualLeft,
                virtualWidth);
            absoluteY = MapAxis(
                pixelY,
                virtualTop,
                virtualHeight);
            return true;
        }

        private static ushort MapAxis(
            double pixel,
            int virtualOrigin,
            int virtualLength)
        {
            var mapped = (pixel - virtualOrigin) * MaximumCoordinate /
                Math.Max(1, virtualLength - 1);
            return checked((ushort)Math.Clamp(
                (int)Math.Round(mapped),
                0,
                MaximumCoordinate));
        }
    }
}
