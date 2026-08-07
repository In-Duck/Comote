#Requires -Version 5.1
[CmdletBinding(DefaultParameterSetName = "Candidate")]
param(
    [Parameter(Mandatory, ParameterSetName = "Candidate")]
    [switch]$CreateCandidate,

    [Parameter(Mandatory, ParameterSetName = "Candidate")]
    [switch]$AcknowledgeDisposableVm,

    [Parameter(Mandatory, ParameterSetName = "Candidate")]
    [ValidateNotNullOrEmpty()]
    [string]$UnifiedPackageRoot,

    [Parameter(Mandatory, ParameterSetName = "Candidate")]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedUnifiedManifestSha256,

    [Parameter(Mandatory, ParameterSetName = "Candidate")]
    [ValidateNotNullOrEmpty()]
    [string]$UnifiedProvisionalReportPath,

    [Parameter(Mandatory, ParameterSetName = "Candidate")]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedUnifiedProvisionalReportSha256,

    [Parameter(Mandatory, ParameterSetName = "Candidate")]
    [ValidateNotNullOrEmpty()]
    [string]$OutputDirectory,

    [Parameter(Mandatory, ParameterSetName = "Promotion")]
    [switch]$PromoteCandidate,

    [Parameter(Mandatory, ParameterSetName = "Promotion")]
    [Parameter(Mandatory, ParameterSetName = "Verify")]
    [ValidateNotNullOrEmpty()]
    [string]$CandidateDirectory,

    [Parameter(Mandatory, ParameterSetName = "Promotion")]
    [Parameter(Mandatory, ParameterSetName = "Verify")]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedCandidateIndexSha256,

    [Parameter(Mandatory, ParameterSetName = "Verify")]
    [switch]$VerifyCandidate,

    [Parameter(Mandatory, ParameterSetName = "Verify")]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedCandidateSourceManifestSha256,

    [Parameter(Mandatory, ParameterSetName = "Verify")]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedCandidateProvisionalReportSha256,

    [Parameter(Mandatory, ParameterSetName = "Promotion")]
    [ValidateNotNullOrEmpty()]
    [string]$FinalReportPath,

    [Parameter(Mandatory, ParameterSetName = "Promotion")]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedFinalReportSha256
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot "VirtualHidPreview.Common.ps1")

function Copy-ComoteRoleFile {
    param(
        [Parameter(Mandatory)]
        [string]$SourceRoot,

        [Parameter(Mandatory)]
        [string]$DestinationRoot,

        [Parameter(Mandatory)]
        [string]$RelativePath,

        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-F]{64}$')]
        [string]$ExpectedSha256,

        [Parameter(Mandatory)]
        [ValidateRange(1, [long]::MaxValue)]
        [long]$ExpectedLength
    )

    Assert-ComoteSafeRelativePath -RelativePath $RelativePath
    $source = Join-Path $SourceRoot $RelativePath.Replace('/', '\')
    $sourceIdentity = Assert-ComoteOrdinaryLocalPath `
        -LiteralPath $source `
        -Directory $false `
        -Description "Unified role source"
    if ([int64]$sourceIdentity.Identity.Length -ne $ExpectedLength -or
        (Get-ComoteSha256 -LiteralPath $source) -cne $ExpectedSha256) {
        throw "A unified role source differs from its authenticated manifest."
    }
    $destination = Join-Path `
        $DestinationRoot `
        $RelativePath.Replace('/', '\')
    [IO.Directory]::CreateDirectory(
        [IO.Path]::GetDirectoryName($destination)
    ) | Out-Null
    [IO.File]::Copy($source, $destination, $false)
    if ((Get-ComoteSha256 -LiteralPath $destination) -cne
        $ExpectedSha256) {
        throw "A role-package copy failed hash verification."
    }
}

function Copy-ComoteRoleTemplate {
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [string]$DestinationRoot
    )

    $source = Assert-ComoteOrdinaryLocalPath `
        -LiteralPath (Join-Path $PSScriptRoot $Name) `
        -Directory $false `
        -Description "Role helper template"
    $destination = Join-Path $DestinationRoot $Name
    [IO.File]::Copy($source.FullPath, $destination, $false)
    if ((Get-ComoteSha256 -LiteralPath $source.FullPath) -cne
        (Get-ComoteSha256 -LiteralPath $destination)) {
        throw "A role helper template copy failed verification."
    }
}

function Write-ComoteRoleAsciiFile {
    param(
        [Parameter(Mandatory)]
        [string]$LiteralPath,

        [Parameter(Mandatory)]
        [string]$Content
    )

    foreach ($character in $Content.ToCharArray()) {
        if ([int]$character -gt 127) {
            throw "Role helper content must remain ASCII."
        }
    }
    [IO.File]::WriteAllText(
        $LiteralPath,
        $Content,
        (New-Object Text.ASCIIEncoding)
    )
}

function Write-ComoteExternalHash {
    param(
        [Parameter(Mandatory)]
        [string]$LiteralPath
    )

    $hash = Get-ComoteSha256 -LiteralPath $LiteralPath
    Write-ComoteRoleAsciiFile `
        -LiteralPath "$LiteralPath.sha256" `
        -Content ("{0} *{1}`r`n" -f
            $hash,
            [IO.Path]::GetFileName($LiteralPath))
    return $hash
}

function Assert-ComoteExternalHash {
    param(
        [Parameter(Mandatory)]
        [string]$LiteralPath,

        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-F]{64}$')]
        [string]$ExpectedSha256,

        [Parameter(Mandatory)]
        [string]$Description
    )

    [void](Assert-ComoteOrdinaryLocalPath `
        -LiteralPath $LiteralPath `
        -Directory $false `
        -Description $Description)
    if ((Get-ComoteSha256 -LiteralPath $LiteralPath) -cne $ExpectedSha256) {
        throw "$Description differs from its pinned SHA-256."
    }
    $sidecar = "$LiteralPath.sha256"
    [void](Assert-ComoteOrdinaryLocalPath `
        -LiteralPath $sidecar `
        -Directory $false `
        -Description "$Description checksum")
    if ([IO.File]::ReadAllText($sidecar) -cne
        ("{0} *{1}`r`n" -f
            $ExpectedSha256,
            [IO.Path]::GetFileName($LiteralPath))) {
        throw "$Description checksum sidecar is invalid."
    }
}

function Get-ComoteRoleFileEntries {
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [Parameter(Mandatory)]
        [string[]]$ExcludedRelativePaths
    )

    return @(
        Get-ChildItem `
            -LiteralPath $Root `
            -File `
            -Force `
            -Recurse `
            -ErrorAction Stop |
            ForEach-Object {
                [void](Assert-ComoteOrdinaryLocalPath `
                    -LiteralPath $_.FullName `
                    -Directory $false `
                    -Description "Role package file")
                $relative = Get-ComoteRelativePath `
                    -Root $Root `
                    -Child $_.FullName
                if ($ExcludedRelativePaths -ccontains $relative) {
                    return
                }
                [PSCustomObject][ordered]@{
                    path = $relative
                    length = [int64]$_.Length
                    sha256 = Get-ComoteSha256 -LiteralPath $_.FullName
                }
            } |
            Sort-Object path
    )
}

function Assert-ComoteRoleDirectoryInventory {
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [Parameter(Mandatory)]
        [object[]]$Entries,

        [Parameter(Mandatory)]
        [string[]]$AdditionalRelativePaths,

        [Parameter(Mandatory)]
        [string]$Description
    )

    $expected = New-Object `
        'Collections.Generic.HashSet[string]' `
        ([StringComparer]::Ordinal)
    foreach ($entry in $Entries) {
        Assert-ComoteExactProperties `
            -InputObject $entry `
            -Expected @("path", "length", "sha256") `
            -Description "$Description file entry"
        $relative = [string]$entry.path
        Assert-ComoteSafeRelativePath -RelativePath $relative
        if (-not $expected.Add($relative) -or
            [int64]$entry.length -le 0 -or
            [string]$entry.sha256 -cnotmatch '^[0-9A-F]{64}$') {
            throw "$Description has an invalid file entry."
        }
        $path = Join-Path $Root $relative.Replace('/', '\')
        $identity = Assert-ComoteOrdinaryLocalPath `
            -LiteralPath $path `
            -Directory $false `
            -Description "$Description file"
        if ([int64]$identity.Identity.Length -ne [int64]$entry.length -or
            (Get-ComoteSha256 -LiteralPath $path) -cne
                [string]$entry.sha256) {
            throw "$Description file differs from its manifest."
        }
    }
    foreach ($relative in $AdditionalRelativePaths) {
        Assert-ComoteSafeRelativePath -RelativePath $relative
        if (-not $expected.Add($relative)) {
            throw "$Description has a duplicated additional file."
        }
    }
    $actual = @(
        Get-ChildItem `
            -LiteralPath $Root `
            -File `
            -Force `
            -Recurse `
            -ErrorAction Stop |
            ForEach-Object {
                Get-ComoteRelativePath -Root $Root -Child $_.FullName
            }
    )
    if ($actual.Count -ne $expected.Count) {
        throw "$Description contains an unexpected file count."
    }
    foreach ($relative in $actual) {
        if (-not $expected.Contains($relative)) {
            throw "$Description contains an unmanifested file."
        }
    }
}

function Get-ComoteRoleStreamSha256 {
    param([Parameter(Mandatory)][IO.Stream]$Stream)

    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString(
            $algorithm.ComputeHash($Stream)
        ).Replace("-", "")
    }
    finally {
        $algorithm.Dispose()
    }
}

function Assert-ComoteRoleZipMatchesDirectory {
    param(
        [Parameter(Mandatory)][string]$ZipPath,
        [Parameter(Mandatory)][string]$Directory
    )

    [void](Assert-ComoteOrdinaryLocalPath `
        -LiteralPath $ZipPath `
        -Directory $false `
        -Description "Candidate role ZIP")
    $root = (Assert-ComoteOrdinaryLocalPath `
        -LiteralPath $Directory `
        -Directory $true `
        -Description "Candidate role directory").FullPath
    $files = New-Object `
        'Collections.Generic.Dictionary[string,object]' `
        ([StringComparer]::Ordinal)
    $directories = New-Object `
        'Collections.Generic.Dictionary[string,bool]' `
        ([StringComparer]::Ordinal)
    $caseNames = New-Object `
        'Collections.Generic.HashSet[string]' `
        ([StringComparer]::OrdinalIgnoreCase)
    foreach ($item in @(Get-ChildItem `
        -LiteralPath $root `
        -Force `
        -Recurse `
        -ErrorAction Stop)) {
        $relative = Get-ComoteRelativePath `
            -Root $root `
            -Child $item.FullName
        Assert-ComoteSafeRelativePath -RelativePath $relative
        if (-not $caseNames.Add($relative)) {
            throw "The role directory contains a case-colliding path."
        }
        $identity = Assert-ComoteOrdinaryLocalPath `
            -LiteralPath $item.FullName `
            -Directory:([bool]$item.PSIsContainer) `
            -Description "Candidate role tree item"
        if ($item.PSIsContainer) {
            $isEmpty = @(Get-ChildItem `
                -LiteralPath $item.FullName `
                -Force `
                -ErrorAction Stop).Count -eq 0
            $directories.Add($relative, $isEmpty)
        }
        else {
            $files.Add($relative, [PSCustomObject]@{
                length = [int64]$identity.Identity.Length
                sha256 = Get-ComoteSha256 -LiteralPath $item.FullName
            })
        }
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        $zipNames = New-Object `
            'Collections.Generic.HashSet[string]' `
            ([StringComparer]::Ordinal)
        $zipCaseNames = New-Object `
            'Collections.Generic.HashSet[string]' `
            ([StringComparer]::OrdinalIgnoreCase)
        $zipFiles = 0
        $zipDirectories = New-Object `
            'Collections.Generic.HashSet[string]' `
            ([StringComparer]::Ordinal)
        foreach ($entry in @($archive.Entries)) {
            $relative = [string]$entry.FullName.Replace('\', '/').TrimEnd('/')
            if ([string]::IsNullOrWhiteSpace($relative)) {
                throw "A candidate role ZIP has an empty entry."
            }
            Assert-ComoteSafeRelativePath -RelativePath $relative
            if (-not $zipNames.Add($relative) -or
                -not $zipCaseNames.Add($relative)) {
                throw "A candidate role ZIP has duplicate/case-colliding entries."
            }
            $isDirectory = [string]$entry.FullName.EndsWith(
                    "/", [StringComparison]::Ordinal) -or
                [string]::IsNullOrEmpty([string]$entry.Name)
            if ($isDirectory) {
                if (-not $directories.ContainsKey($relative)) {
                    throw "A role ZIP contains an unexpected directory."
                }
                [void]$zipDirectories.Add($relative)
                continue
            }
            if (-not $files.ContainsKey($relative)) {
                throw "A role ZIP contains an unexpected file."
            }
            $expected = $files[$relative]
            $stream = $entry.Open()
            try {
                $hash = Get-ComoteRoleStreamSha256 -Stream $stream
            }
            finally {
                $stream.Dispose()
            }
            if ([int64]$entry.Length -ne [int64]$expected.length -or
                $hash -cne [string]$expected.sha256) {
                throw "A role ZIP file differs from its role directory."
            }
            $zipFiles++
        }
        if ($zipFiles -ne $files.Count) {
            throw "A role ZIP omits a role-directory file."
        }
        foreach ($pair in $directories.GetEnumerator()) {
            if ([bool]$pair.Value -and
                -not $zipDirectories.Contains([string]$pair.Key)) {
                throw "A role ZIP omits an empty role directory."
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Assert-ComoteCandidateRoleOutput {
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [AllowNull()]
        [string]$ExpectedIndexSha256,

        [AllowNull()]
        [string]$ExpectedSourceManifestSha256,

        [AllowNull()]
        [string]$ExpectedProvisionalReportSha256,

        [switch]$AllowPromotedIndexWithoutSidecar
    )

    $rootIdentity = Assert-ComoteOrdinaryLocalPath `
        -LiteralPath $Root `
        -Directory $true `
        -Description "Candidate role output"
    $indexPath = Join-Path `
        $rootIdentity.FullPath `
        "CANDIDATE-ROLE-INDEX.json"
    [void](Assert-ComoteOrdinaryLocalPath `
        -LiteralPath $indexPath `
        -Directory $false `
        -Description "Candidate role index")
    $indexHash = Get-ComoteSha256 -LiteralPath $indexPath
    if (-not [string]::IsNullOrWhiteSpace($ExpectedIndexSha256) -and
        $indexHash -cne $ExpectedIndexSha256.ToUpperInvariant()) {
        throw "The candidate role index differs from its out-of-band hash."
    }
    Assert-ComoteExternalHash `
        -LiteralPath $indexPath `
        -ExpectedSha256 $indexHash `
        -Description "Candidate role index"
    $index = Read-ComoteJson `
        -LiteralPath $indexPath `
        -Description "Candidate role index"
    Assert-ComoteExactProperties `
        -InputObject $index `
        -Expected @(
            "schemaVersion",
            "releaseId",
            "status",
            "completedUtc",
            "sourceReleaseManifestSha256",
            "unifiedProvisionalReportSha256",
            "runtimePolicy",
            "manager",
            "clientVirtualHid"
        ) `
        -Description "Candidate role index"
    $roleProperties = @(
        "directory",
        "zip",
        "zipSha256",
        "manifest",
        "manifestSha256"
    )
    Assert-ComoteExactProperties `
        -InputObject $index.manager `
        -Expected ($roleProperties + "containsAdministrativePayload") `
        -Description "Candidate Manager role"
    Assert-ComoteExactProperties `
        -InputObject $index.clientVirtualHid `
        -Expected ($roleProperties + @("testSigned", "targetBuild")) `
        -Description "Candidate Client role"
    $managerName = "Comote-{0}-Manager-win-x64" -f
        [string]$index.releaseId
    $clientName = "Comote-{0}-Client-VirtualHid-win-x64" -f
        [string]$index.releaseId
    if ([int]$index.schemaVersion -ne 1 -or
        [string]$index.releaseId -notmatch
            '^[A-Za-z0-9][A-Za-z0-9._-]{2,63}$' -or
        [string]$index.status -cne "candidate-role-packages" -or
        [string]$index.sourceReleaseManifestSha256 -cnotmatch
            '^[0-9A-F]{64}$' -or
        [string]$index.unifiedProvisionalReportSha256 -cnotmatch
            '^[0-9A-F]{64}$' -or
        (-not [string]::IsNullOrWhiteSpace(
                $ExpectedSourceManifestSha256) -and
            [string]$index.sourceReleaseManifestSha256 -cne
                $ExpectedSourceManifestSha256.ToUpperInvariant()) -or
        (-not [string]::IsNullOrWhiteSpace(
                $ExpectedProvisionalReportSha256) -and
            [string]$index.unifiedProvisionalReportSha256 -cne
                $ExpectedProvisionalReportSha256.ToUpperInvariant()) -or
        [string]$index.manager.directory -cne $managerName -or
        [string]$index.manager.zip -cne "$managerName.zip" -or
        [string]$index.manager.manifest -cne
            "manager-role-manifest.json" -or
        [bool]$index.manager.containsAdministrativePayload -ne $false -or
        [string]$index.clientVirtualHid.directory -cne $clientName -or
        [string]$index.clientVirtualHid.zip -cne "$clientName.zip" -or
        [string]$index.clientVirtualHid.manifest -cne
            $script:ComotePreviewManifestName -or
        [bool]$index.clientVirtualHid.testSigned -ne $true -or
        [string]$index.clientVirtualHid.targetBuild -cne "19045") {
        throw "The candidate role index identity is invalid."
    }

    $expectedTopDirectories = New-Object `
        'Collections.Generic.HashSet[string]' `
        ([StringComparer]::Ordinal)
    [void]$expectedTopDirectories.Add($managerName)
    [void]$expectedTopDirectories.Add($clientName)
    $expectedTopFiles = New-Object `
        'Collections.Generic.HashSet[string]' `
        ([StringComparer]::Ordinal)
    foreach ($name in @(
        "CANDIDATE-ROLE-INDEX.json",
        "CANDIDATE-ROLE-INDEX.json.sha256",
        "$managerName.zip",
        "$managerName.zip.sha256",
        "$clientName.zip",
        "$clientName.zip.sha256"
    )) {
        [void]$expectedTopFiles.Add($name)
    }
    $promotedName = "PROMOTED-ROLE-INDEX.json"
    $hasPromoted = Test-Path `
        -LiteralPath (Join-Path $rootIdentity.FullPath $promotedName) `
        -PathType Leaf
    $hasPromotedSidecar = Test-Path `
        -LiteralPath (Join-Path `
            $rootIdentity.FullPath `
            "$promotedName.sha256") `
        -PathType Leaf
    $recoverablePromotedIndexOnly =
        $AllowPromotedIndexWithoutSidecar.IsPresent -and
        $hasPromoted -and
        -not $hasPromotedSidecar
    if ($hasPromoted -ne $hasPromotedSidecar -and
        -not $recoverablePromotedIndexOnly) {
        throw "The candidate root contains an orphaned promoted index."
    }
    if ($hasPromoted -and $hasPromotedSidecar) {
        [void]$expectedTopFiles.Add($promotedName)
        [void]$expectedTopFiles.Add("$promotedName.sha256")
    }
    elseif ($recoverablePromotedIndexOnly) {
        [void]$expectedTopFiles.Add($promotedName)
    }
    $topItems = @(Get-ChildItem `
        -LiteralPath $rootIdentity.FullPath `
        -Force `
        -ErrorAction Stop)
    if ($topItems.Count -ne
        ($expectedTopDirectories.Count + $expectedTopFiles.Count)) {
        throw "The candidate root contains an unexpected top-level item."
    }
    foreach ($item in $topItems) {
        if ($item.PSIsContainer) {
            if (-not $expectedTopDirectories.Contains([string]$item.Name)) {
                throw "The candidate root contains an unexpected directory."
            }
            [void](Assert-ComoteOrdinaryLocalPath `
                -LiteralPath $item.FullName `
                -Directory $true `
                -Description "Candidate role directory")
        }
        else {
            if (-not $expectedTopFiles.Contains([string]$item.Name)) {
                throw "The candidate root contains an unexpected file."
            }
            [void](Assert-ComoteOrdinaryLocalPath `
                -LiteralPath $item.FullName `
                -Directory $false `
                -Description "Candidate top-level file")
        }
    }

    foreach ($role in @($index.manager, $index.clientVirtualHid)) {
        foreach ($hashName in @("zipSha256", "manifestSha256")) {
            if ([string]$role.$hashName -cnotmatch '^[0-9A-F]{64}$') {
                throw "A candidate role hash is malformed."
            }
        }
        Assert-ComoteExternalHash `
            -LiteralPath (Join-Path $rootIdentity.FullPath ([string]$role.zip)) `
            -ExpectedSha256 ([string]$role.zipSha256) `
            -Description "Candidate role ZIP"
        $roleManifestPath = Join-Path `
            (Join-Path $rootIdentity.FullPath ([string]$role.directory)) `
            ([string]$role.manifest)
        [void](Assert-ComoteOrdinaryLocalPath `
            -LiteralPath $roleManifestPath `
            -Directory $false `
            -Description "Candidate role manifest")
        if ((Get-ComoteSha256 -LiteralPath $roleManifestPath) -cne
            [string]$role.manifestSha256) {
            throw "A candidate role manifest differs from its pinned hash."
        }
    }

    $managerRoot = Join-Path $rootIdentity.FullPath $managerName
    $managerManifestPath = Join-Path `
        $managerRoot `
        "manager-role-manifest.json"
    Assert-ComoteExternalHash `
        -LiteralPath $managerManifestPath `
        -ExpectedSha256 ([string]$index.manager.manifestSha256) `
        -Description "Candidate Manager manifest"
    $managerManifest = Read-ComoteJson `
        -LiteralPath $managerManifestPath `
        -Description "Candidate Manager manifest"
    Assert-ComoteExactProperties `
        -InputObject $managerManifest `
        -Expected @(
            "schemaVersion",
            "releaseId",
            "packageRole",
            "sourceReleaseManifestSha256",
            "unifiedProvisionalReportSha256",
            "runtimePolicy",
            "application",
            "files"
        ) `
        -Description "Candidate Manager manifest"
    Assert-ComoteExactProperties `
        -InputObject $managerManifest.application `
        -Expected @(
            "role",
            "path",
            "originalFilename",
            "sha256",
            "signerThumbprint"
        ) `
        -Description "Candidate Manager application"
    if ([int]$managerManifest.schemaVersion -ne 1 -or
        [string]$managerManifest.releaseId -cne [string]$index.releaseId -or
        [string]$managerManifest.packageRole -cne "manager-portable" -or
        [string]$managerManifest.sourceReleaseManifestSha256 -cne
            [string]$index.sourceReleaseManifestSha256 -or
        [string]$managerManifest.unifiedProvisionalReportSha256 -cne
            [string]$index.unifiedProvisionalReportSha256 -or
        ($managerManifest.runtimePolicy | ConvertTo-Json -Compress) -cne
            ($index.runtimePolicy | ConvertTo-Json -Compress) -or
        [string]$managerManifest.application.role -cne "manager" -or
        [string]$managerManifest.application.path -cne
            "App/Manager/ComoteManager.exe" -or
        [string]$managerManifest.application.originalFilename -cne
            "Viewer.dll") {
        throw "The candidate Manager manifest identity is invalid."
    }
    Assert-ComoteRoleDirectoryInventory `
        -Root $managerRoot `
        -Entries @($managerManifest.files) `
        -AdditionalRelativePaths @(
            "manager-role-manifest.json",
            "manager-role-manifest.json.sha256"
        ) `
        -Description "Candidate Manager role"
    Assert-ComoteRoleZipMatchesDirectory `
        -ZipPath (Join-Path `
            $rootIdentity.FullPath `
            ([string]$index.manager.zip)) `
        -Directory $managerRoot
    foreach ($entry in @($managerManifest.files)) {
        $relative = [string]$entry.path
        if ($relative.StartsWith("Driver/", [StringComparison]::Ordinal) -or
            $relative.StartsWith("Trust/", [StringComparison]::Ordinal) -or
            $relative.StartsWith("App/Broker/", [StringComparison]::Ordinal) -or
            $relative -in @(
                "VirtualHidPreview.Common.ps1",
                "Install-ComoteVirtualHidPreview.ps1",
                "Uninstall-ComoteVirtualHidPreview.ps1",
                "Invoke-ComoteClientRoleUac.ps1"
            )) {
            throw "The Manager role contains administrative payload."
        }
    }
    $managerEntry = @(
        $managerManifest.files |
            Where-Object {
                [string]$_.path -ceq
                    [string]$managerManifest.application.path
            }
    )
    if ($managerEntry.Count -ne 1 -or
        [string]$managerEntry[0].sha256 -cne
            [string]$managerManifest.application.sha256) {
        throw "The Manager executable is not bound to its role manifest."
    }
    $managerApplicationPath = Join-Path `
        $managerRoot `
        ([string]$managerManifest.application.path).Replace('/', '\')
    Assert-ComotePinnedAuthenticodeSigner `
        -LiteralPath $managerApplicationPath `
        -Thumbprint ([string]$managerManifest.application.signerThumbprint) `
        -Description "Candidate Manager application"
    if ([string][Diagnostics.FileVersionInfo]::GetVersionInfo(
            $managerApplicationPath
        ).OriginalFilename -cne
        [string]$managerManifest.application.originalFilename) {
        throw "The candidate Manager executable metadata is invalid."
    }

    $clientRoot = Join-Path $rootIdentity.FullPath $clientName
    $clientRelease = Get-ComoteReleaseManifest `
        -PackageRoot $clientRoot `
        -ExpectedManifestSha256 ([string]$index.clientVirtualHid.manifestSha256)
    Assert-ComoteReleaseInventory -Release $clientRelease
    [void](Assert-ComoteDriverPackageBinding -Release $clientRelease)
    Assert-ComoteReleaseSigners -Release $clientRelease
    if ([string]$clientRelease.Document.releaseId -cne
            [string]$index.releaseId -or
        [string]$clientRelease.Document.packageRole -cne
            "client-virtual-hid" -or
        @($clientRelease.Document.validationTools).Count -ne 0 -or
        ($clientRelease.Document.runtimePolicy | ConvertTo-Json -Compress) -cne
            ($managerManifest.runtimePolicy | ConvertTo-Json -Compress) -or
        ($clientRelease.Document.runtimePolicy | ConvertTo-Json -Compress) -cne
            ($index.runtimePolicy | ConvertTo-Json -Compress)) {
        throw "The candidate Client/Manager runtime identity is invalid."
    }
    foreach ($entry in @($clientRelease.Entries)) {
        if ([string]$entry.path.StartsWith(
                "App/Manager/",
                [StringComparison]::Ordinal) -or
            [string]$entry.path.StartsWith(
                "Validation/",
                [StringComparison]::Ordinal)) {
            throw "The Client role contains Manager or validation payload."
        }
    }
    Assert-ComoteRoleZipMatchesDirectory `
        -ZipPath (Join-Path `
            $rootIdentity.FullPath `
            ([string]$index.clientVirtualHid.zip)) `
        -Directory $clientRoot
    return [PSCustomObject]@{
        Root = $rootIdentity.FullPath
        Index = $index
        IndexPath = $indexPath
        IndexSha256 = $indexHash
        ManagerManifest = $managerManifest
        ClientRelease = $clientRelease
    }
}

function Assert-ComotePromotionEvidenceSemantics {
    param(
        [Parameter(Mandatory)]$Report,
        [Parameter(Mandatory)][string[]]$ExpectedE2ELabels
    )

    $labels = @($Report.e2eRuns | ForEach-Object { [string]$_.label })
    if (($labels | ConvertTo-Json -Compress) -cne
        ($ExpectedE2ELabels | ConvertTo-Json -Compress)) {
        throw "Promotion E2E labels/order are not exact."
    }
    foreach ($run in @($Report.e2eRuns)) {
        if ([string]$run.sha256 -cnotmatch '^[0-9A-F]{64}$' -or
            [string]$run.appReportSha256 -cnotmatch '^[0-9A-F]{64}$' -or
            [string]$run.observerSha256 -cnotmatch '^[0-9A-F]{64}$' -or
            [string]$run.brokerSha256 -cnotmatch '^[0-9A-F]{64}$' -or
            [int]$run.buildExitCode -ne 0 -or
            [int]$run.applicationExitCode -ne 0 -or
            [int]$run.cleanupExitCode -ne 0 -or
            [int]$run.evidenceTestCount -le 0 -or
            [int]$run.rawInputEventCount -le 0) {
            throw "A promotion E2E run is incomplete."
        }
    }
    $samples = @($Report.hubSmoke.samples)
    $offsets = @($samples | ForEach-Object { [int]$_.offsetSeconds })
    if (($offsets | ConvertTo-Json -Compress) -cne
            (@(0, 5, 10, 15, 20, 25) | ConvertTo-Json -Compress) -or
        [bool]$Report.hubSmoke.uiProcessesStopped -ne $true -or
        $null -eq $Report.hubSmoke.client -or
        $null -eq $Report.hubSmoke.manager -or
        $null -eq $Report.hubSmoke.broker -or
        $null -eq $Report.hubSmoke.route) {
        throw "Hub smoke evidence is not the exact six-sample sequence."
    }
    $power = $Report.powerEvidence
    Assert-ComoteExactProperties `
        -InputObject $power `
        -Expected @(
            "normalBootBefore", "normalBootAfter", "s1CheckpointUtc",
            "s1BootMarker", "s1Evidence", "coldCheckpointUtc",
            "coldBootBefore", "coldBootAfter", "coldEvidence",
            "verifierBootBefore", "verifierBootAfter",
            "postCleanupCheckpointUtc", "postCleanupBootBefore",
            "postCleanupBootAfter"
        ) `
        -Description "Promotion power evidence"
    if ([string]::IsNullOrWhiteSpace([string]$power.normalBootBefore) -or
        [string]$power.normalBootAfter -ceq [string]$power.normalBootBefore -or
        [string]$power.s1BootMarker -cne [string]$power.normalBootAfter -or
        [int]$power.s1Evidence.targetState -ne 1 -or
        [int]$power.s1Evidence.effectiveState -ne 1 -or
        [int]$power.coldEvidence.eventId -ne 6006 -or
        [string]$power.coldBootAfter -ceq [string]$power.coldBootBefore -or
        [string]$power.verifierBootAfter -ceq
            [string]$power.verifierBootBefore -or
        [string]$power.postCleanupBootAfter -ceq
            [string]$power.postCleanupBootBefore) {
        throw "Normal/S1/cold/Verifier/clean-reboot evidence is incomplete."
    }
    if ([string]$Report.verifierEvidence.activeCurrentTarget -cne
            "ComoteVirtualHidPhase2.sys" -or
        -not [string]::IsNullOrEmpty(
            [string]$Report.verifierEvidence.nextBootDrivers) -or
        [int]$Report.verifierEvidence.nextBootLevel -ne 0) {
        throw "Oneboot Driver Verifier evidence is not exact."
    }
    $clean = $Report.unifiedCleanup.machineState
    if ([int]$clean.nativeExitCode -ne 20 -or
        [string]$clean.nativeState -cne "not-installed" -or
        [bool]$clean.brokerServiceAbsent -ne $true -or
        [bool]$clean.controllerGroupAbsent -ne $true -or
        [bool]$clean.receiptAbsent -ne $true -or
        [bool]$clean.installRootAbsent -ne $true -or
        [bool]$clean.verifierNextBootClean -ne $true -or
        [bool]$clean.signingCleanPassed -ne $true -or
        [bool]$clean.testSigningChangedByWorkflow -ne $false -or
        [bool]$clean.osLogsRemoved -ne $false -or
        [bool]$Report.signingCleanPassed -ne $true -or
        [bool]$Report.testSigningChangedByWorkflow -ne $false -or
        [bool]$Report.osLogsRemoved -ne $false) {
        throw "Promotion cleanup/signing evidence is incomplete."
    }
}

function Assert-ComoteUnifiedProvisionalReport {
    param(
        [Parameter(Mandatory)]
        [string]$LiteralPath,

        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-F]{64}$')]
        [string]$ExpectedSha256,

        [Parameter(Mandatory)]
        $UnifiedRelease
    )

    $identity = Assert-ComoteOrdinaryLocalPath `
        -LiteralPath $LiteralPath `
        -Directory $false `
        -Description "Unified provisional promotion report"
    if ((Get-ComoteSha256 -LiteralPath $identity.FullPath) -cne
        $ExpectedSha256) {
        throw "The unified provisional report differs from its out-of-band hash."
    }
    Assert-ComoteExternalHash `
        -LiteralPath $identity.FullPath `
        -ExpectedSha256 $ExpectedSha256 `
        -Description "Unified provisional promotion report"
    $report = Read-ComoteJson `
        -LiteralPath $identity.FullPath `
        -Description "Unified provisional promotion report"
    Assert-ComoteExactProperties `
        -InputObject $report `
        -Expected @(
            "schemaVersion", "status", "completedUtc", "releaseId",
            "releaseManifestSha256", "sourceInventorySha256",
            "toolInventorySha256", "snapshotName", "runtimePolicy",
            "preInstallMediaGate", "hubSmoke", "powerEvidence",
            "e2eRuns", "verifierEvidence", "unifiedCleanup",
            "signingCleanPassed", "testSigningChangedByWorkflow",
            "osLogsRemoved"
        ) `
        -Description "Unified provisional promotion report"
    if ([int]$report.schemaVersion -ne 1 -or
        [string]$report.status -cne
            "unified-validation-passed-role-tests-pending" -or
        [string]$report.releaseId -cne
            [string]$UnifiedRelease.Document.releaseId -or
        [string]$report.releaseManifestSha256 -cne
            [string]$UnifiedRelease.Sha256 -or
        [string]$report.sourceInventorySha256 -cnotmatch
            '^[0-9A-F]{64}$' -or
        [string]$report.toolInventorySha256 -cnotmatch
            '^[0-9A-F]{64}$' -or
        [string]::IsNullOrWhiteSpace([string]$report.snapshotName) -or
        ($report.runtimePolicy | ConvertTo-Json -Depth 8 -Compress) -cne
            ($UnifiedRelease.Document.runtimePolicy |
                ConvertTo-Json -Depth 8 -Compress)) {
        throw "The unified provisional report is not bound to this release."
    }
    foreach ($forbidden in @(
        "candidateRoleIndexSha256", "candidateRoles", "managerRoleTest",
        "clientRoleTest", "clientCleanup", "provisionalReportSha256"
    )) {
        if ($report.PSObject.Properties.Name -ccontains $forbidden) {
            throw "The provisional report contains candidate/final evidence."
        }
    }
    Assert-ComotePromotionEvidenceSemantics `
        -Report $report `
        -ExpectedE2ELabels @(
            "normal", "after-normal-reboot", "after-s1-resume",
            "after-cold-start", "under-driver-verifier"
        )
    return $report
}

function New-ComoteRoleZip {
    param(
        [Parameter(Mandatory)]
        [string]$RoleRoot,

        [Parameter(Mandatory)]
        [string]$LiteralPath
    )

    if (Test-Path -LiteralPath $LiteralPath) {
        throw "A role ZIP target already exists."
    }
    Compress-Archive `
        -Path (Join-Path $RoleRoot "*") `
        -DestinationPath $LiteralPath `
        -CompressionLevel Optimal
    return Write-ComoteExternalHash -LiteralPath $LiteralPath
}

function Copy-ComoteAuthenticatedEntry {
    param(
        [Parameter(Mandatory)]
        $UnifiedRelease,

        [Parameter(Mandatory)]
        [string]$DestinationRoot,

        [Parameter(Mandatory)]
        [string]$RelativePath
    )

    if (-not $UnifiedRelease.EntryMap.ContainsKey($RelativePath)) {
        throw "The authenticated unified manifest omits: $RelativePath"
    }
    $entry = $UnifiedRelease.EntryMap[$RelativePath]
    Copy-ComoteRoleFile `
        -SourceRoot $UnifiedRelease.Root `
        -DestinationRoot $DestinationRoot `
        -RelativePath $RelativePath `
        -ExpectedSha256 ([string]$entry.sha256) `
        -ExpectedLength ([int64]$entry.length)
}

if ($PSCmdlet.ParameterSetName -ceq "Candidate") {
    $expectedUnifiedHash =
        $ExpectedUnifiedManifestSha256.ToUpperInvariant()
    $expectedProvisionalHash =
        $ExpectedUnifiedProvisionalReportSha256.ToUpperInvariant()

    # Authenticate the manifest and provisional handoff before environment use.
    $unifiedRelease = Get-ComoteReleaseManifest `
        -PackageRoot $UnifiedPackageRoot `
        -ExpectedManifestSha256 $expectedUnifiedHash
    Assert-ComoteReleaseInventory -Release $unifiedRelease
    [void](Assert-ComoteDriverPackageBinding -Release $unifiedRelease)
    Assert-ComoteReleaseSigners -Release $unifiedRelease
    if ([string]$unifiedRelease.Document.packageRole -cne
        "validation-unified") {
        throw "Candidate role export requires the unified validation package."
    }
    [void](Assert-ComoteUnifiedProvisionalReport `
        -LiteralPath $UnifiedProvisionalReportPath `
        -ExpectedSha256 $expectedProvisionalHash `
        -UnifiedRelease $unifiedRelease)
    [void](Assert-ComoteDisposableVmEnvironment `
        -AcknowledgeDisposableVm:$AcknowledgeDisposableVm `
        -RequireTestSigning)

    $outputFullPath = [IO.Path]::GetFullPath($OutputDirectory)
    $outputParent = [IO.Path]::GetDirectoryName($outputFullPath)
    if ([string]::IsNullOrWhiteSpace($outputParent) -or
        [string]::IsNullOrWhiteSpace(
            [IO.Path]::GetFileName($outputFullPath))) {
        throw "OutputDirectory must name a child directory."
    }
    [void](Assert-ComoteOrdinaryLocalPath `
        -LiteralPath $outputParent `
        -Directory $true `
        -Description "Candidate output parent")

    if (Test-Path -LiteralPath $outputFullPath) {
        throw ("CreateCandidate refuses an existing unpinned output. " +
            "Use the protected promotion state and its exact candidate " +
            "index SHA-256 to resume.")
    }

    $partialPattern = ".partial-{0}-*" -f
        [IO.Path]::GetFileName($outputFullPath)
    $retainedPartials = @(Get-ChildItem `
        -LiteralPath $outputParent `
        -Force `
        -Filter $partialPattern `
        -ErrorAction Stop)
    if ($retainedPartials.Count -ne 0) {
        throw ("A retained candidate partial blocks retry: " +
            ($retainedPartials.FullName -join ", "))
    }
    $partialRoot = Join-Path `
        $outputParent `
        (".partial-{0}-{1}" -f
            [IO.Path]::GetFileName($outputFullPath),
            [Guid]::NewGuid().ToString("N"))
    [IO.Directory]::CreateDirectory($partialRoot) | Out-Null
    $published = $false
    try {
        $releaseId = [string]$unifiedRelease.Document.releaseId
        $managerName = "Comote-$releaseId-Manager-win-x64"
        $clientName = "Comote-$releaseId-Client-VirtualHid-win-x64"
        $managerRoot = Join-Path $partialRoot $managerName
        $clientRoot = Join-Path $partialRoot $clientName
        [IO.Directory]::CreateDirectory($managerRoot) | Out-Null
        [IO.Directory]::CreateDirectory($clientRoot) | Out-Null

        foreach ($entry in @($unifiedRelease.Entries)) {
            $relative = [string]$entry.path
            $managerSelected =
                ($relative.StartsWith(
                    "App/Manager/",
                    [StringComparison]::Ordinal) -and
                    $relative -cne
                        "App/Manager/Start Comote Manager Hub.cmd") -or
                $relative.StartsWith(
                    "THIRD_PARTY_NOTICES/",
                    [StringComparison]::Ordinal) -or
                $relative -ceq "Verify-ComoteManagerRole.ps1"
            $clientSelected =
                $relative.StartsWith(
                    "App/Client/",
                    [StringComparison]::Ordinal) -or
                $relative.StartsWith(
                    "App/Broker/",
                    [StringComparison]::Ordinal) -or
                $relative.StartsWith(
                    "Driver/",
                    [StringComparison]::Ordinal) -or
                $relative.StartsWith(
                    "Trust/",
                    [StringComparison]::Ordinal) -or
                $relative.StartsWith(
                    "THIRD_PARTY_NOTICES/",
                    [StringComparison]::Ordinal) -or
                $relative -cin @(
                    "Install-ComoteVirtualHidPreview.ps1",
                    "Uninstall-ComoteVirtualHidPreview.ps1",
                    "VirtualHidPreview.Common.ps1",
                    "Invoke-ComoteClientRoleUac.ps1",
                    "README.md"
                )
            if ($managerSelected) {
                Copy-ComoteRoleFile `
                    -SourceRoot $unifiedRelease.Root `
                    -DestinationRoot $managerRoot `
                    -RelativePath $relative `
                    -ExpectedSha256 ([string]$entry.sha256) `
                    -ExpectedLength ([int64]$entry.length)
            }
            if ($clientSelected) {
                Copy-ComoteRoleFile `
                    -SourceRoot $unifiedRelease.Root `
                    -DestinationRoot $clientRoot `
                    -RelativePath $relative `
                    -ExpectedSha256 ([string]$entry.sha256) `
                    -ExpectedLength ([int64]$entry.length)
            }
        }

        Write-ComoteRoleAsciiFile `
            -LiteralPath (Join-Path $managerRoot "START COMOTE MANAGER.cmd") `
            -Content (
                "@echo off`r`n" +
                '"%SystemRoot%\System32\WindowsPowerShell\v1.0\' +
                'powershell.exe" -NoLogo -NoProfile ' +
                "-ExecutionPolicy Bypass -File " +
                '"%~dp0Verify-ComoteManagerRole.ps1"' + "`r`n" +
                "if errorlevel 1 exit /b %errorlevel%`r`n" +
                '"%~dp0App\Manager\ComoteManager.exe" --manager-hub' +
                "`r`n"
            )
        Write-ComoteRoleAsciiFile `
            -LiteralPath (Join-Path $managerRoot "ROLE_README.txt") `
            -Content (
                "COMOTE MANAGER PORTABLE ROLE`r`n`r`n" +
                "This role is portable and must run without elevation. " +
                "START COMOTE MANAGER.cmd verifies every packaged file " +
                "before launch.`r`n" +
                "Do not add firewall rules from this package. If the " +
                "private-network Hub port is blocked, an administrator " +
                "must review and create the narrow rule separately.`r`n" +
                "Before extraction, verify this ZIP and its Manager " +
                "manifest against the immutable CANDIDATE role index. " +
                "For release use, also verify the PROMOTED role index and " +
                "its out-of-band SHA-256.`r`n"
            )

        Write-ComoteRoleAsciiFile `
            -LiteralPath (Join-Path `
                $clientRoot `
                "INSTALL CLIENT (Administrator).cmd") `
            -Content (
                "@echo off`r`n" +
                '"%SystemRoot%\System32\WindowsPowerShell\v1.0\' +
                'powershell.exe" -NoLogo -NoProfile ' +
                "-ExecutionPolicy Bypass -File " +
                '"%~dp0Invoke-ComoteClientRoleUac.ps1" ' +
                "-Action Install`r`n"
            )
        Write-ComoteRoleAsciiFile `
            -LiteralPath (Join-Path `
                $clientRoot `
                "UNINSTALL CLIENT (Administrator).cmd") `
            -Content (
                "@echo off`r`n" +
                '"%SystemRoot%\System32\WindowsPowerShell\v1.0\' +
                'powershell.exe" -NoLogo -NoProfile ' +
                "-ExecutionPolicy Bypass -File " +
                '"%~dp0Invoke-ComoteClientRoleUac.ps1" ' +
                "-Action Uninstall`r`n"
            )
        Write-ComoteRoleAsciiFile `
            -LiteralPath (Join-Path `
                $clientRoot `
                "START HERE - Client Virtual HID.cmd") `
            -Content (
                "@echo off`r`n" +
                'if not exist "%ProgramFiles%\Comote\VirtualHidPreview\' +
                'App\Client\ComoteClient.exe" (' + "`r`n" +
                "  echo Install the Client role first. 1>&2`r`n" +
                "  exit /b 1`r`n" +
                ")`r`n" +
                '"%ProgramFiles%\Comote\VirtualHidPreview\App\Client\' +
                'ComoteClient.exe" --manager-hub --virtual-hid' + "`r`n"
            )
        Write-ComoteRoleAsciiFile `
            -LiteralPath (Join-Path $clientRoot "ROLE_README.txt") `
            -Content (
                "COMOTE CLIENT VIRTUAL HID ROLE`r`n`r`n" +
                "Install requires an administrator, a pre-existing active " +
                "TESTSIGNING state, HVCI off, Secure Boot off, the explicit " +
                "test-signed preview switch, and this exact typed phrase:`r`n" +
                "I ACCEPT COMOTE TEST-SIGNED VIRTUAL HID PREVIEW`r`n`r`n" +
                "Uninstall requires only an administrator and the explicit " +
                "test-signed preview cleanup switch. It does not require the " +
                "typed phrase or the current TESTSIGNING, HVCI, or Secure " +
                "Boot state. The protected receipt retains the original " +
                "install approval.`r`n"
            )

        $managerApplications = @(
            $unifiedRelease.Document.cmt1Applications |
                Where-Object { [string]$_.role -ceq "manager" }
        )
        if ($managerApplications.Count -ne 1) {
            throw "The unified release has no singleton Manager application."
        }
        $managerFiles = Get-ComoteRoleFileEntries `
            -Root $managerRoot `
            -ExcludedRelativePaths @(
                "manager-role-manifest.json",
                "manager-role-manifest.json.sha256"
            )
        $managerManifest = [PSCustomObject][ordered]@{
            schemaVersion = 1
            releaseId = $releaseId
            packageRole = "manager-portable"
            sourceReleaseManifestSha256 = $expectedUnifiedHash
            unifiedProvisionalReportSha256 = $expectedProvisionalHash
            runtimePolicy = $unifiedRelease.Document.runtimePolicy
            application = $managerApplications[0]
            files = $managerFiles
        }
        $managerManifestPath = Join-Path `
            $managerRoot `
            "manager-role-manifest.json"
        Write-ComoteJsonAtomically `
            -LiteralPath $managerManifestPath `
            -InputObject $managerManifest `
            -Depth 16
        $managerManifestHash = Write-ComoteExternalHash `
            -LiteralPath $managerManifestPath

        $clientApplications = @(
            $unifiedRelease.Document.cmt1Applications |
                Where-Object {
                    [string]$_.role -ceq "client" -or
                    [string]$_.role -ceq "broker"
                }
        )
        if ($clientApplications.Count -ne 2 -or
            [string]$clientApplications[0].role -cne "client" -or
            [string]$clientApplications[1].role -cne "broker") {
            throw "The unified release Client/Broker ordering is invalid."
        }
        $clientFiles = Get-ComoteRoleFileEntries `
            -Root $clientRoot `
            -ExcludedRelativePaths @($script:ComotePreviewManifestName)
        $clientManifest = [PSCustomObject][ordered]@{
            schemaVersion = 1
            releaseId = $releaseId
            packageRole = "client-virtual-hid"
            target = [PSCustomObject][ordered]@{
                hypervisor = "Any"
                operatingSystem = "Windows 10 22H2"
                productType = 1
                editionSkus = @()
                architecture = "x64"
                buildNumber = "19045"
                anyUbr = $true
            }
            runtimePolicy = $unifiedRelease.Document.runtimePolicy
            driver = $unifiedRelease.Document.driver
            cmt1Applications = $clientApplications
            validationTools = @()
            files = $clientFiles
        }
        $clientManifestPath = Join-Path `
            $clientRoot `
            $script:ComotePreviewManifestName
        Write-ComoteJsonAtomically `
            -LiteralPath $clientManifestPath `
            -InputObject $clientManifest `
            -Depth 16
        $clientManifestHash = Get-ComoteSha256 `
            -LiteralPath $clientManifestPath
        $clientRelease = Get-ComoteReleaseManifest `
            -PackageRoot $clientRoot `
            -ExpectedManifestSha256 $clientManifestHash
        Assert-ComoteReleaseInventory -Release $clientRelease
        [void](Assert-ComoteDriverPackageBinding -Release $clientRelease)
        Assert-ComoteReleaseSigners -Release $clientRelease

        $managerZipPath = Join-Path $partialRoot "$managerName.zip"
        $clientZipPath = Join-Path $partialRoot "$clientName.zip"
        $managerZipHash = New-ComoteRoleZip `
            -RoleRoot $managerRoot `
            -LiteralPath $managerZipPath
        $clientZipHash = New-ComoteRoleZip `
            -RoleRoot $clientRoot `
            -LiteralPath $clientZipPath

        $candidateIndex = [PSCustomObject][ordered]@{
            schemaVersion = 1
            releaseId = $releaseId
            status = "candidate-role-packages"
            completedUtc = [DateTime]::UtcNow.ToString("o")
            sourceReleaseManifestSha256 = $expectedUnifiedHash
            unifiedProvisionalReportSha256 = $expectedProvisionalHash
            runtimePolicy = $unifiedRelease.Document.runtimePolicy
            manager = [PSCustomObject][ordered]@{
                directory = $managerName
                zip = "$managerName.zip"
                zipSha256 = $managerZipHash
                manifest = "manager-role-manifest.json"
                manifestSha256 = $managerManifestHash
                containsAdministrativePayload = $false
            }
            clientVirtualHid = [PSCustomObject][ordered]@{
                directory = $clientName
                zip = "$clientName.zip"
                zipSha256 = $clientZipHash
                manifest = $script:ComotePreviewManifestName
                manifestSha256 = $clientManifestHash
                testSigned = $true
                targetBuild = "19045"
            }
        }
        $candidateIndexPath = Join-Path `
            $partialRoot `
            "CANDIDATE-ROLE-INDEX.json"
        Write-ComoteJsonAtomically `
            -LiteralPath $candidateIndexPath `
            -InputObject $candidateIndex `
            -Depth 16
        $candidateIndexHash = Write-ComoteExternalHash `
            -LiteralPath $candidateIndexPath

        [void](Assert-ComoteCandidateRoleOutput `
            -Root $partialRoot `
            -ExpectedIndexSha256 $candidateIndexHash `
            -ExpectedSourceManifestSha256 $expectedUnifiedHash `
            -ExpectedProvisionalReportSha256 $expectedProvisionalHash)
        [IO.Directory]::Move($partialRoot, $outputFullPath)
        $published = $true
        $publishedCandidate = Assert-ComoteCandidateRoleOutput `
            -Root $outputFullPath `
            -ExpectedIndexSha256 $candidateIndexHash `
            -ExpectedSourceManifestSha256 $expectedUnifiedHash `
            -ExpectedProvisionalReportSha256 $expectedProvisionalHash
        Write-Host "Candidate Manager and Client role packages published."
        Write-Host "Candidate index: $($publishedCandidate.IndexPath)"
        Write-Host "Candidate index SHA-256: $candidateIndexHash"
    }
    finally {
        if (-not $published -and
            (Test-Path -LiteralPath $partialRoot -PathType Container)) {
            Write-Warning "Incomplete candidate output retained: $partialRoot"
        }
    }
    return
}

if ($PSCmdlet.ParameterSetName -ceq "Verify") {
    $verifiedCandidate = Assert-ComoteCandidateRoleOutput `
        -Root $CandidateDirectory `
        -ExpectedIndexSha256 `
            $ExpectedCandidateIndexSha256.ToUpperInvariant() `
        -ExpectedSourceManifestSha256 `
            $ExpectedCandidateSourceManifestSha256.ToUpperInvariant() `
        -ExpectedProvisionalReportSha256 `
            $ExpectedCandidateProvisionalReportSha256.ToUpperInvariant()
    Write-Host "Candidate role output is exact and immutable."
    Write-Host "Candidate index: $($verifiedCandidate.IndexPath)"
    Write-Host "Candidate index SHA-256: $($verifiedCandidate.IndexSha256)"
    return
}

function Assert-ComoteFinalPromotionReport {
    param(
        [Parameter(Mandatory)]
        [string]$LiteralPath,

        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-F]{64}$')]
        [string]$ExpectedSha256,

        [Parameter(Mandatory)]
        $Candidate
    )

    $identity = Assert-ComoteOrdinaryLocalPath `
        -LiteralPath $LiteralPath `
        -Directory $false `
        -Description "Final promotion report"
    if ((Get-ComoteSha256 -LiteralPath $identity.FullPath) -cne
        $ExpectedSha256) {
        throw "The final promotion report differs from its out-of-band hash."
    }
    Assert-ComoteExternalHash `
        -LiteralPath $identity.FullPath `
        -ExpectedSha256 $ExpectedSha256 `
        -Description "Final promotion report"
    $report = Read-ComoteJson `
        -LiteralPath $identity.FullPath `
        -Description "Final promotion report"
    Assert-ComoteExactProperties `
        -InputObject $report `
        -Expected @(
            "schemaVersion", "status", "completedUtc", "releaseId",
            "releaseManifestSha256", "sourceInventorySha256",
            "toolInventorySha256", "snapshotName", "runtimePolicy",
            "preInstallMediaGate", "hubSmoke", "powerEvidence",
            "e2eRuns", "verifierEvidence", "unifiedCleanup",
            "provisionalReportSha256", "candidateRoleIndexSha256",
            "candidateRoles", "managerRoleTest", "clientRoleTest",
            "clientCleanup", "signingCleanPassed",
            "testSigningChangedByWorkflow", "osLogsRemoved"
        ) `
        -Description "Final promotion report"
    Assert-ComoteExactProperties `
        -InputObject $report.candidateRoles `
        -Expected @(
            "managerZipSha256", "managerManifestSha256",
            "clientZipSha256", "clientManifestSha256"
        ) `
        -Description "Final candidate role hashes"
    if ([int]$report.schemaVersion -ne 1 -or
        [string]$report.status -cne "role-validation-passed" -or
        [string]$report.releaseId -cne
            [string]$Candidate.Index.releaseId -or
        [string]$report.releaseManifestSha256 -cne
            [string]$Candidate.Index.sourceReleaseManifestSha256 -or
        [string]$report.sourceInventorySha256 -cnotmatch
            '^[0-9A-F]{64}$' -or
        [string]$report.toolInventorySha256 -cnotmatch
            '^[0-9A-F]{64}$' -or
        [string]$report.provisionalReportSha256 -cne
            [string]$Candidate.Index.unifiedProvisionalReportSha256 -or
        [string]$report.candidateRoleIndexSha256 -cne
            [string]$Candidate.IndexSha256 -or
        ($report.runtimePolicy | ConvertTo-Json -Depth 8 -Compress) -cne
            ($Candidate.Index.runtimePolicy |
                ConvertTo-Json -Depth 8 -Compress) -or
        [string]$report.candidateRoles.managerZipSha256 -cne
            [string]$Candidate.Index.manager.zipSha256 -or
        [string]$report.candidateRoles.managerManifestSha256 -cne
            [string]$Candidate.Index.manager.manifestSha256 -or
        [string]$report.candidateRoles.clientZipSha256 -cne
            [string]$Candidate.Index.clientVirtualHid.zipSha256 -or
        [string]$report.candidateRoles.clientManifestSha256 -cne
            [string]$Candidate.Index.clientVirtualHid.manifestSha256) {
        throw "The final report does not promote this exact candidate."
    }
    Assert-ComotePromotionEvidenceSemantics `
        -Report $report `
        -ExpectedE2ELabels @(
            "normal", "after-normal-reboot", "after-s1-resume",
            "after-cold-start", "under-driver-verifier", "client-role"
        )
    if ([bool]$report.managerRoleTest.verificationPassed -ne $true -or
        [bool]$report.managerRoleTest.processStopped -ne $true -or
        $null -eq $report.managerRoleTest.process -or
        [string]$report.managerRoleTest.roleManifestSha256 -cne
            [string]$Candidate.Index.manager.manifestSha256 -or
        [string]$report.clientRoleTest.manifestSha256 -cne
            [string]$Candidate.Index.clientVirtualHid.manifestSha256 -or
        $null -eq $report.clientRoleTest.process -or
        $null -eq $report.clientRoleTest.brokerProcess -or
        $null -eq $report.clientRoleTest.revalidatedProcess -or
        [string]$report.clientRoleTest.e2e.label -cne "client-role" -or
        [bool]$report.clientRoleTest.processStopped -ne $true -or
        [bool]$report.clientCleanup.signingCleanup.cleanSigningAuditPassed -ne
            $true -or
        [int]$report.clientCleanup.machineState.nativeExitCode -ne 20 -or
        [string]$report.clientCleanup.machineState.nativeState -cne
            "not-installed" -or
        [bool]$report.clientCleanup.machineState.signingCleanPassed -ne $true -or
        [bool]$report.clientCleanup.machineState.osLogsRemoved -ne $false) {
        throw "Final Manager/Client role evidence is incomplete."
    }
    return $report
}

function Get-ComoteCandidateImmutableHashes {
    param([Parameter(Mandatory)]$Candidate)

    return [PSCustomObject][ordered]@{
        candidateIndex = Get-ComoteSha256 `
            -LiteralPath $Candidate.IndexPath
        managerZip = Get-ComoteSha256 `
            -LiteralPath (Join-Path `
                $Candidate.Root `
                ([string]$Candidate.Index.manager.zip))
        managerManifest = Get-ComoteSha256 `
            -LiteralPath (Join-Path `
                (Join-Path `
                    $Candidate.Root `
                    ([string]$Candidate.Index.manager.directory)) `
                ([string]$Candidate.Index.manager.manifest))
        clientZip = Get-ComoteSha256 `
            -LiteralPath (Join-Path `
                $Candidate.Root `
                ([string]$Candidate.Index.clientVirtualHid.zip))
        clientManifest = Get-ComoteSha256 `
            -LiteralPath (Join-Path `
                (Join-Path `
                    $Candidate.Root `
                    ([string]$Candidate.Index.clientVirtualHid.directory)) `
                ([string]$Candidate.Index.clientVirtualHid.manifest))
    }
}

function Assert-ComotePromotedRoleIndex {
    param(
        [Parameter(Mandatory)]
        [string]$LiteralPath,

        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-F]{64}$')]
        [string]$FinalReportSha256,

        [Parameter(Mandatory)]
        $Candidate,

        [switch]$AllowMissingSidecar
    )

    $hash = Get-ComoteSha256 -LiteralPath $LiteralPath
    if (-not $AllowMissingSidecar.IsPresent) {
        Assert-ComoteExternalHash `
            -LiteralPath $LiteralPath `
            -ExpectedSha256 $hash `
            -Description "Promoted role index"
    }
    $index = Read-ComoteJson `
        -LiteralPath $LiteralPath `
        -Description "Promoted role index"
    Assert-ComoteExactProperties `
        -InputObject $index `
        -Expected @(
            "schemaVersion",
            "releaseId",
            "status",
            "completedUtc",
            "sourceReleaseManifestSha256",
            "unifiedProvisionalReportSha256",
            "runtimePolicy",
            "candidateRoleIndexSha256",
            "finalReportSha256",
            "manager",
            "clientVirtualHid"
        ) `
        -Description "Promoted role index"
    if ([int]$index.schemaVersion -ne 1 -or
        [string]$index.releaseId -cne
            [string]$Candidate.Index.releaseId -or
        [string]$index.status -cne "promoted-role-packages" -or
        [string]$index.sourceReleaseManifestSha256 -cne
            [string]$Candidate.Index.sourceReleaseManifestSha256 -or
        [string]$index.unifiedProvisionalReportSha256 -cne
            [string]$Candidate.Index.unifiedProvisionalReportSha256 -or
        [string]$index.candidateRoleIndexSha256 -cne
            [string]$Candidate.IndexSha256 -or
        [string]$index.finalReportSha256 -cne $FinalReportSha256 -or
        ($index.runtimePolicy | ConvertTo-Json -Depth 8 -Compress) -cne
            ($Candidate.Index.runtimePolicy |
                ConvertTo-Json -Depth 8 -Compress) -or
        ($index.manager | ConvertTo-Json -Depth 8 -Compress) -cne
            ($Candidate.Index.manager |
                ConvertTo-Json -Depth 8 -Compress) -or
        ($index.clientVirtualHid |
            ConvertTo-Json -Depth 8 -Compress) -cne
            ($Candidate.Index.clientVirtualHid |
                ConvertTo-Json -Depth 8 -Compress)) {
        throw "The promoted role index is not an immutable candidate binding."
    }
    return [PSCustomObject]@{
        Path = $LiteralPath
        Sha256 = $hash
        Document = $index
    }
}

$expectedCandidateHash = $ExpectedCandidateIndexSha256.ToUpperInvariant()
$expectedFinalHash = $ExpectedFinalReportSha256.ToUpperInvariant()
$candidate = Assert-ComoteCandidateRoleOutput `
    -Root $CandidateDirectory `
    -ExpectedIndexSha256 $expectedCandidateHash `
    -ExpectedSourceManifestSha256 $null `
    -ExpectedProvisionalReportSha256 $null `
    -AllowPromotedIndexWithoutSidecar
[void](Assert-ComoteFinalPromotionReport `
    -LiteralPath $FinalReportPath `
    -ExpectedSha256 $expectedFinalHash `
    -Candidate $candidate)

$before = Get-ComoteCandidateImmutableHashes -Candidate $candidate
$promotedIndexPath = Join-Path `
    $candidate.Root `
    "PROMOTED-ROLE-INDEX.json"
if ((Test-Path -LiteralPath $promotedIndexPath -PathType Leaf) -and
    -not (Test-Path -LiteralPath "$promotedIndexPath.sha256" -PathType Leaf)) {
    $recoverable = Assert-ComotePromotedRoleIndex `
        -LiteralPath $promotedIndexPath `
        -FinalReportSha256 $expectedFinalHash `
        -Candidate $candidate `
        -AllowMissingSidecar
    $recoveredHash = Write-ComoteExternalHash `
        -LiteralPath $promotedIndexPath
    if ([string]$recoverable.Sha256 -cne $recoveredHash) {
        throw "The recovered promoted-index hash changed unexpectedly."
    }
    $recovered = Assert-ComotePromotedRoleIndex `
        -LiteralPath $promotedIndexPath `
        -FinalReportSha256 $expectedFinalHash `
        -Candidate $candidate
    [void](Assert-ComoteCandidateRoleOutput `
        -Root $candidate.Root `
        -ExpectedIndexSha256 $expectedCandidateHash `
        -ExpectedSourceManifestSha256 `
            ([string]$candidate.Index.sourceReleaseManifestSha256) `
        -ExpectedProvisionalReportSha256 `
            ([string]$candidate.Index.unifiedProvisionalReportSha256))
    Write-Host "Recovered the exact promoted-index checksum publication."
    Write-Host "Promoted index SHA-256: $($recovered.Sha256)"
    return
}
if (Test-Path -LiteralPath $promotedIndexPath -PathType Leaf) {
    $existing = Assert-ComotePromotedRoleIndex `
        -LiteralPath $promotedIndexPath `
        -FinalReportSha256 $expectedFinalHash `
        -Candidate $candidate
    Write-Host "Promoted role index already exists and is immutable."
    Write-Host "Promoted index SHA-256: $($existing.Sha256)"
    return
}
if (Test-Path -LiteralPath "$promotedIndexPath.sha256" -PathType Leaf) {
    throw "An orphaned promoted-index checksum blocks promotion."
}

$promotedIndex = [PSCustomObject][ordered]@{
    schemaVersion = 1
    releaseId = [string]$candidate.Index.releaseId
    status = "promoted-role-packages"
    completedUtc = [DateTime]::UtcNow.ToString("o")
    sourceReleaseManifestSha256 =
        [string]$candidate.Index.sourceReleaseManifestSha256
    unifiedProvisionalReportSha256 =
        [string]$candidate.Index.unifiedProvisionalReportSha256
    runtimePolicy = $candidate.Index.runtimePolicy
    candidateRoleIndexSha256 = [string]$candidate.IndexSha256
    finalReportSha256 = $expectedFinalHash
    manager = $candidate.Index.manager
    clientVirtualHid = $candidate.Index.clientVirtualHid
}
Write-ComoteJsonAtomically `
    -LiteralPath $promotedIndexPath `
    -InputObject $promotedIndex `
    -Depth 16
$promotedIndexHash = Write-ComoteExternalHash `
    -LiteralPath $promotedIndexPath
$promoted = Assert-ComotePromotedRoleIndex `
    -LiteralPath $promotedIndexPath `
    -FinalReportSha256 $expectedFinalHash `
    -Candidate $candidate
$afterCandidate = Assert-ComoteCandidateRoleOutput `
    -Root $candidate.Root `
    -ExpectedIndexSha256 $expectedCandidateHash `
    -ExpectedSourceManifestSha256 `
        ([string]$candidate.Index.sourceReleaseManifestSha256) `
    -ExpectedProvisionalReportSha256 `
        ([string]$candidate.Index.unifiedProvisionalReportSha256)
$after = Get-ComoteCandidateImmutableHashes -Candidate $afterCandidate
if (($before | ConvertTo-Json -Compress) -cne
    ($after | ConvertTo-Json -Compress)) {
    throw "Promotion modified candidate manifests or role ZIP bytes."
}
if ([string]$promoted.Sha256 -cne $promotedIndexHash) {
    throw "The promoted index changed after publication."
}
Write-Host "Candidate role bytes promoted without repackaging."
Write-Host "Promoted index: $promotedIndexPath"
Write-Host "Promoted index SHA-256: $promotedIndexHash"
