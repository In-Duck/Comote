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

. (Join-Path $PSScriptRoot "Phase1Runtime.Common.ps1")

function Get-ComotePhase1DriverPackages {
    return @(
        Get-WindowsDriver -Online -ErrorAction Stop |
            Where-Object {
                [string]$_.ProviderName -eq "Comote" -and
                [string]$_.ClassName -eq "System" -and
                [IO.Path]::GetFileName([string]$_.OriginalFileName) -eq
                    "ComoteVirtualHid.inf"
            }
    )
}

function Get-ComotePhase1RootDevices {
    return @(
        Get-CimInstance `
            -ClassName Win32_PnPEntity `
            -ErrorAction SilentlyContinue |
            Where-Object {
                @($_.HardwareID) -contains "ROOT\COMOTEVIRTUALHID"
            }
    )
}

function Test-ComotePhase1DeviceDescendant {
    param(
        [Parameter(Mandatory)]
        [string]$InstanceId,

        [Parameter(Mandatory)]
        [string]$AncestorInstanceId
    )

    $current = $InstanceId
    for ($depth = 0; $depth -lt 10; $depth++) {
        $parentProperties = @(
            Get-PnpDeviceProperty `
                -InstanceId $current `
                -KeyName "DEVPKEY_Device_Parent" `
                -ErrorAction SilentlyContinue
        )
        if ($parentProperties.Count -ne 1) {
            return $false
        }
        $dataProperty =
            $parentProperties[0].PSObject.Properties["Data"]
        if ($null -eq $dataProperty) {
            return $false
        }
        $parent = [string]$dataProperty.Value
        if ([string]::IsNullOrWhiteSpace($parent)) {
            return $false
        }
        if ($parent.Equals(
                $AncestorInstanceId,
                [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
        if ($parent.Equals(
                $current,
                [StringComparison]::OrdinalIgnoreCase)) {
            return $false
        }
        $current = $parent
    }

    return $false
}

$environment = Assert-ComotePhase1RuntimeEnvironment `
    -AcknowledgeDisposableVm:$AcknowledgeDisposableVm `
    -RequiredBuildNumber $RequiredBuildNumber
[void](Assert-ComotePhase1SigningPrerequisites)
if (-not (Get-ComotePhase1TestSigningState)) {
    throw "TESTSIGNING is not configured after reboot."
}
$codeIntegrityState = Get-ComotePhase1ActiveCodeIntegrityState
if (-not $codeIntegrityState.TestSigningActive) {
    throw "The active kernel does not allow test-signed drivers."
}

foreach ($commandName in @(
    "Get-WindowsDriver",
    "Get-PnpDevice",
    "Get-PnpDeviceProperty"
)) {
    if (-not (Get-Command $commandName -ErrorAction SilentlyContinue)) {
        throw "Required Windows command was not found: $commandName"
    }
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$installReceiptPath = Join-Path `
    $projectRoot `
    "artifacts\phase1-runtime-state\enumeration-installation.json"
if (-not (Test-Path -LiteralPath $installReceiptPath -PathType Leaf)) {
    throw "The Phase 1 installation receipt was not found."
}
$installReceipt = Get-Content `
    -LiteralPath $installReceiptPath `
    -Raw |
    ConvertFrom-Json
if ([string]$installReceipt.status -ne "installed-enumerated" -or
    [string]$installReceipt.snapshotName -ne $SnapshotName -or
    [string]$installReceipt.serviceName -ne "ComoteVirtualHid") {
    throw "The Phase 1 installation receipt is not eligible for reboot verification."
}
if ($null -eq $installReceipt.PSObject.Properties["installedBootUtc"]) {
    throw "The installation receipt does not contain its boot identity."
}

$installedBootUtc = [DateTime]::Parse(
    [string]$installReceipt.installedBootUtc,
    [Globalization.CultureInfo]::InvariantCulture,
    [Globalization.DateTimeStyles]::RoundtripKind
).ToUniversalTime()
$currentBootUtc = (
    [DateTime]$environment.OperatingSystem.LastBootUpTime
).ToUniversalTime()
if ($currentBootUtc -le $installedBootUtc) {
    throw "The VM has not completed a newer boot since driver installation."
}

$driverPackages = @(Get-ComotePhase1DriverPackages)
if ($driverPackages.Count -ne 1 -or
    ([string]$driverPackages[0].Driver).ToLowerInvariant() -ne
        ([string]$installReceipt.publishedInfName).ToLowerInvariant()) {
    throw "The post-reboot Driver Store package does not match its receipt."
}

$rootDevices = @(Get-ComotePhase1RootDevices)
if ($rootDevices.Count -ne 1 -or
    -not ([string]$rootDevices[0].PNPDeviceID).Equals(
        [string]$installReceipt.rootDeviceInstanceId,
        [StringComparison]::OrdinalIgnoreCase) -or
    [int]$rootDevices[0].ConfigManagerErrorCode -ne 0) {
    throw "The post-reboot Comote root device is not healthy."
}

$driverService = Get-CimInstance `
    -ClassName Win32_SystemDriver `
    -Filter "Name='ComoteVirtualHid'" `
    -ErrorAction SilentlyContinue
if ($null -eq $driverService -or
    [string]$driverService.State -ne "Running") {
    throw "The post-reboot Comote kernel driver is not running."
}

$rootInstanceId = [string]$rootDevices[0].PNPDeviceID
$linkedKeyboards = @(
    Get-PnpDevice `
        -PresentOnly `
        -Class Keyboard `
        -ErrorAction SilentlyContinue |
        Where-Object {
            Test-ComotePhase1DeviceDescendant `
                -InstanceId ([string]$_.InstanceId) `
                -AncestorInstanceId $rootInstanceId
        }
)
$linkedMice = @(
    Get-PnpDevice `
        -PresentOnly `
        -Class Mouse `
        -ErrorAction SilentlyContinue |
        Where-Object {
            Test-ComotePhase1DeviceDescendant `
                -InstanceId ([string]$_.InstanceId) `
                -AncestorInstanceId $rootInstanceId
        }
)
if ($linkedKeyboards.Count -lt 1 -or
    $linkedMice.Count -lt 1) {
    throw "The VHF keyboard and mouse children did not return after reboot."
}
foreach ($device in @($linkedKeyboards + $linkedMice)) {
    if ([string]$device.Status -ne "OK") {
        throw "A post-reboot VHF child is not healthy."
    }
}

$reportDirectory = Join-Path `
    $projectRoot `
    "artifacts\phase1-reboot-verification"
New-Item -ItemType Directory -Path $reportDirectory -Force |
    Out-Null
$reportPath = Join-Path `
    $reportDirectory `
    ("installed-after-reboot-{0}.json" -f
        (Get-Date -Format "yyyyMMdd-HHmmss"))
$report = [ordered]@{
    completedUtc = [DateTime]::UtcNow.ToString("o")
    status = "passed"
    snapshotName = $SnapshotName
    cycleId = [string]$installReceipt.cycleId
    installedBootUtc = $installedBootUtc.ToString("o")
    currentBootUtc = $currentBootUtc.ToString("o")
    activeCodeIntegrityOptions = [uint32]$codeIntegrityState.Options
    publishedInfName = [string]$installReceipt.publishedInfName
    rootDeviceInstanceId =
        [string]$installReceipt.rootDeviceInstanceId
    serviceState = [string]$driverService.State
    keyboardInstanceIds = @(
        $linkedKeyboards |
            ForEach-Object { [string]$_.InstanceId }
    )
    mouseInstanceIds = @(
        $linkedMice |
            ForEach-Object { [string]$_.InstanceId }
    )
}
$report | ConvertTo-Json -Depth 6 |
    Set-Content -LiteralPath $reportPath -Encoding UTF8

Write-Host ""
Write-Host "Phase 1 installed state survived a real reboot." `
    -ForegroundColor Green
Write-Host "Kernel driver service: Running"
Write-Host "VHF keyboard and mouse children: OK"
Write-Host "No input reports were submitted."
Write-Host "Report: $reportPath"
