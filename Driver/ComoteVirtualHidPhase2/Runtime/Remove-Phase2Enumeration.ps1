#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [switch]$AcknowledgeDisposableVm,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$RecoverySnapshotName,

    [ValidatePattern("^19045$")]
    [string]$RequiredBuildNumber = "19045",

    [switch]$ValidateOnly
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot "Phase2Runtime.Common.ps1")
. (Join-Path $PSScriptRoot "Phase2Enumeration.Common.ps1")

$runtimeLock = Enter-ComotePhase2RuntimeLock
try {

[void](Assert-ComotePhase2RuntimeEnvironment `
    -AcknowledgeDisposableVm:$AcknowledgeDisposableVm `
    -RequiredBuildNumber $RequiredBuildNumber)

$projectRoot = Split-Path -Parent $PSScriptRoot
$installReceiptPath = Join-Path `
    $projectRoot `
    "artifacts\phase2-runtime-state\enumeration-installation.json"
if (-not (Test-Path -LiteralPath $installReceiptPath -PathType Leaf)) {
    throw "The Phase 2 installation receipt was not found."
}
$installReceipt = Read-ComotePhase2JsonDocument `
    -LiteralPath $installReceiptPath `
    -Description "Phase 2 installation receipt"
if ([string]$installReceipt.recoverySnapshotName -ne
    $RecoverySnapshotName) {
    throw "The recovery snapshot name does not match the installation receipt."
}
$state = Assert-ComotePhase2InstalledState `
    -InstallReceipt $installReceipt
$inputIds = @(
    @($state.Keyboards + $state.Mice) |
        ForEach-Object { [string]$_.InstanceId }
)

$programFilesX86 = [Environment]::GetFolderPath(
    [Environment+SpecialFolder]::ProgramFilesX86
)
$devGen = Find-ComotePhase2Tool `
    -Root (Join-Path $programFilesX86 "Windows Kits\10\Tools") `
    -Name "devgen.exe"
if (-not $devGen) {
    throw "DevGen.exe was not found in the installed WDK."
}

if ($ValidateOnly.IsPresent) {
    Write-Host ""
    Write-Host "Phase 2 removal validation passed." -ForegroundColor Green
    Write-Host "No device or driver package was removed."
    return
}

$stateDirectory = Split-Path -Parent $installReceiptPath
$transactionPath = Join-Path `
    $stateDirectory `
    "enumeration-transaction.json"
if (Test-Path -LiteralPath $transactionPath) {
    throw "An enumeration transaction already exists; run the recovery gate first."
}
$operationId = [Guid]::NewGuid().ToString("N")
$transaction = [PSCustomObject][ordered]@{
    schemaVersion = 1
    operationId = $operationId
    operation = "remove"
    status = "remove-prepared"
    createdUtc = [DateTime]::UtcNow.ToString("o")
    recoverySnapshotName = $RecoverySnapshotName
    hardwareId = "ROOT\COMOTEVIRTUALHID_PHASE2"
    serviceName = "ComoteVirtualHidPhase2"
    publishedInfName = $state.PublishedInfName
    rootDeviceInstanceId = $state.RootInstanceId
    inputInstanceIds = $inputIds
}
Write-ComotePhase2JsonAtomically `
    -LiteralPath $transactionPath `
    -InputObject $transaction

try {
    Invoke-ComotePhase2ExactEnumerationCleanup `
        -Transaction $transaction `
        -TransactionPath $transactionPath `
        -DevGenPath $devGen

    Set-ComotePhase2NoteProperty `
        -InputObject $installReceipt `
        -Name "status" `
        -Value "removed"
    Set-ComotePhase2NoteProperty `
        -InputObject $installReceipt `
        -Name "removedUtc" `
        -Value ([DateTime]::UtcNow.ToString("o"))
    Set-ComotePhase2NoteProperty `
        -InputObject $installReceipt `
        -Name "operationId" `
        -Value $operationId
    $historyPath = Join-Path `
        $stateDirectory `
        ("history\enumeration-{0}-removed.json" -f $operationId)
    Write-ComotePhase2JsonAtomically `
        -LiteralPath $historyPath `
        -InputObject $installReceipt
    Remove-Item `
        -LiteralPath $installReceiptPath `
        -Force `
        -ErrorAction Stop
    Remove-Item `
        -LiteralPath $transactionPath `
        -Force `
        -ErrorAction Stop
}
catch {
    throw ("Phase 2 removal did not finalize: {0}. " +
        "The exact-target transaction was retained; run " +
        "Repair-Phase2EnumerationState.ps1 or restore snapshot {1}." -f
        $_.Exception.Message,
        $RecoverySnapshotName)
}
Write-Host ""
Write-Host "Phase 2 root device and Driver Store package were removed." -ForegroundColor Green
Write-Host "The active receipt was archived and removed; reinstall is allowed."
Write-Host "The test certificate and TESTSIGNING state were not changed."
}
finally {
    Exit-ComotePhase2RuntimeLock -Mutex $runtimeLock
}
