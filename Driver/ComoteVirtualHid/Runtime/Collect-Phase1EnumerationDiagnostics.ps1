#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [switch]$AcknowledgeDisposableVm,

    [ValidatePattern("^19045$")]
    [string]$RequiredBuildNumber = "19045",

    [string]$FailureMessage = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot "Phase1Runtime.Common.ps1")

$environment = Assert-ComotePhase1RuntimeEnvironment `
    -AcknowledgeDisposableVm:$AcknowledgeDisposableVm `
    -RequiredBuildNumber $RequiredBuildNumber
$codeIntegrityState = Get-ComotePhase1ActiveCodeIntegrityState
$projectRoot = Split-Path -Parent $PSScriptRoot
$reportDirectory = Join-Path `
    $projectRoot `
    "artifacts\phase1-enumeration-diagnostics"
New-Item -ItemType Directory -Path $reportDirectory -Force |
    Out-Null
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$reportPath = Join-Path `
    $reportDirectory `
    "enumeration-diagnostics-$stamp.json"
$setupApiPath = Join-Path `
    $reportDirectory `
    "setupapi-relevant-$stamp.txt"

$devices = @(
    Get-CimInstance `
        -ClassName Win32_PnPEntity `
        -ErrorAction SilentlyContinue |
        Where-Object {
            [string]$_.PNPDeviceID -match
                "COMOTE|COMOTEVIRTUALHID|COMOTE_PHASE1" -or
            [string]$_.Name -match "Comote" -or
            [string]$_.Service -eq "ComoteVirtualHid" -or
            (@($_.HardwareID) -join "|") -match "COMOTEVIRTUALHID"
        } |
        Select-Object `
            PNPDeviceID,
            Name,
            Status,
            ConfigManagerErrorCode,
            Service,
            HardwareID
)
$driverService = @(
    Get-CimInstance `
        -ClassName Win32_SystemDriver `
        -Filter "Name='ComoteVirtualHid'" `
        -ErrorAction SilentlyContinue |
        Select-Object `
            Name,
            State,
            Status,
            StartMode,
            ExitCode,
            ServiceSpecificExitCode,
            PathName
)
$driverPackages = @()
if (Get-Command Get-WindowsDriver -ErrorAction SilentlyContinue) {
    $driverPackages = @(
        Get-WindowsDriver -Online -ErrorAction SilentlyContinue |
            Where-Object {
                [string]$_.ProviderName -eq "Comote" -or
                [IO.Path]::GetFileName(
                    [string]$_.OriginalFileName
                ) -eq "ComoteVirtualHid.inf"
            } |
            Select-Object `
                Driver,
                OriginalFileName,
                ProviderName,
                ClassName,
                Version,
                Date
    )
}

$scQuery = (& sc.exe queryex ComoteVirtualHid 2>&1 |
    Out-String).Trim()
$scQueryExitCode = $LASTEXITCODE
$scConfiguration = (& sc.exe qc ComoteVirtualHid 2>&1 |
    Out-String).Trim()
$scConfigurationExitCode = $LASTEXITCODE

$setupSource = "$env:SystemRoot\INF\setupapi.dev.log"
$setupRelevant = @()
if (Test-Path -LiteralPath $setupSource -PathType Leaf) {
    $setupTail = @(Get-Content `
        -LiteralPath $setupSource `
        -Tail 20000 `
        -ErrorAction SilentlyContinue)
    $setupRelevant = @(
        $setupTail |
            Select-String `
                -Pattern @(
                    "ComoteVirtualHid",
                    "COMOTEVIRTUALHID",
                    "COMOTE_PHASE1"
                ) `
                -SimpleMatch `
                -Context 12,24 |
            ForEach-Object {
                @($_.Context.PreContext)
                [string]$_.Line
                @($_.Context.PostContext)
            } |
            Select-Object -Unique
    )
}
$setupRelevant |
    Set-Content -LiteralPath $setupApiPath -Encoding UTF8

$startTime = (Get-Date).AddHours(-4)
$events = @()
foreach ($logName in @(
    "System",
    "Microsoft-Windows-CodeIntegrity/Operational"
)) {
    $logEvents = @(
        Get-WinEvent `
            -FilterHashtable @{
                LogName = $logName
                StartTime = $startTime
            } `
            -ErrorAction SilentlyContinue |
            Where-Object {
                [string]$_.Message -match
                    "Comote|COMOTEVIRTUALHID|COMOTE_PHASE1"
            } |
            Select-Object -First 100 |
            ForEach-Object {
                [PSCustomObject]@{
                    logName = $logName
                    timeCreated = $_.TimeCreated.ToUniversalTime().
                        ToString("o")
                    id = [int]$_.Id
                    providerName = [string]$_.ProviderName
                    levelDisplayName = [string]$_.LevelDisplayName
                    message = [string]$_.Message
                }
            }
    )
    $events += $logEvents
}

$currentVersion = Get-ItemProperty `
    -LiteralPath "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion"
$report = [ordered]@{
    collectedUtc = [DateTime]::UtcNow.ToString("o")
    failureMessage = $FailureMessage
    manufacturer = [string]$environment.Computer.Manufacturer
    model = [string]$environment.Computer.Model
    osBuildNumber = [string]$environment.OperatingSystem.BuildNumber
    osUbr = [int]$currentVersion.UBR
    testSigningConfigured = Get-ComotePhase1TestSigningState
    activeCodeIntegrityOptions = [uint32]$codeIntegrityState.Options
    activeTestSigning = [bool]$codeIntegrityState.TestSigningActive
    hvciKernelModeActive =
        [bool]$codeIntegrityState.HvciKernelModeActive
    devices = $devices
    driverService = $driverService
    driverPackages = $driverPackages
    scQueryExitCode = $scQueryExitCode
    scQuery = $scQuery
    scConfigurationExitCode = $scConfigurationExitCode
    scConfiguration = $scConfiguration
    events = $events
    setupApiRelevantPath = $setupApiPath
    setupApiRelevantLineCount = $setupRelevant.Count
}
$report | ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath $reportPath -Encoding UTF8

Write-Host ""
Write-Host "Phase 1 enumeration diagnostics collected." `
    -ForegroundColor Green
Write-Host "No device or driver state was changed."
Write-Host "Report: $reportPath"
Write-Host "SetupAPI excerpt: $setupApiPath"

return [PSCustomObject]@{
    ReportPath = $reportPath
    SetupApiPath = $setupApiPath
}
