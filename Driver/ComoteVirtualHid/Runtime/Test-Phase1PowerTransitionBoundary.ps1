#Requires -Version 5.1
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$preparePath = Join-Path `
    $PSScriptRoot `
    "Prepare-Phase1SleepResume.ps1"
$testPath = Join-Path `
    $PSScriptRoot `
    "Test-Phase1InstalledAfterSleep.ps1"
$coldPreparePath = Join-Path `
    $PSScriptRoot `
    "Prepare-Phase1ColdBoot.ps1"
$coldTestPath = Join-Path `
    $PSScriptRoot `
    "Test-Phase1InstalledAfterColdBoot.ps1"
foreach ($path in @(
    $preparePath,
    $testPath,
    $coldPreparePath,
    $coldTestPath
)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required Phase 1 power-transition file is missing: $path"
    }
    $sourceText = Get-Content -LiteralPath $path -Raw
    if ($sourceText -match '[^\x00-\x7F]') {
        throw "Power-transition PowerShell must remain ASCII-compatible: $path"
    }
}

$prepare = Get-Content -LiteralPath $preparePath -Raw
foreach ($requiredText in @(
    "Assert-ComotePhase1RuntimeEnvironment",
    "Assert-ComotePhase1SigningPrerequisites",
    "Get-ComotePhase1TestSigningState",
    "Get-ComotePhase1ActiveCodeIntegrityState",
    "installed-enumerated",
    "installed-after-reboot-*.json",
    'status -eq "passed"',
    "Get-WindowsDriver",
    "Win32_PnPEntity",
    "Win32_SystemDriver",
    "Get-PnpDevice",
    "Get-PnpDeviceProperty",
    "DEVPKEY_Device_Parent",
    "Class Keyboard",
    "Class Mouse",
    "powercfg.exe /a",
    "Microsoft-Windows-Kernel-Power",
    "Microsoft-Windows-Power-Troubleshooter",
    "sleep-resume-preparation.json",
    'status = "prepared"',
    "No sleep command was issued",
    "Use Start > Power > Sleep"
)) {
    if ($prepare.IndexOf(
            $requiredText,
            [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Required sleep preparation gate is missing: $requiredText"
    }
}
$prepareGuardIndex = $prepare.IndexOf(
    "Assert-ComotePhase1RuntimeEnvironment",
    [StringComparison]::OrdinalIgnoreCase
)
$prepareRebootReportIndex = $prepare.IndexOf(
    "installed-after-reboot-*.json",
    [StringComparison]::OrdinalIgnoreCase
)
$preparePowerQueryIndex = $prepare.IndexOf(
    "powercfg.exe /a",
    [StringComparison]::OrdinalIgnoreCase
)
$prepareMutationIndex = $prepare.IndexOf(
    'Set-Content -LiteralPath $sleepReceiptPath',
    [StringComparison]::OrdinalIgnoreCase
)
if ($prepareGuardIndex -lt 0 -or
    $prepareRebootReportIndex -lt $prepareGuardIndex -or
    $preparePowerQueryIndex -lt $prepareRebootReportIndex -or
    $prepareMutationIndex -lt $preparePowerQueryIndex) {
    throw "Sleep preparation validation must precede receipt creation."
}

$test = Get-Content -LiteralPath $testPath -Raw
foreach ($requiredText in @(
    "Assert-ComotePhase1RuntimeEnvironment",
    "Assert-ComotePhase1SigningPrerequisites",
    "Get-ComotePhase1TestSigningState",
    "Get-ComotePhase1ActiveCodeIntegrityState",
    "sleep-resume-preparation.json",
    'status -ne "prepared"',
    "preparedBootUtc",
    "currentBootUtc",
    "rebooted instead of resuming",
    "Microsoft-Windows-Kernel-Power",
    "Microsoft-Windows-Power-Troubleshooter",
    "priorSleepRecordId",
    "priorWakeRecordId",
    "Get-WindowsDriver",
    "Win32_PnPEntity",
    "Win32_SystemDriver",
    "Get-PnpDeviceProperty",
    "DEVPKEY_Device_Parent",
    "Class Keyboard",
    "Class Mouse",
    "phase1-power-transition",
    'Value "passed"',
    "No input reports were submitted"
)) {
    if ($test.IndexOf(
            $requiredText,
            [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Required post-sleep verification gate is missing: $requiredText"
    }
}

$coldPrepare = Get-Content -LiteralPath $coldPreparePath -Raw
foreach ($requiredText in @(
    "Assert-ComotePhase1RuntimeEnvironment",
    "Assert-ComotePhase1SigningPrerequisites",
    "Get-ComotePhase1TestSigningState",
    "Get-ComotePhase1ActiveCodeIntegrityState",
    "installed-enumerated",
    "sleep-resume-preparation.json",
    'sleepReceipt.status -ne "passed"',
    "Get-WindowsDriver",
    "Win32_PnPEntity",
    "Win32_SystemDriver",
    "Get-PnpDeviceProperty",
    "DEVPKEY_Device_Parent",
    "Class Keyboard",
    "Class Mouse",
    'ProviderName = "EventLog"',
    "Id = 6006",
    "cold-boot-preparation.json",
    'status = "prepared"',
    "No shutdown or reboot command was issued",
    "Use Start > Power > Shut down"
)) {
    if ($coldPrepare.IndexOf(
            $requiredText,
            [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Required cold-boot preparation gate is missing: $requiredText"
    }
}
$coldPrepareGuardIndex = $coldPrepare.IndexOf(
    "Assert-ComotePhase1RuntimeEnvironment",
    [StringComparison]::OrdinalIgnoreCase
)
$coldPrepareSleepPassIndex = $coldPrepare.IndexOf(
    'sleepReceipt.status -ne "passed"',
    [StringComparison]::OrdinalIgnoreCase
)
$coldPrepareEventIndex = $coldPrepare.IndexOf(
    "Id = 6006",
    [StringComparison]::OrdinalIgnoreCase
)
$coldPrepareReceiptIndex = $coldPrepare.IndexOf(
    'Set-Content -LiteralPath $coldBootReceiptPath',
    [StringComparison]::OrdinalIgnoreCase
)
if ($coldPrepareGuardIndex -lt 0 -or
    $coldPrepareSleepPassIndex -lt $coldPrepareGuardIndex -or
    $coldPrepareEventIndex -lt $coldPrepareSleepPassIndex -or
    $coldPrepareReceiptIndex -lt $coldPrepareEventIndex) {
    throw "Cold-boot validation must precede receipt creation."
}

$coldTest = Get-Content -LiteralPath $coldTestPath -Raw
foreach ($requiredText in @(
    "Assert-ComotePhase1RuntimeEnvironment",
    "Assert-ComotePhase1SigningPrerequisites",
    "Get-ComotePhase1TestSigningState",
    "Get-ComotePhase1ActiveCodeIntegrityState",
    "cold-boot-preparation.json",
    'status -ne "prepared"',
    "preparedBootUtc",
    "currentBootUtc",
    "completed a new boot",
    'ProviderName = "EventLog"',
    "Id = 6006",
    "priorShutdownRecordId",
    "Get-WindowsDriver",
    "Win32_PnPEntity",
    "Win32_SystemDriver",
    "Get-PnpDeviceProperty",
    "DEVPKEY_Device_Parent",
    "Class Keyboard",
    "Class Mouse",
    "installed-after-cold-boot",
    'Value "passed"',
    "No input reports were submitted"
)) {
    if ($coldTest.IndexOf(
            $requiredText,
            [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Required cold-boot verification gate is missing: $requiredText"
    }
}

foreach ($source in @(
    $prepare,
    $test,
    $coldPrepare,
    $coldTest
)) {
    foreach ($forbiddenText in @(
        "New-SelfSignedCertificate",
        "Import-Certificate",
        "Remove-Item",
        "bcdedit.exe /set",
        "devgen.exe",
        "pnputil.exe",
        "sc.exe delete",
        "SetSuspendState",
        "rundll32.exe",
        "shutdown.exe",
        "Restart-Computer",
        "Stop-Computer",
        "verifier.exe",
        "VhfReadReportSubmit",
        "DeviceIoControl",
        "powercfg.exe /h"
    )) {
        if ($source.IndexOf(
                $forbiddenText,
                [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw "Power-transition gate crossed its safety boundary: $forbiddenText"
        }
    }
}

Write-Host "Phase 1 power-transition boundaries verified."
