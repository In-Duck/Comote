#Requires -Version 5.1
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$runtimeFiles = [ordered]@{
    "Phase2Runtime.Common.ps1" = Join-Path $PSScriptRoot "Phase2Runtime.Common.ps1"
    "Prepare-Phase2TestSigning.ps1" = Join-Path $PSScriptRoot "Prepare-Phase2TestSigning.ps1"
    "Restore-Phase2TestSigningState.ps1" = Join-Path $PSScriptRoot "Restore-Phase2TestSigningState.ps1"
    "Test-Phase2CleanSigningState.ps1" = Join-Path $PSScriptRoot "Test-Phase2CleanSigningState.ps1"
    "Phase2Enumeration.Common.ps1" = Join-Path $PSScriptRoot "Phase2Enumeration.Common.ps1"
}
foreach ($entry in $runtimeFiles.GetEnumerator()) {
    if (-not (Test-Path -LiteralPath $entry.Value -PathType Leaf)) {
        throw "Required Phase 2 signing gate is missing: $($entry.Value)"
    }
    $runtimeSource = Get-Content -LiteralPath $entry.Value -Raw
    if ($runtimeSource -match '[^\x00-\x7F]') {
        throw "Runtime PowerShell must remain ASCII-compatible: $($entry.Value)"
    }
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$sourceBaseline = [ordered]@{
    "Build-Phase2.ps1" = "5309BDF08A674DA798141F3BA4A517A9217E5575DE3B2C96FA8E44241E5237B8"
    "ComoteVirtualHidPhase2.c" = "AFE6C35A009A5767D16BC58BC73DEF63B350F752C713C567F6C5C81F33586974"
    "ComoteVirtualHidPhase2.h" = "CB82C678EFDFBBF1EA63D773799C76C2E18D68B26EE2903392C93E74C564008C"
    "ComoteVirtualHidPhase2.inf" = "ABA4358C2921455BB6D1533FCF0EE38961E503A877ABD13390B6D8B08F851E0F"
    "ComoteVirtualHidPhase2.vcxproj" = "A74B1ACF416168FF9E0B7F5FB72D7475807FBCF702265F4EAEA5F6D020A8765D"
    "ComoteVirtualHidProtocol.h" = "091EE6F6D25891DA0CAA16E991D5E507D5B7EA19B6F83081FD88527F9E9FCD86"
    "Directory.Build.props" = "6B5C0D3BB13AEFB7AE78AA6DBB91A7123990DE7A950C71D091D3EB162183ABA9"
    "Invoke-Phase2VmBuild.ps1" = "B115DA5EDBF80006812134D97F8E13AD564DE6E01399F537264088B7CB31E161"
    "packages.config" = "71B84B64EF1914934FA7E7C845D4BA08FA4E1979A719CF6F9CDD03D60F095688"
    "Probe\ComoteVirtualHidProbe.c" = "ACC950A2DDB0126C51CC212838541BFBF19EA6F0B3EC6DEEAED14457F464AE42"
    "Probe\ComoteVirtualHidProbe.vcxproj" = "0F4338197A5F9C824BD1AF11D13C4174D4987323D3A8B7C9BF50261A2C2011D5"
    "Test-Phase2Boundary.ps1" = "A6524FF132BF3454E0FE7DDE7D097237F5421C4ECF57C91A6AC6FF3CBC1A45AE"
}
foreach ($entry in $sourceBaseline.GetEnumerator()) {
    $sourcePath = Join-Path $projectRoot $entry.Key
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        throw "Pinned Phase 2 source is missing: $sourcePath"
    }
    $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $sourcePath).Hash
    if ($actualHash -ne $entry.Value) {
        throw "Pinned Phase 2 source changed: $sourcePath"
    }
}

$common = Get-Content -LiteralPath $runtimeFiles["Phase2Runtime.Common.ps1"] -Raw
foreach ($requiredText in @(
    "Test-ComotePhase2VirtualMachine",
    "Assert-ComotePhase2RuntimeEnvironment",
    "Assert-ComotePhase2SigningPrerequisites",
    "Assert-ComotePhase2NoInstalledDevice",
    "ROOT\COMOTEVIRTUALHID_PHASE2",
    "Invoke-ComotePhase2NativeCommand",
    "Enter-ComotePhase2RuntimeLock",
    "Exit-ComotePhase2RuntimeLock",
    "AbandonedMutexException",
    '$global:LASTEXITCODE = $null',
    "Write-ComotePhase2JsonAtomically",
    "Read-ComotePhase2JsonDocument",
    "Get-ComotePhase2NamedCertificates",
    "Get-Command",
    '$resolvedPath',
    '$ErrorActionPreference = "Continue"',
    "Get-ComotePhase2TestSigningState",
    "Get-ComotePhase2ActiveCodeIntegrityState",
    "TestSigningActive",
    "HvciKernelModeActive",
    "Test-ComotePhase2CodeSigningEku",
    "Remove-ComotePhase2TestCertificate"
)) {
    if ($common.IndexOf($requiredText, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Required Phase 2 common gate is missing: $requiredText"
    }
}
foreach ($forbiddenText in @(
    "devgen.exe",
    "devcon.exe",
    "pnputil.exe",
    "verifier.exe",
    "Restart-Computer",
    "shutdown.exe",
    "DeviceIoControl"
)) {
    if ($common.IndexOf($forbiddenText, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Phase 2 common gate crossed its boundary: $forbiddenText"
    }
}

$prepare = Get-Content -LiteralPath $runtimeFiles["Prepare-Phase2TestSigning.ps1"] -Raw
foreach ($requiredText in @(
    "ValidateOnly",
    "Assert-ComotePhase2RuntimeEnvironment",
    "Assert-ComotePhase2SigningPrerequisites",
    "Assert-ComotePhase2NoInstalledDevice",
    "Get-ComotePhase2TestSigningState",
    "Get-ComotePhase2ActiveCodeIntegrityState",
    "phase2-unsigned",
    "SHA256.json",
    '$manifestDocument -is [Array]',
    "ComoteVirtualHidPhase2.sys",
    "ComoteVirtualHidPhase2.inf",
    "ComoteVirtualHidProbe.exe",
    "New-SelfSignedCertificate",
    "Get-ComotePhase2NamedCertificates",
    "Get-ComotePhase2DriverPackages",
    "Write-ComotePhase2JsonAtomically",
    "Enter-ComotePhase2RuntimeLock",
    "Exit-ComotePhase2RuntimeLock",
    "-Type CodeSigningCert",
    "-KeyExportPolicy NonExportable",
    "-NotAfter (Get-Date).AddDays(365)",
    "Cert:\LocalMachine\Root",
    "Cert:\LocalMachine\TrustedPublisher",
    "signtool.exe",
    "inf2cat.exe",
    '"sign", "/v", "/fd", "SHA256"',
    '"verify", "/v", "/pa"',
    "test-signing-preparation.json",
    "TESTSIGNING remains active and was not changed",
    "No device was created and no driver was installed"
)) {
    if ($prepare.IndexOf($requiredText, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Required Phase 2 signing preparation is missing: $requiredText"
    }
}
$prepareGuardIndex = $prepare.IndexOf("Assert-ComotePhase2RuntimeEnvironment", [StringComparison]::Ordinal)
$prepareHashIndex = $prepare.IndexOf("Unsigned package hash mismatch", [StringComparison]::Ordinal)
$prepareValidateIndex = $prepare.IndexOf('if ($ValidateOnly.IsPresent)', [StringComparison]::Ordinal)
$prepareMutationIndex = $prepare.IndexOf('$certificate = New-SelfSignedCertificate', [StringComparison]::Ordinal)
if ($prepareGuardIndex -lt 0 -or
    $prepareHashIndex -lt $prepareGuardIndex -or
    $prepareValidateIndex -lt $prepareHashIndex -or
    $prepareMutationIndex -lt $prepareValidateIndex) {
    throw "VM, hash, and ValidateOnly gates must run before certificate creation."
}
foreach ($forbiddenText in @(
    "AddDays(14)",
    "bcdedit.exe /set",
    "devgen.exe",
    "devcon.exe",
    "pnputil.exe",
    "verifier.exe",
    "Restart-Computer",
    "shutdown.exe",
    "Start-Service",
    "CreateService",
    "DeviceIoControl"
)) {
    if ($prepare.IndexOf($forbiddenText, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Phase 2 signing preparation crossed its boundary: $forbiddenText"
    }
}

$restore = Get-Content -LiteralPath $runtimeFiles["Restore-Phase2TestSigningState.ps1"] -Raw
foreach ($requiredText in @(
    "ValidateOnly",
    "Assert-ComotePhase2RuntimeEnvironment",
    "Assert-ComotePhase2NoInstalledDevice",
    "testSigningChangedByComote",
    '"signing-removal"',
    "Get-ComotePhase2DriverPackages",
    "Get-ComotePhase2NamedCertificates",
    "Write-ComotePhase2JsonAtomically",
    "Enter-ComotePhase2RuntimeLock",
    "Exit-ComotePhase2RuntimeLock",
    "Remove-ComotePhase2TestCertificate",
    "phase2-test-signed",
    "test-signing-preparation.json",
    "TESTSIGNING was not changed"
)) {
    if ($restore.IndexOf($requiredText, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Required Phase 2 signing rollback is missing: $requiredText"
    }
}
$restoreValidateIndex = $restore.IndexOf('if ($ValidateOnly.IsPresent)', [StringComparison]::Ordinal)
$restoreJournalIndex = $restore.IndexOf(
    '-Value "signing-removal"',
    $restoreValidateIndex,
    [StringComparison]::Ordinal
)
$restorePackageIndex = $restore.IndexOf(
    'Remove-Item `',
    $restoreJournalIndex,
    [StringComparison]::Ordinal
)
$restoreCertificateIndex = $restore.IndexOf(
    "Remove-ComotePhase2TestCertificate",
    $restorePackageIndex,
    [StringComparison]::Ordinal
)
if ($restoreValidateIndex -lt 0 -or
    $restoreJournalIndex -lt $restoreValidateIndex -or
    $restorePackageIndex -lt $restoreJournalIndex -or
    $restoreCertificateIndex -lt $restorePackageIndex) {
    throw "Rollback must validate, journal, remove the package, then remove its certificate."
}
foreach ($forbiddenText in @(
    "AddDays(14)",
    "bcdedit.exe /set",
    "New-SelfSignedCertificate",
    "Import-Certificate",
    "devgen.exe",
    "devcon.exe",
    "pnputil.exe",
    "verifier.exe",
    "Restart-Computer",
    "shutdown.exe"
)) {
    if ($restore.IndexOf($forbiddenText, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Phase 2 signing rollback crossed its boundary: $forbiddenText"
    }
}

$clean = Get-Content -LiteralPath $runtimeFiles["Test-Phase2CleanSigningState.ps1"] -Raw
foreach ($requiredText in @(
    "Assert-ComotePhase2RuntimeEnvironment",
    "Assert-ComotePhase2NoInstalledDevice",
    "phase2-test-signed",
    "test-signing-preparation.json",
    "A Comote Phase 2 VM test certificate is still present.",
    "Get-ComotePhase2NamedCertificates",
    "Get-ComotePhase2DriverPackages",
    "enumeration-transaction.json",
    "Phase 2 signing state is clean",
    "TESTSIGNING was not changed"
)) {
    if ($clean.IndexOf($requiredText, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Required Phase 2 clean-state audit is missing: $requiredText"
    }
}
foreach ($forbiddenText in @(
    "New-SelfSignedCertificate",
    "Import-Certificate",
    "Remove-Item",
    "bcdedit.exe /set",
    "devgen.exe",
    "devcon.exe",
    "pnputil.exe",
    "verifier.exe",
    "Restart-Computer",
    "shutdown.exe"
)) {
    if ($clean.IndexOf($forbiddenText, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Phase 2 clean-state audit must remain read-only: $forbiddenText"
    }
}

Write-Host "Phase 2 test-signing boundaries verified." -ForegroundColor Green