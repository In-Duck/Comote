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
    "Get-WinEvent",
    "powercfg.exe"
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
if (-not (Test-Path -LiteralPath $installReceiptPath -PathType Leaf)) {
    throw "The Phase 1 installation receipt was not found."
}
if (Test-Path -LiteralPath $sleepReceiptPath) {
    throw "A Phase 1 sleep-resume receipt already exists."
}

$installReceipt = Get-Content `
    -LiteralPath $installReceiptPath `
    -Raw |
    ConvertFrom-Json
if ([string]$installReceipt.status -ne "installed-enumerated" -or
    [string]$installReceipt.snapshotName -ne $SnapshotName -or
    [string]$installReceipt.serviceName -ne "ComoteVirtualHid") {
    throw "The installed Phase 1 state is not eligible for sleep testing."
}
if ($null -eq $installReceipt.PSObject.Properties["cycleId"]) {
    throw "The installation receipt does not contain a cycle identity."
}

$rebootReportDirectory = Join-Path `
    $projectRoot `
    "artifacts\phase1-reboot-verification"
$matchingRebootReport = $null
if (Test-Path -LiteralPath $rebootReportDirectory -PathType Container) {
    foreach ($reportFile in @(
        Get-ChildItem `
            -LiteralPath $rebootReportDirectory `
            -Filter "installed-after-reboot-*.json" `
            -File |
            Sort-Object LastWriteTimeUtc -Descending
    )) {
        try {
            $candidate = Get-Content `
                -LiteralPath $reportFile.FullName `
                -Raw |
                ConvertFrom-Json
            if ([string]$candidate.status -eq "passed" -and
                [string]$candidate.snapshotName -eq $SnapshotName -and
                [string]$candidate.cycleId -eq
                    [string]$installReceipt.cycleId) {
                $matchingRebootReport = $reportFile
                break
            }
        }
        catch {
        }
    }
}
if ($null -eq $matchingRebootReport) {
    throw "A matching passed reboot-verification report was not found."
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
    throw "The VHF keyboard and mouse children are not present before sleep."
}
foreach ($device in @($linkedKeyboards + $linkedMice)) {
    if ([string]$device.Status -ne "OK") {
        throw "A VHF input child is not healthy before sleep."
    }
}

$powerCapabilitiesOutput = (& powercfg.exe /a 2>&1 | Out-String)
if ($LASTEXITCODE -ne 0) {
    throw "Unable to query the VM sleep capabilities."
}

$priorSleepEvent = Get-WinEvent `
    -FilterHashtable @{
        LogName = "System"
        ProviderName = "Microsoft-Windows-Kernel-Power"
        Id = 42
    } `
    -MaxEvents 1 `
    -ErrorAction SilentlyContinue
$priorWakeEvent = Get-WinEvent `
    -FilterHashtable @{
        LogName = "System"
        ProviderName = "Microsoft-Windows-Power-Troubleshooter"
        Id = 1
    } `
    -MaxEvents 1 `
    -ErrorAction SilentlyContinue
$priorSleepRecordId = if ($null -eq $priorSleepEvent) {
    [long]0
}
else {
    [long]$priorSleepEvent.RecordId
}
$priorWakeRecordId = if ($null -eq $priorWakeEvent) {
    [long]0
}
else {
    [long]$priorWakeEvent.RecordId
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
    priorSleepRecordId = $priorSleepRecordId
    priorWakeRecordId = $priorWakeRecordId
    rebootVerificationReport = $matchingRebootReport.FullName
    activeCodeIntegrityOptions = [uint32]$codeIntegrityState.Options
    powerCapabilities = $powerCapabilitiesOutput.Trim()
}
$receipt | ConvertTo-Json -Depth 6 |
    Set-Content -LiteralPath $sleepReceiptPath -Encoding UTF8

Write-Host ""
Write-Host "Phase 1 sleep-resume preparation passed." `
    -ForegroundColor Green
Write-Host "No sleep command was issued and no device state was changed."
Write-Host ""
Write-Host "VM sleep capabilities reported by Windows:"
Write-Host $powerCapabilitiesOutput.Trim()
Write-Host ""
Write-Host "Use Start > Power > Sleep inside the VM."
Write-Host "Wake the VM, then run Test-Phase1InstalledAfterSleep.ps1."
Write-Host "Receipt: $sleepReceiptPath"
