#Requires -Version 5.1
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$commonPath = Join-Path $PSScriptRoot "Phase1Runtime.Common.ps1"
$preparePath = Join-Path $PSScriptRoot "Prepare-Phase1TestSigning.ps1"
$enablePath = Join-Path $PSScriptRoot "Enable-Phase1TestMode.ps1"
$activeTestModePath = Join-Path $PSScriptRoot "Test-Phase1ActiveTestMode.ps1"
$readyPath = Join-Path $PSScriptRoot "Test-Phase1TestModeReady.ps1"
$restorePath = Join-Path $PSScriptRoot "Restore-Phase1TestSigningState.ps1"
$cleanStatePath = Join-Path $PSScriptRoot "Test-Phase1CleanSigningState.ps1"
$requiredFiles = @(
    $commonPath,
    $preparePath,
    $enablePath,
    $activeTestModePath,
    $readyPath,
    $restorePath,
    $cleanStatePath
)
foreach ($path in $requiredFiles) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required Phase 1 signing source file is missing: $path"
    }
    $sourceText = Get-Content -LiteralPath $path -Raw
    if ($sourceText -match '[^\x00-\x7F]') {
        throw "Runtime PowerShell must remain ASCII-compatible for Windows PowerShell 5.1: $path"
    }
}

$common = Get-Content -LiteralPath $commonPath -Raw
foreach ($requiredText in @(
    "Test-ComotePhase1VirtualMachine",
    "Assert-ComotePhase1RuntimeEnvironment",
    "Windows 10",
    "19045",
    "Confirm-SecureBootUEFI",
    "Get-BitLockerVolume",
    'bcdedit.exe /enum "{current}"',
    "ROOT\COMOTEVIRTUALHID",
    "sc.exe query ComoteVirtualHid",
    "X509EnhancedKeyUsageExtension",
    "Remove-ComotePhase1TestCertificate",
    "Remove-Item -Path `$copy.PSPath -DeleteKey",
    "Set-ComotePhase1NoteProperty",
    "Add-Member",
    "Get-ComotePhase1ActiveCodeIntegrityState",
    "NtQuerySystemInformation",
    "SystemCodeIntegrityInformation",
    "TestSigningActive",
    "-band 0x02"
)) {
    if ($common.IndexOf(
            $requiredText,
            [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Required common runtime gate is missing: $requiredText"
    }
}
foreach ($forbiddenText in @(
    "devgen.exe",
    "pnputil.exe",
    "verifier.exe",
    "shutdown.exe",
    "Restart-Computer"
)) {
    if ($common.IndexOf(
            $forbiddenText,
            [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Common runtime guard must not mutate driver state: $forbiddenText"
    }
}

$prepare = Get-Content -LiteralPath $preparePath -Raw
foreach ($requiredText in @(
    "Invoke-Phase1RuntimePreflight.ps1",
    "New-SelfSignedCertificate",
    "-Type CodeSigningCert",
    "-KeyExportPolicy NonExportable",
    "Cert:\LocalMachine\Root",
    "Cert:\LocalMachine\TrustedPublisher",
    "signtool.exe",
    "inf2cat.exe",
    "sign /v /fd SHA256",
    "verify /v /pa",
    "Test-ComotePhase1CodeSigningEku",
    "Remove-ComotePhase1TestCertificate",
    "Post-failure cleanup audit did not pass",
    "test-signing-preparation.json",
    "No device was created and no driver was installed",
    "TESTSIGNING is still off"
)) {
    if ($prepare.IndexOf(
            $requiredText,
            [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Required test-signing preparation gate is missing: $requiredText"
    }
}
$prepareGuardIndex = $prepare.IndexOf(
    "Assert-ComotePhase1RuntimeEnvironment",
    [StringComparison]::OrdinalIgnoreCase
)
$preparePreflightIndex = $prepare.IndexOf(
    "Invoke-Phase1RuntimePreflight.ps1",
    [StringComparison]::OrdinalIgnoreCase
)
$prepareMutationIndex = $prepare.IndexOf(
    "New-SelfSignedCertificate",
    [StringComparison]::OrdinalIgnoreCase
)
if ($prepareGuardIndex -lt 0 -or
    $preparePreflightIndex -lt $prepareGuardIndex -or
    $prepareMutationIndex -lt $preparePreflightIndex) {
    throw "VM and read-only preflight gates must run before certificate creation."
}
foreach ($forbiddenText in @(
    "EnhancedKeyUsageList",
    "verify /v /kp",
    "bcdedit.exe /set",
    "devgen.exe",
    "pnputil.exe",
    "verifier.exe",
    "shutdown.exe",
    "Restart-Computer"
)) {
    if ($prepare.IndexOf(
            $forbiddenText,
            [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Signing preparation crossed its safety boundary: $forbiddenText"
    }
}

$enable = Get-Content -LiteralPath $enablePath -Raw
$enableGuardIndex = $enable.IndexOf(
    "Assert-ComotePhase1RuntimeEnvironment",
    [StringComparison]::OrdinalIgnoreCase
)
$enableMutationIndex = $enable.IndexOf(
    "& bcdedit.exe",
    [StringComparison]::OrdinalIgnoreCase
)
$enableValidateOnlyIndex = $enable.IndexOf(
    'if ($ValidateOnly.IsPresent)',
    [StringComparison]::OrdinalIgnoreCase
)
if ($enableGuardIndex -lt 0 -or
    $enableValidateOnlyIndex -lt $enableGuardIndex -or
    $enableMutationIndex -lt $enableValidateOnlyIndex) {
    throw "The VM guard must run before the TESTSIGNING BCD change."
}
foreach ($requiredText in @(
    "testsigning",
    "on 2>&1",
    "test-mode-reboot-required",
    "ValidateOnly",
    "signed-package and BCD rollback state is ready",
    "Set-ComotePhase1NoteProperty",
    "Restart the VM manually",
    "No device was created and no driver was installed"
)) {
    if ($enable.IndexOf(
            $requiredText,
            [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Required TESTSIGNING enable gate is missing: $requiredText"
    }
}
foreach ($forbiddenText in @(
    "New-SelfSignedCertificate",
    "Import-Certificate",
    "devgen.exe",
    "pnputil.exe",
    "verifier.exe",
    "shutdown.exe",
    "Restart-Computer"
)) {
    if ($enable.IndexOf(
            $forbiddenText,
            [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "TESTSIGNING enable crossed its safety boundary: $forbiddenText"
    }
}

$activeTestMode = Get-Content -LiteralPath $activeTestModePath -Raw
foreach ($requiredText in @(
    "Assert-ComotePhase1RuntimeEnvironment",
    "Assert-ComotePhase1SigningPrerequisites",
    "Assert-ComotePhase1NoInstalledDevice",
    "Get-ComotePhase1TestSigningState",
    "Get-ComotePhase1ActiveCodeIntegrityState",
    "TestSigningActive",
    "No system state was changed"
)) {
    if ($activeTestMode.IndexOf(
            $requiredText,
            [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Required active test-mode gate is missing: $requiredText"
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
    if ($activeTestMode.IndexOf(
            $forbiddenText,
            [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Active test-mode check must remain read-only: $forbiddenText"
    }
}

$ready = Get-Content -LiteralPath $readyPath -Raw
foreach ($requiredText in @(
    "Assert-ComotePhase1RuntimeEnvironment",
    "Get-ComotePhase1TestSigningState",
    "Get-ComotePhase1ActiveCodeIntegrityState",
    "TestSigningActive",
    "activeCodeIntegrityOptions",
    "test-mode-ready",
    "Win32_DeviceGuard",
    "Set-ComotePhase1NoteProperty",
    "No device was created and no driver was installed"
)) {
    if ($ready.IndexOf(
            $requiredText,
            [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Required post-reboot readiness gate is missing: $requiredText"
    }
}
foreach ($forbiddenText in @(
    "New-SelfSignedCertificate",
    "Import-Certificate",
    "bcdedit.exe /set",
    "devgen.exe",
    "pnputil.exe",
    "verifier.exe",
    "shutdown.exe",
    "Restart-Computer"
)) {
    if ($ready.IndexOf(
            $forbiddenText,
            [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Post-reboot readiness must remain non-installing: $forbiddenText"
    }
}

$restore = Get-Content -LiteralPath $restorePath -Raw
foreach ($requiredText in @(
    "Assert-ComotePhase1RuntimeEnvironment",
    "Assert-ComotePhase1NoInstalledDevice",
    "testsigning",
    "off 2>&1",
    "Remove-ComotePhase1TestCertificate",
    "Set-ComotePhase1NoteProperty",
    "Restart the VM manually"
)) {
    if ($restore.IndexOf(
            $requiredText,
            [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Required signing-state restoration gate is missing: $requiredText"
    }
}
foreach ($forbiddenText in @(
    "New-SelfSignedCertificate",
    "Import-Certificate",
    "devgen.exe",
    "pnputil.exe",
    "verifier.exe",
    "shutdown.exe",
    "Restart-Computer"
)) {
    if ($restore.IndexOf(
            $forbiddenText,
            [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Signing-state restoration crossed its safety boundary: $forbiddenText"
    }
}

$cleanState = Get-Content -LiteralPath $cleanStatePath -Raw
foreach ($requiredText in @(
    "Assert-ComotePhase1RuntimeEnvironment",
    "Assert-ComotePhase1NoInstalledDevice",
    "Get-ComotePhase1TestSigningState",
    "phase1-test-signed",
    "test-signing-preparation.json",
    "CN=Comote Phase 1 VM Test Signing",
    "Phase 1 signing state is clean"
)) {
    if ($cleanState.IndexOf(
            $requiredText,
            [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Required clean-state audit gate is missing: $requiredText"
    }
}
foreach ($forbiddenText in @(
    "New-SelfSignedCertificate",
    "Import-Certificate",
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
        throw "Clean-state audit must remain read-only: $forbiddenText"
    }
}
Write-Host "Phase 1 test-signing boundaries verified."
