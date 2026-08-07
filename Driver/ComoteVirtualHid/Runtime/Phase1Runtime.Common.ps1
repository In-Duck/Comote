#Requires -Version 5.1

Set-StrictMode -Version Latest

function Test-ComotePhase1VirtualMachine {
    param(
        [Parameter(Mandatory)]
        [string]$Manufacturer,

        [Parameter(Mandatory)]
        [string]$Model
    )

    $identity = "$Manufacturer $Model"
    foreach ($pattern in @(
        "Microsoft Corporation Virtual Machine",
        "VMware",
        "VirtualBox",
        "Oracle Corporation",
        "QEMU",
        "KVM",
        "Parallels",
        "Xen",
        "HVM domU"
    )) {
        if ($identity.IndexOf(
                $pattern,
                [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return $true
        }
    }

    return $false
}

function Assert-ComotePhase1RuntimeEnvironment {
    param(
        [Parameter(Mandatory)]
        [switch]$AcknowledgeDisposableVm,

        [Parameter(Mandatory)]
        [ValidatePattern("^19045$")]
        [string]$RequiredBuildNumber
    )

    if (-not $AcknowledgeDisposableVm.IsPresent) {
        throw "A disposable VM acknowledgement is required."
    }

    $principal = New-Object Security.Principal.WindowsPrincipal(
        [Security.Principal.WindowsIdentity]::GetCurrent()
    )
    if (-not $principal.IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Run this script from an elevated PowerShell window inside the VM."
    }

    $computer = Get-CimInstance -ClassName Win32_ComputerSystem
    $operatingSystem = Get-CimInstance -ClassName Win32_OperatingSystem
    if (-not (Test-ComotePhase1VirtualMachine `
            -Manufacturer ([string]$computer.Manufacturer) `
            -Model ([string]$computer.Model))) {
        throw "Refusing to continue because this machine was not recognized as a VM."
    }
    if ([int]$operatingSystem.ProductType -ne 1 -or
        [string]$operatingSystem.Caption -notmatch "Windows 10" -or
        [string]$operatingSystem.OSArchitecture -notmatch "64") {
        throw "Phase 1 runtime testing requires a Windows 10 x64 client VM."
    }
    if ([string]$operatingSystem.BuildNumber -ne $RequiredBuildNumber) {
        throw ("Windows build {0} is required; found {1}." -f
            $RequiredBuildNumber,
            $operatingSystem.BuildNumber)
    }

    return [PSCustomObject]@{
        Computer = $computer
        OperatingSystem = $operatingSystem
    }
}

function Get-ComotePhase1SecureBootState {
    $secureBootEnabled = $null
    try {
        $secureBootEnabled = Confirm-SecureBootUEFI
    }
    catch [PlatformNotSupportedException] {
        $secureBootEnabled = $false
    }
    catch {
        if ($_.Exception.Message -match "not supported") {
            $secureBootEnabled = $false
        }
        else {
            throw
        }
    }

    return $secureBootEnabled
}

function Get-ComotePhase1BitLockerState {
    $protection = "Unavailable"
    $command = Get-Command Get-BitLockerVolume -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        $systemVolume = Get-BitLockerVolume -MountPoint $env:SystemDrive
        $protection = [string]$systemVolume.ProtectionStatus
    }

    return $protection
}

function Get-ComotePhase1TestSigningState {
    $output = (& bcdedit.exe /enum "{current}" 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to read the current BCD entry."
    }

    return [bool]($output -match
        '(?im)^\s*testsigning\s+(Yes|On)\s*$')
}

function Find-ComotePhase1Tool {
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [Parameter(Mandatory)]
        [string]$Name,

        [string[]]$PreferredArchitectures = @("x64", "amd64", "x86")
    )

    $matches = @(
        Get-ChildItem `
            -LiteralPath $Root `
            -Filter $Name `
            -File `
            -Recurse `
            -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending
    )
    foreach ($architecture in $PreferredArchitectures) {
        $preferred = $matches |
            Where-Object FullName -Match ("\\{0}\\" -f
                [Regex]::Escape($architecture)) |
            Select-Object -First 1
        if ($null -ne $preferred) {
            return $preferred.FullName
        }
    }

    $fallback = $matches | Select-Object -First 1
    if ($null -ne $fallback) {
        return $fallback.FullName
    }

    return $null
}

function Assert-ComotePhase1SigningPrerequisites {
    $secureBootEnabled = Get-ComotePhase1SecureBootState
    if ($secureBootEnabled -eq $true) {
        throw "Secure Boot must be disabled inside the VM."
    }

    $bitLockerProtection = Get-ComotePhase1BitLockerState
    if ($bitLockerProtection -eq "On") {
        throw "Suspend BitLocker protection inside the VM before continuing."
    }

    return [PSCustomObject]@{
        SecureBootEnabled = $secureBootEnabled
        BitLockerProtection = $bitLockerProtection
    }
}

function Assert-ComotePhase1NoInstalledDevice {
    $matchingDevices = @(
        Get-CimInstance -ClassName Win32_PnPEntity -ErrorAction SilentlyContinue |
            Where-Object {
                [string]$_.PNPDeviceID -like "ROOT\COMOTEVIRTUALHID*"
            }
    )
    if ($matchingDevices.Count -gt 0) {
        throw "A Comote Virtual HID device already exists; restore the clean snapshot."
    }

    $serviceOutput = (& sc.exe query ComoteVirtualHid 2>&1 | Out-String)
    $serviceExitCode = $LASTEXITCODE
    if ($serviceExitCode -eq 0) {
        throw "The ComoteVirtualHid service may already exist; restore the clean snapshot."
    }
    if ($serviceExitCode -ne 1060) {
        throw ("Unable to prove that the ComoteVirtualHid service is absent " +
            "(sc.exe exit code {0}): {1}" -f
            $serviceExitCode,
            $serviceOutput)
    }
}

function Get-ComotePhase1CertificateCopies {
    param(
        [Parameter(Mandatory)]
        [ValidatePattern("^[0-9A-Fa-f]{40}$")]
        [string]$Thumbprint
    )

    $normalized = $Thumbprint.ToUpperInvariant()
    $paths = @(
        "Cert:\CurrentUser\My\$normalized",
        "Cert:\LocalMachine\My\$normalized",
        "Cert:\LocalMachine\Root\$normalized",
        "Cert:\LocalMachine\TrustedPublisher\$normalized"
    )

    return @(
        $paths |
            Where-Object { Test-Path -LiteralPath $_ } |
            ForEach-Object { Get-Item -LiteralPath $_ }
    )
}
function Test-ComotePhase1CodeSigningEku {
    param(
        [Parameter(Mandatory)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2]
        $Certificate
    )

    foreach ($extension in $Certificate.Extensions) {
        if ($extension -is
            [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]) {
            foreach ($oid in $extension.EnhancedKeyUsages) {
                if ([string]$oid.Value -eq "1.3.6.1.5.5.7.3.3") {
                    return $true
                }
            }
        }
    }

    return $false
}

function Remove-ComotePhase1TestCertificate {
    param(
        [Parameter(Mandatory)]
        [ValidatePattern("^[0-9A-Fa-f]{40}$")]
        [string]$Thumbprint,

        [Parameter(Mandatory)]
        [ValidatePattern("^CN=Comote Phase 1 VM Test Signing [0-9a-f]{32}$")]
        [string]$Subject
    )

    $copies = @(Get-ComotePhase1CertificateCopies `
        -Thumbprint $Thumbprint)
    foreach ($copy in $copies) {
        if ([string]$copy.Subject -ne $Subject) {
            throw "A certificate thumbprint matched but its subject did not."
        }
    }

    foreach ($copy in @($copies | Where-Object { -not $_.HasPrivateKey })) {
        Remove-Item -Path $copy.PSPath
    }
    foreach ($copy in @($copies | Where-Object { $_.HasPrivateKey })) {
        Remove-Item -Path $copy.PSPath -DeleteKey
    }

    $remaining = @(Get-ComotePhase1CertificateCopies `
        -Thumbprint $Thumbprint)
    if ($remaining.Count -ne 0) {
        throw "One or more Phase 1 certificate copies remained after cleanup."
    }
}
function Set-ComotePhase1NoteProperty {
    param(
        [Parameter(Mandatory)]
        [PSObject]$InputObject,

        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$Name,

        [AllowNull()]
        $Value
    )

    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $property) {
        $InputObject |
            Add-Member `
                -MemberType NoteProperty `
                -Name $Name `
                -Value $Value
    }
    else {
        $property.Value = $Value
    }
}
function Get-ComotePhase1ActiveCodeIntegrityState {
    if ($null -eq (
            "Comote.Phase1.CodeIntegrityNativeMethods" -as [Type])) {
        Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

namespace Comote.Phase1
{
    [StructLayout(LayoutKind.Sequential)]
    public struct SystemCodeIntegrityInformation
    {
        public UInt32 Length;
        public UInt32 CodeIntegrityOptions;
    }

    public static class CodeIntegrityNativeMethods
    {
        [DllImport("ntdll.dll")]
        public static extern Int32 NtQuerySystemInformation(
            Int32 systemInformationClass,
            ref SystemCodeIntegrityInformation systemInformation,
            Int32 systemInformationLength,
            out Int32 returnLength);
    }
}
"@
    }

    $information =
        New-Object Comote.Phase1.SystemCodeIntegrityInformation
    $information.Length = [uint32](
        [Runtime.InteropServices.Marshal]::SizeOf($information)
    )
    $returnLength = 0
    $status =
        [Comote.Phase1.CodeIntegrityNativeMethods]::NtQuerySystemInformation(
            103,
            [ref]$information,
            [int]$information.Length,
            [ref]$returnLength
        )
    if ($status -ne 0) {
        throw ("NtQuerySystemInformation failed with NTSTATUS 0x{0:X8}." -f
            ([uint32]$status))
    }

    return [PSCustomObject]@{
        Options = [uint32]$information.CodeIntegrityOptions
        TestSigningActive =
            [bool]($information.CodeIntegrityOptions -band 0x02)
        HvciKernelModeActive =
            [bool]($information.CodeIntegrityOptions -band 0x400)
    }
}