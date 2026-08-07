#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [switch]$AcknowledgeDisposableVm,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$SnapshotName,

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$Inf2CatOs = "10_X64",

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
    $knownVirtualMachinePatterns = @(
        "Microsoft Corporation Virtual Machine",
        "VMware",
        "VirtualBox",
        "Oracle Corporation",
        "QEMU",
        "KVM",
        "Parallels",
        "Xen",
        "HVM domU"
    )

    foreach ($pattern in $knownVirtualMachinePatterns) {
        if ($identity.IndexOf(
                $pattern,
                [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return $true
        }
    }

    return $false
}

if ($env:OS -ne "Windows_NT") {
    throw "Phase 1 VM validation can run only on Windows."
}

if (-not $AcknowledgeDisposableVm.IsPresent) {
    throw "A disposable VM acknowledgement is required."
}

$computer = Get-CimInstance -ClassName Win32_ComputerSystem
$operatingSystem = Get-CimInstance -ClassName Win32_OperatingSystem

if (-not (Test-ComoteVirtualMachine `
        -Manufacturer ([string]$computer.Manufacturer) `
        -Model ([string]$computer.Model))) {
    throw ("Refusing to continue because this machine was not recognized as " +
        "a virtual machine. Manufacturer='{0}', Model='{1}'" -f
        $computer.Manufacturer,
        $computer.Model)
}

if ([int]$operatingSystem.ProductType -ne 1) {
    throw "Phase 1 requires a Windows client VM, not Windows Server or a domain controller."
}

if ([string]$operatingSystem.Caption -notmatch "Windows 10") {
    throw "Phase 1 requires a disposable Windows 10 VM."
}

$windowsHomeSkus = @(98, 99, 100, 101)
if ($windowsHomeSkus -notcontains [int]$operatingSystem.OperatingSystemSKU) {
    throw ("Phase 1 requires a Windows 10 Home-family edition. SKU={0}" -f
        $operatingSystem.OperatingSystemSKU)
}
if ([string]$operatingSystem.OSArchitecture -notmatch "64") {
    throw "Phase 1 currently supports only Windows x64."
}

$actualBuildNumber = [string]$operatingSystem.BuildNumber
if ($actualBuildNumber -ne $RequiredBuildNumber) {
    throw ("Phase 1 requires Windows build {0}, but this VM is build {1}." -f
        $RequiredBuildNumber,
        $actualBuildNumber)
}

$currentVersion = Get-ItemProperty `
    -LiteralPath "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion"
$actualUbr = [int]$currentVersion.UBR

if ([string]::IsNullOrWhiteSpace($SnapshotName)) {
    throw "Record the clean VM snapshot name before continuing."
}

$buildScript = Join-Path $PSScriptRoot "Build-Phase1.ps1"
if (-not (Test-Path -LiteralPath $buildScript -PathType Leaf)) {
    throw "Build-Phase1.ps1 was not found."
}

$reportDirectory = Join-Path $PSScriptRoot "artifacts\phase1-reports"
New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$reportPath = Join-Path $reportDirectory "vm-build-$timestamp.json"
$result = [ordered]@{
    startedUtc = [DateTime]::UtcNow.ToString("o")
    completedUtc = $null
    status = "running"
    error = $null
    configuration = $Configuration
    inf2CatOs = $Inf2CatOs
    snapshotName = $SnapshotName
    manufacturer = [string]$computer.Manufacturer
    model = [string]$computer.Model
    osCaption = [string]$operatingSystem.Caption
    osVersion = [string]$operatingSystem.Version
    osBuildNumber = $actualBuildNumber
    osUbr = $actualUbr
    osArchitecture = [string]$operatingSystem.OSArchitecture
    note = "Build and validation only. No signing, test-mode change, or installation."
}

try {
    & $buildScript `
        -Configuration $Configuration `
        -Inf2CatOs $Inf2CatOs

    $result.status = "passed"
}
catch {
    $result.status = "failed"
    $result.error = $_.Exception.Message
    throw
}
finally {
    $result.completedUtc = [DateTime]::UtcNow.ToString("o")
    $result |
        ConvertTo-Json -Depth 4 |
        Set-Content -LiteralPath $reportPath -Encoding UTF8
    Write-Host "VM build report: $reportPath"
}

Write-Host ""
Write-Host "Phase 1 VM build gate passed." -ForegroundColor Green
Write-Host "The package remains unsigned and was not installed."
