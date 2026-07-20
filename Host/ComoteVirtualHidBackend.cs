using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Host
{
    /// <summary>
    /// Sends keyboard and absolute mouse reports through the transparent
    /// Comote Virtual HID driver. The device identifies itself as Comote
    /// hardware and does not impersonate another vendor's physical device.
    /// </summary>
    public sealed class ComoteVirtualHidBackend : IInputBackend
    {
        private const byte MsgReleaseAll = 0x13;
        private const byte ReportKeyboard = 0x01;
        private const byte ReportAbsoluteMouse = 0x02;
        private const uint IoctlSubmitReport = 0x0022A000;
        private const uint DigcfPresent = 0x00000002;
        private const uint DigcfDeviceInterface = 0x00000010;
        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint OpenExisting = 3;

        private readonly object _sync = new();
        private readonly HashSet<byte> _keys = new();
        private SafeFileHandle _handle;
        private byte _modifiers;
        private byte _mouseButtons;
        private ushort _mouseX;
        private ushort _mouseY;
        private bool _disposed;

        public ComoteVirtualHidBackend(int left, int top, int width, int height)
        {
            UpdateScreenBounds(left, top, width, height);
            _handle = OpenControlDevice();
            Console.WriteLine("[Input] Comote Virtual HID connected.");
        }

        public InputBackendMode Mode => InputBackendMode.VirtualHid;
        public string Name => "Comote Virtual HID";

        public InputBackendStatus GetStatus()
        {
            lock (_sync)
            {
                var available = !_disposed && !_handle.IsInvalid && !_handle.IsClosed;
                return new InputBackendStatus(
                    Mode,
                    Name,
                    available,
                    available
                        ? "Comote Virtual HID driver connected"
                        : "Comote Virtual HID driver is not connected");
            }
        }

        public static bool IsDriverAvailable()
        {
            try
            {
                var handle = OpenControlDevice();
                handle.Dispose();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void UpdateScreenSize(int width, int height) =>
            UpdateScreenBounds(0, 0, width, height);

        public void UpdateScreenBounds(int left, int top, int width, int height)
        {
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
            // Comote's absolute HID report is already normalized to the
            // Windows virtual desktop, so bounds are validated but not stored.
        }

        public void ProcessMessage(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);
            if (data.Length == 0) return;

            lock (_sync)
            {
                ThrowIfDisposed();
                switch (data[0])
                {
                    case MsgReleaseAll:
                        ReleaseAllInputs();
                        break;
                    case SendInputBackend.MsgMouseMove when data.Length >= 9:
                        MoveMouse(
                            BitConverter.ToSingle(data, 1),
                            BitConverter.ToSingle(data, 5));
                        break;
                    case SendInputBackend.MsgMouseDown when data.Length >= 10:
                        SetMouseButton(
                            data[1], true,
                            BitConverter.ToSingle(data, 2),
                            BitConverter.ToSingle(data, 6));
                        break;
                    case SendInputBackend.MsgMouseUp when data.Length >= 10:
                        SetMouseButton(
                            data[1], false,
                            BitConverter.ToSingle(data, 2),
                            BitConverter.ToSingle(data, 6));
                        break;
                    case SendInputBackend.MsgMouseWheel when data.Length >= 5:
                        SendMouse(BitConverter.ToInt32(data, 1));
                        break;
                    case SendInputBackend.MsgKeyDown when data.Length >= 3:
                        SetKey(BitConverter.ToUInt16(data, 1), true);
                        break;
                    case SendInputBackend.MsgKeyUp when data.Length >= 3:
                        SetKey(BitConverter.ToUInt16(data, 1), false);
                        break;
                    case SendInputBackend.MsgTextInput when data.Length >= 3:
                        SendTextCharacter((char)BitConverter.ToUInt16(data, 1));
                        break;
                }
            }
        }

        public void ReleaseAllInputs()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _keys.Clear();
                _modifiers = 0;
                _mouseButtons = 0;
                WriteKeyboard();
                SendMouse(0);
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                ReleaseAllInputs();
                _disposed = true;
                _handle.Dispose();
            }
        }

        private void MoveMouse(float ratioX, float ratioY)
        {
            if (!float.IsFinite(ratioX) || !float.IsFinite(ratioY)) return;
            _mouseX = (ushort)Math.Round(Math.Clamp(ratioX, 0f, 1f) * 32767d);
            _mouseY = (ushort)Math.Round(Math.Clamp(ratioY, 0f, 1f) * 32767d);
            SendMouse(0);
        }

        private void SetMouseButton(
            byte button,
            bool isDown,
            float ratioX,
            float ratioY)
        {
            MoveMouse(ratioX, ratioY);
            var mask = button switch
            {
                SendInputBackend.ButtonLeft => (byte)0x01,
                SendInputBackend.ButtonRight => (byte)0x02,
                SendInputBackend.ButtonMiddle => (byte)0x04,
                _ => (byte)0,
            };
            if (mask == 0) return;
            _mouseButtons = isDown
                ? (byte)(_mouseButtons | mask)
                : (byte)(_mouseButtons & ~mask);
            SendMouse(0);
        }

        private void SendMouse(int wheelDelta)
        {
            var wheel = (sbyte)Math.Clamp(wheelDelta, -127, 127);
            Span<byte> report = stackalloc byte[7];
            report[0] = ReportAbsoluteMouse;
            report[1] = _mouseButtons;
            BitConverter.TryWriteBytes(report[2..4], _mouseX);
            BitConverter.TryWriteBytes(report[4..6], _mouseY);
            report[6] = unchecked((byte)wheel);
            SubmitReport(report);
        }

        private void SetKey(ushort virtualKey, bool isDown)
        {
            if (TryGetModifier(virtualKey, out var modifier))
            {
                _modifiers = isDown
                    ? (byte)(_modifiers | modifier)
                    : (byte)(_modifiers & ~modifier);
                WriteKeyboard();
                return;
            }

            if (!TryMapVirtualKey(virtualKey, out var usage))
            {
                Console.WriteLine($"[Input] No HID usage for VK 0x{virtualKey:X2}");
                return;
            }

            if (isDown)
            {
                if (_keys.Count >= 6 && !_keys.Contains(usage)) return;
                _keys.Add(usage);
            }
            else
            {
                _keys.Remove(usage);
            }
            WriteKeyboard();
        }

        private void SendTextCharacter(char character)
        {
            var vkAndShift = VkKeyScan(character);
            if (vkAndShift == -1)
            {
                Console.WriteLine(
                    $"[Input] Virtual HID text character U+{(int)character:X4} " +
                    "requires normal key events or clipboard paste.");
                return;
            }

            var vk = (ushort)(vkAndShift & 0xFF);
            var shiftState = (byte)((vkAndShift >> 8) & 0xFF);
            var savedModifiers = _modifiers;
            if ((shiftState & 1) != 0) _modifiers |= 0x02;
            if ((shiftState & 2) != 0) _modifiers |= 0x01;
            if ((shiftState & 4) != 0) _modifiers |= 0x04;
            SetKey(vk, true);
            SetKey(vk, false);
            _modifiers = savedModifiers;
            WriteKeyboard();
        }

        private void WriteKeyboard()
        {
            Span<byte> report = stackalloc byte[9];
            report[0] = ReportKeyboard;
            report[1] = _modifiers;
            var index = 3;
            foreach (var key in _keys.OrderBy(value => value))
            {
                if (index >= report.Length) break;
                report[index++] = key;
            }
            SubmitReport(report);
        }

        private void SubmitReport(ReadOnlySpan<byte> report)
        {
            var data = report.ToArray();
            try
            {
                if (!DeviceIoControl(
                    _handle,
                    IoctlSubmitReport,
                    data,
                    data.Length,
                    null,
                    0,
                    out _,
                    IntPtr.Zero))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            catch (Exception ex) when (
                ex is Win32Exception or ObjectDisposedException)
            {
                throw new IOException(
                    "Comote Virtual HID 드라이버 연결이 끊어졌습니다.", ex);
            }
        }

        private static SafeFileHandle OpenControlDevice()
        {
            var interfaceGuid = new Guid(
                "C054A351-7203-4C6A-AFD8-D8299BBAA054");
            var deviceInfo = SetupDiGetClassDevs(
                ref interfaceGuid,
                null,
                IntPtr.Zero,
                DigcfPresent | DigcfDeviceInterface);
            if (deviceInfo == new IntPtr(-1))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            try
            {
                for (uint index = 0; ; index++)
                {
                    var interfaceData = new SpDeviceInterfaceData
                    {
                        Size = Marshal.SizeOf<SpDeviceInterfaceData>(),
                    };
                    if (!SetupDiEnumDeviceInterfaces(
                        deviceInfo, IntPtr.Zero, ref interfaceGuid, index,
                        ref interfaceData))
                    {
                        if (Marshal.GetLastWin32Error() == 259) break;
                        continue;
                    }

                    SetupDiGetDeviceInterfaceDetail(
                        deviceInfo, ref interfaceData, IntPtr.Zero, 0,
                        out var required, IntPtr.Zero);
                    var detail = Marshal.AllocHGlobal((int)required);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetail(
                            deviceInfo, ref interfaceData, detail, required,
                            out _, IntPtr.Zero))
                            continue;

                        var path = Marshal.PtrToStringUni(
                            detail + (IntPtr.Size == 8 ? 8 : 4));
                        if (string.IsNullOrWhiteSpace(path)) continue;
                        var handle = CreateFile(
                            path,
                            GenericRead | GenericWrite,
                            FileShareRead | FileShareWrite,
                            IntPtr.Zero,
                            OpenExisting,
                            0,
                            IntPtr.Zero);
                        if (!handle.IsInvalid) return handle;
                        handle.Dispose();
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(detail);
                    }
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(deviceInfo);
            }

            throw new InvalidOperationException(
                "Comote Virtual HID 드라이버가 설치되어 있지 않거나 " +
                "제어 장치를 열 수 없습니다.");
        }
        private static bool TryGetModifier(ushort vk, out byte modifier)
        {
            modifier = vk switch
            {
                0xA2 or 0x11 => 0x01, // left/generic control
                0xA0 or 0x10 => 0x02, // left/generic shift
                0xA4 or 0x12 => 0x04, // left/generic alt
                0x5B => 0x08,         // left Windows
                0xA3 => 0x10,         // right control
                0xA1 => 0x20,         // right shift
                0xA5 => 0x40,         // right alt
                0x5C => 0x80,         // right Windows
                _ => 0,
            };
            return modifier != 0;
        }

        private static bool TryMapVirtualKey(ushort vk, out byte usage)
        {
            usage = vk switch
            {
                >= 0x41 and <= 0x5A => (byte)(0x04 + vk - 0x41),
                >= 0x31 and <= 0x39 => (byte)(0x1E + vk - 0x31),
                0x30 => 0x27,
                0x0D => 0x28, 0x1B => 0x29, 0x08 => 0x2A,
                0x09 => 0x2B, 0x20 => 0x2C, 0xBD => 0x2D,
                0xBB => 0x2E, 0xDB => 0x2F, 0xDD => 0x30,
                0xDC => 0x31, 0xBA => 0x33, 0xDE => 0x34,
                0xC0 => 0x35, 0xBC => 0x36, 0xBE => 0x37,
                0xBF => 0x38, 0x14 => 0x39,
                >= 0x70 and <= 0x7B => (byte)(0x3A + vk - 0x70),
                0x2C => 0x46, 0x91 => 0x47, 0x13 => 0x48,
                0x2D => 0x49, 0x24 => 0x4A, 0x21 => 0x4B,
                0x2E => 0x4C, 0x23 => 0x4D, 0x22 => 0x4E,
                0x27 => 0x4F, 0x25 => 0x50, 0x28 => 0x51,
                0x26 => 0x52, 0x90 => 0x53, 0x6F => 0x54,
                0x6A => 0x55, 0x6D => 0x56, 0x6B => 0x57,
                0x0C => 0x58, >= 0x61 and <= 0x69 => (byte)(0x59 + vk - 0x61),
                0x60 => 0x62, 0x6E => 0x63, 0x5D => 0x65,
                0x15 => 0x90, 0x19 => 0x91,
                _ => 0,
            };
            return usage != 0;
        }

        private void ThrowIfDisposed() =>
            ObjectDisposedException.ThrowIf(_disposed, this);

        [StructLayout(LayoutKind.Sequential)]
        private struct SpDeviceInterfaceData
        {
            public int Size;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(
            ref Guid classGuid,
            string? enumerator,
            IntPtr parent,
            uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiEnumDeviceInterfaces(
            IntPtr deviceInfoSet,
            IntPtr deviceInfoData,
            ref Guid interfaceClassGuid,
            uint memberIndex,
            ref SpDeviceInterfaceData deviceInterfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(
            IntPtr deviceInfoSet,
            ref SpDeviceInterfaceData deviceInterfaceData,
            IntPtr deviceInterfaceDetailData,
            uint deviceInterfaceDetailDataSize,
            out uint requiredSize,
            IntPtr deviceInfoData);

        [DllImport("setupapi.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiDestroyDeviceInfoList(
            IntPtr deviceInfoSet);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(
            SafeFileHandle device,
            uint controlCode,
            byte[] inputBuffer,
            int inputBufferSize,
            byte[]? outputBuffer,
            int outputBufferSize,
            out int bytesReturned,
            IntPtr overlapped);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern short VkKeyScan(char character);
    }
}