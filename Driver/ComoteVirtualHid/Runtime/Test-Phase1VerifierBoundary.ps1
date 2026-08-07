#Requires -Version 5.1
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$preparePath = Join-Path `
    $PSScriptRoot `
    "Prepare-Phase1Verifier.ps1"
$enablePath = Join-Path `
    $PSScriptRoot `
    "Enable-Phase1Verifier.ps1"
$activePath = Join-Path `
    $PSScriptRoot `
    "Test-Phase1VerifierActive.ps1"
$unloadPath = Join-Path `
    $PSScriptRoot `
    "Invoke-Phase1VerifierUnload.ps1"
$resetPath = Join-Path `
    $PSScriptRoot `
    "Reset-Phase1Verifier.ps1"
foreach ($path in @(
    $preparePath,
    $enablePath,
    $activePath,
    $unloadPath,
    $resetPath
)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required Phase 1 Verifier file is missing: $path"
    }
    $sourceText = Get-Content -LiteralPath $path -Raw
    if ($sourceText -match '[^\x00-\x7F]') {
        throw "Verifier PowerShell must remain ASCII-compatible: $path"
    }
}
$prepare = Get-Content -LiteralPath $preparePath -Raw

foreach ($requiredText in @(
    "Assert-ComotePhase1RuntimeEnvironment",
    "Assert-ComotePhase1SigningPrerequisites",
    "Get-ComotePhase1TestSigningState",
    "Get-ComotePhase1ActiveCodeIntegrityState",
    "installed-enumerated",
    "cold-boot-preparation.json",
    'coldBootReceipt.status -ne "passed"',
    "Get-WindowsDriver",
    "Win32_PnPEntity",
    "Win32_SystemDriver",
    "Get-PnpDeviceProperty",
    "DEVPKEY_Device_Parent",
    "Class Keyboard",
    "Class Mouse",
    "VerifyDrivers",
    "VerifyDriverLevel",
    "verifier.exe /querysettings",
    "CrashDumpEnabled",
    "Win32_PageFileUsage",
    "verifier-preparation.json",
    'status = "prepared"',
    "No Driver Verifier setting was changed",
    "No device state was changed"
)) {
    if ($prepare.IndexOf(
            $requiredText,
            [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Required Verifier preflight gate is missing: $requiredText"
    }
}

$environmentIndex = $prepare.IndexOf(
    "Assert-ComotePhase1RuntimeEnvironment",
    [StringComparison]::OrdinalIgnoreCase
)
$coldBootIndex = $prepare.IndexOf(
    'coldBootReceipt.status -ne "passed"',
    [StringComparison]::OrdinalIgnoreCase
)
$verifierEmptyIndex = $prepare.IndexOf(
    "Driver Verifier already has persistent settings",
    [StringComparison]::OrdinalIgnoreCase
)
$dumpIndex = $prepare.IndexOf(
    "Windows crash-dump collection is not enabled",
    [StringComparison]::OrdinalIgnoreCase
)
$receiptIndex = $prepare.IndexOf(
    'Set-Content -LiteralPath $verifierReceiptPath',
    [StringComparison]::OrdinalIgnoreCase
)
if ($environmentIndex -lt 0 -or
    $coldBootIndex -lt $environmentIndex -or
    $verifierEmptyIndex -lt $coldBootIndex -or
    $dumpIndex -lt $verifierEmptyIndex -or
    $receiptIndex -lt $dumpIndex) {
    throw "Verifier preflight checks must precede receipt creation."
}

foreach ($forbiddenText in @(
    "verifier.exe /standard",
    "verifier.exe /flags",
    "verifier.exe /driver",
    "verifier.exe /bootmode",
    "verifier.exe /reset",
    "verifier.exe /volatile",
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
    "VhfReadReportSubmit",
    "DeviceIoControl"
)) {
    if ($prepare.IndexOf(
            $forbiddenText,
            [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Verifier preflight crossed its safety boundary: $forbiddenText"
    }
}

$enable = Get-Content -LiteralPath $enablePath -Raw
foreach ($requiredText in @(
    "AcknowledgeOneBootCrashRisk",
    "Assert-ComotePhase1RuntimeEnvironment",
    "Get-ComotePhase1TestSigningState",
    "Get-ComotePhase1ActiveCodeIntegrityState",
    "verifier-preparation.json",
    'status -ne "prepared"',
    "preparedBootUtc",
    "ValidateOnly",
    "standardOutput",
    "/driver",
    "ComoteVirtualHid.sys",
    "bootModeOutput",
    "oneboot",
    "Test-ComotePhase1SingleVerifierTarget",
    "VerifyDrivers",
    "VerifyDriverLevel",
    "verifier.exe /querysettings",
    "verifier.exe /reset",
    "configurationStarted",
    "configured-oneboot",
    "verifier-activation.json",
    "No restart was requested"
)) {
    if ($enable.IndexOf(
            $requiredText,
            [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Required Verifier activation gate is missing: $requiredText"
    }
}
$enableEnvironmentIndex = $enable.IndexOf(
    "Assert-ComotePhase1RuntimeEnvironment",
    [StringComparison]::OrdinalIgnoreCase
)
$enableValidateOnlyIndex = $enable.IndexOf(
    'if ($ValidateOnly.IsPresent)',
    [StringComparison]::OrdinalIgnoreCase
)
$enableStandardIndex = $enable.IndexOf(
    'verifier.exe ',
    $enableValidateOnlyIndex,
    [StringComparison]::OrdinalIgnoreCase
)
$enableStandardArgumentIndex = $enable.IndexOf(
    '/standard ',
    $enableStandardIndex,
    [StringComparison]::OrdinalIgnoreCase
)
$enableDriverArgumentIndex = $enable.IndexOf(
    '/driver ',
    $enableStandardArgumentIndex,
    [StringComparison]::OrdinalIgnoreCase
)
$enableTargetIndex = $enable.IndexOf(
    "ComoteVirtualHid.sys 2>&1",
    $enableDriverArgumentIndex,
    [StringComparison]::OrdinalIgnoreCase
)
$enableOneBootIndex = $enable.IndexOf(
    "oneboot 2>&1",
    $enableTargetIndex,
    [StringComparison]::OrdinalIgnoreCase
)
$enableReceiptIndex = $enable.IndexOf(
    'Set-Content -LiteralPath $activationReceiptPath',
    $enableOneBootIndex,
    [StringComparison]::OrdinalIgnoreCase
)
if ($enableEnvironmentIndex -lt 0 -or
    $enableValidateOnlyIndex -lt $enableEnvironmentIndex -or
    $enableStandardIndex -lt $enableValidateOnlyIndex -or
    $enableStandardArgumentIndex -lt $enableStandardIndex -or
    $enableDriverArgumentIndex -lt $enableStandardArgumentIndex -or
    $enableTargetIndex -lt $enableDriverArgumentIndex -or
    $enableOneBootIndex -lt $enableTargetIndex -or
    $enableReceiptIndex -lt $enableOneBootIndex) {
    throw "Verifier activation validation and single-target order is invalid."
}
foreach ($forbiddenText in @(
    "/all",
    "*.sys",
    "/flags",
    "/volatile",
    "/faults",
    "/ruleclasses",
    "bcdedit.exe",
    "devgen.exe",
    "pnputil.exe",
    "shutdown.exe",
    "Restart-Computer",
    "Stop-Computer",
    "VhfReadReportSubmit",
    "DeviceIoControl"
)) {
    if ($enable.IndexOf(
            $forbiddenText,
            [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Verifier activation crossed its safety boundary: $forbiddenText"
    }
}

$active = Get-Content -LiteralPath $activePath -Raw
foreach ($requiredText in @(
    "Assert-ComotePhase1RuntimeEnvironment",
    "Get-ComotePhase1TestSigningState",
    "Get-ComotePhase1ActiveCodeIntegrityState",
    "verifier-activation.json",
    "configured-oneboot",
    "configuredBootUtc",
    "verifier.exe /query",
    "verifier.exe /querysettings",
    "VerifyDrivers",
    "VerifyDriverLevel",
    "Oneboot next-boot Verifier settings were not cleared",
    "nextBootVerifyDriverLevel",
    "Win32_SystemDriver",
    "Get-PnpDeviceProperty",
    "DEVPKEY_Device_Parent",
    "Class Keyboard",
    "Class Mouse",
    "active-verified",
    "No input reports were submitted",
    "Do not restart or reset Verifier"
)) {
    if ($active.IndexOf(
            $requiredText,
            [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Required active Verifier gate is missing: $requiredText"
    }
}
foreach ($forbiddenText in @(
    "verifier.exe /standard",
    "verifier.exe /flags",
    "verifier.exe /driver",
    "verifier.exe /bootmode",
    "verifier.exe /reset",
    "verifier.exe /volatile",
    "bcdedit.exe",
    "devgen.exe",
    "pnputil.exe",
    "shutdown.exe",
    "Restart-Computer",
    "Stop-Computer",
    "VhfReadReportSubmit",
    "DeviceIoControl"
)) {
    if ($active.IndexOf(
            $forbiddenText,
            [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Active Verifier audit crossed its safety boundary: $forbiddenText"
    }
}

$unload = Get-Content -LiteralPath $unloadPath -Raw
foreach ($requiredText in @(
    "AcknowledgeVerifierUnloadRisk",
    "Assert-ComotePhase1RuntimeEnvironment",
    "Assert-ComotePhase1SigningPrerequisites",
    "Get-ComotePhase1TestSigningState",
    "Get-ComotePhase1ActiveCodeIntegrityState",
    "enumeration-installation.json",
    "verifier-activation.json",
    "active-verified",
    "activeReportPath",
    "currentBootUtc",
    "verifier.exe /query",
    "verifier.exe /querysettings",
    "VerifyDrivers",
    "VerifyDriverLevel",
    "Remove-Phase1Enumeration.ps1",
    "Test-Phase1EnumerationCleanState.ps1",
    "-ValidateOnly",
    'if ($ValidateOnly.IsPresent)',
    "unload-in-progress",
    "unload-passed",
    "verifier-unload-",
    "No device or driver package was removed",
    "Driver Verifier was not reset",
    "No restart was requested"
)) {
    if ($unload.IndexOf(
            $requiredText,
            [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Required Verifier unload gate is missing: $requiredText"
    }
}
$unloadEnvironmentIndex = $unload.IndexOf(
    "Assert-ComotePhase1RuntimeEnvironment",
    [StringComparison]::OrdinalIgnoreCase
)
$unloadActiveIndex = $unload.IndexOf(
    "Active Driver Verifier is not limited",
    [StringComparison]::OrdinalIgnoreCase
)
$unloadPreflightIndex = $unload.IndexOf(
    "-ValidateOnly",
    $unloadActiveIndex,
    [StringComparison]::OrdinalIgnoreCase
)
$unloadValidateOnlyIndex = $unload.IndexOf(
    'if ($ValidateOnly.IsPresent)',
    $unloadPreflightIndex,
    [StringComparison]::OrdinalIgnoreCase
)
$unloadMutationIndex = $unload.IndexOf(
    '& $removeScript',
    $unloadValidateOnlyIndex,
    [StringComparison]::OrdinalIgnoreCase
)
$unloadStartedIndex = $unload.IndexOf(
    '-Value "unload-in-progress"',
    $unloadValidateOnlyIndex,
    [StringComparison]::OrdinalIgnoreCase
)
$unloadCleanIndex = $unload.IndexOf(
    '& $cleanStateScript',
    $unloadMutationIndex,
    [StringComparison]::OrdinalIgnoreCase
)
$unloadReceiptIndex = $unload.IndexOf(
    '-Value "unload-passed"',
    $unloadCleanIndex,
    [StringComparison]::OrdinalIgnoreCase
)
if ($unloadEnvironmentIndex -lt 0 -or
    $unloadActiveIndex -lt $unloadEnvironmentIndex -or
    $unloadPreflightIndex -lt $unloadActiveIndex -or
    $unloadValidateOnlyIndex -lt $unloadPreflightIndex -or
    $unloadStartedIndex -lt $unloadValidateOnlyIndex -or
    $unloadMutationIndex -lt $unloadStartedIndex -or
    $unloadCleanIndex -lt $unloadMutationIndex -or
    $unloadReceiptIndex -lt $unloadCleanIndex) {
    throw "Verifier unload validation, mutation, and audit order is invalid."
}
foreach ($forbiddenText in @(
    "verifier.exe /standard",
    "verifier.exe /flags",
    "verifier.exe /driver",
    "verifier.exe /bootmode",
    "verifier.exe /reset",
    "verifier.exe /volatile",
    "bcdedit.exe /set",
    "devgen.exe",
    "pnputil.exe",
    "sc.exe delete",
    "shutdown.exe",
    "Restart-Computer",
    "Stop-Computer",
    "SetSuspendState",
    "VhfReadReportSubmit",
    "DeviceIoControl"
)) {
    if ($unload.IndexOf(
            $forbiddenText,
            [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Verifier unload crossed its safety boundary: $forbiddenText"
    }
}
$reset = Get-Content -LiteralPath $resetPath -Raw
foreach ($requiredText in @(
    "AcknowledgeVerifierReset",
    "Assert-ComotePhase1RuntimeEnvironment",
    "verifier-activation.json",
    "configured-oneboot",
    "active-verified",
    "unload-in-progress",
    "unload-passed",
    "ComoteVirtualHid.sys",
    "pendingTargetIsComote",
    "activeQuery",
    "activeTargetIsComote",
    "otherActiveTargets",
    "unloadReceiptAllowsEmptyTargets",
    "ValidateOnly",
    "verifier.exe /reset",
    "reset-requested",
    "No restart was requested"
)) {
    if ($reset.IndexOf(
            $requiredText,
            [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Required Verifier reset gate is missing: $requiredText"
    }
}
$resetValidateIndex = $reset.IndexOf(
    'if ($ValidateOnly.IsPresent)',
    [StringComparison]::OrdinalIgnoreCase
)
$resetMutationIndex = $reset.IndexOf(
    "verifier.exe /reset",
    $resetValidateIndex,
    [StringComparison]::OrdinalIgnoreCase
)
if ($resetValidateIndex -lt 0 -or
    $resetMutationIndex -lt $resetValidateIndex) {
    throw "Verifier reset validation must precede reset."
}
foreach ($forbiddenText in @(
    "verifier.exe /standard",
    "verifier.exe /flags",
    "verifier.exe /driver",
    "verifier.exe /bootmode",
    "verifier.exe /volatile",
    "/all",
    "bcdedit.exe",
    "devgen.exe",
    "pnputil.exe",
    "shutdown.exe",
    "Restart-Computer",
    "Stop-Computer"
)) {
    if ($reset.IndexOf(
            $forbiddenText,
            [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Verifier reset crossed its safety boundary: $forbiddenText"
    }
}

Write-Host "Phase 1 Driver Verifier boundaries verified."
