#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [switch]$AcknowledgeDisposableVm,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$SnapshotName,

    [ValidatePattern("^19045$")]
    [string]$RequiredBuildNumber = "19045"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Test-ComoteVirtualMachine {
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

function Find-ComoteTool {
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [Parameter(Mandatory)]
        [string]$Name
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
    $preferred = $matches |
        Where-Object FullName -Match '\\(x64|amd64)\\' |
        Select-Object -First 1
    if ($null -ne $preferred) {
        return $preferred.FullName
    }

    $fallback = $matches | Select-Object -First 1
    if ($null -ne $fallback) {
        return $fallback.FullName
    }

    return $null
}

if (-not $AcknowledgeDisposableVm.IsPresent) {
    throw "A disposable VM acknowledgement is required."
}

$principal = New-Object Security.Principal.WindowsPrincipal(
    [Security.Principal.WindowsIdentity]::GetCurrent()
)
if (-not $principal.IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run the runtime preflight from an elevated PowerShell window inside the VM."
}

$computer = Get-CimInstance -ClassName Win32_ComputerSystem
$operatingSystem = Get-CimInstance -ClassName Win32_OperatingSystem
if (-not (Test-ComoteVirtualMachine `
        -Manufacturer ([string]$computer.Manufacturer) `
        -Model ([string]$computer.Model))) {
    throw "Refusing to continue because this machine was not recognized as a VM."
}
if ([int]$operatingSystem.ProductType -ne 1 -or
    [string]$operatingSystem.Caption -notmatch "Windows 10" -or
    [string]$operatingSystem.OSArchitecture -notmatch "64") {
    throw "Phase 1 runtime preflight requires a Windows 10 x64 client VM."
}
if ([string]$operatingSystem.BuildNumber -ne $RequiredBuildNumber) {
    throw ("Windows build {0} is required; found {1}." -f
        $RequiredBuildNumber,
        $operatingSystem.BuildNumber)
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$packagePath = Join-Path $projectRoot "artifacts\phase1-unsigned"
$manifestPath = Join-Path $packagePath "SHA256.json"
foreach ($requiredPath in @(
    (Join-Path $packagePath "ComoteVirtualHid.sys"),
    (Join-Path $packagePath "ComoteVirtualHid.inf"),
    (Join-Path $packagePath "ComoteVirtualHid.cat"),
    $manifestPath
)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required unsigned package file is missing: $requiredPath"
    }
}

$manifestDocument = Get-Content -LiteralPath $manifestPath -Raw |
    ConvertFrom-Json
if ($manifestDocument -is [Array]) {
    $manifestEntries = $manifestDocument
} else {
    $manifestEntries = @($manifestDocument)
}
foreach ($entry in $manifestEntries) {
    $filePath = Join-Path $packagePath ([string]$entry.file)
    if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) {
        throw "Manifest file is missing: $filePath"
    }
    $actualHash = (Get-FileHash `
        -Algorithm SHA256 `
        -LiteralPath $filePath).Hash
    if ($actualHash -ne [string]$entry.sha256) {
        throw "SHA-256 mismatch: $filePath"
    }
}

foreach ($unsignedFile in @(
    (Join-Path $packagePath "ComoteVirtualHid.sys"),
    (Join-Path $packagePath "ComoteVirtualHid.cat")
)) {
    $signature = Get-AuthenticodeSignature -LiteralPath $unsignedFile
    if ($signature.Status -ne
        [System.Management.Automation.SignatureStatus]::NotSigned) {
        throw "Expected an unsigned Phase 1 file: $unsignedFile"
    }
}

$oldThumbprint = "60EE21C9DF1C019DE8B259CF1E265CDCF61EB9BC"
$oldCertificatePaths = @(
    "Cert:\CurrentUser\My\$oldThumbprint",
    "Cert:\LocalMachine\My\$oldThumbprint",
    "Cert:\LocalMachine\Root\$oldThumbprint",
    "Cert:\LocalMachine\TrustedPublisher\$oldThumbprint"
)
$remainingOldCertificates = @(
    $oldCertificatePaths |
        Where-Object { Test-Path -LiteralPath $_ }
)
if ($remainingOldCertificates.Count -gt 0) {
    throw ("The accidental v5 test certificate is still present: {0}" -f
        ($remainingOldCertificates -join ", "))
}

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
if ($secureBootEnabled -eq $true) {
    throw "Secure Boot must be disabled in the VM before test-signing runtime tests."
}

$bitLockerProtection = "Unavailable"
$getBitLockerVolume = Get-Command Get-BitLockerVolume -ErrorAction SilentlyContinue
if ($null -ne $getBitLockerVolume) {
    $systemVolume = Get-BitLockerVolume -MountPoint $env:SystemDrive
    $bitLockerProtection = [string]$systemVolume.ProtectionStatus
    if ($bitLockerProtection -eq "On") {
        throw "Suspend BitLocker protection inside the VM before changing test-signing state."
    }
}

$bcdOutput = (& bcdedit.exe /enum "{current}" 2>&1 | Out-String)
if ($LASTEXITCODE -ne 0) {
    throw "Unable to read the current BCD entry."
}
$testSigningEnabled = $bcdOutput -match
    '(?im)^\s*testsigning\s+(Yes|On)\s*$'
if ($testSigningEnabled) {
    throw "TESTSIGNING is already enabled; restore the clean snapshot before continuing."
}

$programFilesX86 = [Environment]::GetFolderPath(
    [Environment+SpecialFolder]::ProgramFilesX86
)
$kitRoot = Join-Path $programFilesX86 "Windows Kits\10"
$signTool = Find-ComoteTool -Root (Join-Path $kitRoot "bin") -Name "signtool.exe"
$devGen = Find-ComoteTool -Root (Join-Path $kitRoot "Tools") -Name "devgen.exe"
if (-not $signTool) {
    throw "SignTool.exe was not found in the installed Windows Kit."
}
if (-not $devGen) {
    throw "DevGen.exe was not found in the installed WDK."
}

$currentVersion = Get-ItemProperty `
    -LiteralPath "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion"
$report = [ordered]@{
    completedUtc = [DateTime]::UtcNow.ToString("o")
    status = "passed"
    note = "Read-only runtime preflight. No signing, certificate import, BCD change, device creation, or driver installation."
    snapshotName = $SnapshotName
    manufacturer = [string]$computer.Manufacturer
    model = [string]$computer.Model
    osCaption = [string]$operatingSystem.Caption
    osBuildNumber = [string]$operatingSystem.BuildNumber
    osUbr = [int]$currentVersion.UBR
    secureBootEnabled = $secureBootEnabled
    bitLockerProtection = $bitLockerProtection
    testSigningEnabled = $testSigningEnabled
    packagePath = $packagePath
    signTool = $signTool
    devGen = $devGen
}

$reportDirectory = Join-Path $projectRoot "artifacts\phase1-runtime-preflight"
New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
$reportPath = Join-Path `
    $reportDirectory `
    ("runtime-preflight-{0}.json" -f (Get-Date -Format "yyyyMMdd-HHmmss"))
$report | ConvertTo-Json -Depth 4 |
    Set-Content -LiteralPath $reportPath -Encoding UTF8

Write-Host ""
Write-Host "Phase 1 runtime preflight passed." -ForegroundColor Green
Write-Host "No system state was changed."
Write-Host "Report: $reportPath"
