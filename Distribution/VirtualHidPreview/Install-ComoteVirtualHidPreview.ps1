#Requires -Version 5.1
[CmdletBinding()]
param(
    [switch]$AcknowledgeDisposableVm,

    [switch]$AcknowledgeTestSignedPreview,

    [AllowNull()]
    [string]$PreviewAcceptancePhrase,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedReleaseManifestSha256,

    [Parameter(Mandatory)]
    [ValidatePattern('^[^\\/:*?"<>|]{1,64}\\[^\\/:*?"<>|]{1,64}$')]
    [string]$ControllerUser
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot "VirtualHidPreview.Common.ps1")

function New-ComoteInstalledFilePlan {
    param(
        [Parameter(Mandatory)]
        $Release,

        [Parameter(Mandatory)]
        [string]$InstallRoot
    )

    $plans = @()
    foreach ($entry in @($Release.Entries)) {
        $sourceRelative = [string]$entry.path
        $destinationRelative = $null
        if ($sourceRelative.StartsWith(
                "App/",
                [StringComparison]::Ordinal)) {
            $destinationRelative = $sourceRelative
        }
        elseif ($sourceRelative.StartsWith(
                "THIRD_PARTY_NOTICES/",
                [StringComparison]::Ordinal)) {
            $destinationRelative = $sourceRelative
        }
        elseif ($sourceRelative -ceq
            "Driver/ComoteDriverInstaller.exe") {
            $destinationRelative =
                "Maintenance/ComoteDriverInstaller.exe"
        }
        elseif ($sourceRelative -ceq
            "Driver/package-manifest.txt") {
            $destinationRelative = "Maintenance/package-manifest.txt"
        }
        elseif ($sourceRelative -ceq
            "VirtualHidPreview.Common.ps1") {
            $destinationRelative =
                "Maintenance/VirtualHidPreview.Common.ps1"
        }
        elseif ($sourceRelative -ceq
            "Uninstall-ComoteVirtualHidPreview.ps1") {
            $destinationRelative =
                "Maintenance/Uninstall-ComoteVirtualHidPreview.ps1"
        }
        if ($null -ne $destinationRelative) {
            $plans += [PSCustomObject][ordered]@{
                SourceRelativePath = $sourceRelative
                SourcePath = Join-Path `
                    $Release.Root `
                    $sourceRelative.Replace('/', '\')
                RelativePath = $destinationRelative
                DestinationPath = Join-Path `
                    $InstallRoot `
                    $destinationRelative.Replace('/', '\')
                Length = [int64]$entry.length
                Sha256 = [string]$entry.sha256
            }
        }
    }

    $plans += [PSCustomObject][ordered]@{
        SourceRelativePath = $script:ComotePreviewManifestName
        SourcePath = $Release.Path
        RelativePath = "Maintenance/release-manifest.json"
        DestinationPath = Join-Path `
            $InstallRoot `
            "Maintenance\release-manifest.json"
        Length = [int64]$Release.Length
        Sha256 = [string]$Release.Sha256
    }
    return $plans
}

function Copy-ComoteInstalledFiles {
    param(
        [Parameter(Mandatory)]
        $Release,

        [Parameter(Mandatory)]
        [object[]]$Plans
    )

    foreach ($plan in $Plans) {
        $sourceIdentity = Assert-ComoteOrdinaryLocalPath `
            -LiteralPath ([string]$plan.SourcePath) `
            -Directory $false `
            -Description "Release install source"
        if ([int64]$sourceIdentity.Identity.Length -ne [int64]$plan.Length -or
            (Get-ComoteSha256 -LiteralPath $sourceIdentity.FullPath) -cne
                [string]$plan.Sha256) {
            throw "A release file changed after inventory verification."
        }
        if ([IO.File]::Exists([string]$plan.DestinationPath)) {
            throw "An install destination file already exists."
        }
        [IO.File]::Copy(
            $sourceIdentity.FullPath,
            [string]$plan.DestinationPath,
            $false
        )
        $destinationIdentity = Assert-ComoteOrdinaryLocalPath `
            -LiteralPath ([string]$plan.DestinationPath) `
            -Directory $false `
            -Description "Protected installed file"
        if ([int64]$destinationIdentity.Identity.Length -ne
                [int64]$plan.Length -or
            (Get-ComoteSha256 `
                -LiteralPath $destinationIdentity.FullPath) -cne
                [string]$plan.Sha256) {
            throw "A protected installed-file copy failed verification."
        }
        Assert-ComoteNoUntrustedWriteAcl `
            -LiteralPath $destinationIdentity.FullPath
    }
}

$release = Get-ComoteReleaseManifest `
    -PackageRoot $PSScriptRoot `
    -ExpectedManifestSha256 $ExpectedReleaseManifestSha256
Assert-ComoteReleaseInventory -Release $release
$environment = Assert-ComoteRoleInstallEnvironment `
    -PackageRole ([string]$release.Document.packageRole) `
    -AcknowledgeDisposableVm:$AcknowledgeDisposableVm `
    -AcknowledgeTestSignedPreview:$AcknowledgeTestSignedPreview `
    -PreviewAcceptancePhrase $PreviewAcceptancePhrase
$operationLock = Enter-ComotePreviewLock
$certificateInfo = $null
try {
    $driverManifest = Assert-ComoteDriverPackageBinding -Release $release
    $certificateInfo = Get-ComotePinnedCertificate -Release $release
    Assert-ComoteReleaseSigners -Release $release

    $paths = Get-ComotePreviewPaths
    if (Test-Path -LiteralPath $paths.StateRoot) {
        throw ("A wrapper state directory already exists. Use the exact " +
            "receipt-owned uninstaller or restore the clean snapshot.")
    }
    if (Test-Path -LiteralPath $paths.InstallRoot) {
        throw "The protected preview install root already exists."
    }
    if (@(Get-ComoteBrokerService).Count -ne 0) {
        throw "A ComoteInputBroker service already exists without this receipt."
    }

    $nativeInstallerPath = Join-Path `
        $release.Root `
        ([string]$release.Document.driver.installerPath).Replace('/', '\')
    $nativeManifestPath = $driverManifest.Path
    $nativePackagePath = Join-Path `
        $release.Root `
        ([string]$release.Document.driver.packageDirectory).Replace('/', '\')
    $nativeStatus = Invoke-ComoteNativeDriverInstaller `
        -Command status `
        -InstallerPath $nativeInstallerPath `
        -ManifestPath $nativeManifestPath
    if ($nativeStatus.ExitCode -ne 20 -or
        $nativeStatus.State -cne "not-installed") {
        throw ("Fresh installation requires native status 20/not-installed; " +
            "found " + $nativeStatus.ResultLine)
    }

    $controller = Get-ComoteInteractiveLocalUser `
        -ControllerUser $ControllerUser
    $groupState = Get-ComoteLocalGroup
    $groupOwned = ($null -eq $groupState.Group)
    $membershipOwned = $true
    if ($null -ne $groupState.Group) {
        $existingMemberSids = @(
            Get-ComoteLocalGroupMemberSids -Group $groupState.Group
        )
        if ($existingMemberSids.Count -gt 1 -or
            @(
                $existingMemberSids |
                    Where-Object { $_ -cne $controller.Sid }
            ).Count -ne 0) {
            throw ("The pre-existing Comote Input Controllers group may be " +
                "empty or contain only the exact ControllerUser SID.")
        }
        $membershipOwned = $existingMemberSids.Count -eq 0
    }

    $storePlans = @()
    foreach ($storeName in @("Root", "TrustedPublisher")) {
        $existingCopies = @(Get-ComoteCertificateFromStore `
            -StoreName $storeName `
            -Thumbprint $certificateInfo.Certificate.Thumbprint.ToUpperInvariant())
        if ($existingCopies.Count -gt 1) {
            throw "The pinned certificate is duplicated in $storeName."
        }
        foreach ($copy in $existingCopies) {
            $rawHash = [BitConverter]::ToString(
                [Security.Cryptography.SHA256]::Create().ComputeHash(
                    $copy.RawData
                )
            ).Replace("-", "")
            if ($rawHash -cne $certificateInfo.Sha256 -or
                [string]$copy.Subject -cne
                    [string]$certificateInfo.Certificate.Subject -or
                -not (Test-ComoteCodeSigningEku -Certificate $copy)) {
                throw "An existing certificate copy does not match the pin."
            }
        }
        $storePlans += [PSCustomObject][ordered]@{
            name = $storeName
            owned = ($existingCopies.Count -eq 0)
        }
    }

    $filePlans = @(New-ComoteInstalledFilePlan `
        -Release $release `
        -InstallRoot $paths.InstallRoot)
    $installedFiles = @(
        $filePlans |
            Sort-Object RelativePath |
            ForEach-Object {
                [PSCustomObject][ordered]@{
                    relativePath = [string]$_.RelativePath
                    length = [int64]$_.Length
                    sha256 = [string]$_.Sha256
                }
            }
    )
    $directorySet = Get-ComoteExpectedDirectorySet `
        -RelativeFiles @(
            $installedFiles |
                ForEach-Object { [string]$_.relativePath }
        )
    $installedDirectories = @(
        $directorySet |
            Sort-Object |
            ForEach-Object {
                [PSCustomObject][ordered]@{
                    relativePath = [string]$_
                }
            }
    )
    $brokerPlan = @(
        $filePlans |
            Where-Object {
                [string]$_.RelativePath -ceq
                    "App/Broker/Comote.InputBroker.exe"
            }
    )
    if ($brokerPlan.Count -ne 1) {
        throw "The Broker install plan is not exact."
    }

    $maintenanceInstaller = Join-Path `
        $paths.InstallRoot `
        "Maintenance\ComoteDriverInstaller.exe"
    $maintenanceManifest = Join-Path `
        $paths.InstallRoot `
        "Maintenance\package-manifest.txt"
    $receipt = [PSCustomObject][ordered]@{
        schemaVersion = 2
        releaseId = [string]$release.Document.releaseId
        packageRole = [string]$release.Document.packageRole
        releaseManifestSha256 = [string]$release.Sha256
        transactionId = [Guid]::NewGuid().ToString("N")
        status = "installing"
        createdUtc = [DateTime]::UtcNow.ToString("o")
        updatedUtc = [DateTime]::UtcNow.ToString("o")
        approval = $environment.Approval
        target = [PSCustomObject][ordered]@{
            manufacturer = [string]$environment.Computer.Manufacturer
            model = [string]$environment.Computer.Model
            productType =
                [int]$environment.OperatingSystem.ProductType
            architecture = "x64"
            buildNumber =
                [string]$environment.OperatingSystem.BuildNumber
            ubr = [int]$environment.Ubr
            editionSku =
                [int]$environment.OperatingSystem.OperatingSystemSKU
        }
        controller = [PSCustomObject][ordered]@{
            account = [string]$controller.Account
            sid = [string]$controller.Sid
        }
        ownership = [PSCustomObject][ordered]@{
            installRoot = $true
            stateRoot = $true
            group = [bool]$groupOwned
            membership = [bool]$membershipOwned
            brokerLogRoot =
                -not (Test-Path -LiteralPath $paths.BrokerLogRoot)
        }
        certificate = [PSCustomObject][ordered]@{
            thumbprint =
                $certificateInfo.Certificate.Thumbprint.ToUpperInvariant()
            subject = [string]$certificateInfo.Certificate.Subject
            sha256 = [string]$certificateInfo.Sha256
        }
        certificateStores = $storePlans
        service = [PSCustomObject][ordered]@{
            name = $script:ComotePreviewServiceName
            binaryPath = [string]$brokerPlan[0].DestinationPath
            binarySha256 = [string]$brokerPlan[0].Sha256
            createAttempted = $false
        }
        driver = [PSCustomObject][ordered]@{
            installAttempted = $false
            installerPath = $maintenanceInstaller
            installerSha256 =
                [string]$release.Document.driver.installerSha256
            manifestPath = $maintenanceManifest
            manifestSha256 = [string]$driverManifest.Sha256
            packagePath = $nativePackagePath
        }
        paths = [PSCustomObject][ordered]@{
            installRoot = [string]$paths.InstallRoot
            stateRoot = [string]$paths.StateRoot
            receiptPath = [string]$paths.ReceiptPath
            brokerLogRoot = [string]$paths.BrokerLogRoot
        }
        installedFiles = $installedFiles
        installedDirectories = $installedDirectories
    }

    [IO.Directory]::CreateDirectory($paths.StateRoot) | Out-Null
    Set-ComoteProtectedDirectoryAcl -LiteralPath $paths.StateRoot
    Write-ComoteReceipt -Receipt $receipt

    [IO.Directory]::CreateDirectory($paths.InstallRoot) | Out-Null
    Set-ComoteProtectedDirectoryAcl `
        -LiteralPath $paths.InstallRoot `
        -AllowUsersReadExecute
    foreach ($directory in $installedDirectories) {
        $directoryPath = Join-Path `
            $paths.InstallRoot `
            ([string]$directory.relativePath).Replace('/', '\')
        [IO.Directory]::CreateDirectory($directoryPath) | Out-Null
    }
    Copy-ComoteInstalledFiles -Release $release -Plans $filePlans
    Assert-ComoteOwnedInstallTree -Receipt $receipt -RequireComplete

    $brokerDataRoot = [IO.Path]::GetDirectoryName($paths.BrokerLogRoot)
    if (-not (Test-Path -LiteralPath $brokerDataRoot)) {
        [IO.Directory]::CreateDirectory($brokerDataRoot) | Out-Null
        Set-ComoteProtectedDirectoryAcl -LiteralPath $brokerDataRoot
    }
    else {
        [void](Assert-ComoteOrdinaryLocalPath `
            -LiteralPath $brokerDataRoot `
            -Directory $true `
            -Description "Broker data root")
        Assert-ComoteNoUntrustedWriteAcl -LiteralPath $brokerDataRoot
    }
    if (-not (Test-Path -LiteralPath $paths.BrokerLogRoot)) {
        [IO.Directory]::CreateDirectory($paths.BrokerLogRoot) | Out-Null
    }
    Set-ComoteProtectedDirectoryAcl -LiteralPath $paths.BrokerLogRoot

    Add-ComoteControllerGroupAndMember `
        -User $controller `
        -CreateGroup:$groupOwned `
        -AddMembership:$membershipOwned

    foreach ($storePlan in $storePlans) {
        if ([bool]$storePlan.owned) {
            Add-ComotePinnedCertificateToStore `
                -StoreName ([string]$storePlan.name) `
                -Certificate $certificateInfo.Certificate
        }
    }
    Assert-ComoteReleaseSigners -Release $release -RequireTrusted

    $receipt.driver.installAttempted = $true
    Write-ComoteReceipt -Receipt $receipt
    $installResult = Invoke-ComoteNativeDriverInstaller `
        -Command install `
        -InstallerPath $maintenanceInstaller `
        -ManifestPath $maintenanceManifest `
        -PackagePath $nativePackagePath
    if ($installResult.ExitCode -ne 0 -or
        $installResult.State -cne "installed") {
        throw "Native driver installation failed: $($installResult.ResultLine)"
    }
    $installedStatus = Invoke-ComoteNativeDriverInstaller `
        -Command status `
        -InstallerPath $maintenanceInstaller `
        -ManifestPath $maintenanceManifest
    if ($installedStatus.ExitCode -ne 0 -or
        $installedStatus.State -cne "installed") {
        throw "Native driver status verification failed."
    }

    $receipt.service.createAttempted = $true
    Write-ComoteReceipt -Receipt $receipt
    New-ComoteBrokerService `
        -BinaryPath ([string]$receipt.service.binaryPath) `
        -BinarySha256 ([string]$receipt.service.binarySha256)
    Start-ComoteBrokerService

    $receipt.status = "installed"
    Write-ComoteReceipt -Receipt $receipt
    Assert-ComoteOwnedInstallTree -Receipt $receipt -RequireComplete

    Write-Host ""
    Write-Host "Comote Virtual HID preview installed." -ForegroundColor Green
    Write-Host "Target: Windows 10 build 19045.$($environment.Ubr)"
    Write-Host "Controller: $($controller.Account)"
    Write-Host ("Sign out and sign in before launching Client so its token " +
        "contains the new controller-group SID.")
    Write-Host ("Use the protected App launchers. They open Manager Hub setup " +
        "in the UI; no access key or remote-task opt-in is placed on a command line.")
    Write-Host "TESTSIGNING, Secure Boot, and HVCI were not changed."
}
catch {
    $installError = $_
    $pathsForRollback = Get-ComotePreviewPaths
    if (Test-Path -LiteralPath $pathsForRollback.ReceiptPath -PathType Leaf) {
        try {
            $rollbackReceipt = Read-ComoteProtectedReceipt `
                -ExpectedReleaseManifestSha256 `
                    $ExpectedReleaseManifestSha256 `
                -ExpectedPackageRole `
                    ([string]$release.Document.packageRole)
            Invoke-ComoteReceiptOwnedRemoval -Receipt $rollbackReceipt
        }
        catch {
            throw ("Installation failed: {0} Exact rollback also failed: {1}. " +
                "Preserve the VM and protected receipt for recovery." -f
                $installError.Exception.Message,
                $_.Exception.Message)
        }
    }
    throw $installError
}
finally {
    if ($null -ne $certificateInfo -and
        $null -ne $certificateInfo.Certificate) {
        $certificateInfo.Certificate.Dispose()
    }
    Exit-ComotePreviewLock -Mutex $operationLock
}
