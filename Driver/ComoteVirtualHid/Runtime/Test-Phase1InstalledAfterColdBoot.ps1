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
    throw "TESTSIGNING is not configured after cold boot."
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
$coldBootReceiptPath = Join-Path `
    $stateDirectory `
    "cold-boot-preparation.json"
foreach ($requiredReceiptPath in @(
    $installReceiptPath,
    $coldBootReceiptPath
)) {
    if (-not (Test-Path `
            -LiteralPath $requiredReceiptPath `
            -PathType Leaf)) {
        throw "A required Phase 1 receipt is missing: $requiredReceiptPath"
    }
}

$installReceipt = Get-Content `
    -LiteralPath $installReceiptPath `
    -Raw |
    ConvertFrom-Json
$coldBootReceipt = Get-Content `
    -LiteralPath $coldBootReceiptPath `
    -Raw |
    ConvertFrom-Json
if ([string]$installReceipt.status -ne "installed-enumerated" -or
    [string]$installReceipt.snapshotName -ne $SnapshotName -or
    [string]$installReceipt.serviceName -ne "ComoteVirtualHid") {
    throw "The Phase 1 installation receipt is not eligible."
}
if ([string]$coldBootReceipt.status -ne "prepared" -or
    [string]$coldBootReceipt.snapshotName -ne $SnapshotName -or
    [string]$coldBootReceipt.cycleId -ne
        [string]$installReceipt.cycleId -or
    [string]$coldBootReceipt.rootDeviceInstanceId -ne
        [string]$installReceipt.rootDeviceInstanceId) {
    throw "The Phase 1 cold-boot receipt is not eligible."
}

$preparedUtc = [DateTime]::Parse(
    [string]$coldBootReceipt.preparedUtc,
    [Globalization.CultureInfo]::InvariantCulture,
    [Globalization.DateTimeStyles]::RoundtripKind
).ToUniversalTime()
$preparedBootUtc = [DateTime]::Parse(
    [string]$coldBootReceipt.preparedBootUtc,
    [Globalization.CultureInfo]::InvariantCulture,
    [Globalization.DateTimeStyles]::RoundtripKind
).ToUniversalTime()
$currentBootUtc = (
    [DateTime]$environment.OperatingSystem.LastBootUpTime
).ToUniversalTime()
if ($currentBootUtc -le $preparedBootUtc -or
    $currentBootUtc -le $preparedUtc) {
    throw "The VM has not completed a new boot since preparation."
}

$cleanShutdownEvent = $null
for ($attempt = 0; $attempt -lt 30; $attempt++) {
    $cleanShutdownEvent = @(
        Get-WinEvent `
            -FilterHashtable @{
                LogName = "System"
                ProviderName = "EventLog"
                Id = 6006
            } `
            -MaxEvents 20 `
            -ErrorAction SilentlyContinue |
            Where-Object {
                [long]$_.RecordId -gt
                    [long]$coldBootReceipt.priorShutdownRecordId -and
                $_.TimeCreated.ToUniversalTime() -gt $preparedUtc -and
                $_.TimeCreated.ToUniversalTime() -le $currentBootUtc
            } |
            Sort-Object TimeCreated -Descending
    ) | Select-Object -First 1
    if ($null -ne $cleanShutdownEvent) {
        break
    }
    Start-Sleep -Seconds 1
}
if ($null -eq $cleanShutdownEvent) {
    throw "Windows did not record a clean shutdown after preparation."
}

$driverPackages = @(Get-ComotePhase1DriverPackages)
if ($driverPackages.Count -ne 1 -or
    ([string]$driverPackages[0].Driver).ToLowerInvariant() -ne
        ([string]$installReceipt.publishedInfName).ToLowerInvariant()) {
    throw "The cold-boot Driver Store package does not match its receipt."
}
$rootDevices = @(Get-ComotePhase1RootDevices)
if ($rootDevices.Count -ne 1 -or
    -not ([string]$rootDevices[0].PNPDeviceID).Equals(
        [string]$installReceipt.rootDeviceInstanceId,
        [StringComparison]::OrdinalIgnoreCase) -or
    [int]$rootDevices[0].ConfigManagerErrorCode -ne 0) {
    throw "The cold-boot Comote root device is not healthy."
}
$driverService = Get-CimInstance `
    -ClassName Win32_SystemDriver `
    -Filter "Name='ComoteVirtualHid'" `
    -ErrorAction SilentlyContinue
if ($null -eq $driverService -or
    [string]$driverService.State -ne "Running") {
    throw "The cold-boot Comote kernel driver is not running."
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
    throw "The VHF keyboard and mouse children did not return after cold boot."
}
foreach ($device in @($linkedKeyboards + $linkedMice)) {
    if ([string]$device.Status -ne "OK") {
        throw "A cold-boot VHF child is not healthy."
    }
}

$completedUtc = [DateTime]::UtcNow.ToString("o")
$reportDirectory = Join-Path `
    $projectRoot `
    "artifacts\phase1-power-transition"
New-Item -ItemType Directory -Path $reportDirectory -Force |
    Out-Null
$reportPath = Join-Path `
    $reportDirectory `
    ("installed-after-cold-boot-{0}.json" -f
        (Get-Date -Format "yyyyMMdd-HHmmss"))
$report = [ordered]@{
    completedUtc = $completedUtc
    status = "passed"
    snapshotName = $SnapshotName
    cycleId = [string]$installReceipt.cycleId
    preparedUtc = $preparedUtc.ToString("o")
    previousBootUtc = $preparedBootUtc.ToString("o")
    currentBootUtc = $currentBootUtc.ToString("o")
    cleanShutdownEventRecordId = [long]$cleanShutdownEvent.RecordId
    cleanShutdownEventUtc =
        $cleanShutdownEvent.TimeCreated.ToUniversalTime().ToString("o")
    activeCodeIntegrityOptions = [uint32]$codeIntegrityState.Options
    publishedInfName = [string]$installReceipt.publishedInfName
    rootDeviceInstanceId = [string]$installReceipt.rootDeviceInstanceId
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
Set-ComotePhase1NoteProperty `
    -InputObject $coldBootReceipt `
    -Name "status" `
    -Value "passed"
Set-ComotePhase1NoteProperty `
    -InputObject $coldBootReceipt `
    -Name "completedUtc" `
    -Value $completedUtc
Set-ComotePhase1NoteProperty `
    -InputObject $coldBootReceipt `
    -Name "reportPath" `
    -Value $reportPath
$coldBootReceipt | ConvertTo-Json -Depth 6 |
    Set-Content -LiteralPath $coldBootReceiptPath -Encoding UTF8

Write-Host ""
Write-Host "Phase 1 installed state survived a clean cold boot." `
    -ForegroundColor Green
Write-Host "Clean Windows shutdown: confirmed"
Write-Host "New Windows boot session: confirmed"
Write-Host "Kernel driver service: Running"
Write-Host "VHF keyboard and mouse children: OK"
Write-Host "No input reports were submitted."
Write-Host "Report: $reportPath"
