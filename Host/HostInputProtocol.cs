using System.Buffers.Binary;
using Comote.Input;

namespace Host
{
    public enum HostInputMessageKind
    {
        MouseMove,
        MouseDown,
        MouseUp,
        MouseWheel,
        MouseHorizontalWheel,
        KeyDown,
        KeyUp,
        TextInput,
        ReleaseAll,
    }

    public readonly record struct HostInputMessage(
        HostInputMessageKind Kind,
        byte Button,
        float NormalizedX,
        float NormalizedY,
        int WheelDelta,
        ushort VirtualKey,
        ushort ScanCode,
        uint KeyFlags,
        ushort Character,
        bool HasExtendedKeyData);

    public static class HostInputProtocol
    {
        public const byte MouseMoveMessage = 0x01;
        public const byte MouseDownMessage = 0x02;
        public const byte MouseUpMessage = 0x03;
        public const byte MouseWheelMessage = 0x04;
        public const byte MouseHorizontalWheelMessage = 0x05;
        public const byte KeyDownMessage = 0x10;
        public const byte KeyUpMessage = 0x11;
        public const byte TextInputMessage = 0x12;
        public const byte ReleaseAllMessage = 0x13;

        public const byte LeftButton = 0;
        public const byte RightButton = 1;
        public const byte MiddleButton = 2;
        public const byte XButton1 = 3;
        public const byte XButton2 = 4;

        public const int MouseMoveSize = 9;
        public const int MouseButtonSize = 10;
        public const int MouseWheelSize = 5;
        public const int LegacyKeySize = 3;
        public const int ExtendedKeySize = 9;
        public const int TextInputSize = 3;
        public const int ReleaseAllSize = 1;
        public const int MaximumWheelMagnitude = 32767;

        public static InputDispatchResult Parse(
            ReadOnlySpan<byte> data,
            out HostInputMessage message)
        {
            message = default;
            if (data.Length == 0)
            {
                return InputDispatchResult.Invalid(
                    "The input message is empty.");
            }

            switch (data[0])
            {
                case MouseMoveMessage:
                    if (data.Length != MouseMoveSize)
                    {
                        return WrongSize("mouse move", MouseMoveSize);
                    }
                    if (!TryReadCoordinates(
                            data.Slice(1),
                            out var moveX,
                            out var moveY))
                    {
                        return InvalidCoordinates();
                    }
                    message = new HostInputMessage(
                        HostInputMessageKind.MouseMove,
                        0,
                        moveX,
                        moveY,
                        0,
                        0,
                        0,
                        0,
                        0,
                        false);
                    return InputDispatchResult.Success();

                case MouseDownMessage:
                case MouseUpMessage:
                    if (data.Length != MouseButtonSize)
                    {
                        return WrongSize("mouse button", MouseButtonSize);
                    }
                    if (data[1] > XButton2)
                    {
                        return InputDispatchResult.Invalid(
                            "The mouse button is out of range.");
                    }
                    if (!TryReadCoordinates(
                            data.Slice(2),
                            out var buttonX,
                            out var buttonY))
                    {
                        return InvalidCoordinates();
                    }
                    message = new HostInputMessage(
                        data[0] == MouseDownMessage
                            ? HostInputMessageKind.MouseDown
                            : HostInputMessageKind.MouseUp,
                        data[1],
                        buttonX,
                        buttonY,
                        0,
                        0,
                        0,
                        0,
                        0,
                        false);
                    return InputDispatchResult.Success();

                case MouseWheelMessage:
                case MouseHorizontalWheelMessage:
                    if (data.Length != MouseWheelSize)
                    {
                        return WrongSize("mouse wheel", MouseWheelSize);
                    }
                    var wheelDelta =
                        BinaryPrimitives.ReadInt32LittleEndian(data.Slice(1));
                    if (Math.Abs((long)wheelDelta) > MaximumWheelMagnitude)
                    {
                        return InputDispatchResult.Invalid(
                            "The mouse-wheel delta is out of range.");
                    }
                    message = new HostInputMessage(
                        data[0] == MouseWheelMessage
                            ? HostInputMessageKind.MouseWheel
                            : HostInputMessageKind.MouseHorizontalWheel,
                        0,
                        0,
                        0,
                        wheelDelta,
                        0,
                        0,
                        0,
                        0,
                        false);
                    return InputDispatchResult.Success();

                case KeyDownMessage:
                case KeyUpMessage:
                    if (data.Length != LegacyKeySize &&
                        data.Length != ExtendedKeySize)
                    {
                        return InputDispatchResult.Invalid(
                            "A key message must contain exactly 3 or 9 bytes.");
                    }
                    var hasExtendedData = data.Length == ExtendedKeySize;
                    var keyFlags = hasExtendedData
                        ? BinaryPrimitives.ReadUInt32LittleEndian(
                            data.Slice(5))
                        : 0;
                    if (hasExtendedData &&
                        VirtualKeyToHidUsage.IsInjected(keyFlags))
                    {
                        return InputDispatchResult.Invalid(
                            "Injected keyboard-hook packets are not forwarded.");
                    }
                    message = new HostInputMessage(
                        data[0] == KeyDownMessage
                            ? HostInputMessageKind.KeyDown
                            : HostInputMessageKind.KeyUp,
                        0,
                        0,
                        0,
                        0,
                        BinaryPrimitives.ReadUInt16LittleEndian(
                            data.Slice(1)),
                        hasExtendedData
                            ? BinaryPrimitives.ReadUInt16LittleEndian(
                                data.Slice(3))
                            : (ushort)0,
                        keyFlags,
                        0,
                        hasExtendedData);
                    return InputDispatchResult.Success();

                case TextInputMessage:
                    if (data.Length != TextInputSize)
                    {
                        return WrongSize("text input", TextInputSize);
                    }
                    message = new HostInputMessage(
                        HostInputMessageKind.TextInput,
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        BinaryPrimitives.ReadUInt16LittleEndian(
                            data.Slice(1)),
                        false);
                    return InputDispatchResult.Success();

                case ReleaseAllMessage:
                    if (data.Length != ReleaseAllSize)
                    {
                        return WrongSize("release-all", ReleaseAllSize);
                    }
                    message = new HostInputMessage(
                        HostInputMessageKind.ReleaseAll,
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        0,
                        false);
                    return InputDispatchResult.Success();

                default:
                    return InputDispatchResult.NotSupported(
                        $"Unknown input message type 0x{data[0]:X2}.");
            }
        }

        private static bool TryReadCoordinates(
            ReadOnlySpan<byte> data,
            out float normalizedX,
            out float normalizedY)
        {
            normalizedX = BitConverter.Int32BitsToSingle(
                BinaryPrimitives.ReadInt32LittleEndian(data));
            normalizedY = BitConverter.Int32BitsToSingle(
                BinaryPrimitives.ReadInt32LittleEndian(data.Slice(4)));
            return float.IsFinite(normalizedX) &&
                float.IsFinite(normalizedY) &&
                normalizedX is >= 0 and <= 1 &&
                normalizedY is >= 0 and <= 1;
        }

        private static InputDispatchResult WrongSize(
            string messageName,
            int expectedSize) =>
            InputDispatchResult.Invalid(
                $"The {messageName} message must contain exactly " +
                $"{expectedSize} bytes.");

        private static InputDispatchResult InvalidCoordinates() =>
            InputDispatchResult.Invalid(
                "Normalized mouse coordinates must be finite and " +
                "between 0 and 1.");
    }
}
