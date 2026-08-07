#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [switch]$AcknowledgeDisposableVm,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$SnapshotName,

    [ValidatePattern("^19045$")]
    [string]$RequiredBuildNumber = "19045",

    [switch]$ValidateOnly
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot "Phase1Runtime.Common.ps1")

function Get-ComotePhase1DriverPackages {
    return @(
        Get-WindowsDriver -Online -ErrorAction Stop |
            Where-Object {
                [string]$_.ProviderName -eq "Comote" -and
                [string]$_.ClassName -eq "System" -and
                [IO.Path]::GetFileName([string]$_.OriginalFileName) -eq
                    "ComoteVirtualHid.inf"
            }
    )
}

function Get-ComotePhase1RootDevices {
    return @(
        Get-CimInstance `
            -ClassName Win32_PnPEntity `
            -ErrorAction SilentlyContinue |
            Where-Object {
                @($_.HardwareID) -contains "ROOT\COMOTEVIRTUALHID"
            }
    )
}

function Test-ComotePhase1DeviceDescendant {
    param(
        [Parameter(Mandatory)]
        [string]$InstanceId,

        [Parameter(Mandatory)]
        [string]$AncestorInstanceId
    )

    $current = $InstanceId
    for ($depth = 0; $depth -lt 10; $depth++) {
        $parentProperties = @(
            Get-PnpDeviceProperty `
                -InstanceId $current `
                -KeyName "DEVPKEY_Device_Parent" `
                -ErrorAction SilentlyContinue
        )
        if ($parentProperties.Count -ne 1) {
            return $false
        }
        $dataProperty =
            $parentProperties[0].PSObject.Properties["Data"]
        if ($null -eq $dataProperty) {
            return $false
        }
        $parent = [string]$dataProperty.Value
        if ([string]::IsNullOrWhiteSpace($parent)) {
            return $false
        }
        if ($parent.Equals(
                $AncestorInstanceId,
                [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
        if ($parent.Equals(
                $current,
                [StringComparison]::OrdinalIgnoreCase)) {
            return $false
        }
        $current = $parent
    }

    return $false
}

function Invoke-ComotePhase1InstallRollback {
    param(
        [Parameter(Mandatory)]
        [string]$DevGenPath,

        [AllowNull()]
        [string]$PublishedInfName,

        [switch]$PackageWasStaged
    )

    $rollbackErrors = @()
    $rootDevices = @(Get-ComotePhase1RootDevices)
    foreach ($device in $rootDevices) {
        try {
            $removeOutput = (& $DevGenPath `
                /remove `
                ([string]$device.PNPDeviceID) `
                /subtree 2>&1 | Out-String)
            if ($LASTEXITCODE -ne 0) {
                $rollbackErrors +=
                    "DevGen removal failed: $removeOutput"
            }
        }
        catch {
            $rollbackErrors += $_.Exception.Message
        }
    }

    for ($attempt = 0; $attempt -lt 15; $attempt++) {
        if (@(Get-ComotePhase1RootDevices).Count -eq 0) {
            break
        }
        Start-Sleep -Seconds 1
    }
    if (@(Get-ComotePhase1RootDevices).Count -ne 0) {
        $rollbackErrors +=
            "The Comote root device remained after rollback."
    }

    if ($PackageWasStaged.IsPresent -and
        [string]::IsNullOrWhiteSpace($PublishedInfName)) {
        $rollbackErrors +=
            "The staged package published name is unknown."
    }
    elseif (-not [string]::IsNullOrWhiteSpace($PublishedInfName)) {
        try {
            $deleteOutput = (& pnputil.exe `
                /delete-driver `
                $PublishedInfName `
                /uninstall 2>&1 | Out-String)
            if ($LASTEXITCODE -ne 0) {
                $rollbackErrors +=
                    "PnPUtil package removal failed: $deleteOutput"
            }
        }
        catch {
            $rollbackErrors += $_.Exception.Message
        }
    }

    return @($rollbackErrors)
}

$environment = Assert-ComotePhase1RuntimeEnvironment `
    -AcknowledgeDisposableVm:$AcknowledgeDisposableVm `
    -RequiredBuildNumber $RequiredBuildNumber
[void](Assert-ComotePhase1SigningPrerequisites)
if (-not (Get-ComotePhase1TestSigningState)) {
    throw "TESTSIGNING is not configured in the current BCD entry."
}
$codeIntegrityState = Get-ComotePhase1ActiveCodeIntegrityState
if (-not $codeIntegrityState.TestSigningActive) {
    throw "TESTSIGNING is configured in BCD but is not active in the current kernel. Restart the VM before continuing."
}
Assert-ComotePhase1NoInstalledDevice

foreach ($commandName in @(
    "Get-WindowsDriver",
    "Get-PnpDevice",
    "Get-PnpDeviceProperty"
)) {
    if (-not (Get-Command $commandName -ErrorAction SilentlyContinue)) {
        throw "Required Windows command was not found: $commandName"
    }
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$signingReceiptPath = Join-Path `
    $projectRoot `
    "artifacts\phase1-runtime-state\test-signing-preparation.json"
$installReceiptPath = Join-Path `
    $projectRoot `
    "artifacts\phase1-runtime-state\enumeration-installation.json"
if (-not (Test-Path -LiteralPath $signingReceiptPath -PathType Leaf)) {
    throw "The Phase 1 signing receipt was not found."
}
$priorRemovalReceipt = $null
if (Test-Path -LiteralPath $installReceiptPath) {
    $priorRemovalReceipt = Get-Content `
        -LiteralPath $installReceiptPath `
        -Raw |
        ConvertFrom-Json
    if ([string]$priorRemovalReceipt.status -ne "removed" -or
        [string]$priorRemovalReceipt.snapshotName -ne $SnapshotName -or
        [string]$priorRemovalReceipt.serviceName -ne
            "ComoteVirtualHid") {
        throw "An active or invalid Phase 1 installation receipt already exists."
    }
}

$signingReceipt = Get-Content `
    -LiteralPath $signingReceiptPath `
    -Raw |
    ConvertFrom-Json
if ($null -eq $signingReceipt.PSObject.Properties["activeTestSigning"] -or
    $null -eq
        $signingReceipt.PSObject.Properties["activeCodeIntegrityOptions"] -or
    [string]$signingReceipt.status -ne "test-mode-ready" -or
    [string]$signingReceipt.testModeSnapshotName -ne $SnapshotName -or
    -not [bool]$signingReceipt.testSigningChangedByComote -or
    -not [bool]$signingReceipt.activeTestSigning -or
    -not ([uint32]$signingReceipt.activeCodeIntegrityOptions -band 0x02)) {
    throw "The Phase 1 signing receipt is not ready for installation."
}

$thumbprint = [string]$signingReceipt.certificateThumbprint
if ($thumbprint -notmatch "^[0-9A-Fa-f]{40}$" -or
    [string]$signingReceipt.certificateSubject -notlike
        "CN=Comote Phase 1 VM Test Signing *") {
    throw "The Phase 1 signing receipt contains an invalid certificate identity."
}
foreach ($requiredCertificatePath in @(
    "Cert:\LocalMachine\My\$thumbprint",
    "Cert:\LocalMachine\Root\$thumbprint",
    "Cert:\LocalMachine\TrustedPublisher\$thumbprint"
)) {
    if (-not (Test-Path -LiteralPath $requiredCertificatePath)) {
        throw "A required Phase 1 certificate copy is missing."
    }
    $copy = Get-Item -LiteralPath $requiredCertificatePath
    if ([string]$copy.Subject -ne
        [string]$signingReceipt.certificateSubject) {
        throw "A Phase 1 certificate subject does not match its receipt."
    }
}

$signedPackagePath = [string]$signingReceipt.signedPackagePath
$expectedSignedPackagePath = Join-Path `
    $projectRoot `
    "artifacts\phase1-test-signed"
if ([IO.Path]::GetFullPath($signedPackagePath) -ne
    [IO.Path]::GetFullPath($expectedSignedPackagePath)) {
    throw "The signed package path does not match this project."
}
foreach ($entry in @($signingReceipt.files)) {
    $filePath = Join-Path $signedPackagePath ([string]$entry.file)
    if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) {
        throw "A signed package file is missing: $filePath"
    }
    $actualHash = (Get-FileHash `
        -Algorithm SHA256 `
        -LiteralPath $filePath).Hash
    if ($actualHash -ne [string]$entry.sha256) {
        throw "The signed package changed after preparation: $filePath"
    }
}
foreach ($signedFileName in @(
    "ComoteVirtualHid.sys",
    "ComoteVirtualHid.cat"
)) {
    $signedFile = Join-Path $signedPackagePath $signedFileName
    $signature = Get-AuthenticodeSignature -LiteralPath $signedFile
    if ($signature.Status -ne
        [System.Management.Automation.SignatureStatus]::Valid -or
        $signature.SignerCertificate.Thumbprint -ne $thumbprint) {
        throw "The signed package failed Authenticode validation."
    }
}

$existingPackages = @(Get-ComotePhase1DriverPackages)
if ($existingPackages.Count -ne 0) {
    throw "A Comote driver package is already in the Driver Store."
}

$programFilesX86 = [Environment]::GetFolderPath(
    [Environment+SpecialFolder]::ProgramFilesX86
)
$devGen = Find-ComotePhase1Tool `
    -Root (Join-Path $programFilesX86 "Windows Kits\10\Tools") `
    -Name "devgen.exe"
if (-not $devGen) {
    throw "DevGen.exe was not found in the installed WDK."
}

$baselineKeyboardIds = @(
    Get-PnpDevice `
        -PresentOnly `
        -Class Keyboard `
        -ErrorAction SilentlyContinue |
        ForEach-Object { [string]$_.InstanceId }
)
$baselineMouseIds = @(
    Get-PnpDevice `
        -PresentOnly `
        -Class Mouse `
        -ErrorAction SilentlyContinue |
        ForEach-Object { [string]$_.InstanceId }
)

if ($ValidateOnly.IsPresent) {
    Write-Host ""
    Write-Host "Phase 1 enumeration installation preflight passed." -ForegroundColor Green
    Write-Host "No driver package was staged and no device was created."
    return
}

$cycleId = [guid]::NewGuid().ToString("N")
$priorRemovalReceiptArchivePath = $null
if ($null -ne $priorRemovalReceipt) {
    $historyDirectory = Join-Path `
        (Split-Path -Parent $installReceiptPath) `
        "enumeration-history"
    New-Item `
        -ItemType Directory `
        -Path $historyDirectory `
        -Force |
        Out-Null
    $priorRemovalReceiptArchivePath = Join-Path `
        $historyDirectory `
        ("removed-{0}-{1}.json" -f
            (Get-Date -Format "yyyyMMdd-HHmmss"),
            $cycleId)
    if (Test-Path -LiteralPath $priorRemovalReceiptArchivePath) {
        throw "The generated removal history path already exists."
    }
    Move-Item `
        -LiteralPath $installReceiptPath `
        -Destination $priorRemovalReceiptArchivePath
    if ((Test-Path -LiteralPath $installReceiptPath) -or
        -not (Test-Path `
            -LiteralPath $priorRemovalReceiptArchivePath `
            -PathType Leaf)) {
        throw "The prior removal receipt was not archived safely."
    }
}

$infPath = Join-Path $signedPackagePath "ComoteVirtualHid.inf"
$publishedInfName = $null
$packageWasStaged = $false
$installationError = $null
try {
    $stageOutput = (& pnputil.exe `
        /add-driver `
        $infPath 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) {
        throw "PnPUtil failed to stage the driver package: $stageOutput"
    }
    $packageWasStaged = $true
    $publishedMatch = [regex]::Match(
        $stageOutput,
        '(?i)\boem\d+\.inf\b'
    )
    if ($publishedMatch.Success) {
        $publishedInfName = $publishedMatch.Value.ToLowerInvariant()
    }

    $driverPackages = @()
    for ($attempt = 0; $attempt -lt 15; $attempt++) {
        $driverPackages = @(Get-ComotePhase1DriverPackages)
        if ($driverPackages.Count -eq 1) {
            break
        }
        Start-Sleep -Seconds 1
    }
    if ($driverPackages.Count -ne 1) {
        throw "The staged Comote package could not be uniquely identified."
    }
    $inventoryPublishedName =
        ([string]$driverPackages[0].Driver).ToLowerInvariant()
    if ($inventoryPublishedName -notmatch "^oem\d+\.inf$") {
        throw "The staged package has an invalid published INF name."
    }
    if (-not [string]::IsNullOrWhiteSpace($publishedInfName) -and
        $publishedInfName -ne $inventoryPublishedName) {
        throw "PnPUtil and Driver Store inventory disagree on the published INF."
    }
    $publishedInfName = $inventoryPublishedName

    $devGenOutput = (& $devGen `
        /add `
        /bus ROOT `
        /instanceid COMOTE_PHASE1 `
        /hardwareid "ROOT\COMOTEVIRTUALHID" 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) {
        throw "DevGen failed to create the root device: $devGenOutput"
    }

    $bindOutput = (& pnputil.exe `
        /add-driver `
        $infPath `
        /install 2>&1 | Out-String)
    $bindExitCode = $LASTEXITCODE
    if ($bindExitCode -eq 3010) {
        throw "PnPUtil requested a reboot while binding the driver; this Phase 1 gate does not permit an in-place reboot: $bindOutput"
    }
    if ($bindExitCode -notin @(0, 259)) {
        throw ("PnPUtil failed to bind the driver to the DevGen device " +
            "(exit code {0}): {1}" -f
            $bindExitCode,
            $bindOutput)
    }

    $rootDevices = @()
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        $rootDevices = @(Get-ComotePhase1RootDevices)
        if ($rootDevices.Count -eq 1 -and
            [int]$rootDevices[0].ConfigManagerErrorCode -eq 0) {
            break
        }
        Start-Sleep -Seconds 1
    }
    if ($rootDevices.Count -ne 1) {
        throw "The Comote root device did not enumerate uniquely."
    }
    $rootDevice = $rootDevices[0]
    if ([int]$rootDevice.ConfigManagerErrorCode -ne 0) {
        throw ("The Comote root device reported ConfigManagerErrorCode={0}." -f
            $rootDevice.ConfigManagerErrorCode)
    }
    $rootInstanceId = [string]$rootDevice.PNPDeviceID

    $linkedKeyboards = @()
    $linkedMice = @()
    $driverService = $null
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        $driverService = Get-CimInstance `
            -ClassName Win32_SystemDriver `
            -Filter "Name='ComoteVirtualHid'" `
            -ErrorAction SilentlyContinue
        $newKeyboards = @(
            Get-PnpDevice `
                -PresentOnly `
                -Class Keyboard `
                -ErrorAction SilentlyContinue |
                Where-Object {
                    $baselineKeyboardIds -notcontains
                        [string]$_.InstanceId
                }
        )
        $newMice = @(
            Get-PnpDevice `
                -PresentOnly `
                -Class Mouse `
                -ErrorAction SilentlyContinue |
                Where-Object {
                    $baselineMouseIds -notcontains
                        [string]$_.InstanceId
                }
        )
        $linkedKeyboards = @(
            $newKeyboards |
                Where-Object {
                    Test-ComotePhase1DeviceDescendant `
                        -InstanceId ([string]$_.InstanceId) `
                        -AncestorInstanceId $rootInstanceId
                }
        )
        $linkedMice = @(
            $newMice |
                Where-Object {
                    Test-ComotePhase1DeviceDescendant `
                        -InstanceId ([string]$_.InstanceId) `
                        -AncestorInstanceId $rootInstanceId
                }
        )
        $driverServiceState = if ($null -ne $driverService) {
            [string]$driverService.State
        } else {
            ""
        }
        if ($driverServiceState -eq "Running" -and
            $linkedKeyboards.Count -ge 1 -and
            $linkedMice.Count -ge 1) {
            break
        }
        Start-Sleep -Seconds 1
    }
    $driverServiceState = if ($null -ne $driverService) {
        [string]$driverService.State
    } else {
        ""
    }
    if ($driverServiceState -ne "Running") {
        throw "The ComoteVirtualHid kernel driver service is not running."
    }
    if ($linkedKeyboards.Count -lt 1 -or
        $linkedMice.Count -lt 1) {
        throw "The VHF keyboard and mouse children did not enumerate."
    }
    foreach ($inputDevice in @($linkedKeyboards + $linkedMice)) {
        if ([string]$inputDevice.Status -ne "OK") {
            throw "A VHF input child reported a non-OK status."
        }
    }

    $stateDirectory = Split-Path -Parent $installReceiptPath
    New-Item -ItemType Directory -Path $stateDirectory -Force | Out-Null
    $currentVersion = Get-ItemProperty `
        -LiteralPath "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion"
    $installReceipt = [ordered]@{
        schemaVersion = 1
        cycleId = $cycleId
        status = "installed-enumerated"
        installedUtc = [DateTime]::UtcNow.ToString("o")
        snapshotName = $SnapshotName
        manufacturer = [string]$environment.Computer.Manufacturer
        model = [string]$environment.Computer.Model
        osBuildNumber = [string]$environment.OperatingSystem.BuildNumber
        osUbr = [int]$currentVersion.UBR
        installedBootUtc = (
            [DateTime]$environment.OperatingSystem.LastBootUpTime
        ).ToUniversalTime().ToString("o")
        publishedInfName = $publishedInfName
        rootDeviceInstanceId = $rootInstanceId
        serviceName = "ComoteVirtualHid"
        keyboardInstanceIds = @(
            $linkedKeyboards |
                ForEach-Object { [string]$_.InstanceId }
        )
        mouseInstanceIds = @(
            $linkedMice |
                ForEach-Object { [string]$_.InstanceId }
        )
        setupApiLog = "$env:SystemRoot\INF\setupapi.dev.log"
        priorRemovalReceiptArchivePath =
            $priorRemovalReceiptArchivePath
    }
    $installReceipt | ConvertTo-Json -Depth 6 |
        Set-Content -LiteralPath $installReceiptPath -Encoding UTF8
}
catch {
    $installationError = $_
    $diagnosticReportPath = ""
    $diagnosticError = ""
    try {
        $diagnostics = & (Join-Path `
            $PSScriptRoot `
            "Collect-Phase1EnumerationDiagnostics.ps1") `
            -AcknowledgeDisposableVm:$AcknowledgeDisposableVm `
            -RequiredBuildNumber $RequiredBuildNumber `
            -FailureMessage $installationError.Exception.Message
        $diagnosticReportPath = [string]$diagnostics.ReportPath
    }
    catch {
        $diagnosticError = $_.Exception.Message
    }
    $rollbackErrors = @(
        Invoke-ComotePhase1InstallRollback `
            -DevGenPath $devGen `
            -PublishedInfName $publishedInfName `
            -PackageWasStaged:$packageWasStaged
    )
    if (Test-Path -LiteralPath $installReceiptPath) {
        Remove-Item -LiteralPath $installReceiptPath -Force
    }
    if ($rollbackErrors.Count -ne 0 -or
        @(Get-ComotePhase1RootDevices).Count -ne 0 -or
        @(Get-ComotePhase1DriverPackages).Count -ne 0) {
        throw ("Installation failed: {0} Rollback did not complete: {1} Diagnostics: {2} Diagnostic collection error: {3}" -f
            $installationError.Exception.Message,
            ($rollbackErrors -join " | "),
            $diagnosticReportPath,
            $diagnosticError)
    }
    throw ("Installation failed: {0} Rollback completed. Diagnostics: {1} Diagnostic collection error: {2}" -f
        $installationError.Exception.Message,
        $diagnosticReportPath,
        $diagnosticError)
}

Write-Host ""
Write-Host "Phase 1 driver and VHF children enumerated successfully." -ForegroundColor Green
Write-Host "No input-report submission path exists in this build."
Write-Host "Installation receipt: $installReceiptPath"
