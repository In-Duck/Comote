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
    throw "TESTSIGNING is not configured."
}
$codeIntegrityState = Get-ComotePhase1ActiveCodeIntegrityState
if (-not $codeIntegrityState.TestSigningActive) {
    throw "The active kernel does not allow test-signed drivers."
}

foreach ($commandName in @(
    "Get-WindowsDriver",
    "Get-PnpDevice",
    "Get-PnpDeviceProperty",
    "Get-WinEvent"
)) {
    if (-not (Get-Command $commandName -ErrorAction SilentlyContinue)) {
        throw "Required Windows command was not found: $commandName"
    }
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$stateDirectory = Join-Path `
    $projectRoot `
    "artifacts\phase1-runtime-state"
$installReceiptPath = Join-Path `
    $stateDirectory `
    "enumeration-installation.json"
$sleepReceiptPath = Join-Path `
    $stateDirectory `
    "sleep-resume-preparation.json"
$coldBootReceiptPath = Join-Path `
    $stateDirectory `
    "cold-boot-preparation.json"
foreach ($requiredReceiptPath in @(
    $installReceiptPath,
    $sleepReceiptPath
)) {
    if (-not (Test-Path `
            -LiteralPath $requiredReceiptPath `
            -PathType Leaf)) {
        throw "A required Phase 1 receipt is missing: $requiredReceiptPath"
    }
}
if (Test-Path -LiteralPath $coldBootReceiptPath) {
    throw "A Phase 1 cold-boot receipt already exists."
}

$installReceipt = Get-Content `
    -LiteralPath $installReceiptPath `
    -Raw |
    ConvertFrom-Json
$sleepReceipt = Get-Content `
    -LiteralPath $sleepReceiptPath `
    -Raw |
    ConvertFrom-Json
if ([string]$installReceipt.status -ne "installed-enumerated" -or
    [string]$installReceipt.snapshotName -ne $SnapshotName -or
    [string]$installReceipt.serviceName -ne "ComoteVirtualHid") {
    throw "The installed Phase 1 state is not eligible for cold-boot testing."
}
if ([string]$sleepReceipt.status -ne "passed" -or
    [string]$sleepReceipt.snapshotName -ne $SnapshotName -or
    [string]$sleepReceipt.cycleId -ne [string]$installReceipt.cycleId) {
    throw "A matching passed sleep-resume receipt was not found."
}
if ($null -eq $sleepReceipt.PSObject.Properties["reportPath"] -or
    -not (Test-Path `
        -LiteralPath ([string]$sleepReceipt.reportPath) `
        -PathType Leaf)) {
    throw "The passed sleep-resume report is missing."
}
$sleepReport = Get-Content `
    -LiteralPath ([string]$sleepReceipt.reportPath) `
    -Raw |
    ConvertFrom-Json
if ([string]$sleepReport.status -ne "passed" -or
    [string]$sleepReport.cycleId -ne [string]$installReceipt.cycleId) {
    throw "The sleep-resume report does not match this installation."
}

$driverPackages = @(Get-ComotePhase1DriverPackages)
if ($driverPackages.Count -ne 1 -or
    ([string]$driverPackages[0].Driver).ToLowerInvariant() -ne
        ([string]$installReceipt.publishedInfName).ToLowerInvariant()) {
    throw "The installed Driver Store package does not match its receipt."
}
$rootDevices = @(Get-ComotePhase1RootDevices)
if ($rootDevices.Count -ne 1 -or
    -not ([string]$rootDevices[0].PNPDeviceID).Equals(
        [string]$installReceipt.rootDeviceInstanceId,
        [StringComparison]::OrdinalIgnoreCase) -or
    [int]$rootDevices[0].ConfigManagerErrorCode -ne 0) {
    throw "The installed Comote root device is not healthy."
}
$driverService = Get-CimInstance `
    -ClassName Win32_SystemDriver `
    -Filter "Name='ComoteVirtualHid'" `
    -ErrorAction SilentlyContinue
if ($null -eq $driverService -or
    [string]$driverService.State -ne "Running") {
    throw "The ComoteVirtualHid kernel driver is not running."
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
    throw "The VHF children are not present before cold boot."
}
foreach ($device in @($linkedKeyboards + $linkedMice)) {
    if ([string]$device.Status -ne "OK") {
        throw "A VHF child is not healthy before cold boot."
    }
}

$priorShutdownEvent = Get-WinEvent `
    -FilterHashtable @{
        LogName = "System"
        ProviderName = "EventLog"
        Id = 6006
    } `
    -MaxEvents 1 `
    -ErrorAction SilentlyContinue
$priorShutdownRecordId = if ($null -eq $priorShutdownEvent) {
    [long]0
}
else {
    [long]$priorShutdownEvent.RecordId
}

New-Item -ItemType Directory -Path $stateDirectory -Force |
    Out-Null
$receipt = [ordered]@{
    schemaVersion = 1
    status = "prepared"
    preparedUtc = [DateTime]::UtcNow.ToString("o")
    preparedBootUtc = (
        [DateTime]$environment.OperatingSystem.LastBootUpTime
    ).ToUniversalTime().ToString("o")
    snapshotName = $SnapshotName
    cycleId = [string]$installReceipt.cycleId
    publishedInfName = [string]$installReceipt.publishedInfName
    rootDeviceInstanceId = [string]$installReceipt.rootDeviceInstanceId
    serviceName = "ComoteVirtualHid"
    priorShutdownRecordId = $priorShutdownRecordId
    sleepResumeReport = [string]$sleepReceipt.reportPath
    activeCodeIntegrityOptions = [uint32]$codeIntegrityState.Options
}
$receipt | ConvertTo-Json -Depth 6 |
    Set-Content -LiteralPath $coldBootReceiptPath -Encoding UTF8

Write-Host ""
Write-Host "Phase 1 cold-boot preparation passed." `
    -ForegroundColor Green
Write-Host "No shutdown or reboot command was issued."
Write-Host "Use Start > Power > Shut down inside the VM."
Write-Host "After VMware shows the VM is powered off, start it manually."
Write-Host "Then run Test-Phase1InstalledAfterColdBoot.ps1."
Write-Host "Receipt: $coldBootReceiptPath"
