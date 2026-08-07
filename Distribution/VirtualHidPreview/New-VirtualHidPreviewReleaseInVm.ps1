#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [switch]$AcknowledgeDisposableVm,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$SnapshotName,

    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{2,63}$')]
    [string]$ReleaseId,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$SourceRoot,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedSourceInventorySha256,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$IsolatedWorkRoot,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$SignToolPath,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$OutputDirectory
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot "VirtualHidPreview.Common.ps1")

function Get-ComoteSourceRecords {
    param(
        [Parameter(Mandatory)]
        [string]$Root
    )

    $includedRoots = @(
        "ApiCheck",
        "Driver",
        "Host",
        "Viewer",
        "InputCore",
        "InputCore.SelfTest",
        "HostInputSelfTest",
        "InputBroker",
        "ViewerLifecycleSelfTest",
        "RemoteFileSenderSelfTest",
        "SecureChannelSelfTest",
        "HubTransportSelfTest",
        "ffmpeg",
        "Distribution/VirtualHidPreview"
    )
    $excludedSegments = @(
        ".git",
        ".vs",
        "artifacts",
        "bin",
        "obj"
    )
    $backupSuffixPattern =
        '(?i)(?:\.bak|\.backup|\.codex-backup|' +
        '\.servicecredential-backup|\.autoclipboard-backup|[-.]backup)$'
    $records = @()
    $allowedRootInputNames = @(
        ".editorconfig",
        "global.json",
        "NuGet.config",
        "nuget.config",
        "Directory.Build.props",
        "Directory.Build.targets",
        "Directory.Build.rsp",
        "Directory.Packages.props",
        "Directory.Packages.targets",
        "MSBuild.rsp"
    )
    $rootInputFiles = @()
    foreach ($file in @(
        Get-ChildItem `
            -LiteralPath $Root `
            -File `
            -Force `
            -ErrorAction Stop
    )) {
        $candidate =
            $file.Name -ieq ".editorconfig" -or
            $file.Name -ieq "global.json" -or
            $file.Name -ieq "nuget.config" -or
            $file.Name -ieq "MSBuild.rsp" -or
            $file.Name -imatch '^Directory\.(?:Build|Packages)\.' -or
            $file.Extension -in @(".props", ".targets", ".rsp")
        if (-not $candidate) {
            continue
        }
        if ($allowedRootInputNames -cnotcontains $file.Name) {
            throw "Unhandled or incorrectly cased root build input: $($file.Name)"
        }
        [void](Assert-ComoteOrdinaryLocalPath `
            -LiteralPath $file.FullName `
            -Directory $false `
            -Description "Root build input")
        $rootInputFiles += $file
    }
    if (@(
            $rootInputFiles |
                Where-Object {
                    $_.Name -in @("NuGet.config", "nuget.config")
                }
        ).Count -gt 1) {
        throw "NuGet.config and nuget.config are ambiguous at the source root."
    }
    foreach ($file in $rootInputFiles) {
        $records += [PSCustomObject][ordered]@{
            path = $file.Name
            sourcePath = $file.FullName
            length = [int64]$file.Length
            sha256 = Get-ComoteSha256 -LiteralPath $file.FullName
        }
    }
    foreach ($includedRoot in $includedRoots) {
        $path = Join-Path $Root $includedRoot.Replace('/', '\')
        if (-not (Test-Path -LiteralPath $path -PathType Container)) {
            throw "Required source directory is missing: $includedRoot"
        }
        foreach ($file in @(
            Get-ChildItem `
                -LiteralPath $path `
                -File `
                -Force `
                -Recurse `
                -ErrorAction Stop
        )) {
            $relative = $file.FullName.Substring(
                $Root.TrimEnd('\').Length + 1
            ).Replace('\', '/')
            if ($file.Name -match $backupSuffixPattern) {
                throw "Release copy rejects backup artifact: $relative"
            }
            $segments = @($relative.Split('/'))
            $excluded = $false
            foreach ($segment in $segments) {
                if ($excludedSegments -contains $segment) {
                    $excluded = $true
                    break
                }
            }
            if ($excluded) {
                continue
            }
            [void](Assert-ComoteOrdinaryLocalPath `
                -LiteralPath $file.FullName `
                -Directory $false `
                -Description "Release source file")
            $records += [PSCustomObject][ordered]@{
                path = $relative
                sourcePath = $file.FullName
                length = [int64]$file.Length
                sha256 = Get-ComoteSha256 -LiteralPath $file.FullName
            }
        }
    }
    return @($records | Sort-Object path)
}

function Get-ComoteSourceInventoryHash {
    param(
        [Parameter(Mandatory)]
        [object[]]$Records
    )

    $lines = @(
        "COMOTE-VIRTUAL-HID-PREVIEW-SOURCE-V1"
        foreach ($record in $Records) {
            "{0}|{1}|{2}" -f
                $record.path,
                $record.length,
                $record.sha256
        }
    )
    $bytes = (New-Object Text.ASCIIEncoding).GetBytes(
        (($lines -join "`r`n") + "`r`n")
    )
    return [BitConverter]::ToString(
        [Security.Cryptography.SHA256]::Create().ComputeHash($bytes)
    ).Replace("-", "")
}

function Write-ComoteStrictDriverManifest {
    param(
        [Parameter(Mandatory)]
        [string]$PackagePath,

        [Parameter(Mandatory)]
        [string]$OutputPath
    )

    $inf = Get-Item `
        -LiteralPath (Join-Path `
            $PackagePath `
            "ComoteVirtualHidPhase2.inf") `
        -ErrorAction Stop
    $cat = Get-Item `
        -LiteralPath (Join-Path `
            $PackagePath `
            "ComoteVirtualHidPhase2.cat") `
        -ErrorAction Stop
    $sys = Get-Item `
        -LiteralPath (Join-Path `
            $PackagePath `
            "ComoteVirtualHidPhase2.sys") `
        -ErrorAction Stop
    $lines = @(
        "COMOTE-PHASE2-PACKAGE-MANIFEST-V1",
        "HardwareId=ROOT\COMOTEVIRTUALHID_PHASE2",
        "RootInstanceId=ROOT\COMOTEVIRTUALHID_PHASE2\COMOTE_PHASE2",
        "ServiceName=ComoteVirtualHidPhase2",
        "Provider=Comote",
        ("PackageFiles=" +
            "ComoteVirtualHidPhase2.inf," +
            "ComoteVirtualHidPhase2.cat," +
            "ComoteVirtualHidPhase2.sys"),
        "InfSize=$($inf.Length)",
        "InfSha256=$(Get-ComoteSha256 -LiteralPath $inf.FullName)",
        "CatSize=$($cat.Length)",
        "CatSha256=$(Get-ComoteSha256 -LiteralPath $cat.FullName)",
        "SysSize=$($sys.Length)",
        "SysSha256=$(Get-ComoteSha256 -LiteralPath $sys.FullName)"
    )
    [IO.File]::WriteAllText(
        $OutputPath,
        (($lines -join "`r`n") + "`r`n"),
        (New-Object Text.ASCIIEncoding)
    )
    return Read-ComotePhase2DriverManifest -LiteralPath $OutputPath
}

$expectedSourceHash = $ExpectedSourceInventorySha256.ToUpperInvariant()
[void](Assert-ComoteDisposableVmEnvironment `
    -AcknowledgeDisposableVm:$AcknowledgeDisposableVm `
    -RequireTestSigning)
$operationLock = Enter-ComotePreviewLock
try {
    if ($SnapshotName.Length -lt 3 -or
        $SnapshotName.Length -gt 128 -or
        $SnapshotName -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{2,127}$') {
        throw "SnapshotName must be the exact safe clean VMware snapshot name."
    }
    $sourceFullPath = [IO.Path]::GetFullPath($SourceRoot)
    [void](Assert-ComoteOrdinaryLocalPath `
        -LiteralPath $sourceFullPath `
        -Directory $true `
        -Description "Final source root")
    $records = @(Get-ComoteSourceRecords -Root $sourceFullPath)
    if ($records.Count -lt 50) {
        throw "The final source inventory is unexpectedly small."
    }
    $actualSourceHash = Get-ComoteSourceInventoryHash -Records $records
    if ($actualSourceHash -cne $expectedSourceHash) {
        throw "The final source inventory does not match its out-of-band pin."
    }

    $isolatedFullPath = [IO.Path]::GetFullPath($IsolatedWorkRoot)
    $outputFullPath = [IO.Path]::GetFullPath($OutputDirectory)
    if (Test-Path -LiteralPath $isolatedFullPath) {
        throw "IsolatedWorkRoot must not already exist."
    }
    if (Test-Path -LiteralPath $outputFullPath) {
        throw "OutputDirectory must not already exist."
    }
    foreach ($parent in @(
        [IO.Path]::GetDirectoryName($isolatedFullPath),
        [IO.Path]::GetDirectoryName($outputFullPath)
    )) {
        [void](Assert-ComoteOrdinaryLocalPath `
            -LiteralPath $parent `
            -Directory $true `
            -Description "VM release output parent")
    }
    if ($isolatedFullPath.Equals(
            $sourceFullPath,
            [StringComparison]::OrdinalIgnoreCase) -or
        $isolatedFullPath.StartsWith(
            "$($sourceFullPath.TrimEnd('\'))\",
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "The isolated work root must not be the final source tree."
    }

    [IO.Directory]::CreateDirectory($isolatedFullPath) | Out-Null
    foreach ($record in $records) {
        $destination = Join-Path `
            $isolatedFullPath `
            ([string]$record.path).Replace('/', '\')
        $parent = [IO.Path]::GetDirectoryName($destination)
        [IO.Directory]::CreateDirectory($parent) | Out-Null
        [IO.File]::Copy([string]$record.sourcePath, $destination, $false)
        if ((Get-ComoteSha256 -LiteralPath $destination) -cne
            [string]$record.sha256) {
            throw "The isolated source copy failed verification."
        }
    }
    $isolatedRecords = @(Get-ComoteSourceRecords -Root $isolatedFullPath)
    if ((Get-ComoteSourceInventoryHash -Records $isolatedRecords) -cne
        $expectedSourceHash) {
        throw "The complete isolated source inventory differs from the pin."
    }

    $handoffRoot = Join-Path $isolatedFullPath "ReleaseHandoff"
    $regressionGate = Join-Path `
        $isolatedFullPath `
        "Distribution\VirtualHidPreview\Invoke-VirtualHidPreviewRegressionGate.ps1"
    & $regressionGate `
        -AcknowledgeDisposableVm `
        -SnapshotName $SnapshotName `
        -SourceRoot $isolatedFullPath `
        -ExpectedSourceInventorySha256 $expectedSourceHash `
        -ReleaseHandoffRoot $handoffRoot
    if ($LASTEXITCODE -notin @($null, 0)) {
        throw "The isolated pre-driver regression gate failed."
    }
    $regressionGatePath = Join-Path $handoffRoot "REGRESSION_GATE.json"
    $nugetSbomPath = Join-Path $handoffRoot "NUGET_SBOM.json"
    $mediaGatePath = Join-Path $handoffRoot "MEDIA_GATE.json"
    foreach ($gateArtifact in @(
        $regressionGatePath,
        $nugetSbomPath,
        $mediaGatePath
    )) {
        [void](Assert-ComoteOrdinaryLocalPath `
            -LiteralPath $gateArtifact `
            -Directory $false `
            -Description "Pre-driver regression artifact")
    }
    $regressionReport = Read-ComoteJson `
        -LiteralPath $regressionGatePath `
        -Description "Pre-driver regression report"
    $regressionGateHash = Get-ComoteSha256 `
        -LiteralPath $regressionGatePath
    $nugetSbomHash = Get-ComoteSha256 -LiteralPath $nugetSbomPath
    $mediaGateHash = Get-ComoteSha256 -LiteralPath $mediaGatePath
    if ([int]$regressionReport.schemaVersion -ne 1 -or
        [string]$regressionReport.status -cne "passed" -or
        [string]$regressionReport.sourceInventorySha256 -cne
            $expectedSourceHash -or
        [int]$regressionReport.vulnerabilityCount -ne 0 -or
        [bool]$regressionReport.noDriverDeviceInputOrSystemMutation -ne
            $true -or
        [string]$regressionReport.nugetSbom.sha256 -cne $nugetSbomHash -or
        [string]$regressionReport.mediaGate.evidenceSha256 -cne
            $mediaGateHash) {
        throw "The isolated pre-driver regression evidence is invalid."
    }

    $phase2Root = Join-Path `
        $isolatedFullPath `
        "Driver\ComoteVirtualHidPhase2"
    $vmBuild = Join-Path $phase2Root "Invoke-Phase2VmBuild.ps1"
    & $vmBuild `
        -AcknowledgeDisposableVm `
        -SnapshotName $SnapshotName `
        -Configuration Release `
        -Inf2CatOs "10_X64" `
        -RequiredBuildNumber "19045"
    if ($LASTEXITCODE -notin @($null, 0)) {
        throw "The fresh Phase 2 VM build gate failed."
    }

    $prepareSigning = Join-Path `
        $phase2Root `
        "Runtime\Prepare-Phase2TestSigning.ps1"
    & $prepareSigning `
        -AcknowledgeDisposableVm `
        -SnapshotName $SnapshotName `
        -RequiredBuildNumber "19045"
    if ($LASTEXITCODE -notin @($null, 0)) {
        throw "Fresh Phase 2 signing preparation failed."
    }

    $signedPackagePath = Join-Path `
        $phase2Root `
        "artifacts\phase2-test-signed"
    $signingReceiptPath = Join-Path `
        $phase2Root `
        "artifacts\phase2-runtime-state\test-signing-preparation.json"
    $signingReceipt = Read-ComoteJson `
        -LiteralPath $signingReceiptPath `
        -Description "Fresh Phase 2 signing receipt"
    if ([string]$signingReceipt.status -cne "signed-package-ready" -or
        [bool]$signingReceipt.testSigningChangedByComote -ne $false -or
        [string]$signingReceipt.snapshotName -cne $SnapshotName) {
        throw "Fresh Phase 2 signing preparation did not produce a valid receipt."
    }

    $finalDriverPackage = Join-Path $handoffRoot "DriverPackage"
    [IO.Directory]::CreateDirectory($finalDriverPackage) | Out-Null
    foreach ($driverName in @(
        "ComoteVirtualHidPhase2.inf",
        "ComoteVirtualHidPhase2.cat",
        "ComoteVirtualHidPhase2.sys"
    )) {
        [IO.File]::Copy(
            (Join-Path $signedPackagePath $driverName),
            (Join-Path $finalDriverPackage $driverName),
            $false
        )
    }
    $driverManifestPath = Join-Path `
        $handoffRoot `
        "package-manifest.txt"
    $driverManifest = Write-ComoteStrictDriverManifest `
        -PackagePath $finalDriverPackage `
        -OutputPath $driverManifestPath

    $installerRoot = Join-Path $isolatedFullPath "Driver\Installer"
    $pinBuilder = Join-Path `
        $installerRoot `
        "Build-ComoteReleasePinnedInstaller.ps1"
    & $pinBuilder `
        -ManifestPath $driverManifestPath `
        -PackageDirectory $finalDriverPackage `
        -ProjectPath (Join-Path `
            $installerRoot `
            "ComoteDriverInstaller.vcxproj") `
        -OutputHeader (Join-Path `
            $installerRoot `
            "ComoteReleaseManifestPin.h") `
        -Build
    if ($LASTEXITCODE -notin @($null, 0)) {
        throw "The release-pinned native installer build failed."
    }
    $nativeInstallerPath = Join-Path `
        $installerRoot `
        "bin\x64\Release\ComoteDriverInstaller.exe"
    [void](Assert-ComoteOrdinaryLocalPath `
        -LiteralPath $nativeInstallerPath `
        -Directory $false `
        -Description "Fresh release-pinned native installer")
    $nativeInstallerHash = Get-ComoteSha256 `
        -LiteralPath $nativeInstallerPath
    $signingReceiptHash = Get-ComoteSha256 `
        -LiteralPath $signingReceiptPath

    $releaseBuilder = Join-Path `
        $isolatedFullPath `
        "Distribution\VirtualHidPreview\Build-VirtualHidPreviewRelease.ps1"
    & $releaseBuilder `
        -AcknowledgeDisposableVm `
        -ReleaseId $ReleaseId `
        -SignedDriverPackageDirectory $signedPackagePath `
        -Phase2SigningReceiptPath $signingReceiptPath `
        -ExpectedSigningReceiptSha256 $signingReceiptHash `
        -DriverManifestPath $driverManifestPath `
        -ExpectedDriverManifestSha256 $driverManifest.Sha256 `
        -PinnedNativeInstallerPath $nativeInstallerPath `
        -ExpectedPinnedInstallerSha256 $nativeInstallerHash `
        -RegressionGatePath $regressionGatePath `
        -ExpectedRegressionGateSha256 $regressionGateHash `
        -NuGetSbomPath $nugetSbomPath `
        -ExpectedNuGetSbomSha256 $nugetSbomHash `
        -ExpectedSourceInventorySha256 $expectedSourceHash `
        -ExpectedCodeSigningCertificateThumbprint (
            [string]$signingReceipt.certificateThumbprint
        ) `
        -SignToolPath $SignToolPath `
        -OutputDirectory $outputFullPath
    if ($LASTEXITCODE -notin @($null, 0)) {
        throw "The final preview release packaging step failed."
    }

    $preparationReport = [PSCustomObject][ordered]@{
        schemaVersion = 1
        releaseId = $ReleaseId
        completedUtc = [DateTime]::UtcNow.ToString("o")
        snapshotName = $SnapshotName
        sourceInventorySha256 = $expectedSourceHash
        regressionGateSha256 = $regressionGateHash
        nugetSbomSha256 = $nugetSbomHash
        mediaGateSha256 = $mediaGateHash
        signingReceiptSha256 = $signingReceiptHash
        driverManifestSha256 = [string]$driverManifest.Sha256
        pinnedInstallerInputSha256 = $nativeInstallerHash
        certificateThumbprint =
            ([string]$signingReceipt.certificateThumbprint).ToUpperInvariant()
        isolatedWorkRoot = $isolatedFullPath
        outputDirectory = $outputFullPath
        testSigningChangedByWorkflow = $false
        note = "Fresh build/sign/package only; no driver was installed or loaded."
    }
    [IO.Directory]::CreateDirectory($outputFullPath) | Out-Null
    Write-ComoteJsonAtomically `
        -LiteralPath (Join-Path `
            $outputFullPath `
            "phase2-release-preparation-report.json") `
        -InputObject $preparationReport `
        -Depth 8

    Write-Host ""
    Write-Host "Fresh isolated VM release build completed." `
        -ForegroundColor Green
    Write-Host "Source inventory SHA-256: $expectedSourceHash"
    Write-Host "Output: $outputFullPath"
    Write-Host "No driver was installed or loaded."
    Write-Host "TESTSIGNING, Secure Boot, and HVCI were not changed."
}
finally {
    Exit-ComotePreviewLock -Mutex $operationLock
}
