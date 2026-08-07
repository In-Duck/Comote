#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [switch]$AcknowledgeDisposableVm,

    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{2,63}$')]
    [string]$ReleaseId,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$SignedDriverPackageDirectory,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$Phase2SigningReceiptPath,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedSigningReceiptSha256,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$DriverManifestPath,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedDriverManifestSha256,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$PinnedNativeInstallerPath,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedPinnedInstallerSha256,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$RegressionGatePath,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedRegressionGateSha256,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$NuGetSbomPath,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedNuGetSbomSha256,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedSourceInventorySha256,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{40}$')]
    [string]$ExpectedCodeSigningCertificateThumbprint,

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

function Assert-ComoteSigningReceipt {
    param(
        [Parameter(Mandatory)]
        [string]$LiteralPath,

        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-F]{64}$')]
        [string]$ExpectedSha256,

        [Parameter(Mandatory)]
        [string]$SignedPackagePath,

        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-F]{40}$')]
        [string]$ExpectedThumbprint
    )

    [void](Assert-ComoteOrdinaryLocalPath `
        -LiteralPath $LiteralPath `
        -Directory $false `
        -Description "Phase 2 signing receipt")
    if ((Get-ComoteSha256 -LiteralPath $LiteralPath) -cne $ExpectedSha256) {
        throw "The Phase 2 signing receipt SHA-256 is not pinned."
    }
    $receipt = Read-ComoteJson `
        -LiteralPath $LiteralPath `
        -Description "Phase 2 signing receipt"
    if ([int]$receipt.schemaVersion -ne 1 -or
        [string]$receipt.status -cne "signed-package-ready" -or
        [string]$receipt.osBuildNumber -cne "19045" -or
        [bool]$receipt.testSigningInitiallyEnabled -ne $true -or
        [bool]$receipt.testSigningChangedByComote -ne $false -or
        [string]$receipt.certificateThumbprint.ToUpperInvariant() -cne
            $ExpectedThumbprint -or
        [string]::IsNullOrWhiteSpace(
            [string]$receipt.certificateSubject)) {
        throw "The Phase 2 signing receipt is not a final supported receipt."
    }
    if (-not [IO.Path]::GetFullPath(
            [string]$receipt.signedPackagePath
        ).Equals(
            [IO.Path]::GetFullPath($SignedPackagePath),
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "The Phase 2 signing receipt names a different package path."
    }

    $expectedNames = @(
        "ComotePhase2Test.cer",
        "ComoteVirtualHidPhase2.cat",
        "ComoteVirtualHidPhase2.inf",
        "ComoteVirtualHidPhase2.sys",
        "ComoteVirtualHidProbe.exe"
    )
    $receiptFiles = @($receipt.files)
    if ($receiptFiles.Count -ne $expectedNames.Count) {
        throw "The Phase 2 signing receipt file inventory is not exact."
    }
    $receiptMap = New-Object `
        'Collections.Generic.Dictionary[string,object]' `
        ([StringComparer]::Ordinal)
    foreach ($entry in $receiptFiles) {
        if ([string]$entry.file -notin $expectedNames -or
            [string]$entry.sha256 -cnotmatch '^[0-9A-F]{64}$' -or
            $receiptMap.ContainsKey([string]$entry.file)) {
            throw "The Phase 2 signing receipt has an invalid file entry."
        }
        $receiptMap.Add([string]$entry.file, $entry)
    }
    foreach ($expectedName in $expectedNames) {
        if (-not $receiptMap.ContainsKey($expectedName)) {
            throw "The Phase 2 signing receipt is missing: $expectedName"
        }
        $filePath = Join-Path $SignedPackagePath $expectedName
        [void](Assert-ComoteOrdinaryLocalPath `
            -LiteralPath $filePath `
            -Directory $false `
            -Description "Prepared Phase 2 file")
        if ((Get-ComoteSha256 -LiteralPath $filePath) -cne
            [string]$receiptMap[$expectedName].sha256) {
            throw "A prepared Phase 2 file differs from its signing receipt."
        }
    }

    $actualNames = @(
        Get-ChildItem -LiteralPath $SignedPackagePath -Force |
            ForEach-Object { $_.Name }
    )
    [Array]::Sort($actualNames, [StringComparer]::Ordinal)
    [Array]::Sort($expectedNames, [StringComparer]::Ordinal)
    if ($actualNames.Count -ne $expectedNames.Count) {
        throw "The prepared Phase 2 directory inventory is not exact."
    }
    for ($index = 0; $index -lt $expectedNames.Count; $index++) {
        if ($actualNames[$index] -cne $expectedNames[$index]) {
            throw "The prepared Phase 2 directory contains an unexpected item."
        }
    }
    return $receipt
}

function Invoke-ComoteDotNetPublish {
    param(
        [Parameter(Mandatory)]
        [string]$ProjectPath,

        [Parameter(Mandatory)]
        [string]$OutputPath,

        [Parameter(Mandatory)]
        [string]$DotNetPath
    )

    [IO.Directory]::CreateDirectory($OutputPath) | Out-Null
    [void](Assert-ComoteOrdinaryLocalPath `
        -LiteralPath $DotNetPath `
        -Directory $false `
        -Description "Regression-pinned dotnet host")
    & $DotNetPath publish $ProjectPath `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        --no-restore `
        -p:RestoreLockedMode=true `
        -p:RuntimeFrameworkVersion=10.0.10 `
        -p:TargetLatestRuntimePatch=false `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        --output $OutputPath
    $publishExitCode = $LASTEXITCODE
    if ($publishExitCode -ne 0) {
        throw "dotnet publish failed with exit code $publishExitCode."
    }
}

function Assert-ComoteRegressionInputs {
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot,

        [Parameter(Mandatory)]
        [string]$ReportPath,

        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-F]{64}$')]
        [string]$ReportSha256,

        [Parameter(Mandatory)]
        [string]$SbomPath,

        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-F]{64}$')]
        [string]$SbomSha256,

        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-F]{64}$')]
        [string]$SourceInventorySha256
    )

    foreach ($binding in @(
        @($ReportPath, $ReportSha256, "Regression gate report"),
        @($SbomPath, $SbomSha256, "Locked NuGet SBOM")
    )) {
        [void](Assert-ComoteOrdinaryLocalPath `
            -LiteralPath $binding[0] `
            -Directory $false `
            -Description $binding[2])
        if ((Get-ComoteSha256 -LiteralPath $binding[0]) -cne
            [string]$binding[1]) {
            throw "$($binding[2]) SHA-256 is not pinned."
        }
    }
    if (-not [IO.Path]::GetDirectoryName(
            [IO.Path]::GetFullPath($ReportPath)
        ).Equals(
            [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($SbomPath)),
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Regression report and SBOM must share the exact handoff root."
    }
    $report = Read-ComoteJson `
        -LiteralPath $ReportPath `
        -Description "Regression gate report"
    Assert-ComoteExactProperties `
        -InputObject $report `
        -Expected @(
            "schemaVersion",
            "status",
            "completedUtc",
            "snapshotName",
            "sourceInventorySha256",
            "sourceHygiene",
            "environment",
            "toolchain",
            "runtimePolicy",
            "projects",
            "lockFiles",
            "assetsFiles",
            "pureTests",
            "observer",
            "mediaGate",
            "boundaryGroups",
            "boundaryScripts",
            "vulnerabilityScans",
            "topLevelVulnerableCount",
            "transitiveVulnerableCount",
            "vulnerabilityCount",
            "nugetSbom",
            "noDriverDeviceInputOrSystemMutation"
        ) `
        -Description "Regression gate report"
    Assert-ComoteExactProperties `
        -InputObject $report.sourceHygiene `
        -Expected @(
            "backupArtifactCount",
            "clipboardDefaultOff",
            "explicitClipboardConsentProtocol"
        ) `
        -Description "Regression source hygiene"
    if ([int]$report.sourceHygiene.backupArtifactCount -ne 0 -or
        [bool]$report.sourceHygiene.clipboardDefaultOff -ne $true -or
        [bool]$report.sourceHygiene.explicitClipboardConsentProtocol -ne
            $true) {
        throw "Regression source hygiene did not pass."
    }
    Assert-ComoteExactProperties `
        -InputObject $report.nugetSbom `
        -Expected @("path", "sha256", "packageCount") `
        -Description "Regression NuGet SBOM binding"
    Assert-ComoteExactProperties `
        -InputObject $report.observer `
        -Expected @(
            "projectPath",
            "projectSha256",
            "sourceSha256",
            "executableSha256",
            "buildOutputSha256"
        ) `
        -Description "Regression observer binding"
    Assert-ComoteExactProperties `
        -InputObject $report.mediaGate `
        -Expected @(
            "projectPath",
            "publishOutputSha256",
            "executableSha256",
            "runOutputSha256",
            "evidencePath",
            "evidenceSha256",
            "ffmpegReceiptSha256",
            "runtimeVersion"
        ) `
        -Description "Regression MediaGate binding"
    Assert-ComoteExactProperties `
        -InputObject $report.runtimePolicy `
        -Expected @(
            "frameworkVersion",
            "runtimePackVersion",
            "coreclrLength",
            "coreclrSha256",
            "coreclrFileVersion",
            "coreclrProductVersion"
        ) `
        -Description "Regression runtime policy"
    if ([int]$report.schemaVersion -ne 1 -or
        [string]$report.status -cne "passed" -or
        [string]$report.sourceInventorySha256 -cne
            $SourceInventorySha256 -or
        [string]$report.toolchain.sdkVersion -cne "10.0.302" -or
        [string]$report.toolchain.runtimeVersion -cne "10.0.10" -or
        [int]$report.boundaryGroups -ne 5 -or
        @($report.boundaryScripts).Count -ne 6 -or
        @($report.pureTests).Count -ne 7 -or
        @($report.projects).Count -ne 13 -or
        @($report.vulnerabilityScans).Count -ne 13 -or
        [int]$report.topLevelVulnerableCount -ne 0 -or
        [int]$report.transitiveVulnerableCount -ne 0 -or
        [int]$report.vulnerabilityCount -ne 0 -or
        [bool]$report.noDriverDeviceInputOrSystemMutation -ne $true -or
        [string]$report.nugetSbom.path -cne "NUGET_SBOM.json" -or
        [string]$report.nugetSbom.sha256 -cne $SbomSha256 -or
        [int]$report.nugetSbom.packageCount -le 0 -or
        [string]$report.observer.executableSha256 -cnotmatch
            '^[0-9A-F]{64}$' -or
        [string]$report.mediaGate.evidencePath -cne "MEDIA_GATE.json" -or
        [string]$report.mediaGate.runtimeVersion -cne "10.0.10" -or
        [string]$report.runtimePolicy.frameworkVersion -cne "10.0.10" -or
        [string]$report.runtimePolicy.runtimePackVersion -cne "10.0.10" -or
        [int64]$report.runtimePolicy.coreclrLength -ne 4614952 -or
        [string]$report.runtimePolicy.coreclrSha256 -cne
            "58859F85A30CC71313B281898E7CFBDBB9ECCB95AE2A3F865329EFD47EBF31BB" -or
        [string]$report.runtimePolicy.coreclrFileVersion -cne
            "10,0,1026,32716 @Commit: f7d90799ce4ef09a0bb257852a57248d2a8fb8dd" -or
        [string]$report.runtimePolicy.coreclrProductVersion -cne
            "10,0,1026,32716 @Commit: f7d90799ce4ef09a0bb257852a57248d2a8fb8dd") {
        throw "The regression gate report did not pass the exact release gate."
    }
    $apiCheckProjects = @(
        $report.projects |
            Where-Object {
                [string]$_.name -ceq "ApiCheck" -and
                [string]$_.path -ceq "ApiCheck/ApiCheck.csproj"
            }
    )
    if ($apiCheckProjects.Count -ne 1 -or
        [bool]$apiCheckProjects[0].publishRestore -ne $false -or
        [string]$apiCheckProjects[0].lockOrigin -cne "source" -or
        [string]$apiCheckProjects[0].lockSha256 -cnotmatch
            '^[0-9A-F]{64}$') {
        throw "Regression evidence omits the exact ApiCheck lock graph."
    }
    foreach ($scan in @($report.vulnerabilityScans)) {
        Assert-ComoteExactProperties `
            -InputObject $scan `
            -Expected @(
                "projectPath",
                "topLevelVulnerableCount",
                "transitiveVulnerableCount",
                "outputSha256"
            ) `
            -Description "Regression vulnerability scan"
        Assert-ComoteSafeRelativePath `
            -RelativePath ([string]$scan.projectPath)
        if ([int]$scan.topLevelVulnerableCount -ne 0 -or
            [int]$scan.transitiveVulnerableCount -ne 0 -or
            [string]$scan.outputSha256 -cnotmatch '^[0-9A-F]{64}$') {
            throw "A regression vulnerability scan is not clean."
        }
    }

    $artifactSets = @(
        @("lockFiles", "packages.lock.json", $false),
        @("assetsFiles", "obj/project.assets.json", $true)
    )
    $artifactMaps = @{}
    foreach ($artifactSet in $artifactSets) {
        $propertyName = [string]$artifactSet[0]
        $suffix = [string]$artifactSet[1]
        $entries = @($report.$propertyName)
        if ($entries.Count -lt 13) {
            throw "Regression $propertyName is incomplete."
        }
        $map = New-Object `
            'Collections.Generic.Dictionary[string,object]' `
            ([StringComparer]::Ordinal)
        foreach ($entry in $entries) {
            Assert-ComoteExactProperties `
                -InputObject $entry `
                -Expected @("path", "length", "sha256") `
                -Description "Regression restore artifact"
            $relative = [string]$entry.path
            Assert-ComoteSafeRelativePath -RelativePath $relative
            if (-not $relative.EndsWith(
                    $suffix,
                    [StringComparison]::Ordinal) -or
                $map.ContainsKey($relative) -or
                [int64]$entry.length -le 0 -or
                [string]$entry.sha256 -cnotmatch '^[0-9A-F]{64}$') {
                throw "A regression restore artifact binding is invalid."
            }
            $path = Join-Path $RepositoryRoot $relative.Replace('/', '\')
            $identity = Assert-ComoteOrdinaryLocalPath `
                -LiteralPath $path `
                -Directory $false `
                -Description "Pinned regression restore artifact"
            if ([int64]$identity.Identity.Length -ne [int64]$entry.length -or
                (Get-ComoteSha256 -LiteralPath $path) -cne
                    [string]$entry.sha256) {
                throw "A locked restore artifact changed after regression."
            }
            $map.Add($relative, $entry)
        }
        $artifactMaps[$propertyName] = $map
    }
    foreach ($project in @($report.projects)) {
        $projectPath = [string]$project.path
        Assert-ComoteSafeRelativePath -RelativePath $projectPath
        $directory = $projectPath.Substring(
            0,
            $projectPath.LastIndexOf('/') + 1
        )
        if (-not $artifactMaps.lockFiles.ContainsKey(
                $directory + "packages.lock.json") -or
            -not $artifactMaps.assetsFiles.ContainsKey(
                $directory + "obj/project.assets.json")) {
            throw "A regression project is not bound to lock/assets evidence."
        }
    }

    $mediaPath = Join-Path `
        ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($ReportPath))) `
        "MEDIA_GATE.json"
    [void](Assert-ComoteOrdinaryLocalPath `
        -LiteralPath $mediaPath `
        -Directory $false `
        -Description "Regression MediaGate evidence")
    if ((Get-ComoteSha256 -LiteralPath $mediaPath) -cne
        [string]$report.mediaGate.evidenceSha256) {
        throw "MediaGate evidence changed after regression."
    }

    $sbom = Read-ComoteJson `
        -LiteralPath $SbomPath `
        -Description "Locked NuGet SBOM"
    Assert-ComoteExactProperties `
        -InputObject $sbom `
        -Expected @(
            "schemaVersion",
            "sourceInventorySha256",
            "runtimePolicy",
            "lockFiles",
            "packageCount",
            "packages"
        ) `
        -Description "Locked NuGet SBOM"
    if ([int]$sbom.schemaVersion -ne 1 -or
        [string]$sbom.sourceInventorySha256 -cne
            $SourceInventorySha256 -or
        [int]$sbom.packageCount -ne @($sbom.packages).Count -or
        [int]$sbom.packageCount -ne [int]$report.nugetSbom.packageCount -or
        [int]$sbom.packageCount -le 0 -or
        @($sbom.lockFiles).Count -ne 13 -or
        ($sbom.lockFiles | ConvertTo-Json -Depth 8 -Compress) -cne
            ($report.lockFiles | ConvertTo-Json -Depth 8 -Compress) -or
        ($sbom.runtimePolicy | ConvertTo-Json -Compress) -cne
            ($report.runtimePolicy | ConvertTo-Json -Compress)) {
        throw "The locked NuGet SBOM identity/count is invalid."
    }
    foreach ($package in @($sbom.packages)) {
        Assert-ComoteExactProperties `
            -InputObject $package `
            -Expected @(
                "name",
                "resolvedVersion",
                "contentHash",
                "nupkgSha512",
                "dependencyTypes",
                "usedByRoles",
                "roleDependencies",
                "licenseExpression",
                "licenseUrl",
                "provenanceUrls",
                "nuspecSha256",
                "sha512FileSha256"
            ) `
            -Description "Locked NuGet SBOM package"
        if ([string]$package.contentHash -notmatch
                '^[A-Za-z0-9+/]+={0,2}$' -or
            [string]$package.nupkgSha512 -cne
                [string]$package.contentHash -or
            [string]$package.nuspecSha256 -cnotmatch '^[0-9A-F]{64}$' -or
            [string]$package.sha512FileSha256 -cnotmatch '^[0-9A-F]{64}$' -or
            (@($package.dependencyTypes) -notcontains "direct" -and
                @($package.dependencyTypes) -notcontains "transitive") -or
            @($package.usedByRoles).Count -lt 1 -or
            @($package.roleDependencies).Count -lt 1 -or
            ([string]::IsNullOrWhiteSpace(
                    [string]$package.licenseExpression) -and
                [string]$package.licenseUrl -notmatch '^https://') -or
            @(
                $package.provenanceUrls |
                    Where-Object { [string]$_ -match '^https://' }
            ).Count -lt 1) {
            throw "A locked NuGet SBOM package lacks license/provenance identity."
        }
        if ([string]$package.name -like "Microsoft.Extensions.*" -and
            [string]$package.resolvedVersion -match
                '^10\.0\.(\d+)(?:[-+].*)?$' -and
            [int]$Matches[1] -lt 10) {
            throw "Microsoft.Extensions.* 10.0.x is below 10.0.10."
        }
    }
    $dotnetPath = [string]$report.toolchain.dotnetPath
    [void](Assert-ComoteOrdinaryLocalPath `
        -LiteralPath $dotnetPath `
        -Directory $false `
        -Description "Regression-pinned dotnet host")
    if ((Get-ComoteSha256 -LiteralPath $dotnetPath) -cne
        [string]$report.toolchain.dotnetSha256) {
        throw "The regression-pinned dotnet host changed."
    }
    return [PSCustomObject][ordered]@{
        Report = $report
        Sbom = $sbom
        MediaPath = $mediaPath
        DotNetPath = $dotnetPath
    }
}

function Assert-ComoteFfmpegReleaseInputs {
    param(
        [Parameter(Mandatory)]
        [string]$RepositoryRoot
    )

    $ffmpegRoot = Join-Path $RepositoryRoot "ffmpeg"
    $noticeSource = Join-Path `
        $PSScriptRoot `
        "THIRD_PARTY_NOTICES"
    $manifestPath = Join-Path $ffmpegRoot "manifest.json"
    $receiptPath = Join-Path $noticeSource "FFMPEG_ASSET_RECEIPT.json"
    $manifest = Read-ComoteJson `
        -LiteralPath $manifestPath `
        -Description "Canonical FFmpeg manifest"
    $receipt = Read-ComoteJson `
        -LiteralPath $receiptPath `
        -Description "External FFmpeg asset receipt"
    Assert-ComoteExactProperties `
        -InputObject $manifest `
        -Expected @(
            "schemaVersion",
            "build",
            "license",
            "architecture",
            "redistributable",
            "gplEnabled",
            "softwareH264Fallback",
            "encoderOptions",
            "archive",
            "publisherChecksums",
            "source",
            "managedComponents",
            "abiMajors",
            "files"
        ) `
        -Description "Canonical FFmpeg manifest"
    Assert-ComoteExactProperties `
        -InputObject $receipt `
        -Expected @(
            "schemaVersion",
            "component",
            "build",
            "license",
            "architecture",
            "redistributable",
            "gplEnabled",
            "softwareH264Fallback",
            "encoderOptions",
            "archive",
            "publisherChecksums",
            "source",
            "managedComponents",
            "abiMajors",
            "files"
        ) `
        -Description "External FFmpeg asset receipt"
    if ([int]$receipt.schemaVersion -ne 2 -or
        [string]$receipt.component -cne "FFmpeg" -or
        [string]$receipt.build -cne
            "n8.1.2-32-gcfa62de001-20260730" -or
        [string]$receipt.license -cne "LGPL-3.0-or-later" -or
        [string]$receipt.architecture -cne "x86_64" -or
        [bool]$receipt.redistributable -ne $true -or
        [bool]$receipt.gplEnabled -ne $false -or
        [string]$receipt.softwareH264Fallback -cne "h264_mf") {
        throw "The FFmpeg receipt identity is not the frozen release."
    }
    $receiptCanonical = [PSCustomObject][ordered]@{
        schemaVersion = $receipt.schemaVersion
        build = $receipt.build
        license = $receipt.license
        architecture = $receipt.architecture
        redistributable = $receipt.redistributable
        gplEnabled = $receipt.gplEnabled
        softwareH264Fallback = $receipt.softwareH264Fallback
        encoderOptions = $receipt.encoderOptions
        archive = $receipt.archive
        publisherChecksums = $receipt.publisherChecksums
        source = $receipt.source
        managedComponents = $receipt.managedComponents
        abiMajors = $receipt.abiMajors
        files = $receipt.files
    }
    if (($receiptCanonical | ConvertTo-Json -Depth 20 -Compress) -cne
        ($manifest | ConvertTo-Json -Depth 20 -Compress)) {
        throw "The external FFmpeg receipt differs from the canonical manifest."
    }
    $expectedDlls = @(
        "avcodec-62.dll",
        "avdevice-62.dll",
        "avfilter-11.dll",
        "avformat-62.dll",
        "avutil-60.dll",
        "swresample-6.dll",
        "swscale-9.dll"
    )
    $files = @($receipt.files)
    if ($files.Count -ne $expectedDlls.Count) {
        throw "The FFmpeg receipt must bind exactly seven shared libraries."
    }
    for ($index = 0; $index -lt $expectedDlls.Count; $index++) {
        $entry = $files[$index]
        Assert-ComoteExactProperties `
            -InputObject $entry `
            -Expected @("name", "length", "sha256") `
            -Description "FFmpeg receipt file"
        if ([string]$entry.name -cne $expectedDlls[$index] -or
            [int64]$entry.length -le 0 -or
            [string]$entry.sha256 -cnotmatch '^[0-9A-F]{64}$') {
            throw "The FFmpeg receipt file identity/order is invalid."
        }
        $filePath = Join-Path $ffmpegRoot ([string]$entry.name)
        $identity = Assert-ComoteOrdinaryLocalPath `
            -LiteralPath $filePath `
            -Directory $false `
            -Description "Frozen FFmpeg shared library"
        if ([int64]$identity.Identity.Length -ne [int64]$entry.length -or
            (Get-ComoteSha256 -LiteralPath $filePath) -cne
                [string]$entry.sha256) {
            throw "A frozen FFmpeg shared library differs from its receipt."
        }
    }
    $noticeNames = @(
        "LICENSE.LGPLv3.txt",
        "LICENSE.SIPSorceryMedia.FFmpeg.LGPL-2.1.txt",
        "LICENSE.FFmpeg.AutoGen.MIT.txt",
        "NOTICE.md",
        "SOURCE_OFFER.md",
        "manifest.json"
    )
    foreach ($noticeName in $noticeNames) {
        [void](Assert-ComoteOrdinaryLocalPath `
            -LiteralPath (Join-Path $ffmpegRoot $noticeName) `
            -Directory $false `
            -Description "FFmpeg six-file notice topology")
    }
    foreach ($noticeName in @(
        "DOTNET_DIRECT_DEPENDENCIES.md",
        "FFMPEG.md",
        "FFMPEG_ASSET_RECEIPT.json"
    )) {
        [void](Assert-ComoteOrdinaryLocalPath `
            -LiteralPath (Join-Path $noticeSource $noticeName) `
            -Directory $false `
            -Description "Release third-party notice")
    }
    return [PSCustomObject][ordered]@{
        FfmpegRoot = $ffmpegRoot
        NoticeSource = $noticeSource
        ManifestPath = $manifestPath
        ReceiptPath = $receiptPath
        ReceiptSha256 = Get-ComoteSha256 -LiteralPath $receiptPath
        NoticeNames = $noticeNames
    }
}

function Invoke-ComoteAuthenticodeSign {
    param(
        [Parameter(Mandatory)]
        [string]$LiteralPath,

        [Parameter(Mandatory)]
        [string]$SignTool,

        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-F]{40}$')]
        [string]$Thumbprint
    )

    $signatureBefore = Get-AuthenticodeSignature -LiteralPath $LiteralPath
    if ($signatureBefore.Status -ne
        [Management.Automation.SignatureStatus]::NotSigned) {
        throw "A user-mode release input was unexpectedly pre-signed."
    }
    $result = Invoke-ComoteNativeProcess `
        -FilePath $SignTool `
        -Arguments @(
            "sign",
            "/v",
            "/fd",
            "SHA256",
            "/sha1",
            $Thumbprint,
            "/sm",
            $LiteralPath
        )
    if ($result.ExitCode -ne 0) {
        throw "Authenticode signing failed: $LiteralPath"
    }
    Assert-ComotePinnedAuthenticodeSigner `
        -LiteralPath $LiteralPath `
        -Thumbprint $Thumbprint `
        -Description "Signed release binary" `
        -RequireTrusted
}

function Write-ComoteAsciiFile {
    param(
        [Parameter(Mandatory)]
        [string]$LiteralPath,

        [Parameter(Mandatory)]
        [string]$Content
    )

    foreach ($character in $Content.ToCharArray()) {
        if ([int]$character -gt 127) {
            throw "ASCII release content contains a non-ASCII character."
        }
    }
    [IO.File]::WriteAllText(
        $LiteralPath,
        $Content,
        (New-Object Text.ASCIIEncoding)
    )
}

function Get-ComoteReleaseFileEntries {
    param(
        [Parameter(Mandatory)]
        [string]$StageRoot
    )

    return @(
        Get-ChildItem `
            -LiteralPath $StageRoot `
            -File `
            -Force `
            -Recurse |
            Where-Object {
                $_.Name -cne $script:ComotePreviewManifestName
            } |
            ForEach-Object {
                [void](Assert-ComoteOrdinaryLocalPath `
                    -LiteralPath $_.FullName `
                    -Directory $false `
                    -Description "Staged release file")
                [PSCustomObject][ordered]@{
                    path = Get-ComoteRelativePath `
                        -Root $StageRoot `
                        -Child $_.FullName
                    length = [int64]$_.Length
                    sha256 = Get-ComoteSha256 -LiteralPath $_.FullName
                }
            } |
            Sort-Object path
    )
}

$expectedReceiptHash = $ExpectedSigningReceiptSha256.ToUpperInvariant()
$expectedDriverManifestHash =
    $ExpectedDriverManifestSha256.ToUpperInvariant()
$expectedInstallerHash = $ExpectedPinnedInstallerSha256.ToUpperInvariant()
$expectedRegressionHash =
    $ExpectedRegressionGateSha256.ToUpperInvariant()
$expectedSbomHash = $ExpectedNuGetSbomSha256.ToUpperInvariant()
$expectedSourceHash = $ExpectedSourceInventorySha256.ToUpperInvariant()
$expectedThumbprint =
    $ExpectedCodeSigningCertificateThumbprint.ToUpperInvariant()

$environment = Assert-ComoteDisposableVmEnvironment `
    -AcknowledgeDisposableVm:$AcknowledgeDisposableVm `
    -RequireTestSigning
$operationLock = Enter-ComotePreviewLock
$signingCertificate = $null
try {
    $signedPackageIdentity = Assert-ComoteOrdinaryLocalPath `
        -LiteralPath $SignedDriverPackageDirectory `
        -Directory $true `
        -Description "Prepared signed driver package"
    $signingReceipt = Assert-ComoteSigningReceipt `
        -LiteralPath $Phase2SigningReceiptPath `
        -ExpectedSha256 $expectedReceiptHash `
        -SignedPackagePath $signedPackageIdentity.FullPath `
        -ExpectedThumbprint $expectedThumbprint

    $driverManifest = Read-ComotePhase2DriverManifest `
        -LiteralPath $DriverManifestPath
    if ($driverManifest.Sha256 -cne $expectedDriverManifestHash) {
        throw "The final native driver manifest SHA-256 is not pinned."
    }
    foreach ($binding in @(
        @("ComoteVirtualHidPhase2.inf", "InfSize", "InfSha256"),
        @("ComoteVirtualHidPhase2.cat", "CatSize", "CatSha256"),
        @("ComoteVirtualHidPhase2.sys", "SysSize", "SysSha256")
    )) {
        $path = Join-Path $signedPackageIdentity.FullPath $binding[0]
        $identity = Assert-ComoteOrdinaryLocalPath `
            -LiteralPath $path `
            -Directory $false `
            -Description "Final signed driver file"
        if ([uint64]$identity.Identity.Length -ne
                [uint64]$driverManifest.Values[$binding[1]] -or
            (Get-ComoteSha256 -LiteralPath $path) -cne
                [string]$driverManifest.Values[$binding[2]]) {
            throw "The final native manifest does not bind: $($binding[0])"
        }
    }

    $installerIdentity = Assert-ComoteOrdinaryLocalPath `
        -LiteralPath $PinnedNativeInstallerPath `
        -Directory $false `
        -Description "Pinned native installer"
    if ((Get-ComoteSha256 -LiteralPath $installerIdentity.FullPath) -cne
        $expectedInstallerHash) {
        throw "The native installer input SHA-256 is not pinned."
    }
    $installerAscii = [Text.Encoding]::ASCII.GetString(
        [IO.File]::ReadAllBytes($installerIdentity.FullPath)
    )
    if ($installerAscii.IndexOf(
            $driverManifest.Sha256,
            [StringComparison]::Ordinal
        ) -lt 0 -or
        $installerAscii.IndexOf(
            "UNPINNED-INSTALL-MUST-REJECT",
            [StringComparison]::Ordinal
        ) -ge 0) {
        throw "The native installer does not expose the exact compiled manifest pin."
    }
    if ((Get-AuthenticodeSignature `
            -LiteralPath $installerIdentity.FullPath).Status -ne
        [Management.Automation.SignatureStatus]::NotSigned) {
        throw "The pinned native installer input must be unsigned before release signing."
    }

    $certificatePath = Join-Path `
        $signedPackageIdentity.FullPath `
        "ComotePhase2Test.cer"
    $publicCertificate = New-Object `
        Security.Cryptography.X509Certificates.X509Certificate2(
            $certificatePath
        )
    try {
        if ($publicCertificate.Thumbprint.ToUpperInvariant() -cne
                $expectedThumbprint -or
            [string]$publicCertificate.Subject -cne
                [string]$signingReceipt.certificateSubject -or
            $publicCertificate.HasPrivateKey -or
            -not (Test-ComoteCodeSigningEku `
                -Certificate $publicCertificate)) {
            throw "The prepared public signing certificate is not pinned."
        }
    }
    finally {
        $publicCertificate.Dispose()
    }

    $localCertificatePath =
        "Cert:\LocalMachine\My\$expectedThumbprint"
    if (-not (Test-Path -LiteralPath $localCertificatePath)) {
        throw "The pinned VM signing private key is not available."
    }
    $signingCertificate = Get-Item `
        -LiteralPath $localCertificatePath `
        -ErrorAction Stop
    if (-not $signingCertificate.HasPrivateKey -or
        -not (Test-ComoteCodeSigningEku `
            -Certificate $signingCertificate)) {
        throw "The pinned VM signing certificate cannot sign code."
    }
    [void](Assert-ComoteOrdinaryLocalPath `
        -LiteralPath $SignToolPath `
        -Directory $false `
        -Description "SignTool")

    $outputFullPath = [IO.Path]::GetFullPath($OutputDirectory)
    if (Test-Path -LiteralPath $outputFullPath) {
        throw "OutputDirectory must not already exist."
    }
    $outputParent = [IO.Path]::GetDirectoryName($outputFullPath)
    [void](Assert-ComoteOrdinaryLocalPath `
        -LiteralPath $outputParent `
        -Directory $true `
        -Description "Release output parent")

    $repositoryRoot = Split-Path -Parent (
        Split-Path -Parent $PSScriptRoot
    )
    $regressionInputs = Assert-ComoteRegressionInputs `
        -RepositoryRoot $repositoryRoot `
        -ReportPath $RegressionGatePath `
        -ReportSha256 $expectedRegressionHash `
        -SbomPath $NuGetSbomPath `
        -SbomSha256 $expectedSbomHash `
        -SourceInventorySha256 $expectedSourceHash
    $ffmpegInputs = Assert-ComoteFfmpegReleaseInputs `
        -RepositoryRoot $repositoryRoot
    $hostProject = Join-Path $repositoryRoot "Host\Host.csproj"
    $viewerProject = Join-Path $repositoryRoot "Viewer\Viewer.csproj"
    $brokerProject = Join-Path `
        $repositoryRoot `
        "InputBroker\Comote.InputBroker.csproj"
    $mediaGateProject = Join-Path `
        $repositoryRoot `
        "Distribution\VirtualHidPreview\MediaGate\Comote.MediaGate.csproj"
    foreach ($projectPath in @(
        $hostProject,
        $viewerProject,
        $brokerProject,
        $mediaGateProject
    )) {
        [void](Assert-ComoteOrdinaryLocalPath `
            -LiteralPath $projectPath `
            -Directory $false `
            -Description "Application project")
    }

    [IO.Directory]::CreateDirectory($outputFullPath) | Out-Null
    $stageName = "Comote-{0}-win-x64" -f $ReleaseId
    $stageRoot = Join-Path $outputFullPath $stageName
    $clientRoot = Join-Path $stageRoot "App\Client"
    $managerRoot = Join-Path $stageRoot "App\Manager"
    $brokerRoot = Join-Path $stageRoot "App\Broker"
    $driverRoot = Join-Path $stageRoot "Driver"
    $driverPackageRoot = Join-Path $driverRoot "Package"
    $trustRoot = Join-Path $stageRoot "Trust"
    $validationRoot = Join-Path $stageRoot "Validation"
    $validationLocksRoot = Join-Path $validationRoot "NuGetLocks"
    $noticesRoot = Join-Path $stageRoot "THIRD_PARTY_NOTICES"
    foreach ($directory in @(
        $stageRoot,
        $clientRoot,
        $managerRoot,
        $brokerRoot,
        $driverRoot,
        $driverPackageRoot,
        $trustRoot,
        $validationRoot,
        $validationLocksRoot,
        $noticesRoot
    )) {
        [IO.Directory]::CreateDirectory($directory) | Out-Null
    }

    Invoke-ComoteDotNetPublish `
        -ProjectPath $hostProject `
        -OutputPath $clientRoot `
        -DotNetPath ([string]$regressionInputs.DotNetPath)
    Invoke-ComoteDotNetPublish `
        -ProjectPath $viewerProject `
        -OutputPath $managerRoot `
        -DotNetPath ([string]$regressionInputs.DotNetPath)
    Invoke-ComoteDotNetPublish `
        -ProjectPath $brokerProject `
        -OutputPath $brokerRoot `
        -DotNetPath ([string]$regressionInputs.DotNetPath)
    Invoke-ComoteDotNetPublish `
        -ProjectPath $mediaGateProject `
        -OutputPath $validationRoot `
        -DotNetPath ([string]$regressionInputs.DotNetPath)

    [void](Assert-ComoteRegressionInputs `
        -RepositoryRoot $repositoryRoot `
        -ReportPath $RegressionGatePath `
        -ReportSha256 $expectedRegressionHash `
        -SbomPath $NuGetSbomPath `
        -SbomSha256 $expectedSbomHash `
        -SourceInventorySha256 $expectedSourceHash)

    $publishedHost = Join-Path $clientRoot "Host.exe"
    $publishedViewer = Join-Path $managerRoot "Viewer.exe"
    $publishedBroker = Join-Path $brokerRoot "Comote.InputBroker.exe"
    $publishedMediaGate = Join-Path `
        $validationRoot `
        "Comote.MediaGate.exe"
    foreach ($publishedFile in @(
        $publishedHost,
        $publishedViewer,
        $publishedBroker,
        $publishedMediaGate
    )) {
        [void](Assert-ComoteOrdinaryLocalPath `
            -LiteralPath $publishedFile `
            -Directory $false `
            -Description "Published application")
    }
    [IO.File]::Move(
        $publishedHost,
        (Join-Path $clientRoot "ComoteClient.exe")
    )
    [IO.File]::Move(
        $publishedViewer,
        (Join-Path $managerRoot "ComoteManager.exe")
    )

    Write-ComoteAsciiFile `
        -LiteralPath (Join-Path `
            $clientRoot `
            "Start Comote Client Virtual HID.cmd") `
        -Content (
            "@echo off`r`n" +
            '"%~dp0ComoteClient.exe" --manager-hub --virtual-hid' +
            "`r`n"
        )
    Write-ComoteAsciiFile `
        -LiteralPath (Join-Path `
            $managerRoot `
            "Start Comote Manager Hub.cmd") `
        -Content (
            "@echo off`r`n" +
            '"%~dp0ComoteManager.exe" --manager-hub' +
            "`r`n"
        )
    Write-ComoteAsciiFile `
        -LiteralPath (Join-Path `
            $stageRoot `
            "START HERE - Manager Hub.cmd") `
        -Content (
            "@echo off`r`n" +
            'if not exist "%ProgramFiles%\Comote\VirtualHidPreview\' +
            'App\Manager\ComoteManager.exe" (' +
            "`r`n" +
            "  echo Comote Virtual HID preview is not installed. 1>&2`r`n" +
            "  exit /b 1`r`n" +
            ")`r`n" +
            '"%ProgramFiles%\Comote\VirtualHidPreview\App\Manager\' +
            'ComoteManager.exe" --manager-hub' +
            "`r`n"
        )
    Write-ComoteAsciiFile `
        -LiteralPath (Join-Path `
            $stageRoot `
            "START HERE - Client Virtual HID.cmd") `
        -Content (
            "@echo off`r`n" +
            'if not exist "%ProgramFiles%\Comote\VirtualHidPreview\' +
            'App\Client\ComoteClient.exe" (' +
            "`r`n" +
            "  echo Comote Virtual HID preview is not installed. 1>&2`r`n" +
            "  exit /b 1`r`n" +
            ")`r`n" +
            '"%ProgramFiles%\Comote\VirtualHidPreview\App\Client\' +
            'ComoteClient.exe" --manager-hub --virtual-hid' +
            "`r`n"
        )

    foreach ($driverName in @(
        "ComoteVirtualHidPhase2.inf",
        "ComoteVirtualHidPhase2.cat",
        "ComoteVirtualHidPhase2.sys"
    )) {
        [IO.File]::Copy(
            (Join-Path $signedPackageIdentity.FullPath $driverName),
            (Join-Path $driverPackageRoot $driverName),
            $false
        )
    }
    [IO.File]::Copy(
        $driverManifest.Path,
        (Join-Path $driverRoot "package-manifest.txt"),
        $false
    )
    $stagedInstaller = Join-Path $driverRoot "ComoteDriverInstaller.exe"
    [IO.File]::Copy(
        $installerIdentity.FullPath,
        $stagedInstaller,
        $false
    )
    [IO.File]::Copy(
        $certificatePath,
        (Join-Path $trustRoot "ComotePhase2Test.cer"),
        $false
    )
    foreach ($sourceName in @(
        "Install-ComoteVirtualHidPreview.ps1",
        "Uninstall-ComoteVirtualHidPreview.ps1",
        "VirtualHidPreview.Common.ps1",
        "Invoke-ComoteClientRoleUac.ps1",
        "Verify-ComoteManagerRole.ps1",
        "README.md"
    )) {
        [IO.File]::Copy(
            (Join-Path $PSScriptRoot $sourceName),
            (Join-Path $stageRoot $sourceName),
            $false
        )
    }
    [IO.File]::Copy(
        [IO.Path]::GetFullPath($RegressionGatePath),
        (Join-Path $validationRoot "REGRESSION_GATE.json"),
        $false
    )
    [IO.File]::Copy(
        [string]$regressionInputs.MediaPath,
        (Join-Path $validationRoot "MEDIA_GATE.json"),
        $false
    )
    [IO.File]::Copy(
        [IO.Path]::GetFullPath($NuGetSbomPath),
        (Join-Path $noticesRoot "NUGET_SBOM.json"),
        $false
    )
    foreach ($noticeName in @(
        "DOTNET_DIRECT_DEPENDENCIES.md",
        "FFMPEG.md",
        "FFMPEG_ASSET_RECEIPT.json"
    )) {
        [IO.File]::Copy(
            (Join-Path ([string]$ffmpegInputs.NoticeSource) $noticeName),
            (Join-Path $noticesRoot $noticeName),
            $false
        )
    }
    $stagedFfmpegNoticeRoot = Join-Path $noticesRoot "FFmpeg"
    [IO.Directory]::CreateDirectory($stagedFfmpegNoticeRoot) | Out-Null
    foreach ($noticeName in @($ffmpegInputs.NoticeNames)) {
        [IO.File]::Copy(
            (Join-Path ([string]$ffmpegInputs.FfmpegRoot) $noticeName),
            (Join-Path $stagedFfmpegNoticeRoot $noticeName),
            $false
        )
    }
    foreach ($lockEntry in @($regressionInputs.Report.lockFiles)) {
        $relative = [string]$lockEntry.path
        Assert-ComoteSafeRelativePath -RelativePath $relative
        $sourceLock = Join-Path $repositoryRoot $relative.Replace('/', '\')
        $destinationLock = Join-Path `
            $validationLocksRoot `
            $relative.Replace('/', '\')
        [IO.Directory]::CreateDirectory(
            [IO.Path]::GetDirectoryName($destinationLock)
        ) | Out-Null
        [IO.File]::Copy($sourceLock, $destinationLock, $false)
        if ((Get-ComoteSha256 -LiteralPath $destinationLock) -cne
            [string]$lockEntry.sha256) {
            throw "A staged packages.lock.json failed hash verification."
        }
    }

    $clientExecutable = Join-Path $clientRoot "ComoteClient.exe"
    $managerExecutable = Join-Path $managerRoot "ComoteManager.exe"
    foreach ($binary in @(
        $clientExecutable,
        $managerExecutable,
        $publishedBroker,
        $publishedMediaGate,
        $stagedInstaller
    )) {
        Invoke-ComoteAuthenticodeSign `
            -LiteralPath $binary `
            -SignTool $SignToolPath `
            -Thumbprint $expectedThumbprint
    }

    $originalFilenames = [ordered]@{
        client = "Host.dll"
        manager = "Viewer.dll"
        broker = "Comote.InputBroker.dll"
        mediaGate = "Comote.MediaGate.dll"
    }
    foreach ($identity in @(
        @("client", $clientExecutable),
        @("manager", $managerExecutable),
        @("broker", $publishedBroker),
        @("mediaGate", $publishedMediaGate)
    )) {
        $versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo(
            $identity[1]
        )
        if ([string]$versionInfo.OriginalFilename -cne
            [string]$originalFilenames[$identity[0]]) {
            throw "Published application role metadata is invalid: $($identity[0])"
        }
    }

    $fileEntries = @(Get-ComoteReleaseFileEntries -StageRoot $stageRoot)
    $entryMap = @{}
    foreach ($entry in $fileEntries) {
        $entryMap[[string]$entry.path] = $entry
    }
    $certificateOutputPath = Join-Path `
        $trustRoot `
        "ComotePhase2Test.cer"
    $releaseDocument = [PSCustomObject][ordered]@{
        schemaVersion = 1
        releaseId = $ReleaseId
        packageRole = "validation-unified"
        target = [PSCustomObject][ordered]@{
            hypervisor = "VMware"
            operatingSystem = "Windows 10 22H2"
            productType = 1
            editionSkus = @(98, 99, 100, 101)
            architecture = "x64"
            buildNumber = "19045"
            anyUbr = $true
        }
        runtimePolicy = [PSCustomObject][ordered]@{
            frameworkVersion =
                [string]$regressionInputs.Report.runtimePolicy.frameworkVersion
            runtimePackVersion =
                [string]$regressionInputs.Report.runtimePolicy.runtimePackVersion
            coreclrLength =
                [int64]$regressionInputs.Report.runtimePolicy.coreclrLength
            coreclrSha256 =
                [string]$regressionInputs.Report.runtimePolicy.coreclrSha256
            coreclrFileVersion =
                [string]$regressionInputs.Report.runtimePolicy.coreclrFileVersion
            coreclrProductVersion =
                [string]$regressionInputs.Report.runtimePolicy.coreclrProductVersion
        }
        driver = [PSCustomObject][ordered]@{
            packageDirectory = "Driver/Package"
            manifestPath = "Driver/package-manifest.txt"
            manifestSha256 = Get-ComoteSha256 `
                -LiteralPath (Join-Path `
                    $driverRoot `
                    "package-manifest.txt")
            installerPath = "Driver/ComoteDriverInstaller.exe"
            installerSha256 = Get-ComoteSha256 `
                -LiteralPath $stagedInstaller
            certificatePath = "Trust/ComotePhase2Test.cer"
            certificateSha256 = Get-ComoteSha256 `
                -LiteralPath $certificateOutputPath
            certificateThumbprint = $expectedThumbprint
            certificateSubject = [string]$signingReceipt.certificateSubject
        }
        cmt1Applications = @(
            [PSCustomObject][ordered]@{
                role = "client"
                path = "App/Client/ComoteClient.exe"
                originalFilename = "Host.dll"
                sha256 = [string]$entryMap[
                    "App/Client/ComoteClient.exe"
                ].sha256
                signerThumbprint = $expectedThumbprint
            },
            [PSCustomObject][ordered]@{
                role = "manager"
                path = "App/Manager/ComoteManager.exe"
                originalFilename = "Viewer.dll"
                sha256 = [string]$entryMap[
                    "App/Manager/ComoteManager.exe"
                ].sha256
                signerThumbprint = $expectedThumbprint
            },
            [PSCustomObject][ordered]@{
                role = "broker"
                path = "App/Broker/Comote.InputBroker.exe"
                originalFilename = "Comote.InputBroker.dll"
                sha256 = [string]$entryMap[
                    "App/Broker/Comote.InputBroker.exe"
                ].sha256
                signerThumbprint = $expectedThumbprint
            }
        )
        validationTools = @(
            [PSCustomObject][ordered]@{
                role = "media-gate"
                path = "Validation/Comote.MediaGate.exe"
                originalFilename = "Comote.MediaGate.dll"
                sha256 = [string]$entryMap[
                    "Validation/Comote.MediaGate.exe"
                ].sha256
                signerThumbprint = $expectedThumbprint
            }
        )
        files = $fileEntries
    }
    $manifestOutputPath = Join-Path `
        $stageRoot `
        $script:ComotePreviewManifestName
    Write-ComoteJsonAtomically `
        -LiteralPath $manifestOutputPath `
        -InputObject $releaseDocument `
        -Depth 16
    $manifestOutputHash = Get-ComoteSha256 `
        -LiteralPath $manifestOutputPath

    $release = Get-ComoteReleaseManifest `
        -PackageRoot $stageRoot `
        -ExpectedManifestSha256 $manifestOutputHash
    Assert-ComoteReleaseInventory -Release $release
    [void](Assert-ComoteDriverPackageBinding -Release $release)
    $releaseCertificate = Get-ComotePinnedCertificate -Release $release
    try {
        Assert-ComoteReleaseSigners -Release $release -RequireTrusted
    }
    finally {
        $releaseCertificate.Certificate.Dispose()
    }

    $zipPath = Join-Path $outputFullPath "$stageName.zip"
    Compress-Archive `
        -Path (Join-Path $stageRoot "*") `
        -DestinationPath $zipPath `
        -CompressionLevel Optimal
    if ($LASTEXITCODE -notin @($null, 0)) {
        throw "Release ZIP creation failed."
    }
    $zipHash = Get-ComoteSha256 -LiteralPath $zipPath
    Write-ComoteAsciiFile `
        -LiteralPath "$zipPath.sha256" `
        -Content ("{0} *{1}`r`n" -f
            $zipHash,
            [IO.Path]::GetFileName($zipPath))
    Write-ComoteAsciiFile `
        -LiteralPath (
            Join-Path `
                $outputFullPath `
                "$stageName.release-manifest.sha256"
        ) `
        -Content ("{0} *{1}`r`n" -f
            $manifestOutputHash,
            $script:ComotePreviewManifestName)

    $report = [PSCustomObject][ordered]@{
        schemaVersion = 1
        releaseId = $ReleaseId
        completedUtc = [DateTime]::UtcNow.ToString("o")
        vmManufacturer = [string]$environment.Computer.Manufacturer
        vmModel = [string]$environment.Computer.Model
        osBuild = [string]$environment.OperatingSystem.BuildNumber
        osUbr = [int]$environment.Ubr
        sourceInventorySha256 = $expectedSourceHash
        regressionGateSha256 = $expectedRegressionHash
        nugetSbomSha256 = $expectedSbomHash
        mediaGateSha256 =
            [string]$regressionInputs.Report.mediaGate.evidenceSha256
        ffmpegAssetReceiptSha256 = [string]$ffmpegInputs.ReceiptSha256
        dotnetSdkVersion = "10.0.302"
        selfContainedRuntimeVersion = "10.0.10"
        validationMediaGateSha256 = Get-ComoteSha256 `
            -LiteralPath $publishedMediaGate
        signingReceiptSha256 = $expectedReceiptHash
        driverManifestSha256 = $expectedDriverManifestHash
        pinnedInstallerInputSha256 = $expectedInstallerHash
        releaseManifestSha256 = $manifestOutputHash
        zipFile = [IO.Path]::GetFileName($zipPath)
        zipSha256 = $zipHash
        certificateThumbprint = $expectedThumbprint
        note = "No driver was installed, loaded, or tested by this build step."
    }
    Write-ComoteJsonAtomically `
        -LiteralPath (Join-Path $outputFullPath "$stageName.build-report.json") `
        -InputObject $report `
        -Depth 8

    Write-Host ""
    Write-Host "VMware validation-only unified bundle packaged." `
        -ForegroundColor Green
    Write-Host "Package: $stageRoot"
    Write-Host "ZIP: $zipPath"
    Write-Host "ZIP SHA-256: $zipHash"
    Write-Host "Release manifest SHA-256: $manifestOutputHash"
    Write-Host "No driver was installed, loaded, or tested."
}
finally {
    if ($null -ne $signingCertificate) {
        $signingCertificate.Dispose()
    }
    Exit-ComotePreviewLock -Mutex $operationLock
}
