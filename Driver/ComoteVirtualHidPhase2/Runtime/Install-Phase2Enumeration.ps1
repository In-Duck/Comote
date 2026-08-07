#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [switch]$AcknowledgeDisposableVm,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$SigningSnapshotName,

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

& (Join-Path $PSScriptRoot "Test-Phase2EnumerationPreflight.ps1") `
    -AcknowledgeDisposableVm:$AcknowledgeDisposableVm `
    -SnapshotName $SigningSnapshotName `
    -RequiredBuildNumber $RequiredBuildNumber

$environment = Assert-ComotePhase2RuntimeEnvironment `
    -AcknowledgeDisposableVm:$AcknowledgeDisposableVm `
    -RequiredBuildNumber $RequiredBuildNumber
$projectRoot = Split-Path -Parent $PSScriptRoot
$signedPackagePath = Join-Path `
    $projectRoot `
    "artifacts\phase2-test-signed"
$stateDirectory = Join-Path $projectRoot "artifacts\phase2-runtime-state"
$installReceiptPath = Join-Path `
    $stateDirectory `
    "enumeration-installation.json"
$transactionPath = Join-Path `
    $stateDirectory `
    "enumeration-transaction.json"
if (Test-Path -LiteralPath $transactionPath) {
    throw "An enumeration transaction already exists; run the recovery gate first."
}
$infPath = Join-Path `
    $signedPackagePath `
    "ComoteVirtualHidPhase2.inf"

$programFilesX86 = [Environment]::GetFolderPath(
    [Environment+SpecialFolder]::ProgramFilesX86
)
$devGen = Find-ComotePhase2Tool `
    -Root (Join-Path $programFilesX86 "Windows Kits\10\Tools") `
    -Name "devgen.exe"
if (-not $devGen) {
    throw "DevGen.exe was not found in the installed WDK."
}

$baselineKeyboardIds = @(
    Get-PnpDevice `
        -PresentOnly `
        -Class Keyboard `
        -ErrorAction Stop |
        ForEach-Object { [string]$_.InstanceId }
)
$baselineMouseIds = @(
    Get-PnpDevice `
        -PresentOnly `
        -Class Mouse `
        -ErrorAction Stop |
        ForEach-Object { [string]$_.InstanceId }
)

if ($ValidateOnly.IsPresent) {
    Write-Host ""
    Write-Host "Phase 2 installation validation passed." -ForegroundColor Green
    Write-Host "No package was staged and no device was created."
    Write-Host "The probe was not launched and no input report was submitted."
    return
}

$publishedInfName = $null
$stagePublishedInfName = $null
$operationId = [Guid]::NewGuid().ToString("N")
$transaction = [PSCustomObject][ordered]@{
    schemaVersion = 1
    operationId = $operationId
    operation = "install"
    status = "install-prepared"
    createdUtc = [DateTime]::UtcNow.ToString("o")
    signingSnapshotName = $SigningSnapshotName
    recoverySnapshotName = $RecoverySnapshotName
    hardwareId = "ROOT\COMOTEVIRTUALHID_PHASE2"
    serviceName = "ComoteVirtualHidPhase2"
    publishedInfName = $null
    rootDeviceInstanceId = $null
    inputInstanceIds = @()
}
Write-ComotePhase2JsonAtomically `
    -LiteralPath $transactionPath `
    -InputObject $transaction
$installationCommitted = $false
try {
    $stageResult = Invoke-ComotePhase2NativeCommand `
        -FilePath "pnputil.exe" `
        -Arguments @("/add-driver", $infPath)
    $stageOutput = $stageResult.Output
    if ($stageResult.ExitCode -ne 0) {
        throw "PnPUtil failed to stage the Phase 2 package: $stageOutput"
    }
    $publishedMatch = [regex]::Match(
        $stageOutput,
        '(?i)\boem\d+\.inf\b'
    )
    if ($publishedMatch.Success) {
        $stagePublishedInfName =
            $publishedMatch.Value.ToLowerInvariant()
        $stagePackage = $null
        for ($attempt = 0; $attempt -lt 15; $attempt++) {
            try {
                $stagePackage = Get-WindowsDriver `
                    -Online `
                    -Driver $stagePublishedInfName `
                    -ErrorAction Stop
            }
            catch {
                $stagePackage = $null
            }
            if ($null -ne $stagePackage) {
                break
            }
            Start-Sleep -Seconds 1
        }
        if ($null -eq $stagePackage) {
            throw "The PnPUtil published name could not be verified in the Driver Store."
        }
        [void](Assert-ComotePhase2DriverPackageIdentity `
            -Package $stagePackage `
            -ExpectedPublishedInfName $stagePublishedInfName)
        $publishedInfName = $stagePublishedInfName
    }

    $driverPackages = @()
    for ($attempt = 0; $attempt -lt 15; $attempt++) {
        $driverPackages = @(Get-ComotePhase2DriverPackages)
        if ($driverPackages.Count -eq 1) {
            break
        }
        Start-Sleep -Seconds 1
    }
    if ($driverPackages.Count -ne 1) {
        throw "The staged Phase 2 package was not identified uniquely."
    }
    $inventoryPublishedInfName =
        ([string]$driverPackages[0].Driver).ToLowerInvariant()
    if ($inventoryPublishedInfName -notmatch "^oem\d+\.inf$") {
        throw "The staged Phase 2 package has an invalid published name."
    }
    [void](Assert-ComotePhase2DriverPackageIdentity `
        -Package $driverPackages[0] `
        -ExpectedPublishedInfName $inventoryPublishedInfName)
    $publishedInfName = $inventoryPublishedInfName
    if (-not [string]::IsNullOrWhiteSpace($stagePublishedInfName) -and
        $stagePublishedInfName -ne $inventoryPublishedInfName) {
        throw "PnPUtil output and Driver Store inventory disagree."
    }
    Set-ComotePhase2NoteProperty `
        -InputObject $transaction `
        -Name "publishedInfName" `
        -Value $publishedInfName
    Set-ComotePhase2NoteProperty `
        -InputObject $transaction `
        -Name "status" `
        -Value "package-staged"
    Write-ComotePhase2JsonAtomically `
        -LiteralPath $transactionPath `
        -InputObject $transaction

    $devGenResult = Invoke-ComotePhase2NativeCommand `
        -FilePath $devGen `
        -Arguments @(
            "/add",
            "/bus", "ROOT",
            "/instanceid", "COMOTE_PHASE2",
            "/hardwareid", "ROOT\COMOTEVIRTUALHID_PHASE2"
        )
    $devGenOutput = $devGenResult.Output
    if ($devGenResult.ExitCode -ne 0) {
        throw "DevGen failed to create the Phase 2 root device: $devGenOutput"
    }

    $bindResult = Invoke-ComotePhase2NativeCommand `
        -FilePath "pnputil.exe" `
        -Arguments @("/add-driver", $infPath, "/install")
    $bindOutput = $bindResult.Output
    $bindExitCode = $bindResult.ExitCode
    if ($bindExitCode -eq 3010) {
        throw "PnPUtil requested a reboot; this installation gate refuses it."
    }
    if ($bindExitCode -notin @(0, 259)) {
        throw ("PnPUtil failed to bind the Phase 2 device " +
            "(exit code {0}): {1}" -f
            $bindExitCode,
            $bindOutput)
    }

    $rootDevices = @()
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        $rootDevices = @(Get-ComotePhase2RootDevices)
        if ($rootDevices.Count -eq 1 -and
            [int]$rootDevices[0].ConfigManagerErrorCode -eq 0) {
            break
        }
        Start-Sleep -Seconds 1
    }
    if ($rootDevices.Count -ne 1 -or
        [int]$rootDevices[0].ConfigManagerErrorCode -ne 0) {
        throw "The Phase 2 root device did not enumerate cleanly."
    }
    $rootInstanceId = [string]$rootDevices[0].PNPDeviceID

    $linkedKeyboards = @()
    $linkedMice = @()
    $driverService = $null
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        $driverService = Get-CimInstance `
            -ClassName Win32_SystemDriver `
            -Filter "Name='ComoteVirtualHidPhase2'" `
            -ErrorAction Stop
        $newKeyboards = @(
            Get-PnpDevice `
                -PresentOnly `
                -Class Keyboard `
                -ErrorAction Stop |
                Where-Object {
                    $baselineKeyboardIds -notcontains
                        [string]$_.InstanceId
                }
        )
        $newMice = @(
            Get-PnpDevice `
                -PresentOnly `
                -Class Mouse `
                -ErrorAction Stop |
                Where-Object {
                    $baselineMouseIds -notcontains
                        [string]$_.InstanceId
                }
        )
        $linkedKeyboards = @(
            $newKeyboards |
                Where-Object {
                    Test-ComotePhase2DeviceDescendant `
                        -InstanceId ([string]$_.InstanceId) `
                        -AncestorInstanceId $rootInstanceId
                }
        )
        $linkedMice = @(
            $newMice |
                Where-Object {
                    Test-ComotePhase2DeviceDescendant `
                        -InstanceId ([string]$_.InstanceId) `
                        -AncestorInstanceId $rootInstanceId
                }
        )
        $serviceState = if ($null -eq $driverService) {
            ""
        }
        else {
            [string]$driverService.State
        }
        if ($serviceState -eq "Running" -and
            $linkedKeyboards.Count -eq 1 -and
            $linkedMice.Count -eq 2) {
            break
        }
        Start-Sleep -Seconds 1
    }
    $serviceState = if ($null -eq $driverService) {
        ""
    }
    else {
        [string]$driverService.State
    }
    if ($serviceState -ne "Running") {
        throw "The ComoteVirtualHidPhase2 service is not running."
    }
    if ($linkedKeyboards.Count -ne 1 -or
        $linkedMice.Count -ne 2) {
        throw ("The Phase 2 VHF keyboard, relative mouse, and " +
            "absolute mouse did not enumerate.")
    }
    foreach ($inputDevice in @($linkedKeyboards + $linkedMice)) {
        if ([string]$inputDevice.Status -ne "OK") {
            throw "A Phase 2 VHF child reported a non-OK state."
        }
    }

    Set-ComotePhase2NoteProperty `
        -InputObject $transaction `
        -Name "rootDeviceInstanceId" `
        -Value $rootInstanceId
    Set-ComotePhase2NoteProperty `
        -InputObject $transaction `
        -Name "inputInstanceIds" `
        -Value @(
            @($linkedKeyboards | ForEach-Object { [string]$_.InstanceId }) +
            @($linkedMice | ForEach-Object { [string]$_.InstanceId })
        )
    Set-ComotePhase2NoteProperty `
        -InputObject $transaction `
        -Name "status" `
        -Value "device-enumerated"
    Write-ComotePhase2JsonAtomically `
        -LiteralPath $transactionPath `
        -InputObject $transaction

    $currentVersion = Get-ItemProperty `
        -LiteralPath "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion"
    $installReceipt = [ordered]@{
        schemaVersion = 1
        status = "installed-enumerated"
        installedUtc = [DateTime]::UtcNow.ToString("o")
        signingSnapshotName = $SigningSnapshotName
        recoverySnapshotName = $RecoverySnapshotName
        manufacturer = [string]$environment.Computer.Manufacturer
        model = [string]$environment.Computer.Model
        osBuildNumber = [string]$environment.OperatingSystem.BuildNumber
        osUbr = [int]$currentVersion.UBR
        publishedInfName = $publishedInfName
        rootDeviceInstanceId = $rootInstanceId
        serviceName = "ComoteVirtualHidPhase2"
        keyboardInstanceIds = @(
            $linkedKeyboards |
                ForEach-Object { [string]$_.InstanceId }
        )
        mouseInstanceIds = @(
            $linkedMice |
                ForEach-Object { [string]$_.InstanceId }
        )
        probeLaunched = $false
        inputReportsSubmitted = 0
        setupApiLog = "$env:SystemRoot\INF\setupapi.dev.log"
    }
    Write-ComotePhase2JsonAtomically `
        -LiteralPath $installReceiptPath `
        -InputObject $installReceipt
    [void](Assert-ComotePhase2InstalledState `
        -InstallReceipt ([PSCustomObject]$installReceipt))
    $installationCommitted = $true
}
catch {
    $installationError = $_
    if ($installationCommitted) {
        throw ("Installation committed, but transaction finalization failed: {0}. " +
            "Run Repair-Phase2EnumerationState.ps1; do not reinstall." -f
            $installationError.Exception.Message)
    }

    try {
        Invoke-ComotePhase2ExactEnumerationCleanup `
            -Transaction $transaction `
            -TransactionPath $transactionPath `
            -DevGenPath $devGen
        if (Test-Path -LiteralPath $installReceiptPath) {
            Remove-Item `
                -LiteralPath $installReceiptPath `
                -Force `
                -ErrorAction Stop
        }
        Set-ComotePhase2NoteProperty `
            -InputObject $transaction `
            -Name "status" `
            -Value "rollback-complete"
        Set-ComotePhase2NoteProperty `
            -InputObject $transaction `
            -Name "failureMessage" `
            -Value $installationError.Exception.Message
        $historyPath = Join-Path `
            $stateDirectory `
            ("history\enumeration-{0}-install-rollback.json" -f
                $operationId)
        Write-ComotePhase2JsonAtomically `
            -LiteralPath $historyPath `
            -InputObject $transaction
        Remove-Item `
            -LiteralPath $transactionPath `
            -Force `
            -ErrorAction Stop
    }
    catch {
        throw ("Installation failed: {0} Exact rollback also failed: {1}. " +
            "Restore snapshot {2}." -f
            $installationError.Exception.Message,
            $_.Exception.Message,
            $RecoverySnapshotName)
    }
    throw ("Installation failed: {0} Exact rollback completed." -f
        $installationError.Exception.Message)
}

if (Test-Path -LiteralPath $transactionPath) {
    Remove-Item `
        -LiteralPath $transactionPath `
        -Force `
        -ErrorAction Stop
}
Write-Host ""
Write-Host "Phase 2 driver and VHF children enumerated successfully." -ForegroundColor Green
Write-Host "Kernel service: Running"
Write-Host "Virtual keyboard, relative mouse, and absolute mouse: OK"
Write-Host "The probe was not launched and no input report was submitted."
Write-Host "Installation receipt: $installReceiptPath"
}
finally {
    Exit-ComotePhase2RuntimeLock -Mutex $runtimeLock
}
