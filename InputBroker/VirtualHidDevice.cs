using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Comote.InputBroker;

internal sealed class VirtualHidDevice : IDisposable
{
    private const uint ProtocolMagic = 0x32485643;
    internal const uint ProtocolVersion = 2;
    internal const int VersionResponseSize = 24;
    internal const int CapabilitiesResponseSize = 32;
    internal const uint MaximumAbsoluteCoordinate = 32767;
    private const uint FileDeviceComoteVirtualHid = 0x8000;
    private const uint FileReadData = 1;
    private const uint FileWriteData = 2;
    private const uint MethodBuffered = 0;

    private const uint IoctlGetVersion = (FileDeviceComoteVirtualHid << 16) |
        (FileReadData << 14) | (0x800 << 2) | MethodBuffered;
    private const uint IoctlGetCapabilities =
        (FileDeviceComoteVirtualHid << 16) |
        (FileReadData << 14) | (0x801 << 2) | MethodBuffered;
    private const uint IoctlReleaseAll = (FileDeviceComoteVirtualHid << 16) |
        (FileWriteData << 14) | (0x802 << 2) | MethodBuffered;
    private const uint IoctlSetKeyboardState =
        (FileDeviceComoteVirtualHid << 16) |
        (FileWriteData << 14) | (0x803 << 2) | MethodBuffered;
    private const uint IoctlMouseRelative =
        (FileDeviceComoteVirtualHid << 16) |
        (FileWriteData << 14) | (0x804 << 2) | MethodBuffered;
    private const uint IoctlMouseAbsolute =
        (FileDeviceComoteVirtualHid << 16) |
        (FileWriteData << 14) | (0x805 << 2) | MethodBuffered;

    private const uint CapabilityKeyboard6Kro = 0x00000001;
    private const uint CapabilityMouseRelative = 0x00000002;
    private const uint CapabilityMouseFiveButtons = 0x00000004;
    private const uint CapabilityMouseVerticalWheel = 0x00000008;
    private const uint CapabilityMouseHorizontalWheel = 0x00000010;
    private const uint CapabilityReleaseAll = 0x00000020;
    private const uint CapabilityStrictSequence = 0x00000040;
    private const uint CapabilitySystemOnly = 0x00000080;
    private const uint CapabilityReleaseOnClose = 0x00000100;
    private const uint CapabilityReleaseOnPowerDown = 0x00000200;
    private const uint CapabilityMouseAbsolute = 0x00000400;

    private const uint RequiredCapabilities =
        CapabilityKeyboard6Kro |
        CapabilityMouseRelative |
        CapabilityMouseFiveButtons |
        CapabilityMouseVerticalWheel |
        CapabilityMouseHorizontalWheel |
        CapabilityReleaseAll |
        CapabilityStrictSequence |
        CapabilitySystemOnly |
        CapabilityReleaseOnClose |
        CapabilityReleaseOnPowerDown |
        CapabilityMouseAbsolute;

    private static readonly Guid DeviceInterfaceGuid =
        new("ba2bc8d8-8d1b-48e4-8ea7-79139b1307a8");

    private readonly SafeFileHandle _handle;
    private uint _sequence;
    private bool _disposed;

    private VirtualHidDevice(SafeFileHandle handle)
    {
        _handle = handle;
        VerifyIdentityAndCapabilities();
    }

    public static VirtualHidDevice Open()
    {
        var paths = GetPresentInterfacePaths();
        if (paths.Count != 1)
        {
            throw new InvalidOperationException(
                $"Expected one Comote Phase 2 interface; found {paths.Count}.");
        }

        var handle = CreateFile(
            paths[0],
            0x80000000u | 0x40000000u,
            0,
            IntPtr.Zero,
            3,
            0x80,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(
                error,
                "Unable to open the Comote Virtual HID control device.");
        }

        try
        {
            return new VirtualHidDevice(handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public void SetKeyboardState(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != Comote.Input.BrokerProtocol.KeyboardPayloadSize)
        {
            throw new ArgumentException(
                "Invalid keyboard payload.",
                nameof(payload));
        }

        var request = CreateRequest(24);
        payload.CopyTo(request.AsSpan(16));
        SendInputIoctl(IoctlSetKeyboardState, request);
    }

    public void MouseRelative(ReadOnlySpan<byte> payload)
    {
        ValidateMousePayload(
            payload,
            absolute: false);
        var request = CreateRequest(24);
        payload.CopyTo(request.AsSpan(16));
        SendInputIoctl(IoctlMouseRelative, request);
    }

    public void MouseAbsolute(ReadOnlySpan<byte> payload)
    {
        ValidateMousePayload(
            payload,
            absolute: true);
        var request = CreateRequest(24);
        payload.CopyTo(request.AsSpan(16));
        SendInputIoctl(IoctlMouseAbsolute, request);
    }

    public void ReleaseAll()
    {
        var request = CreateRequest(16);
        SendInputIoctl(IoctlReleaseAll, request);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (!_handle.IsInvalid && !_handle.IsClosed)
            {
                ReleaseAll();
            }
        }
        catch
        {
        }
        finally
        {
            _disposed = true;
            _handle.Dispose();
        }
    }

    private byte[] CreateRequest(int size)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_sequence == uint.MaxValue)
        {
            throw new InvalidOperationException(
                "The driver sequence was exhausted; reconnect the broker.");
        }

        var request = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(
            request,
            checked((uint)size));
        BinaryPrimitives.WriteUInt32LittleEndian(
            request.AsSpan(4),
            ProtocolMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(
            request.AsSpan(8),
            ProtocolVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(
            request.AsSpan(12),
            _sequence + 1);
        return request;
    }

    private void SendInputIoctl(uint controlCode, byte[] request)
    {
        if (!DeviceIoControlInput(
                _handle,
                controlCode,
                request,
                checked((uint)request.Length),
                IntPtr.Zero,
                0,
                out var bytesReturned,
                IntPtr.Zero))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Driver IOCTL 0x{controlCode:X8} failed.");
        }
        if (bytesReturned != 0)
        {
            throw new InvalidDataException(
                "An input IOCTL returned unexpected output.");
        }
        _sequence++;
    }

    private void VerifyIdentityAndCapabilities()
    {
        var version = Query(IoctlGetVersion, VersionResponseSize);
        ValidateVersionResponse(version);

        var capabilities = Query(
            IoctlGetCapabilities,
            CapabilitiesResponseSize);
        ValidateCapabilitiesResponse(capabilities);
    }

    internal static void ValidateVersionResponse(
        ReadOnlySpan<byte> version)
    {
        if (version.Length != VersionResponseSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(version) !=
                VersionResponseSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(
                version.Slice(4)) != ProtocolVersion)
        {
            throw new InvalidDataException(
                "The driver version response is incompatible.");
        }
    }

    internal static void ValidateCapabilitiesResponse(
        ReadOnlySpan<byte> capabilities)
    {
        if (capabilities.Length != CapabilitiesResponseSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(capabilities) !=
                CapabilitiesResponseSize ||
            BinaryPrimitives.ReadUInt32LittleEndian(
                capabilities.Slice(4)) != ProtocolVersion)
        {
            throw new InvalidDataException(
                "The driver capability response is incompatible.");
        }
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(
            capabilities.Slice(8));
        if ((flags & RequiredCapabilities) != RequiredCapabilities)
        {
            throw new InvalidDataException(
                $"The driver is missing required capabilities: 0x{flags:X8}.");
        }
        if (BinaryPrimitives.ReadUInt32LittleEndian(
                capabilities.Slice(12)) != 6 ||
            BinaryPrimitives.ReadUInt32LittleEndian(
                capabilities.Slice(16)) != 5 ||
            BinaryPrimitives.ReadInt32LittleEndian(
                capabilities.Slice(20)) != 32767 ||
            BinaryPrimitives.ReadInt32LittleEndian(
                capabilities.Slice(24)) != 127 ||
            BinaryPrimitives.ReadUInt32LittleEndian(
                capabilities.Slice(28)) != MaximumAbsoluteCoordinate)
        {
            throw new InvalidDataException(
                "The driver capability limits are incompatible.");
        }
    }
    private byte[] Query(uint controlCode, int outputSize)
    {
        var output = new byte[outputSize];
        if (!DeviceIoControlOutput(
                _handle,
                controlCode,
                IntPtr.Zero,
                0,
                output,
                checked((uint)output.Length),
                out var bytesReturned,
                IntPtr.Zero))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Driver query 0x{controlCode:X8} failed.");
        }
        if (bytesReturned != output.Length)
        {
            throw new InvalidDataException(
                "The driver query returned an unexpected size.");
        }
        return output;
    }

    private static void ValidateMousePayload(
        ReadOnlySpan<byte> payload,
        bool absolute)
    {
        if (payload.Length !=
                Comote.Input.BrokerProtocol.RelativeMousePayloadSize ||
            payload[1] != 0 ||
            (payload[0] & ~0x1F) != 0 ||
            unchecked((sbyte)payload[6]) == sbyte.MinValue ||
            unchecked((sbyte)payload[7]) == sbyte.MinValue)
        {
            throw new ArgumentException("Invalid mouse payload.");
        }

        if (absolute &&
            (BinaryPrimitives.ReadUInt16LittleEndian(
                    payload.Slice(2)) > 32767 ||
                BinaryPrimitives.ReadUInt16LittleEndian(
                    payload.Slice(4)) > 32767))
        {
            throw new ArgumentException(
                "Absolute mouse coordinates are out of range.");
        }
        if (!absolute &&
            (BinaryPrimitives.ReadInt16LittleEndian(
                    payload.Slice(2)) == short.MinValue ||
                BinaryPrimitives.ReadInt16LittleEndian(
                    payload.Slice(4)) == short.MinValue))
        {
            throw new ArgumentException(
                "Relative mouse deltas are out of range.");
        }
    }

    private static List<string> GetPresentInterfacePaths()
    {
        var guid = DeviceInterfaceGuid;
        var result = CM_Get_Device_Interface_List_Size(
            out var characterCount,
            ref guid,
            null,
            0);
        if (result != 0)
        {
            throw new Win32Exception(
                unchecked((int)result),
                "Unable to size the Comote device interface list.");
        }
        if (characterCount <= 1)
        {
            return [];
        }

        var buffer = new char[characterCount];
        result = CM_Get_Device_Interface_List(
            ref guid,
            null,
            buffer,
            characterCount,
            0);
        if (result != 0)
        {
            throw new Win32Exception(
                unchecked((int)result),
                "Unable to enumerate the Comote device interface.");
        }

        var paths = new List<string>();
        var start = 0;
        for (var index = 0; index < buffer.Length; index++)
        {
            if (buffer[index] != '\0')
            {
                continue;
            }
            if (index == start)
            {
                break;
            }
            paths.Add(new string(buffer, start, index - start));
            start = index + 1;
        }
        return paths;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_Interface_List_Size(
        out uint pulLen,
        ref Guid interfaceClassGuid,
        string? pDeviceId,
        uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_Interface_List(
        ref Guid interfaceClassGuid,
        string? pDeviceId,
        [Out] char[] buffer,
        uint bufferLength,
        uint flags);

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "DeviceIoControl",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControlInput(
        SafeFileHandle device,
        uint controlCode,
        byte[] inputBuffer,
        uint inputBufferSize,
        IntPtr outputBuffer,
        uint outputBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "DeviceIoControl",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControlOutput(
        SafeFileHandle device,
        uint controlCode,
        IntPtr inputBuffer,
        uint inputBufferSize,
        byte[] outputBuffer,
        uint outputBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);
}
