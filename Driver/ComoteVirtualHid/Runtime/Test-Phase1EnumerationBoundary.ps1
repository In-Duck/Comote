#Requires -Version 5.1
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$commonPath = Join-Path $PSScriptRoot "Phase1Runtime.Common.ps1"
$installPath = Join-Path $PSScriptRoot "Install-Phase1Enumeration.ps1"
$removePath = Join-Path $PSScriptRoot "Remove-Phase1Enumeration.ps1"
$cleanStatePath = Join-Path $PSScriptRoot "Test-Phase1EnumerationCleanState.ps1"
$diagnosticsPath = Join-Path `
    $PSScriptRoot `
    "Collect-Phase1EnumerationDiagnostics.ps1"
$repairPath = Join-Path `
    $PSScriptRoot `
    "Repair-Phase1RemovedEnumerationState.ps1"
$stressPath = Join-Path `
    $PSScriptRoot `
    "Invoke-Phase1EnumerationStress.ps1"
$rebootPath = Join-Path `
    $PSScriptRoot `
    "Test-Phase1InstalledAfterReboot.ps1"
foreach ($path in @(
    $commonPath,
    $installPath,
    $removePath,
    $cleanStatePath,
    $diagnosticsPath,
    $repairPath,
    $stressPath,
    $rebootPath
)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required Phase 1 enumeration source file is missing: $path"
    }
    $sourceText = Get-Content -LiteralPath $path -Raw
    if ($sourceText -match '[^\x00-\x7F]') {
        throw "Runtime PowerShell must remain ASCII-compatible for Windows PowerShell 5.1: $path"
    }
}

$install = Get-Content -LiteralPath $installPath -Raw
foreach ($requiredText in @(
    "Assert-ComotePhase1RuntimeEnvironment",
    "Assert-ComotePhase1SigningPrerequisites",
    "Get-ComotePhase1TestSigningState",
    "Get-ComotePhase1ActiveCodeIntegrityState",
    "TestSigningActive",
    "activeCodeIntegrityOptions",
    "test-mode-ready",
    "testModeSnapshotName",
    "priorRemovalReceipt.status",
    'status -ne "removed"',
    "enumeration-history",
    "Move-Item",
    "cycleId",
    "installedBootUtc",
    "LastBootUpTime",
    "priorRemovalReceiptArchivePath",
    "Get-FileHash",
    "Get-AuthenticodeSignature",
    "Get-WindowsDriver",
    "Get-PnpDevice",
    "Get-PnpDeviceProperty",
    "DEVPKEY_Device_Parent",
    "ValidateOnly",
    "pnputil.exe",
    "/add-driver",
    "/install 2>&1",
    "bindExitCode",
    "3010",
    "259",
    "devgen.exe",
    "/bus ROOT",
    "/instanceid COMOTE_PHASE1",
    '/hardwareid "ROOT\COMOTEVIRTUALHID"',
    "ConfigManagerErrorCode",
    "Win32_SystemDriver",
    "installed-enumerated",
    "enumeration-installation.json",
    "Invoke-ComotePhase1InstallRollback",
    "Collect-Phase1EnumerationDiagnostics.ps1",
    "FailureMessage",
    "Rollback completed. Diagnostics",
    "/delete-driver",
    "/uninstall",
    "PackageWasStaged",
    "No input-report submission path exists"
)) {
    if ($install.IndexOf(
            $requiredText,
            [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Required Phase 1 installation gate is missing: $requiredText"
    }
}
$installGuardIndex = $install.IndexOf(
    "Assert-ComotePhase1RuntimeEnvironment",
    [StringComparison]::OrdinalIgnoreCase
)
$installActiveTestModeIndex = $install.IndexOf(
    "Get-ComotePhase1ActiveCodeIntegrityState",
    [StringComparison]::OrdinalIgnoreCase
)
$installValidateOnlyIndex = $install.IndexOf(
    'if ($ValidateOnly.IsPresent)',
    [StringComparison]::OrdinalIgnoreCase
)
$installArchiveIndex = $install.IndexOf(
    "Move-Item",
    [StringComparison]::OrdinalIgnoreCase
)
$installMutationIndex = $install.IndexOf(
    "/add-driver",
    [StringComparison]::OrdinalIgnoreCase
)
$deviceCreateIndex = $install.IndexOf(
    "/instanceid COMOTE_PHASE1",
    $installMutationIndex,
    [StringComparison]::OrdinalIgnoreCase
)
$explicitBindIndex = $install.IndexOf(
    "/install 2>&1",
    $deviceCreateIndex,
    [StringComparison]::OrdinalIgnoreCase
)
if ($installGuardIndex -lt 0 -or
    $installActiveTestModeIndex -lt $installGuardIndex -or
    $installValidateOnlyIndex -lt $installActiveTestModeIndex -or
    $installArchiveIndex -lt $installValidateOnlyIndex -or
    $installMutationIndex -lt $installArchiveIndex -or
    $deviceCreateIndex -lt $installMutationIndex -or
    $explicitBindIndex -lt $deviceCreateIndex) {
    throw "VM, active TESTSIGNING, ValidateOnly, receipt archive, device creation, and explicit binding gates are out of order."
}
foreach ($forbiddenText in @(
    "New-SelfSignedCertificate",
    "Import-Certificate",
    "bcdedit.exe /set",
    "/force",
    "/reboot",
    "shutdown.exe",
    "Restart-Computer",
    "verifier.exe",
    "VhfReadReportSubmit",
    "DeviceIoControl"
)) {
    if ($install.IndexOf(
            $forbiddenText,
            [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Phase 1 installation crossed its safety boundary: $forbiddenText"
    }
}

$remove = Get-Content -LiteralPath $removePath -Raw
foreach ($requiredText in @(
    "Assert-ComotePhase1RuntimeEnvironment",
    "Get-ComotePhase1TestSigningState",
    "installed-enumerated",
    "enumeration-installation.json",
    "Get-PnpDeviceProperty",
    "DEVPKEY_Device_Parent",
    "Class Keyboard",
    "Class Mouse",
    "current VHF keyboard and mouse children",
    "ValidateOnly",
    "devgen.exe",
    "/remove",
    "/subtree",
    "pnputil.exe",
    "/delete-driver",
    "/uninstall",
    "Repair-Phase1RemovedEnumerationState.ps1",
    "orphanServiceExitCode",
    "Assert-ComotePhase1NoInstalledDevice",
    "Set-ComotePhase1NoteProperty",
    'Value "removed"',
    "test certificate and TESTSIGNING state were not changed"
)) {
    if ($remove.IndexOf(
            $requiredText,
            [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Required Phase 1 removal gate is missing: $requiredText"
    }
}
$removeGuardIndex = $remove.IndexOf(
    "Assert-ComotePhase1RuntimeEnvironment",
    [StringComparison]::OrdinalIgnoreCase
)
$removeValidateOnlyIndex = $remove.IndexOf(
    'if ($ValidateOnly.IsPresent)',
    [StringComparison]::OrdinalIgnoreCase
)
$removeMutationIndex = $remove.IndexOf(
    '& $devGen',
    [StringComparison]::OrdinalIgnoreCase
)
if ($removeGuardIndex -lt 0 -or
    $removeValidateOnlyIndex -lt $removeGuardIndex -or
    $removeMutationIndex -lt $removeValidateOnlyIndex) {
    throw "VM and ValidateOnly gates must run before device removal."
}
foreach ($forbiddenText in @(
    "New-SelfSignedCertificate",
    "Import-Certificate",
    "bcdedit.exe /set",
    "/force",
    "/reboot",
    "shutdown.exe",
    "Restart-Computer",
    "verifier.exe",
    "VhfReadReportSubmit",
    "DeviceIoControl"
)) {
    if ($remove.IndexOf(
            $forbiddenText,
            [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Phase 1 removal crossed its safety boundary: $forbiddenText"
    }
}

$cleanState = Get-Content -LiteralPath $cleanStatePath -Raw
foreach ($requiredText in @(
    "Assert-ComotePhase1RuntimeEnvironment",
    "Assert-ComotePhase1NoInstalledDevice",
    "Get-WindowsDriver",
    "Get-PnpDevice",
    "enumeration-installation.json",
    "No system state was changed"
)) {
    if ($cleanState.IndexOf(
            $requiredText,
            [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Required enumeration clean-state gate is missing: $requiredText"
    }
}
foreach ($forbiddenText in @(
    "New-SelfSignedCertificate",
    "Import-Certificate",
    "Set-Content",
    "Remove-Item",
    "bcdedit.exe /set",
    "devgen.exe",
    "pnputil.exe",
    "verifier.exe",
    "shutdown.exe",
    "Restart-Computer"
)) {
    if ($cleanState.IndexOf(
            $forbiddenText,
            [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Enumeration clean-state audit must remain read-only: $forbiddenText"
    }
}

$repair = Get-Content -LiteralPath $repairPath -Raw
foreach ($requiredText in @(
    "Assert-ComotePhase1RuntimeEnvironment",
    "Get-WindowsDriver",
    "Get-PnpDevice",
    "enumeration-installation.json",
    "installed-enumerated",
    "CurrentControlSet\Services\ComoteVirtualHid",
    "serviceRegistry.Type",
    "serviceRegistry.Start",
    "DriverStore\\FileRepository",
    "Get-Service",
    "ServiceControllerStatus]::Stopped",
    "ValidateOnly",
    "sc.exe delete ComoteVirtualHid",
    "1060",
    'Name "status"',
    'Value "removed"',
    "orphaned-service-deleted",
    "Assert-ComotePhase1NoInstalledDevice"
)) {
    if ($repair.IndexOf(
            $requiredText,
            [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Required partial-removal recovery gate is missing: $requiredText"
    }
}
$repairGuardIndex = $repair.IndexOf(
    "Assert-ComotePhase1RuntimeEnvironment",
    [StringComparison]::OrdinalIgnoreCase
)
$repairStoppedIndex = $repair.IndexOf(
    "ServiceControllerStatus]::Stopped",
    [StringComparison]::OrdinalIgnoreCase
)
$repairValidateOnlyIndex = $repair.IndexOf(
    'if ($ValidateOnly.IsPresent)',
    [StringComparison]::OrdinalIgnoreCase
)
$repairMutationIndex = $repair.IndexOf(
    "sc.exe delete ComoteVirtualHid",
    [StringComparison]::OrdinalIgnoreCase
)
if ($repairGuardIndex -lt 0 -or
    $repairStoppedIndex -lt $repairGuardIndex -or
    $repairValidateOnlyIndex -lt $repairStoppedIndex -or
    $repairMutationIndex -lt $repairValidateOnlyIndex) {
    throw "VM, stopped-service, and ValidateOnly recovery gates must run before service deletion."
}
foreach ($forbiddenText in @(
    "New-SelfSignedCertificate",
    "Import-Certificate",
    "Remove-Item",
    "bcdedit.exe /set",
    "devgen.exe",
    "pnputil.exe",
    "/force",
    "/reboot",
    "verifier.exe",
    "shutdown.exe",
    "Restart-Computer"
)) {
    if ($repair.IndexOf(
            $forbiddenText,
            [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Partial-removal recovery crossed its safety boundary: $forbiddenText"
    }
}

$removePackageGoneIndex = $remove.IndexOf(
    "The Comote package remained in the Driver Store.",
    [StringComparison]::OrdinalIgnoreCase
)
$removeRepairCallIndex = $remove.IndexOf(
    "Repair-Phase1RemovedEnumerationState.ps1",
    $removePackageGoneIndex,
    [StringComparison]::OrdinalIgnoreCase
)
$removeFinalAuditIndex = $remove.LastIndexOf(
    "Assert-ComotePhase1NoInstalledDevice",
    [StringComparison]::OrdinalIgnoreCase
)
if ($removePackageGoneIndex -lt 0 -or
    $removeRepairCallIndex -lt $removePackageGoneIndex -or
    $removeFinalAuditIndex -lt $removeRepairCallIndex) {
    throw "Orphan-service recovery must run after package removal and before the final audit."
}

$reboot = Get-Content -LiteralPath $rebootPath -Raw
foreach ($requiredText in @(
    "Assert-ComotePhase1RuntimeEnvironment",
    "Assert-ComotePhase1SigningPrerequisites",
    "Get-ComotePhase1TestSigningState",
    "Get-ComotePhase1ActiveCodeIntegrityState",
    "installed-enumerated",
    "installedBootUtc",
    "LastBootUpTime",
    'currentBootUtc -le $installedBootUtc',
    "Get-WindowsDriver",
    "Win32_PnPEntity",
    "ConfigManagerErrorCode",
    "Win32_SystemDriver",
    'State -ne "Running"',
    "Get-PnpDeviceProperty",
    "DEVPKEY_Device_Parent",
    "Class Keyboard",
    "Class Mouse",
    "phase1-reboot-verification",
    "No input reports were submitted"
)) {
    if ($reboot.IndexOf(
            $requiredText,
            [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Required post-reboot verification gate is missing: $requiredText"
    }
}
foreach ($forbiddenText in @(
    "New-SelfSignedCertificate",
    "Import-Certificate",
    "Remove-Item",
    "bcdedit.exe /set",
    "devgen.exe",
    "pnputil.exe",
    "sc.exe delete",
    "verifier.exe",
    "shutdown.exe",
    "Restart-Computer",
    "VhfReadReportSubmit",
    "DeviceIoControl"
)) {
    if ($reboot.IndexOf(
            $forbiddenText,
            [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Post-reboot verification crossed its safety boundary: $forbiddenText"
    }
}

$stress = Get-Content -LiteralPath $stressPath -Raw
foreach ($requiredText in @(
    "ValidateRange(1, 5)",
    "Test-Phase1EnumerationBoundary.ps1",
    "Test-Phase1EnumerationCleanState.ps1",
    "Test-Phase1ActiveTestMode.ps1",
    "Install-Phase1Enumeration.ps1",
    "Remove-Phase1Enumeration.ps1",
    "-ValidateOnly",
    "enumeration-installation.json",
    "cycleId",
    "enumeration-stress",
    'Status "failed"',
    'Status "passed"',
    "Final state: clean"
)) {
    if ($stress.IndexOf(
            $requiredText,
            [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Required enumeration stress gate is missing: $requiredText"
    }
}
foreach ($forbiddenText in @(
    "New-SelfSignedCertificate",
    "Import-Certificate",
    "Remove-Item",
    "bcdedit.exe",
    "devgen.exe",
    "pnputil.exe",
    "sc.exe",
    "verifier.exe",
    "shutdown.exe",
    "Restart-Computer",
    "VhfReadReportSubmit",
    "DeviceIoControl"
)) {
    if ($stress.IndexOf(
            $forbiddenText,
            [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Enumeration stress runner crossed its safety boundary: $forbiddenText"
    }
}

$stressBoundaryIndex = $stress.IndexOf(
    "Test-Phase1EnumerationBoundary.ps1",
    [StringComparison]::OrdinalIgnoreCase
)
$stressInstallIndex = $stress.IndexOf(
    "Install-Phase1Enumeration.ps1",
    [StringComparison]::OrdinalIgnoreCase
)
$stressRemoveIndex = $stress.IndexOf(
    "Remove-Phase1Enumeration.ps1",
    [StringComparison]::OrdinalIgnoreCase
)
$stressCleanAuditIndex = $stress.LastIndexOf(
    "Test-Phase1EnumerationCleanState.ps1",
    [StringComparison]::OrdinalIgnoreCase
)
if ($stressBoundaryIndex -lt 0 -or
    $stressInstallIndex -lt $stressBoundaryIndex -or
    $stressRemoveIndex -lt $stressInstallIndex -or
    $stressCleanAuditIndex -lt $stressRemoveIndex) {
    throw "Stress-test safety calls are out of order."
}

$diagnostics = Get-Content -LiteralPath $diagnosticsPath -Raw
foreach ($requiredText in @(
    "Assert-ComotePhase1RuntimeEnvironment",
    "Get-ComotePhase1ActiveCodeIntegrityState",
    "Win32_PnPEntity",
    "Win32_SystemDriver",
    "Get-WindowsDriver",
    "sc.exe queryex",
    "setupapi.dev.log",
    "Get-WinEvent",
    "Microsoft-Windows-CodeIntegrity/Operational",
    "No device or driver state was changed"
)) {
    if ($diagnostics.IndexOf(
            $requiredText,
            [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Required enumeration diagnostic gate is missing: $requiredText"
    }
}
foreach ($forbiddenText in @(
    "New-SelfSignedCertificate",
    "Import-Certificate",
    "Remove-Item",
    "bcdedit.exe /set",
    "devgen.exe",
    "pnputil.exe /add-driver",
    "pnputil.exe /delete-driver",
    "verifier.exe",
    "shutdown.exe",
    "Restart-Computer"
)) {
    if ($diagnostics.IndexOf(
            $forbiddenText,
            [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Enumeration diagnostics crossed its read-only boundary: $forbiddenText"
    }
}

$mainCatchIndex = $install.LastIndexOf(
    '$installationError = $_',
    [StringComparison]::OrdinalIgnoreCase
)
$diagnosticCallIndex = $install.IndexOf(
    "Collect-Phase1EnumerationDiagnostics.ps1",
    $mainCatchIndex,
    [StringComparison]::OrdinalIgnoreCase
)
$rollbackCallIndex = $install.IndexOf(
    "Invoke-ComotePhase1InstallRollback",
    $mainCatchIndex,
    [StringComparison]::OrdinalIgnoreCase
)
if ($mainCatchIndex -lt 0 -or
    $diagnosticCallIndex -lt $mainCatchIndex -or
    $rollbackCallIndex -lt $diagnosticCallIndex) {
    throw "Failure diagnostics must be captured before automatic rollback."
}

Write-Host "Phase 1 enumeration boundaries verified."
