using Microsoft.Win32;
using System.Security.Principal;

namespace Comote.VirtualHidE2E;

internal static class Phase2EnvironmentGuard
{
    public const string RequiredAcknowledgement =
        "COMOTE-WIN10-VM-E2E";

    public static EnvironmentEvidence Assert(
        ValidationConfig config,
        string acknowledgement)
    {
        if (!acknowledgement.Equals(
                RequiredAcknowledgement,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The exact disposable-VM acknowledgement is required.");
        }
        if (config.SchemaVersion != 1 ||
            string.IsNullOrWhiteSpace(config.RunId) ||
            string.IsNullOrWhiteSpace(config.RecoverySnapshotName) ||
            config.ExpectedKeyboardInstanceIds.Length != 1 ||
            config.ExpectedMouseInstanceIds.Length != 2)
        {
            throw new InvalidDataException(
                "The final-validation configuration is invalid.");
        }

        using var bios = Registry.LocalMachine.OpenSubKey(
            @"HARDWARE\DESCRIPTION\System\BIOS",
            writable: false) ??
            throw new InvalidOperationException(
                "The system BIOS identity was unavailable.");
        var manufacturer =
            Convert.ToString(bios.GetValue("SystemManufacturer")) ?? "";
        var productName =
            Convert.ToString(bios.GetValue("SystemProductName")) ?? "";
        if (!manufacturer.Equals(
                "VMware, Inc.",
                StringComparison.OrdinalIgnoreCase) ||
            !(productName.StartsWith(
                    "VMware",
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "Refusing to generate input because this system is not " +
                "an explicitly identified VMware virtual machine.");
        }

        using var currentVersion = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion",
            writable: false) ??
            throw new InvalidOperationException(
                "The Windows version registry key was unavailable.");
        var windowsProductName =
            Convert.ToString(currentVersion.GetValue("ProductName")) ?? "";
        var currentBuildText =
            Convert.ToString(currentVersion.GetValue("CurrentBuildNumber")) ??
            Convert.ToString(currentVersion.GetValue("CurrentBuild")) ??
            "";
        if (!int.TryParse(currentBuildText, out var build) ||
            build != 19045 ||
            !windowsProductName.Contains(
                "Windows 10",
                StringComparison.OrdinalIgnoreCase) ||
            !Environment.Is64BitOperatingSystem ||
            !Environment.UserInteractive)
        {
            throw new InvalidOperationException(
                "Final E2E input validation requires an interactive " +
                "Windows 10 x64 build 19045 VMware guest.");
        }

        using var identity = WindowsIdentity.GetCurrent();
        var localSystem = new SecurityIdentifier(
            WellKnownSidType.LocalSystemSid,
            null);
        if (identity.User?.Equals(localSystem) == true)
        {
            throw new InvalidOperationException(
                "Raw Input validation must run in an interactive user " +
                "session, not as LocalSystem.");
        }

        return new EnvironmentEvidence
        {
            SystemManufacturer = manufacturer,
            SystemProductName = productName,
            ProductName = windowsProductName,
            OsBuild = build,
            Is64BitOperatingSystem = Environment.Is64BitOperatingSystem,
            UserName = identity.Name,
            UserInteractive = Environment.UserInteractive,
            ExpectedKeyboardInstanceIds =
                config.ExpectedKeyboardInstanceIds.ToArray(),
            ExpectedMouseInstanceIds =
                config.ExpectedMouseInstanceIds.ToArray(),
        };
    }
}
