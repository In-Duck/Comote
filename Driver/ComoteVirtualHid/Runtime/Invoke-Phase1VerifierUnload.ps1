#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [switch]$AcknowledgeDisposableVm,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$SnapshotName,

    [Parameter(Mandatory)]
    [ValidatePattern("^comote-phase1-pre-verifier-19045\.[0-9]+$")]
    [string]$VerifierSnapshotName,

    [Parameter(Mandatory)]
    [switch]$AcknowledgeVerifierUnloadRisk,

    [ValidatePattern("^19045$")]
    [string]$RequiredBuildNumber = "19045",

    [switch]$ValidateOnly
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot "Phase1Runtime.Common.ps1")

if (-not $AcknowledgeVerifierUnloadRisk.IsPresent) {
    throw "Explicit Driver Verifier unload-risk acknowledgement is required."
}
$environment = Assert-ComotePhase1RuntimeEnvironment `
    -AcknowledgeDisposableVm:$AcknowledgeDisposableVm `
    -RequiredBuildNumber $RequiredBuildNumber
[void](Assert-ComotePhase1SigningPrerequisites)
if (-not (Get-ComotePhase1TestSigningState)) {
    throw "TESTSIGNING is not configured during Verifier testing."
}
$codeIntegrityState = Get-ComotePhase1ActiveCodeIntegrityState
if (-not $codeIntegrityState.TestSigningActive) {
    throw "The active kernel does not allow test-signed drivers."
}
if (-not (Get-Command verifier.exe -ErrorAction SilentlyContinue)) {
    throw "verifier.exe was not found."
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$stateDirectory = Join-Path `
    $projectRoot `
    "artifacts\phase1-runtime-state"
$installReceiptPath = Join-Path `
    $stateDirectory `
    "enumeration-installation.json"
$activationReceiptPath = Join-Path `
    $stateDirectory `
    "verifier-activation.json"
foreach ($receiptPath in @(
    $installReceiptPath,
    $activationReceiptPath
)) {
    if (-not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) {
        throw "A required Phase 1 receipt is missing: $receiptPath"
    }
}
$installReceipt = Get-Content `
    -LiteralPath $installReceiptPath `
    -Raw |
    ConvertFrom-Json
$activationReceipt = Get-Content `
    -LiteralPath $activationReceiptPath `
    -Raw |
    ConvertFrom-Json
if ([string]$installReceipt.status -ne "installed-enumerated" -or
    [string]$installReceipt.snapshotName -ne $SnapshotName) {
    throw "The installed Phase 1 state is not unload-eligible."
}
if ([string]$activationReceipt.status -ne "active-verified" -or
    [string]$activationReceipt.snapshotName -ne $SnapshotName -or
    [string]$activationReceipt.verifierSnapshotName -ne
        $VerifierSnapshotName -or
    [string]$activationReceipt.targetDriver -ne
        "ComoteVirtualHid.sys" -or
    [string]$activationReceipt.bootMode -ne "oneboot") {
    throw "The active Phase 1 Verifier receipt is not unload-eligible."
}
if ([string]$installReceipt.cycleId -ne
    [string]$activationReceipt.cycleId) {
    throw "The installation and Verifier cycle identifiers do not match."
}

$activeReportProperty =
    $activationReceipt.PSObject.Properties["activeReportPath"]
if ($null -eq $activeReportProperty -or
    [string]::IsNullOrWhiteSpace(
        [string]$activeReportProperty.Value)) {
    throw "The active Verifier report path is missing."
}
$reportDirectory = Join-Path `
    $projectRoot `
    "artifacts\phase1-verifier"
$expectedReportRoot =
    [IO.Path]::GetFullPath($reportDirectory) +
    [IO.Path]::DirectorySeparatorChar
$activeReportPath =
    [IO.Path]::GetFullPath([string]$activeReportProperty.Value)
if (-not $activeReportPath.StartsWith(
        $expectedReportRoot,
        [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetFileName($activeReportPath) -notmatch
        "^verifier-active-\d{8}-\d{6}\.json$" -or
    -not (Test-Path -LiteralPath $activeReportPath -PathType Leaf)) {
    throw "The active Verifier report path is invalid."
}
$activeReport = Get-Content `
    -LiteralPath $activeReportPath `
    -Raw |
    ConvertFrom-Json
if ([string]$activeReport.status -ne "active-verified" -or
    [string]$activeReport.snapshotName -ne $SnapshotName -or
    [string]$activeReport.verifierSnapshotName -ne
        $VerifierSnapshotName -or
    [string]$activeReport.targetDriver -ne
        "ComoteVirtualHid.sys" -or
    [string]$activeReport.cycleId -ne
        [string]$installReceipt.cycleId) {
    throw "The active Verifier report does not match the current cycle."
}

$activeBootUtc = [DateTime]::Parse(
    [string]$activeReport.currentBootUtc,
    [Globalization.CultureInfo]::InvariantCulture,
    [Globalization.DateTimeStyles]::RoundtripKind
).ToUniversalTime()
$currentBootUtc = (
    [DateTime]$environment.OperatingSystem.LastBootUpTime
).ToUniversalTime()
if ([Math]::Abs(
        ($currentBootUtc - $activeBootUtc).TotalSeconds) -gt 2) {
    throw "The VM restarted after the active Verifier report was created."
}

$queryBefore = (& verifier.exe /query 2>&1 | Out-String)
$queryBeforeExitCode = $LASTEXITCODE
$activeDriverNames = @(
    [Regex]::Matches(
        $queryBefore,
        '(?i)\b[A-Za-z0-9_.-]+\.sys\b') |
        ForEach-Object { $_.Value } |
        Sort-Object -Unique
)
if ($queryBeforeExitCode -ne 0 -or
    $activeDriverNames.Count -ne 1 -or
    -not $activeDriverNames[0].Equals(
        "ComoteVirtualHid.sys",
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Active Driver Verifier is not limited to ComoteVirtualHid.sys."
}

$querySettingsBefore =
    (& verifier.exe /querysettings 2>&1 | Out-String)
if ($LASTEXITCODE -ne 0 -or
    $querySettingsBefore.IndexOf(
        "ComoteVirtualHid.sys",
        [StringComparison]::OrdinalIgnoreCase) -ge 0) {
    throw "Oneboot next-boot Verifier settings are not empty."
}
$memoryManagement = Get-ItemProperty `
    -LiteralPath `
        "HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management"
$driversProperty =
    $memoryManagement.PSObject.Properties["VerifyDrivers"]
$levelProperty =
    $memoryManagement.PSObject.Properties["VerifyDriverLevel"]
$nextBootDrivers = if ($null -eq $driversProperty) {
    ""
}
else {
    [string]$driversProperty.Value
}
$nextBootLevel = if ($null -eq $levelProperty) {
    [uint32]0
}
else {
    [uint32]$levelProperty.Value
}
if (-not [string]::IsNullOrWhiteSpace($nextBootDrivers) -or
    $nextBootLevel -ne 0) {
    throw "Oneboot next-boot Verifier registry settings are not empty."
}

$removeScript = Join-Path `
    $PSScriptRoot `
    "Remove-Phase1Enumeration.ps1"
$cleanStateScript = Join-Path `
    $PSScriptRoot `
    "Test-Phase1EnumerationCleanState.ps1"
foreach ($requiredScript in @(
    $removeScript,
    $cleanStateScript
)) {
    if (-not (Test-Path -LiteralPath $requiredScript -PathType Leaf)) {
        throw "A required unload helper is missing: $requiredScript"
    }
}

& $removeScript `
    -AcknowledgeDisposableVm:$AcknowledgeDisposableVm `
    -SnapshotName $SnapshotName `
    -RequiredBuildNumber $RequiredBuildNumber `
    -ValidateOnly

if ($ValidateOnly.IsPresent) {
    Write-Host ""
    Write-Host "Phase 1 Verifier unload validation passed." `
        -ForegroundColor Green
    Write-Host "Active verified target: ComoteVirtualHid.sys only"
    Write-Host "Current VHF keyboard and mouse children: healthy"
    Write-Host "No device or driver package was removed."
    Write-Host "Driver Verifier was not reset."
    Write-Host "No restart was requested."
    return
}

Set-ComotePhase1NoteProperty `
    -InputObject $activationReceipt `
    -Name "status" `
    -Value "unload-in-progress"
Set-ComotePhase1NoteProperty `
    -InputObject $activationReceipt `
    -Name "unloadStartedUtc" `
    -Value ([DateTime]::UtcNow.ToString("o"))
$activationReceipt | ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath $activationReceiptPath -Encoding UTF8

& $removeScript `
    -AcknowledgeDisposableVm:$AcknowledgeDisposableVm `
    -SnapshotName $SnapshotName `
    -RequiredBuildNumber $RequiredBuildNumber
& $cleanStateScript `
    -AcknowledgeDisposableVm:$AcknowledgeDisposableVm `
    -RequiredBuildNumber $RequiredBuildNumber

Start-Sleep -Seconds 5
$queryAfter = (& verifier.exe /query 2>&1 | Out-String)
$queryAfterExitCode = $LASTEXITCODE
if ($queryAfterExitCode -ne 0) {
    throw "Unable to query Driver Verifier after unload."
}
$otherVerifiedDrivers = @(
    [Regex]::Matches(
        $queryAfter,
        '(?i)\b[A-Za-z0-9_.-]+\.sys\b') |
        ForEach-Object { $_.Value } |
        Where-Object {
            -not $_.Equals(
                "ComoteVirtualHid.sys",
                [StringComparison]::OrdinalIgnoreCase)
        } |
        Sort-Object -Unique
)
if ($otherVerifiedDrivers.Count -ne 0) {
    throw "Another driver appeared in the active Verifier session."
}

$completedUtc = [DateTime]::UtcNow.ToString("o")
New-Item -ItemType Directory -Path $reportDirectory -Force |
    Out-Null
$unloadReportPath = Join-Path `
    $reportDirectory `
    ("verifier-unload-{0}.json" -f
        (Get-Date -Format "yyyyMMdd-HHmmss"))
$report = [ordered]@{
    completedUtc = $completedUtc
    status = "unload-passed"
    snapshotName = $SnapshotName
    verifierSnapshotName = $VerifierSnapshotName
    cycleId = [string]$installReceipt.cycleId
    currentBootUtc = $currentBootUtc.ToString("o")
    targetDriver = "ComoteVirtualHid.sys"
    activeReportPath = $activeReportPath
    verifierQueryBefore = $queryBefore.Trim()
    verifierQueryAfter = $queryAfter.Trim()
    nextBootVerifyDrivers = $nextBootDrivers
    nextBootVerifyDriverLevel = $nextBootLevel
}
$report | ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath $unloadReportPath -Encoding UTF8
Set-ComotePhase1NoteProperty `
    -InputObject $activationReceipt `
    -Name "status" `
    -Value "unload-passed"
Set-ComotePhase1NoteProperty `
    -InputObject $activationReceipt `
    -Name "unloadPassedUtc" `
    -Value $completedUtc
Set-ComotePhase1NoteProperty `
    -InputObject $activationReceipt `
    -Name "unloadReportPath" `
    -Value $unloadReportPath
$activationReceipt | ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath $activationReceiptPath -Encoding UTF8

Write-Host ""
Write-Host "Phase 1 driver unloaded cleanly under Driver Verifier." `
    -ForegroundColor Green
Write-Host "Root device, VHF children, service, and Driver Store package: removed"
Write-Host "Driver Verifier was not reset."
Write-Host "Do not restart until the reset gate is validated."
Write-Host "Report: $unloadReportPath"
