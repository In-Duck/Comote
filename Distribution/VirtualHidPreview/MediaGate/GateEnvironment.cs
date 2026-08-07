using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Comote.MediaGate;

internal static class GateEnvironment
{
    private static readonly string[] VirtualMachineMarkers =
    [
        "VMware",
        "VirtualBox",
        "Virtual Machine",
        "KVM",
        "QEMU",
        "Parallels",
        "Xen",
        "HVM domU",
    ];

    internal static EnvironmentEvidence Validate()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "MediaGate requires Windows.");
        }

        if (!Environment.Is64BitOperatingSystem ||
            !Environment.Is64BitProcess ||
            RuntimeInformation.OSArchitecture !=
                Architecture.X64 ||
            RuntimeInformation.ProcessArchitecture !=
                Architecture.X64)
        {
            throw new PlatformNotSupportedException(
                "MediaGate requires an x64 OS and x64 process.");
        }

        string runtimeVersion = Environment.Version.ToString();
        string frameworkDescription =
            RuntimeInformation.FrameworkDescription;
        if (!string.Equals(
                runtimeVersion,
                GateConstants.RuntimeVersion,
                StringComparison.Ordinal) ||
            !string.Equals(
                frameworkDescription,
                $".NET {GateConstants.RuntimeVersion}",
                StringComparison.Ordinal))
        {
            throw new PlatformNotSupportedException(
                $"MediaGate requires the self-contained .NET " +
                $"{GateConstants.RuntimeVersion} runtime; found " +
                $"{frameworkDescription} ({runtimeVersion}).");
        }

        int osBuild = Environment.OSVersion.Version.Build;
        if (osBuild != GateConstants.WindowsBuild)
        {
            throw new PlatformNotSupportedException(
                $"MediaGate requires Windows build " +
                $"{GateConstants.WindowsBuild}; found {osBuild}.");
        }

        int ubr = ReadIntRegistryValue(
            Registry.LocalMachine,
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion",
            "UBR");
        string manufacturer = ReadStringRegistryValue(
            Registry.LocalMachine,
            @"HARDWARE\DESCRIPTION\System\BIOS",
            "SystemManufacturer");
        string model = ReadStringRegistryValue(
            Registry.LocalMachine,
            @"HARDWARE\DESCRIPTION\System\BIOS",
            "SystemProductName");
        string identity = $"{manufacturer} {model}";
        if (!VirtualMachineMarkers.Any(
                marker => identity.Contains(
                    marker,
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new PlatformNotSupportedException(
                "MediaGate must run inside a recognised release VM.");
        }

        return new EnvironmentEvidence
        {
            OsDescription =
                RuntimeInformation.OSDescription,
            FrameworkDescription = frameworkDescription,
            RuntimeVersion = runtimeVersion,
            OsBuild = osBuild,
            Ubr = ubr,
            OsArchitecture =
                RuntimeInformation.OSArchitecture.ToString(),
            ProcessArchitecture =
                RuntimeInformation.ProcessArchitecture.ToString(),
            Manufacturer = manufacturer,
            Model = model,
        };
    }

    private static int ReadIntRegistryValue(
        RegistryKey root,
        string subKey,
        string valueName)
    {
        using var key = root.OpenSubKey(subKey, writable: false)
            ?? throw new InvalidDataException(
                $"Registry key is missing: {subKey}.");
        object? value = key.GetValue(
            valueName,
            null,
            RegistryValueOptions.DoNotExpandEnvironmentNames);
        return value switch
        {
            int intValue when intValue >= 0 => intValue,
            _ => throw new InvalidDataException(
                $"Registry value is invalid: {valueName}."),
        };
    }

    private static string ReadStringRegistryValue(
        RegistryKey root,
        string subKey,
        string valueName)
    {
        using var key = root.OpenSubKey(subKey, writable: false)
            ?? throw new InvalidDataException(
                $"Registry key is missing: {subKey}.");
        string value = Convert.ToString(
            key.GetValue(
                valueName,
                null,
                RegistryValueOptions.DoNotExpandEnvironmentNames),
            System.Globalization.CultureInfo.InvariantCulture)
            ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 256)
        {
            throw new InvalidDataException(
                $"Registry value is invalid: {valueName}.");
        }

        return value.Trim();
    }
}
