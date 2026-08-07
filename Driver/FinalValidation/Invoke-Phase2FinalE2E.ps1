#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [switch]$AcknowledgeDisposableVm,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$RecoverySnapshotName,

    [Parameter(Mandatory)]
    [switch]$AcknowledgeInputGeneration,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$ConfirmationPhrase,

    [ValidatePattern("^[0-9A-Fa-f]{64}$")]
    [string]$ExpectedBrokerSha256 = "",

    [switch]$ValidateOnly
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot "Phase2FinalValidation.Common.ps1")

Assert-ComoteFinalValidationSnapshotName -Name $RecoverySnapshotName
$environment = Assert-ComoteFinalValidationVm `
    -AcknowledgeDisposableVm:$AcknowledgeDisposableVm
$paths = Get-ComoteFinalValidationPaths

$installedStateScript = Join-Path `
    $paths.Phase2Root `
    "Runtime\Test-Phase2InstalledState.ps1"
if (-not (Test-Path -LiteralPath $installedStateScript -PathType Leaf)) {
    throw "The existing Phase 2 installed-state gate was not found."
}

& $installedStateScript `
    -AcknowledgeDisposableVm `
    -RecoverySnapshotName $RecoverySnapshotName `
    -RequiredBuildNumber "19045"

Assert-ComoteFinalValidationControllerAccess
$brokerEvidence = Assert-ComoteFinalValidationBroker
$brokerService = $brokerEvidence.Service
$normalizedExpectedBrokerSha256 =
    $ExpectedBrokerSha256.ToUpperInvariant()
if (-not [string]::IsNullOrWhiteSpace(
        $normalizedExpectedBrokerSha256) -and
    $normalizedExpectedBrokerSha256 -cne
        [string]$brokerEvidence.Sha256) {
    throw "The running Broker does not match the expected release SHA-256."
}

$installReceiptPath = Join-Path `
    $paths.Phase2Root `
    "artifacts\phase2-runtime-state\enumeration-installation.json"
$installReceipt = Get-Content `
    -LiteralPath $installReceiptPath `
    -Raw `
    -ErrorAction Stop |
    ConvertFrom-Json -ErrorAction Stop
$keyboardIds = @($installReceipt.keyboardInstanceIds)
$mouseIds = @($installReceipt.mouseInstanceIds)
if ($keyboardIds.Count -ne 1 -or $mouseIds.Count -ne 2) {
    throw "The Phase 2 receipt does not contain one keyboard and two mice."
}

if ($ValidateOnly.IsPresent) {
    Write-Host ""
    Write-Host "Final Phase 2 E2E preflight passed." -ForegroundColor Green
    Write-Host "VMware guest: $($environment.Computer.Model)"
    Write-Host ("Windows: 19045.{0}" -f $environment.Ubr)
    Write-Host "Driver/VHF children: healthy"
    Write-Host "Input Broker: running as LocalSystem"
    Write-Host "Current controller token: authorized"
    Write-Host "No project was built and no input was generated."
    return
}

if (-not $AcknowledgeInputGeneration.IsPresent -or
    $ConfirmationPhrase -cne
        "RUN COMOTE VIRTUAL HID E2E IN VM") {
    throw ("Real keyboard and mouse reports require both the input " +
        "acknowledgement and exact confirmation phrase: " +
        "RUN COMOTE VIRTUAL HID E2E IN VM")
}
if ([string]::IsNullOrWhiteSpace(
        $normalizedExpectedBrokerSha256)) {
    throw ("The real E2E run requires ExpectedBrokerSha256 from the " +
        "externally verified final release manifest.")
}

$dotnet = Get-Command `
    -Name "dotnet.exe" `
    -CommandType Application `
    -ErrorAction Stop |
    Select-Object -First 1
if ($null -eq $dotnet -or
    [string]::IsNullOrWhiteSpace([string]$dotnet.Path)) {
    throw ".NET SDK 10 was not found inside the VMware guest."
}
$sdkList = & $dotnet.Path --list-sdks 2>&1 | Out-String
if ($LASTEXITCODE -ne 0 -or $sdkList -notmatch "(?m)^10\.") {
    throw ".NET SDK 10 is required for the final E2E observer."
}

$runId = [Guid]::NewGuid().ToString("N")
$runDirectory = Join-Path `
    $PSScriptRoot `
    ("artifacts\phase2-final-e2e\{0}" -f $runId)
[IO.Directory]::CreateDirectory($runDirectory) | Out-Null
$configPath = Join-Path $runDirectory "e2e-config.json"
$appReportPath = Join-Path $runDirectory "raw-input-report.json"
$recoveryStatePath = Join-Path $runDirectory "input-recovery-state.json"
$wrapperReportPath = Join-Path $runDirectory "validation-summary.json"

$configuration = [ordered]@{
    schemaVersion = 1
    runId = $runId
    recoverySnapshotName = $RecoverySnapshotName
    expectedKeyboardInstanceIds = @(
        $keyboardIds | ForEach-Object { [string]$_ }
    )
    expectedMouseInstanceIds = @(
        $mouseIds | ForEach-Object { [string]$_ }
    )
    reportPath = $appReportPath
    recoveryStatePath = $recoveryStatePath
}
Write-ComoteFinalValidationJson `
    -LiteralPath $configPath `
    -InputObject $configuration
$configSha256 = (
    Get-FileHash -Algorithm SHA256 -LiteralPath $configPath
).Hash

$summary = [ordered]@{
    schemaVersion = 1
    runId = $runId
    status = "running"
    startedUtc = [DateTime]::UtcNow.ToString("o")
    completedUtc = $null
    recoverySnapshotName = $RecoverySnapshotName
    manufacturer = [string]$environment.Computer.Manufacturer
    model = [string]$environment.Computer.Model
    osBuildNumber = "19045"
    osUbr = [int]$environment.Ubr
    brokerServiceName = [string]$brokerService.Name
    brokerServiceState = [string]$brokerService.State
    brokerExecutablePath = [string]$brokerEvidence.ExecutablePath
    brokerExecutableSha256 = [string]$brokerEvidence.Sha256
    expectedBrokerSha256 = $normalizedExpectedBrokerSha256
    appReportPath = $appReportPath
    configSha256 = $configSha256
    observerSha256 = $null
    recoveryStateSha256 = $null
    cleanupExitCode = $null
    buildExitCode = $null
    applicationExitCode = $null
    evidenceTestCount = $null
    rawInputEventCount = $null
    appReportSha256 = $null
    failureType = $null
    failureMessage = $null
}
Write-ComoteFinalValidationJson `
    -LiteralPath $wrapperReportPath `
    -InputObject $summary

$applicationPath = Join-Path `
    $PSScriptRoot `
    "VirtualHidE2E\bin\Release\net10.0-windows\Comote.VirtualHidE2E.exe"
$applicationStarted = $false
$operationError = $null
try {
    $buildOutput = & $dotnet.Path `
        build `
        $paths.E2EProject `
        --configuration Release `
        --nologo `
        -p:RestoreIgnoreFailedSources=true `
        2>&1 |
        Out-String
    $summary.buildExitCode = [int]$LASTEXITCODE
    if ($summary.buildExitCode -ne 0) {
        throw "The VM-only E2E observer build failed: $buildOutput"
    }
    if (-not (Test-Path -LiteralPath $applicationPath -PathType Leaf)) {
        throw "The VM-only E2E observer executable was not produced."
    }
    $summary.observerSha256 = (
        Get-FileHash -Algorithm SHA256 -LiteralPath $applicationPath
    ).Hash

    $applicationStarted = $true
    $summary.applicationExitCode = Invoke-ComoteFinalValidationProcess `
        -FilePath $applicationPath `
        -ArgumentList @(
            "--config",
            $configPath,
            "--acknowledge-disposable-vm",
            "COMOTE-WIN10-VM-E2E"
        ) `
        -TimeoutSeconds 120
    if ($summary.applicationExitCode -ne 0) {
        throw ("Raw Input E2E validation failed with exit code {0}. " +
            "Read {1}." -f
            $summary.applicationExitCode,
            $appReportPath)
    }
    if (-not (Test-Path -LiteralPath $appReportPath -PathType Leaf)) {
        throw "The successful E2E process did not create its evidence report."
    }
    $appReport = Get-Content `
        -LiteralPath $appReportPath `
        -Raw `
        -ErrorAction Stop |
        ConvertFrom-Json -ErrorAction Stop
    $evidenceInventory = Assert-ComoteFinalValidationEvidence `
        -Report $appReport `
        -RunId $runId `
        -RecoverySnapshotName $RecoverySnapshotName `
        -ExpectedKeyboardInstanceIds $keyboardIds `
        -ExpectedMouseInstanceIds $mouseIds
    $summary.evidenceTestCount = [int]$evidenceInventory.TestCount
    $summary.rawInputEventCount = [int]$evidenceInventory.RawInputEventCount
    $summary.appReportSha256 = (
        Get-FileHash `
            -Algorithm SHA256 `
            -LiteralPath $appReportPath
    ).Hash
}
catch {
    $operationError = $_
    $summary.failureType = $_.Exception.GetType().Name
    $summary.failureMessage = $_.Exception.Message
}
finally {
    if ($applicationStarted -and
        (Test-Path -LiteralPath $applicationPath -PathType Leaf)) {
        try {
            $summary.cleanupExitCode = Invoke-ComoteFinalValidationProcess `
                -FilePath $applicationPath `
                -ArgumentList @(
                    "--config",
                    $configPath,
                    "--acknowledge-disposable-vm",
                    "COMOTE-WIN10-VM-E2E",
                    "--cleanup-only"
                ) `
                -TimeoutSeconds 30
        }
        catch {
            $summary.cleanupExitCode = -1
            if ($null -eq $operationError) {
                $operationError = $_
                $summary.failureType = $_.Exception.GetType().Name
                $summary.failureMessage = $_.Exception.Message
            }
        }
    }

    $cleanupPassed = (
        -not $applicationStarted -or
        [int]$summary.cleanupExitCode -eq 0
    )
    $summary.status = if (
        $null -eq $operationError -and
        $cleanupPassed
    ) {
        "passed"
    }
    else {
        "failed"
    }
    if (Test-Path -LiteralPath $recoveryStatePath -PathType Leaf) {
        $summary.recoveryStateSha256 = (
            Get-FileHash `
                -Algorithm SHA256 `
                -LiteralPath $recoveryStatePath
        ).Hash
    }
    $summary.completedUtc = [DateTime]::UtcNow.ToString("o")
    Write-ComoteFinalValidationJson `
        -LiteralPath $wrapperReportPath `
        -InputObject $summary
}

if ($null -ne $operationError) {
    throw $operationError
}
if ([int]$summary.cleanupExitCode -ne 0) {
    throw ("Emergency RELEASE_ALL/cursor restoration failed. " +
        "Restore VMware snapshot '{0}' before any further test." -f
        $RecoverySnapshotName)
}

Write-Host ""
Write-Host "Comote Phase 2 final E2E passed." -ForegroundColor Green
Write-Host "Broker → driver → Windows Raw Input keyboard: PASS"
Write-Host "Broker → driver → relative mouse Raw Input: PASS"
Write-Host "Broker → driver → absolute mouse Raw Input: PASS"
Write-Host "HostInputProtocol/VirtualHidInputBackend path: PASS"
Write-Host "RELEASE_ALL and cursor restoration: PASS"
Write-Host "Summary: $wrapperReportPath"
Write-Host "Raw Input evidence: $appReportPath"
