#Requires -Version 5.1

Set-StrictMode -Version Latest

function Test-ComotePhase2VirtualMachine {
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

function Assert-ComotePhase2RuntimeEnvironment {
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
    if (-not (Test-ComotePhase2VirtualMachine `
            -Manufacturer ([string]$computer.Manufacturer) `
            -Model ([string]$computer.Model))) {
        throw "Refusing to continue because this machine was not recognized as a VM."
    }
    if ([int]$operatingSystem.ProductType -ne 1 -or
        [string]$operatingSystem.Caption -notmatch "Windows 10" -or
        [string]$operatingSystem.OSArchitecture -notmatch "64") {
        throw "Phase 2 runtime testing requires a Windows 10 x64 client VM."
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

function Get-ComotePhase2SecureBootState {
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

function Get-ComotePhase2BitLockerState {
    $protection = "Unavailable"
    $command = Get-Command Get-BitLockerVolume -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        $systemVolume = Get-BitLockerVolume -MountPoint $env:SystemDrive
        $protection = [string]$systemVolume.ProtectionStatus
    }

    return $protection
}

function Get-ComotePhase2TestSigningState {
    $result = Invoke-ComotePhase2NativeCommand `
        -FilePath "bcdedit.exe" `
        -Arguments @("/enum", "{current}")
    if ($result.ExitCode -ne 0) {
        throw "Unable to read the current BCD entry: $($result.Output)"
    }

    return [bool]($result.Output -match
        '(?im)^\s*testsigning\s+(Yes|On)\s*$')
}

function Find-ComotePhase2Tool {
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

function Invoke-ComotePhase2NativeCommand {
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$FilePath,

        [string[]]$Arguments = @()
    )

    $resolvedCommand = Get-Command `
        -Name $FilePath `
        -CommandType Application `
        -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -eq $resolvedCommand) {
        return [PSCustomObject]@{
            ExitCode = -1
            Output = "Native executable was not found: $FilePath"
        }
    }
    $resolvedPath = [string]$resolvedCommand.Path
    if ([string]::IsNullOrWhiteSpace($resolvedPath)) {
        return [PSCustomObject]@{
            ExitCode = -1
            Output = "Native executable path was not resolved: $FilePath"
        }
    }
    $priorErrorActionPreference = $ErrorActionPreference
    $output = ""
    $exitCode = $null
    try {
        $ErrorActionPreference = "Continue"
        $global:LASTEXITCODE = $null
        $output = (& $resolvedPath @Arguments 2>&1 | Out-String)
        $exitCode = $global:LASTEXITCODE
    }
    catch {
        $output = ($_ | Out-String)
        $exitCode = $null
    }
    finally {
        $ErrorActionPreference = $priorErrorActionPreference
    }
    if ($null -eq $exitCode) {
        return [PSCustomObject]@{
            ExitCode = -1
            Output = $output
        }
    }

    return [PSCustomObject]@{
        ExitCode = [int]$exitCode
        Output = $output
    }
}
function Enter-ComotePhase2RuntimeLock {
    $mutex = New-Object Threading.Mutex(
        $false,
        "Global\ComotePhase2RuntimeState"
    )
    $acquired = $false
    try {
        try {
            $acquired = $mutex.WaitOne(0)
        }
        catch [Threading.AbandonedMutexException] {
            # The abandoned owner is gone and this thread now owns the mutex.
            # Transaction journals decide whether recovery can continue safely.
            $acquired = $true
        }
        if (-not $acquired) {
            throw "Another Phase 2 runtime operation is already active."
        }
        return $mutex
    }
    catch {
        if ($acquired) {
            try {
                $mutex.ReleaseMutex()
            }
            catch {
                # Preserve the original lock-acquisition failure.
            }
        }
        $mutex.Dispose()
        throw
    }
}

function Exit-ComotePhase2RuntimeLock {
    param(
        [Parameter(Mandatory)]
        [Threading.Mutex]$Mutex
    )

    try {
        $Mutex.ReleaseMutex()
    }
    finally {
        $Mutex.Dispose()
    }
}
function Write-ComotePhase2JsonAtomically {
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$LiteralPath,

        [Parameter(Mandatory)]
        [AllowNull()]
        $InputObject,

        [ValidateRange(2, 20)]
        [int]$Depth = 8
    )

    $fullPath = [IO.Path]::GetFullPath($LiteralPath)
    $directory = [IO.Path]::GetDirectoryName($fullPath)
    if ([string]::IsNullOrWhiteSpace($directory)) {
        throw "Atomic JSON output must have a parent directory."
    }
    [IO.Directory]::CreateDirectory($directory) | Out-Null

    $temporaryPath = Join-Path $directory (
        ".{0}.{1}.tmp" -f
        [IO.Path]::GetFileName($fullPath),
        [Guid]::NewGuid().ToString("N")
    )
    $backupPath = Join-Path $directory (
        ".{0}.replace-backup" -f [IO.Path]::GetFileName($fullPath)
    )
    if ([IO.File]::Exists($backupPath)) {
        if ([IO.File]::Exists($fullPath)) {
            [IO.File]::Delete($backupPath)
        }
        else {
            [IO.File]::Move($backupPath, $fullPath)
        }
    }

    try {
        $json = ($InputObject | ConvertTo-Json -Depth $Depth) +
            [Environment]::NewLine
        $encoding = New-Object Text.UTF8Encoding($false)
        $bytes = $encoding.GetBytes($json)
        $stream = New-Object IO.FileStream(
            $temporaryPath,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None
        )
        try {
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Flush($true)
        }
        finally {
            $stream.Dispose()
        }

        if ([IO.File]::Exists($fullPath)) {
            [IO.File]::Replace(
                $temporaryPath,
                $fullPath,
                $backupPath,
                $true
            )
        }
        else {
            [IO.File]::Move($temporaryPath, $fullPath)
        }
    }
    finally {
        if ([IO.File]::Exists($temporaryPath)) {
            [IO.File]::Delete($temporaryPath)
        }
        if ([IO.File]::Exists($backupPath) -and
            [IO.File]::Exists($fullPath)) {
            [IO.File]::Delete($backupPath)
        }
    }
}

function Read-ComotePhase2JsonDocument {
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$LiteralPath,

        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$Description
    )

    if (-not (Test-Path -LiteralPath $LiteralPath -PathType Leaf)) {
        throw "$Description was not found: $LiteralPath"
    }
    try {
        return Get-Content -LiteralPath $LiteralPath -Raw -ErrorAction Stop |
            ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "$Description is not valid JSON: $($_.Exception.Message)"
    }
}

function Get-ComotePhase2NamedCertificates {
    $certificateStores = @(
        "Cert:\CurrentUser\My",
        "Cert:\LocalMachine\My",
        "Cert:\LocalMachine\Root",
        "Cert:\LocalMachine\TrustedPublisher"
    )
    return @(
        foreach ($store in $certificateStores) {
            Get-ChildItem -LiteralPath $store -ErrorAction Stop |
                Where-Object {
                    [string]$_.Subject -like
                        "CN=Comote Phase 2 VM Test Signing *"
                }
        }
    )
}
function Assert-ComotePhase2SigningPrerequisites {
    $secureBootEnabled = Get-ComotePhase2SecureBootState
    if ($secureBootEnabled -eq $true) {
        throw "Secure Boot must be disabled inside the VM."
    }

    $bitLockerProtection = Get-ComotePhase2BitLockerState
    if ($bitLockerProtection -eq "On") {
        throw "Suspend BitLocker protection inside the VM before continuing."
    }

    return [PSCustomObject]@{
        SecureBootEnabled = $secureBootEnabled
        BitLockerProtection = $bitLockerProtection
    }
}

function Assert-ComotePhase2NoInstalledDevice {
    $matchingDevices = @(
        Get-CimInstance -ClassName Win32_PnPEntity -ErrorAction Stop |
            Where-Object {
                [string]$_.PNPDeviceID -like
                    "ROOT\COMOTEVIRTUALHID_PHASE2*" -or
                [string]$_.PNPDeviceID -like
                    "ROOT\DEVGEN\COMOTE_PHASE2*" -or
                @($_.HardwareID) -contains
                    "ROOT\COMOTEVIRTUALHID_PHASE2"
            }
    )
    if ($matchingDevices.Count -gt 0) {
        throw "A Comote Virtual HID device already exists; restore the clean snapshot."
    }

    $serviceResult = Invoke-ComotePhase2NativeCommand `
        -FilePath "sc.exe" `
        -Arguments @("query", "ComoteVirtualHidPhase2")
    $serviceOutput = $serviceResult.Output
    $serviceExitCode = $serviceResult.ExitCode
    if ($serviceExitCode -eq 0) {
        throw "The ComoteVirtualHidPhase2 service may already exist; restore the clean snapshot."
    }
    if ($serviceExitCode -ne 1060) {
        throw ("Unable to prove that the ComoteVirtualHidPhase2 service is absent " +
            "(sc.exe exit code {0}): {1}" -f
            $serviceExitCode,
            $serviceOutput)
    }
}

function Get-ComotePhase2CertificateCopies {
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
            Where-Object {
                Test-Path -LiteralPath $_ -ErrorAction Stop
            } |
            ForEach-Object {
                Get-Item -LiteralPath $_ -ErrorAction Stop
            }
    )
}
function Test-ComotePhase2CodeSigningEku {
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

function Remove-ComotePhase2TestCertificate {
    param(
        [Parameter(Mandatory)]
        [ValidatePattern("^[0-9A-Fa-f]{40}$")]
        [string]$Thumbprint,

        [Parameter(Mandatory)]
        [ValidatePattern("^CN=Comote Phase 2 VM Test Signing [0-9a-f]{32}$")]
        [string]$Subject
    )

    $copies = @(Get-ComotePhase2CertificateCopies `
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

    $remaining = @(Get-ComotePhase2CertificateCopies `
        -Thumbprint $Thumbprint)
    if ($remaining.Count -ne 0) {
        throw "One or more Phase 2 certificate copies remained after cleanup."
    }
}
function Set-ComotePhase2NoteProperty {
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
function Get-ComotePhase2ActiveCodeIntegrityState {
    if ($null -eq (
            "Comote.Phase2.CodeIntegrityNativeMethods" -as [Type])) {
        Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

namespace Comote.Phase2
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
        New-Object Comote.Phase2.SystemCodeIntegrityInformation
    $information.Length = [uint32](
        [Runtime.InteropServices.Marshal]::SizeOf($information)
    )
    $returnLength = 0
    $status =
        [Comote.Phase2.CodeIntegrityNativeMethods]::NtQuerySystemInformation(
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