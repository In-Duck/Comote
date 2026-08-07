#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [switch]$AcknowledgeDisposableVm,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$SnapshotName,

    [ValidateRange(1, 5)]
    [int]$Iterations = 5,

    [ValidatePattern("^19045$")]
    [string]$RequiredBuildNumber = "19045"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$projectRoot = Split-Path -Parent $PSScriptRoot
$reportDirectory = Join-Path `
    $projectRoot `
    "artifacts\phase1-enumeration-stress"
New-Item -ItemType Directory -Path $reportDirectory -Force |
    Out-Null
$reportPath = Join-Path `
    $reportDirectory `
    ("enumeration-stress-{0}.json" -f
        (Get-Date -Format "yyyyMMdd-HHmmss"))
$results = @()

function Write-ComotePhase1StressReport {
    param(
        [Parameter(Mandatory)]
        [string]$Status,

        [string]$FailureMessage = ""
    )

    $report = [ordered]@{
        completedUtc = [DateTime]::UtcNow.ToString("o")
        status = $Status
        failureMessage = $FailureMessage
        requestedIterations = $Iterations
        completedIterations = @(
            $results |
                Where-Object { $_.status -eq "passed" }
        ).Count
        snapshotName = $SnapshotName
        results = $results
    }
    $report | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath $reportPath -Encoding UTF8
}

try {
    & (Join-Path `
        $PSScriptRoot `
        "Test-Phase1EnumerationBoundary.ps1")
    & (Join-Path `
        $PSScriptRoot `
        "Test-Phase1EnumerationCleanState.ps1") `
        -AcknowledgeDisposableVm:$AcknowledgeDisposableVm `
        -RequiredBuildNumber $RequiredBuildNumber
    & (Join-Path `
        $PSScriptRoot `
        "Test-Phase1ActiveTestMode.ps1") `
        -AcknowledgeDisposableVm:$AcknowledgeDisposableVm `
        -RequiredBuildNumber $RequiredBuildNumber

    for ($iteration = 1;
        $iteration -le $Iterations;
        $iteration++) {
        $startedUtc = [DateTime]::UtcNow.ToString("o")
        try {
            & (Join-Path `
                $PSScriptRoot `
                "Install-Phase1Enumeration.ps1") `
                -AcknowledgeDisposableVm:$AcknowledgeDisposableVm `
                -SnapshotName $SnapshotName `
                -RequiredBuildNumber $RequiredBuildNumber `
                -ValidateOnly
            & (Join-Path `
                $PSScriptRoot `
                "Install-Phase1Enumeration.ps1") `
                -AcknowledgeDisposableVm:$AcknowledgeDisposableVm `
                -SnapshotName $SnapshotName `
                -RequiredBuildNumber $RequiredBuildNumber

            $installReceiptPath = Join-Path `
                $projectRoot `
                "artifacts\phase1-runtime-state\enumeration-installation.json"
            $installReceipt = Get-Content `
                -LiteralPath $installReceiptPath `
                -Raw |
                ConvertFrom-Json
            $cycleId = [string]$installReceipt.cycleId

            & (Join-Path `
                $PSScriptRoot `
                "Remove-Phase1Enumeration.ps1") `
                -AcknowledgeDisposableVm:$AcknowledgeDisposableVm `
                -SnapshotName $SnapshotName `
                -RequiredBuildNumber $RequiredBuildNumber `
                -ValidateOnly
            & (Join-Path `
                $PSScriptRoot `
                "Remove-Phase1Enumeration.ps1") `
                -AcknowledgeDisposableVm:$AcknowledgeDisposableVm `
                -SnapshotName $SnapshotName `
                -RequiredBuildNumber $RequiredBuildNumber
            & (Join-Path `
                $PSScriptRoot `
                "Test-Phase1EnumerationCleanState.ps1") `
                -AcknowledgeDisposableVm:$AcknowledgeDisposableVm `
                -RequiredBuildNumber $RequiredBuildNumber

            $results += [PSCustomObject]@{
                iteration = $iteration
                cycleId = $cycleId
                startedUtc = $startedUtc
                completedUtc = [DateTime]::UtcNow.ToString("o")
                status = "passed"
                failureMessage = ""
            }
        }
        catch {
            $results += [PSCustomObject]@{
                iteration = $iteration
                cycleId = ""
                startedUtc = $startedUtc
                completedUtc = [DateTime]::UtcNow.ToString("o")
                status = "failed"
                failureMessage = $_.Exception.Message
            }
            throw
        }
    }

    Write-ComotePhase1StressReport -Status "passed"
}
catch {
    Write-ComotePhase1StressReport `
        -Status "failed" `
        -FailureMessage $_.Exception.Message
    throw
}

Write-Host ""
Write-Host "Phase 1 enumeration stress test passed." `
    -ForegroundColor Green
Write-Host "Completed iterations: $Iterations"
Write-Host "Final state: clean"
Write-Host "Report: $reportPath"
