using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Comote.VirtualHid.Installer;

internal static class Program
{
    private const string HardwareId = @"Root\ComoteVirtualHid";
    private const uint DicdGenerateId = 0x00000001;
    private const uint DifRegisterDevice = 0x00000019;
    private const uint InstallFlagForce = 0x00000001;
    private const uint SpdrpHardwareId = 0x00000001;
    private static Guid HidClassGuid =
        new("745A17A0-74D3-11D0-B6FE-00A0C90F57DA");

    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "install";

        try
        {
            return command switch
            {
                "install" => Install(),
                _ => ShowUsage(),
            };
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Comote Virtual HID 설치에 실패했습니다.\n\n" + ex.Message,
                "Comote Virtual HID",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }
    }

    private static int Install()
    {
        var infPath = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "ComoteVirtualHid.inf"));
        if (!File.Exists(infPath))
            throw new FileNotFoundException(
                "드라이버 설치 파일을 찾을 수 없습니다.", infPath);

        if (!DeviceExists())
            CreateRootDevice();

        if (!UpdateDriverForPlugAndPlayDevices(
            IntPtr.Zero,
            HardwareId,
            infPath,
            InstallFlagForce,
            out var rebootRequired))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        MessageBox.Show(
            rebootRequired
                ? "Comote Virtual HID를 설치했습니다.\n" +
                  "Client 컴퓨터를 재부팅한 뒤 Comote를 실행해 주세요."
                : "Comote Virtual HID를 설치했습니다.\n" +
                  "Comote Client를 다시 실행해 주세요.",
            "Comote Virtual HID",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
        return rebootRequired ? 2 : 0;
    }

    private static bool DeviceExists()
    {
        var deviceInfoSet = SetupDiGetClassDevs(
            ref HidClassGuid, null, IntPtr.Zero, 0);
        if (deviceInfoSet == new IntPtr(-1))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        try
        {
            for (uint index = 0; ; index++)
            {
                var deviceInfo = new SpDevinfoData
                {
                    Size = Marshal.SizeOf<SpDevinfoData>(),
                };
                if (!SetupDiEnumDeviceInfo(deviceInfoSet, index, ref deviceInfo))
                {
                    if (Marshal.GetLastWin32Error() == 259) break;
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                var buffer = new byte[4096];
                if (!SetupDiGetDeviceRegistryProperty(
                    deviceInfoSet,
                    ref deviceInfo,
                    SpdrpHardwareId,
                    out _,
                    buffer,
                    buffer.Length,
                    out _))
                {
                    continue;
                }

                var hardwareIds = Encoding.Unicode.GetString(buffer)
                    .Split('\0', StringSplitOptions.RemoveEmptyEntries);
                if (hardwareIds.Any(id => string.Equals(
                    id, HardwareId, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
    }
    private static void CreateRootDevice()
    {
        var deviceInfoSet = SetupDiCreateDeviceInfoList(
            ref HidClassGuid, IntPtr.Zero);
        if (deviceInfoSet == new IntPtr(-1))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        try
        {
            var deviceInfo = new SpDevinfoData
            {
                Size = Marshal.SizeOf<SpDevinfoData>(),
            };
            if (!SetupDiCreateDeviceInfo(
                deviceInfoSet,
                "ComoteVirtualHid",
                ref HidClassGuid,
                "Comote Virtual HID Keyboard and Mouse",
                IntPtr.Zero,
                DicdGenerateId,
                ref deviceInfo))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var hardwareIds = Encoding.Unicode.GetBytes(HardwareId + "\0\0");
            if (!SetupDiSetDeviceRegistryProperty(
                deviceInfoSet,
                ref deviceInfo,
                1,
                hardwareIds,
                hardwareIds.Length))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if (!SetupDiCallClassInstaller(
                DifRegisterDevice,
                deviceInfoSet,
                ref deviceInfo))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
    }

    private static int ShowUsage()
    {
        MessageBox.Show(
            "사용법: ComoteVirtualHidInstaller.exe install",
            "Comote Virtual HID");
        return 2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevinfoData
    {
        public int Size;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport(
        "setupapi.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(
        ref Guid classGuid,
        string? enumerator,
        IntPtr parentWindow,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(
        IntPtr deviceInfoSet,
        uint memberIndex,
        ref SpDevinfoData deviceInfoData);

    [DllImport(
        "setupapi.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryProperty(
        IntPtr deviceInfoSet,
        ref SpDevinfoData deviceInfoData,
        uint property,
        out uint propertyRegistryDataType,
        byte[] propertyBuffer,
        int propertyBufferSize,
        out uint requiredSize);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiCreateDeviceInfoList(
        ref Guid classGuid,
        IntPtr parentWindow);

    [DllImport(
        "setupapi.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiCreateDeviceInfo(
        IntPtr deviceInfoSet,
        string deviceName,
        ref Guid classGuid,
        string deviceDescription,
        IntPtr parentWindow,
        uint creationFlags,
        ref SpDevinfoData deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiSetDeviceRegistryProperty(
        IntPtr deviceInfoSet,
        ref SpDevinfoData deviceInfoData,
        uint property,
        byte[] propertyBuffer,
        int propertyBufferSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiCallClassInstaller(
        uint installFunction,
        IntPtr deviceInfoSet,
        ref SpDevinfoData deviceInfoData);

    [DllImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(
        IntPtr deviceInfoSet);

    [DllImport(
        "newdev.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateDriverForPlugAndPlayDevices(
        IntPtr parentWindow,
        string hardwareId,
        string fullInfPath,
        uint installFlags,
        [MarshalAs(UnmanagedType.Bool)] out bool rebootRequired);
}
