#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [switch]$AcknowledgeDisposableVm,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$RecoverySnapshotName,

    [ValidatePattern("^19045$")]
    [string]$RequiredBuildNumber = "19045"
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
$stateDirectory = Join-Path $projectRoot "artifacts\phase2-runtime-state"
$historyDirectory = Join-Path $stateDirectory "history"
$installReceiptPath = Join-Path `
    $stateDirectory `
    "enumeration-installation.json"
$transactionPath = Join-Path `
    $stateDirectory `
    "enumeration-transaction.json"

$programFilesX86 = [Environment]::GetFolderPath(
    [Environment+SpecialFolder]::ProgramFilesX86
)
$devGen = Find-ComotePhase2Tool `
    -Root (Join-Path $programFilesX86 "Windows Kits\10\Tools") `
    -Name "devgen.exe"
if (-not $devGen) {
    throw "DevGen.exe was not found in the installed WDK."
}

function Save-ComotePhase2EnumerationHistory {
    param(
        [Parameter(Mandatory)]
        [PSObject]$Document,

        [Parameter(Mandatory)]
        [ValidatePattern("^[0-9a-f]{32}$")]
        [string]$OperationId,

        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$Suffix
    )

    Set-ComotePhase2NoteProperty `
        -InputObject $Document `
        -Name "archivedUtc" `
        -Value ([DateTime]::UtcNow.ToString("o"))
    $historyPath = Join-Path `
        $historyDirectory `
        ("enumeration-{0}-{1}.json" -f $OperationId, $Suffix)
    Write-ComotePhase2JsonAtomically `
        -LiteralPath $historyPath `
        -InputObject $Document
}

function Complete-ComotePhase2RecoveredCleanup {
    param(
        [Parameter(Mandatory)]
        [PSObject]$Transaction
    )

    Invoke-ComotePhase2ExactEnumerationCleanup `
        -Transaction $Transaction `
        -TransactionPath $transactionPath `
        -DevGenPath $devGen

    $operationId = [string]$Transaction.operationId
    if (Test-Path -LiteralPath $installReceiptPath -PathType Leaf) {
        $receipt = Read-ComotePhase2JsonDocument `
            -LiteralPath $installReceiptPath `
            -Description "Phase 2 installation receipt"
        Set-ComotePhase2NoteProperty `
            -InputObject $receipt `
            -Name "status" `
            -Value "removed"
        Set-ComotePhase2NoteProperty `
            -InputObject $receipt `
            -Name "removedUtc" `
            -Value ([DateTime]::UtcNow.ToString("o"))
        Set-ComotePhase2NoteProperty `
            -InputObject $receipt `
            -Name "recoveredFromTransaction" `
            -Value $true
        Save-ComotePhase2EnumerationHistory `
            -Document $receipt `
            -OperationId $operationId `
            -Suffix "removed"
        Remove-Item -LiteralPath $installReceiptPath -Force -ErrorAction Stop
    }
    else {
        Set-ComotePhase2NoteProperty `
            -InputObject $Transaction `
            -Name "status" `
            -Value "cleanup-complete"
        Save-ComotePhase2EnumerationHistory `
            -Document $Transaction `
            -OperationId $operationId `
            -Suffix "recovered"
    }

    Remove-Item -LiteralPath $transactionPath -Force -ErrorAction Stop
}

if (Test-Path -LiteralPath $transactionPath -PathType Leaf) {
    $transaction = Read-ComotePhase2JsonDocument `
        -LiteralPath $transactionPath `
        -Description "Phase 2 enumeration transaction"
    $operation = [string]$transaction.operation
    if ($operation -notin @("install", "remove")) {
        throw "The enumeration transaction operation is invalid."
    }
    Assert-ComotePhase2EnumerationTransaction `
        -Transaction $transaction `
        -ExpectedOperation $operation `
        -RecoverySnapshotName $RecoverySnapshotName

    if ($operation -eq "install" -and
        (Test-Path -LiteralPath $installReceiptPath -PathType Leaf)) {
        $receipt = Read-ComotePhase2JsonDocument `
            -LiteralPath $installReceiptPath `
            -Description "Phase 2 installation receipt"
        if ([string]$receipt.recoverySnapshotName -ne
            $RecoverySnapshotName) {
            throw "The installation receipt and transaction snapshots disagree."
        }
        try {
            [void](Assert-ComotePhase2InstalledState `
                -InstallReceipt $receipt)
            Remove-Item `
                -LiteralPath $transactionPath `
                -Force `
                -ErrorAction Stop
            Write-Host ""
            Write-Host "A completed Phase 2 installation transaction was finalized." `
                -ForegroundColor Green
            return
        }
        catch {
            Write-Verbose (
                "The interrupted install is not healthy; exact cleanup will resume: {0}" -f
                $_.Exception.Message
            )
        }
    }

    Complete-ComotePhase2RecoveredCleanup -Transaction $transaction
    Write-Host ""
    Write-Host "The interrupted Phase 2 enumeration transaction was cleaned safely." `
        -ForegroundColor Green
    Write-Host "The active receipt was archived and removed when present."
    return
}

if (Test-Path -LiteralPath $installReceiptPath -PathType Leaf) {
    $receipt = Read-ComotePhase2JsonDocument `
        -LiteralPath $installReceiptPath `
        -Description "Phase 2 installation receipt"
    if ([string]$receipt.recoverySnapshotName -ne
        $RecoverySnapshotName) {
        throw "The recovery snapshot name does not match the installation receipt."
    }

    if ([string]$receipt.status -eq "installed-enumerated") {
        try {
            [void](Assert-ComotePhase2InstalledState `
                -InstallReceipt $receipt)
            Write-Host ""
            Write-Host "The Phase 2 installation is already healthy." `
                -ForegroundColor Green
            return
        }
        catch {
            foreach ($requiredTarget in @(
                "publishedInfName",
                "rootDeviceInstanceId",
                "serviceName"
            )) {
                if ($null -eq $receipt.PSObject.Properties[$requiredTarget]) {
                    throw "An unhealthy installation receipt lacks an exact cleanup target."
                }
            }
            if ([string]$receipt.publishedInfName -notmatch
                    "^oem\d+\.inf$" -or
                [string]$receipt.rootDeviceInstanceId -notlike "ROOT\*" -or
                [string]$receipt.serviceName -ne
                    "ComoteVirtualHidPhase2") {
                throw "The unhealthy installation receipt target is invalid."
            }

            $operationId = [Guid]::NewGuid().ToString("N")
            $inputIds = @(
                @($receipt.keyboardInstanceIds) +
                @($receipt.mouseInstanceIds)
            )
            $transaction = [PSCustomObject][ordered]@{
                schemaVersion = 1
                operationId = $operationId
                operation = "remove"
                status = "recovered-from-install-receipt"
                createdUtc = [DateTime]::UtcNow.ToString("o")
                recoverySnapshotName = $RecoverySnapshotName
                hardwareId = "ROOT\COMOTEVIRTUALHID_PHASE2"
                serviceName = "ComoteVirtualHidPhase2"
                publishedInfName =
                    ([string]$receipt.publishedInfName).ToLowerInvariant()
                rootDeviceInstanceId =
                    [string]$receipt.rootDeviceInstanceId
                inputInstanceIds = $inputIds
            }
            Write-ComotePhase2JsonAtomically `
                -LiteralPath $transactionPath `
                -InputObject $transaction
            Complete-ComotePhase2RecoveredCleanup -Transaction $transaction
            Write-Host ""
            Write-Host "The unhealthy Phase 2 installation was cleaned safely." `
                -ForegroundColor Green
            return
        }
    }

    if ([string]$receipt.status -eq "removed") {
        Assert-ComotePhase2NoInstalledDevice
        if (@(Get-ComotePhase2DriverPackages).Count -ne 0) {
            throw "A removed receipt remains but a Phase 2 package still exists."
        }
        $operationId = [Guid]::NewGuid().ToString("N")
        Save-ComotePhase2EnumerationHistory `
            -Document $receipt `
            -OperationId $operationId `
            -Suffix "removed-legacy"
        Remove-Item `
            -LiteralPath $installReceiptPath `
            -Force `
            -ErrorAction Stop
        Write-Host ""
        Write-Host "The legacy removed receipt was archived." `
            -ForegroundColor Green
        return
    }

    throw "The Phase 2 installation receipt status is not recoverable."
}

$rootDevices = @(Get-ComotePhase2RootDevices)
$driverPackages = @(Get-ComotePhase2DriverPackages)
$serviceAudit = Invoke-ComotePhase2NativeCommand `
    -FilePath "sc.exe" `
    -Arguments @("query", "ComoteVirtualHidPhase2")
if ($rootDevices.Count -ne 0 -or
    $driverPackages.Count -ne 0 -or
    $serviceAudit.ExitCode -eq 0) {
    throw ("Phase 2 state exists without an exact transaction or receipt. " +
        "Restore snapshot {0}; no target was mutated." -f
        $RecoverySnapshotName)
}
if ($serviceAudit.ExitCode -ne 1060) {
    throw ("Unable to prove the Phase 2 service is absent " +
        "(exit code {0}): {1}" -f
        $serviceAudit.ExitCode,
        $serviceAudit.Output)
}

Write-Host ""
Write-Host "Phase 2 enumeration state is clean." -ForegroundColor Green
Write-Host "No recovery mutation was required."
}
finally {
    Exit-ComotePhase2RuntimeLock -Mutex $runtimeLock
}
