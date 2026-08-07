#Requires -Version 5.1

Set-StrictMode -Version Latest

$script:ComotePreviewGroupName = "Comote Input Controllers"
$script:ComotePreviewServiceName = "ComoteInputBroker"
$script:ComotePreviewServiceSddl =
    "D:P(A;;GA;;;SY)(A;;GA;;;BA)"
$script:ComotePreviewManifestName = "release-manifest.json"
$script:ComotePreviewReceiptName = "install-receipt.json"
$script:ComotePreviewCodeSigningOid = "1.3.6.1.5.5.7.3.3"
$script:ComotePreviewAcceptancePhrase =
    "I ACCEPT COMOTE TEST-SIGNED VIRTUAL HID PREVIEW"

function Get-ComoteSha256 {
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$LiteralPath
    )

    return (
        Get-FileHash `
            -Algorithm SHA256 `
            -LiteralPath $LiteralPath `
            -ErrorAction Stop
    ).Hash.ToUpperInvariant()
}

function Get-ComotePreviewPaths {
    $programFiles = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::ProgramFiles
    )
    $programData = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::CommonApplicationData
    )
    if ([string]::IsNullOrWhiteSpace($programFiles) -or
        [string]::IsNullOrWhiteSpace($programData)) {
        throw "Windows protected application paths could not be resolved."
    }

    $installRoot = Join-Path $programFiles "Comote\VirtualHidPreview"
    $stateRoot = Join-Path $programData "Comote\VirtualHidPreview"
    return [PSCustomObject]@{
        InstallRoot = $installRoot
        StateRoot = $stateRoot
        ReceiptPath = Join-Path $stateRoot $script:ComotePreviewReceiptName
        BrokerLogRoot = Join-Path $programData "ComoteInputBroker\Logs"
        NativeStatePath = Join-Path `
            $programData `
            "ComoteDriverInstaller\Phase2\phase2-installer.state"
    }
}

function Assert-ComoteExactProperties {
    param(
        [Parameter(Mandatory)]
        [PSObject]$InputObject,

        [Parameter(Mandatory)]
        [string[]]$Expected,

        [Parameter(Mandatory)]
        [string]$Description
    )

    $actual = @($InputObject.PSObject.Properties.Name)
    if ($actual.Count -ne $Expected.Count) {
        throw "$Description has an unexpected property count."
    }
    for ($index = 0; $index -lt $Expected.Count; $index++) {
        if ($actual[$index] -cne $Expected[$index]) {
            throw ("{0} property {1} must be exactly '{2}', found '{3}'." -f
                $Description,
                $index,
                $Expected[$index],
                $actual[$index])
        }
    }
}

function Assert-ComoteSafeRelativePath {
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$RelativePath
    )

    if ($RelativePath.Length -gt 240 -or
        $RelativePath -notmatch
            '^[A-Za-z0-9][A-Za-z0-9._ -]*(/[A-Za-z0-9][A-Za-z0-9._ -]*)*$' -or
        $RelativePath.Contains("..") -or
        $RelativePath.Contains("\") -or
        [IO.Path]::IsPathRooted($RelativePath)) {
        throw "Unsafe release-relative path: $RelativePath"
    }
}

function ConvertTo-ComoteNativeFinalPath {
    param(
        [Parameter(Mandatory)]
        [string]$LiteralPath
    )

    if ($LiteralPath.StartsWith(
            "\\?\UNC\",
            [StringComparison]::OrdinalIgnoreCase)) {
        return "\\" + $LiteralPath.Substring(8)
    }
    if ($LiteralPath.StartsWith(
            "\\?\",
            [StringComparison]::OrdinalIgnoreCase)) {
        return $LiteralPath.Substring(4)
    }
    return $LiteralPath
}

function Initialize-ComotePreviewNativeTypes {
    if ($null -ne ("Comote.VirtualHidPreview.NativeMethods" -as [Type])) {
        return
    }

    Add-Type -TypeDefinition @"
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Comote.VirtualHidPreview
{
    public sealed class FileIdentity
    {
        public string FinalPath { get; set; }
        public uint Attributes { get; set; }
        public uint NumberOfLinks { get; set; }
        public ulong Length { get; set; }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SystemCodeIntegrityInformation
    {
        public UInt32 Length;
        public UInt32 CodeIntegrityOptions;
    }

    public static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct BY_HANDLE_FILE_INFORMATION
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(
            SafeFileHandle file,
            out BY_HANDLE_FILE_INFORMATION information);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandleW(
            SafeFileHandle file,
            StringBuilder path,
            uint pathLength,
            uint flags);

        [DllImport("ntdll.dll")]
        public static extern Int32 NtQuerySystemInformation(
            Int32 systemInformationClass,
            ref SystemCodeIntegrityInformation systemInformation,
            Int32 systemInformationLength,
            out Int32 returnLength);

        public static FileIdentity Inspect(string path, bool directory)
        {
            const uint GENERIC_READ = 0x80000000;
            const uint FILE_READ_ATTRIBUTES = 0x80;
            const uint FILE_SHARE_READ = 0x1;
            const uint FILE_SHARE_WRITE = 0x2;
            const uint FILE_SHARE_DELETE = 0x4;
            const uint OPEN_EXISTING = 3;
            const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
            const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

            uint access = directory ? FILE_READ_ATTRIBUTES : GENERIC_READ;
            uint sharing = directory
                ? FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE
                : FILE_SHARE_READ;
            uint flags = FILE_FLAG_OPEN_REPARSE_POINT;
            if (directory)
            {
                flags |= FILE_FLAG_BACKUP_SEMANTICS;
            }

            using (SafeFileHandle handle = CreateFileW(
                path,
                access,
                sharing,
                IntPtr.Zero,
                OPEN_EXISTING,
                flags,
                IntPtr.Zero))
            {
                if (handle.IsInvalid)
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Unable to open a release object by handle.");
                }

                BY_HANDLE_FILE_INFORMATION information;
                if (!GetFileInformationByHandle(handle, out information))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Unable to query a release object identity.");
                }

                StringBuilder finalPath = new StringBuilder(32768);
                uint written = GetFinalPathNameByHandleW(
                    handle,
                    finalPath,
                    (uint)finalPath.Capacity,
                    0);
                if (written == 0 || written >= finalPath.Capacity)
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Unable to resolve a release object final path.");
                }

                return new FileIdentity
                {
                    FinalPath = finalPath.ToString(),
                    Attributes = information.FileAttributes,
                    NumberOfLinks = information.NumberOfLinks,
                    Length = ((ulong)information.FileSizeHigh << 32) |
                        information.FileSizeLow
                };
            }
        }
    }
}
"@
}

function Assert-ComoteOrdinaryLocalPath {
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$LiteralPath,

        [Parameter(Mandatory)]
        [bool]$Directory,

        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$Description
    )

    if ($LiteralPath.StartsWith("\\", [StringComparison]::Ordinal) -or
        $LiteralPath.StartsWith("\\?\", [StringComparison]::Ordinal) -or
        $LiteralPath.StartsWith("\\.\", [StringComparison]::Ordinal)) {
        throw "$Description must use a normal fixed local path."
    }
    $fullPath = [IO.Path]::GetFullPath($LiteralPath)
    if ($Directory) {
        if (-not (Test-Path -LiteralPath $fullPath -PathType Container)) {
            throw "$Description directory was not found: $fullPath"
        }
    }
    elseif (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "$Description file was not found: $fullPath"
    }

    $root = [IO.Path]::GetPathRoot($fullPath)
    if ($root -cnotmatch '^[A-Za-z]:\\$') {
        throw "$Description must resolve to a drive-letter path."
    }
    $drive = New-Object IO.DriveInfo($root)
    if (-not $drive.IsReady -or
        $drive.DriveType -ne [IO.DriveType]::Fixed -or
        [string]$drive.DriveFormat -cne "NTFS") {
        throw "$Description must be on a ready fixed NTFS local volume: $fullPath"
    }

    $cursor = if ($Directory) {
        $fullPath
    }
    else {
        [IO.Path]::GetDirectoryName($fullPath)
    }
    while (-not [string]::IsNullOrWhiteSpace($cursor)) {
        $item = Get-Item -LiteralPath $cursor -Force -ErrorAction Stop
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$Description has a reparse-point ancestor: $cursor"
        }
        if ($cursor.TrimEnd('\') -ieq $root.TrimEnd('\')) {
            break
        }
        $cursor = [IO.Path]::GetDirectoryName($cursor.TrimEnd('\'))
    }

    Initialize-ComotePreviewNativeTypes
    $identity = [Comote.VirtualHidPreview.NativeMethods]::Inspect(
        $fullPath,
        $Directory
    )
    $directoryAttribute = [uint32]0x10
    $reparseAttribute = [uint32]0x400
    if (($identity.Attributes -band $reparseAttribute) -ne 0 -or
        ($Directory -and
            ($identity.Attributes -band $directoryAttribute) -eq 0) -or
        (-not $Directory -and
            ($identity.Attributes -band $directoryAttribute) -ne 0)) {
        throw "$Description is not an ordinary requested object."
    }
    if (-not $Directory -and $identity.NumberOfLinks -ne 1) {
        throw "$Description must have exactly one hard-link name."
    }

    $finalPath = [IO.Path]::GetFullPath(
        (ConvertTo-ComoteNativeFinalPath -LiteralPath $identity.FinalPath)
    )
    if (-not $finalPath.Equals(
            $fullPath,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description final path differs from its requested path."
    }

    return [PSCustomObject]@{
        FullPath = $fullPath
        Identity = $identity
    }
}

function Assert-ComotePathBelowRoot {
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [Parameter(Mandatory)]
        [string]$Candidate,

        [Parameter(Mandatory)]
        [string]$Description
    )

    $normalizedRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    $normalizedCandidate = [IO.Path]::GetFullPath($Candidate)
    if (-not $normalizedCandidate.StartsWith(
            "$normalizedRoot\",
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description escaped its fixed root."
    }
    return $normalizedCandidate
}

function Get-ComoteRelativePath {
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [Parameter(Mandatory)]
        [string]$Child
    )

    $normalizedRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    $normalizedChild = Assert-ComotePathBelowRoot `
        -Root $normalizedRoot `
        -Candidate $Child `
        -Description "Release object"
    return $normalizedChild.Substring($normalizedRoot.Length + 1).
        Replace('\', '/')
}

function Get-ComoteExpectedDirectorySet {
    param(
        [Parameter(Mandatory)]
        [string[]]$RelativeFiles
    )

    $set = New-Object 'Collections.Generic.HashSet[string]'(
        [StringComparer]::Ordinal
    )
    foreach ($relativeFile in $RelativeFiles) {
        $cursor = $relativeFile
        while ($cursor.Contains("/")) {
            $cursor = $cursor.Substring(0, $cursor.LastIndexOf("/"))
            [void]$set.Add($cursor)
        }
    }
    return $set
}

function Read-ComoteJson {
    param(
        [Parameter(Mandatory)]
        [string]$LiteralPath,

        [Parameter(Mandatory)]
        [string]$Description,

        [ValidateRange(1, 4194304)]
        [int]$MaximumBytes = 1048576
    )

    $validated = Assert-ComoteOrdinaryLocalPath `
        -LiteralPath $LiteralPath `
        -Directory $false `
        -Description $Description
    if ([uint64]$validated.Identity.Length -gt [uint64]$MaximumBytes) {
        throw "$Description exceeds the size limit."
    }
    try {
        return [IO.File]::ReadAllText(
            $validated.FullPath,
            (New-Object Text.UTF8Encoding($false, $true))
        ) | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "$Description is not strict UTF-8 JSON: $($_.Exception.Message)"
    }
}

function Write-ComoteJsonAtomically {
    param(
        [Parameter(Mandatory)]
        [string]$LiteralPath,

        [Parameter(Mandatory)]
        [AllowNull()]
        $InputObject,

        [ValidateRange(2, 20)]
        [int]$Depth = 12
    )

    $fullPath = [IO.Path]::GetFullPath($LiteralPath)
    $directory = [IO.Path]::GetDirectoryName($fullPath)
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        throw "Atomic JSON parent directory does not exist: $directory"
    }
    $temporaryPath = Join-Path $directory (
        ".{0}.{1}.tmp" -f
        [IO.Path]::GetFileName($fullPath),
        [Guid]::NewGuid().ToString("N")
    )
    try {
        $json = ($InputObject | ConvertTo-Json -Depth $Depth) +
            [Environment]::NewLine
        $encoding = New-Object Text.UTF8Encoding($false)
        $stream = New-Object IO.FileStream(
            $temporaryPath,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None
        )
        try {
            $bytes = $encoding.GetBytes($json)
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Flush($true)
        }
        finally {
            $stream.Dispose()
        }

        if ([IO.File]::Exists($fullPath)) {
            [IO.File]::Replace($temporaryPath, $fullPath, $null, $true)
        }
        else {
            [IO.File]::Move($temporaryPath, $fullPath)
        }
    }
    finally {
        if ([IO.File]::Exists($temporaryPath)) {
            [IO.File]::Delete($temporaryPath)
        }
    }
}

function Get-ComoteReleaseManifest {
    param(
        [Parameter(Mandatory)]
        [string]$PackageRoot,

        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-Fa-f]{64}$')]
        [string]$ExpectedManifestSha256
    )

    $rootIdentity = Assert-ComoteOrdinaryLocalPath `
        -LiteralPath $PackageRoot `
        -Directory $true `
        -Description "Release package root"
    $manifestPath = Join-Path `
        $rootIdentity.FullPath `
        $script:ComotePreviewManifestName
    $manifestIdentity = Assert-ComoteOrdinaryLocalPath `
        -LiteralPath $manifestPath `
        -Directory $false `
        -Description "Release manifest"
    $manifestHash = Get-ComoteSha256 -LiteralPath $manifestIdentity.FullPath
    if ($manifestHash -cne $ExpectedManifestSha256.ToUpperInvariant()) {
        throw "The release manifest does not match the out-of-band SHA-256."
    }

    $document = Read-ComoteJson `
        -LiteralPath $manifestIdentity.FullPath `
        -Description "Release manifest"
    Assert-ComoteExactProperties `
        -InputObject $document `
        -Expected @(
            "schemaVersion",
            "releaseId",
            "packageRole",
            "target",
            "runtimePolicy",
            "driver",
            "cmt1Applications",
            "validationTools",
            "files"
        ) `
        -Description "Release manifest"
    $packageRole = [string]$document.packageRole
    if ([int]$document.schemaVersion -ne 1 -or
        [string]$document.releaseId -notmatch
            '^[A-Za-z0-9][A-Za-z0-9._-]{2,63}$' -or
        $packageRole -cnotin @(
            "validation-unified",
            "client-virtual-hid"
        )) {
        throw "The release manifest identity is invalid."
    }

    Assert-ComoteExactProperties `
        -InputObject $document.target `
        -Expected @(
            "hypervisor",
            "operatingSystem",
            "productType",
            "editionSkus",
            "architecture",
            "buildNumber",
            "anyUbr"
        ) `
        -Description "Release target"
    if ([string]$document.target.operatingSystem -cne "Windows 10 22H2" -or
        [int]$document.target.productType -ne 1 -or
        [string]$document.target.architecture -cne "x64" -or
        [string]$document.target.buildNumber -cne "19045" -or
        [bool]$document.target.anyUbr -ne $true) {
        throw "The release target is not the exact Windows 10 workstation."
    }
    if ($packageRole -ceq "validation-unified") {
        if ([string]$document.target.hypervisor -cne "VMware" -or
            @($document.target.editionSkus).Count -ne 4 -or
            [int]$document.target.editionSkus[0] -ne 98 -or
            [int]$document.target.editionSkus[1] -ne 99 -or
            [int]$document.target.editionSkus[2] -ne 100 -or
            [int]$document.target.editionSkus[3] -ne 101) {
            throw "The unified target is not the exact VMware/Home-family VM."
        }
    }
    elseif ([string]$document.target.hypervisor -cne "Any" -or
        @($document.target.editionSkus).Count -ne 0) {
        throw "The Client target must allow any hypervisor and workstation SKU."
    }

    Assert-ComoteExactProperties `
        -InputObject $document.runtimePolicy `
        -Expected @(
            "frameworkVersion",
            "runtimePackVersion",
            "coreclrLength",
            "coreclrSha256",
            "coreclrFileVersion",
            "coreclrProductVersion"
        ) `
        -Description "Release runtime policy"
    $expectedCoreclrVersion =
        "10,0,1026,32716 @Commit: f7d90799ce4ef09a0bb257852a57248d2a8fb8dd"
    if ([string]$document.runtimePolicy.frameworkVersion -cne "10.0.10" -or
        [string]$document.runtimePolicy.runtimePackVersion -cne "10.0.10" -or
        [int64]$document.runtimePolicy.coreclrLength -ne 4614952 -or
        [string]$document.runtimePolicy.coreclrSha256 -cne
            "58859F85A30CC71313B281898E7CFBDBB9ECCB95AE2A3F865329EFD47EBF31BB" -or
        [string]$document.runtimePolicy.coreclrFileVersion -cne
            $expectedCoreclrVersion -or
        [string]$document.runtimePolicy.coreclrProductVersion -cne
            $expectedCoreclrVersion) {
        throw "The release runtime policy is not the exact .NET 10.0.10 pin."
    }

    Assert-ComoteExactProperties `
        -InputObject $document.driver `
        -Expected @(
            "packageDirectory",
            "manifestPath",
            "manifestSha256",
            "installerPath",
            "installerSha256",
            "certificatePath",
            "certificateSha256",
            "certificateThumbprint",
            "certificateSubject"
        ) `
        -Description "Release driver"
    if ([string]$document.driver.packageDirectory -cne "Driver/Package" -or
        [string]$document.driver.manifestPath -cne
            "Driver/package-manifest.txt" -or
        [string]$document.driver.installerPath -cne
            "Driver/ComoteDriverInstaller.exe" -or
        [string]$document.driver.certificatePath -cne
            "Trust/ComotePhase2Test.cer") {
        throw "The release driver layout is not canonical."
    }
    foreach ($hashValue in @(
        [string]$document.driver.manifestSha256,
        [string]$document.driver.installerSha256,
        [string]$document.driver.certificateSha256
    )) {
        if ($hashValue -cnotmatch '^[0-9A-F]{64}$') {
            throw "A release driver SHA-256 is malformed."
        }
    }
    if ([string]$document.driver.certificateThumbprint -cnotmatch
            '^[0-9A-F]{40}$' -or
        [string]::IsNullOrWhiteSpace(
            [string]$document.driver.certificateSubject)) {
        throw "The release certificate identity is malformed."
    }

    $entries = @($document.files)
    if ($entries.Count -lt 12 -or $entries.Count -gt 256) {
        throw "The release file inventory count is outside policy."
    }
    $entryMap = New-Object `
        'Collections.Generic.Dictionary[string,object]' `
        ([StringComparer]::Ordinal)
    foreach ($entry in $entries) {
        Assert-ComoteExactProperties `
            -InputObject $entry `
            -Expected @("path", "length", "sha256") `
            -Description "Release file entry"
        $relativePath = [string]$entry.path
        Assert-ComoteSafeRelativePath -RelativePath $relativePath
        if ($relativePath -ceq $script:ComotePreviewManifestName -or
            [int64]$entry.length -le 0 -or
            [string]$entry.sha256 -cnotmatch '^[0-9A-F]{64}$' -or
            $entryMap.ContainsKey($relativePath)) {
            throw "A release file entry is invalid or duplicated: $relativePath"
        }
        $entryMap.Add($relativePath, $entry)
    }

    $requiredPaths = @(
        "START HERE - Client Virtual HID.cmd",
        "Invoke-ComoteClientRoleUac.ps1",
        "App/Client/ComoteClient.exe",
        "App/Client/Start Comote Client Virtual HID.cmd",
        "App/Broker/Comote.InputBroker.exe",
        "Driver/Package/ComoteVirtualHidPhase2.inf",
        "Driver/Package/ComoteVirtualHidPhase2.cat",
        "Driver/Package/ComoteVirtualHidPhase2.sys",
        "Driver/package-manifest.txt",
        "Driver/ComoteDriverInstaller.exe",
        "Trust/ComotePhase2Test.cer",
        "THIRD_PARTY_NOTICES/DOTNET_DIRECT_DEPENDENCIES.md",
        "THIRD_PARTY_NOTICES/NUGET_SBOM.json",
        "THIRD_PARTY_NOTICES/FFMPEG.md",
        "THIRD_PARTY_NOTICES/FFMPEG_ASSET_RECEIPT.json",
        "THIRD_PARTY_NOTICES/FFmpeg/LICENSE.LGPLv3.txt",
        "THIRD_PARTY_NOTICES/FFmpeg/LICENSE.SIPSorceryMedia.FFmpeg.LGPL-2.1.txt",
        "THIRD_PARTY_NOTICES/FFmpeg/LICENSE.FFmpeg.AutoGen.MIT.txt",
        "THIRD_PARTY_NOTICES/FFmpeg/NOTICE.md",
        "THIRD_PARTY_NOTICES/FFmpeg/SOURCE_OFFER.md",
        "THIRD_PARTY_NOTICES/FFmpeg/manifest.json",
        "App/Client/ThirdParty/FFmpeg/LICENSE.LGPLv3.txt",
        "App/Client/ThirdParty/FFmpeg/LICENSE.SIPSorceryMedia.FFmpeg.LGPL-2.1.txt",
        "App/Client/ThirdParty/FFmpeg/LICENSE.FFmpeg.AutoGen.MIT.txt",
        "App/Client/ThirdParty/FFmpeg/NOTICE.md",
        "App/Client/ThirdParty/FFmpeg/SOURCE_OFFER.md",
        "App/Client/ThirdParty/FFmpeg/manifest.json",
        "Install-ComoteVirtualHidPreview.ps1",
        "Uninstall-ComoteVirtualHidPreview.ps1",
        "VirtualHidPreview.Common.ps1",
        "README.md"
    )
    if ($packageRole -ceq "validation-unified") {
        $requiredPaths += @(
            "Verify-ComoteManagerRole.ps1",
            "Validation/REGRESSION_GATE.json",
            "Validation/MEDIA_GATE.json",
            "Validation/Comote.MediaGate.exe",
            "Validation/NuGetLocks/ApiCheck/packages.lock.json",
            "Validation/NuGetLocks/InputCore.SelfTest/packages.lock.json",
            "Validation/NuGetLocks/InputCore/packages.lock.json",
            "Validation/NuGetLocks/HostInputSelfTest/packages.lock.json",
            "Validation/NuGetLocks/InputBroker/packages.lock.json",
            "Validation/NuGetLocks/ViewerLifecycleSelfTest/packages.lock.json",
            "Validation/NuGetLocks/RemoteFileSenderSelfTest/packages.lock.json",
            "Validation/NuGetLocks/SecureChannelSelfTest/packages.lock.json",
            "Validation/NuGetLocks/HubTransportSelfTest/packages.lock.json",
            "Validation/NuGetLocks/Host/packages.lock.json",
            "Validation/NuGetLocks/Viewer/packages.lock.json",
            "Validation/NuGetLocks/Driver/FinalValidation/VirtualHidE2E/packages.lock.json",
            "Validation/NuGetLocks/Distribution/VirtualHidPreview/MediaGate/packages.lock.json",
            "START HERE - Manager Hub.cmd",
            "App/Manager/ComoteManager.exe",
            "App/Manager/Start Comote Manager Hub.cmd",
            "App/Manager/ThirdParty/FFmpeg/LICENSE.LGPLv3.txt",
            "App/Manager/ThirdParty/FFmpeg/LICENSE.SIPSorceryMedia.FFmpeg.LGPL-2.1.txt",
            "App/Manager/ThirdParty/FFmpeg/LICENSE.FFmpeg.AutoGen.MIT.txt",
            "App/Manager/ThirdParty/FFmpeg/NOTICE.md",
            "App/Manager/ThirdParty/FFmpeg/SOURCE_OFFER.md",
            "App/Manager/ThirdParty/FFmpeg/manifest.json"
        )
    }
    else {
        $requiredPaths += @(
            "INSTALL CLIENT (Administrator).cmd",
            "UNINSTALL CLIENT (Administrator).cmd",
            "ROLE_README.txt"
        )
    }
    foreach ($requiredPath in $requiredPaths) {
        if (-not $entryMap.ContainsKey($requiredPath)) {
            throw "The release inventory is missing: $requiredPath"
        }
    }

    $applications = @($document.cmt1Applications)
    if ($packageRole -ceq "validation-unified") {
        $expectedApplications = @(
            @("client", "App/Client/ComoteClient.exe", "Host.dll"),
            @("manager", "App/Manager/ComoteManager.exe", "Viewer.dll"),
            @("broker", "App/Broker/Comote.InputBroker.exe",
                "Comote.InputBroker.dll")
        )
    }
    else {
        $expectedApplications = @(
            @("client", "App/Client/ComoteClient.exe", "Host.dll"),
            @("broker", "App/Broker/Comote.InputBroker.exe",
                "Comote.InputBroker.dll")
        )
    }
    if ($applications.Count -ne $expectedApplications.Count) {
        throw "The release package role has an invalid application count."
    }
    for ($index = 0; $index -lt $expectedApplications.Count; $index++) {
        $application = $applications[$index]
        Assert-ComoteExactProperties `
            -InputObject $application `
            -Expected @(
                "role",
                "path",
                "originalFilename",
                "sha256",
                "signerThumbprint"
            ) `
            -Description "CMT1 application"
        if ([string]$application.role -cne
                $expectedApplications[$index][0] -or
            [string]$application.path -cne
                $expectedApplications[$index][1] -or
            [string]$application.originalFilename -cne
                $expectedApplications[$index][2] -or
            -not $entryMap.ContainsKey([string]$application.path) -or
            [string]$application.sha256 -cne
                [string]$entryMap[[string]$application.path].sha256 -or
            [string]$application.signerThumbprint -cne
                [string]$document.driver.certificateThumbprint) {
            throw "A CMT1 application identity/hash/signer pin is invalid."
        }
    }

    $validationTools = @($document.validationTools)
    if ($packageRole -ceq "validation-unified") {
        if ($validationTools.Count -ne 1) {
            throw "The unified package must contain one validation tool."
        }
        $validationTool = $validationTools[0]
        Assert-ComoteExactProperties `
            -InputObject $validationTool `
            -Expected @(
                "role",
                "path",
                "originalFilename",
                "sha256",
                "signerThumbprint"
            ) `
            -Description "Validation tool"
        if ([string]$validationTool.role -cne "media-gate" -or
            [string]$validationTool.path -cne
                "Validation/Comote.MediaGate.exe" -or
            [string]$validationTool.originalFilename -cne
                "Comote.MediaGate.dll" -or
            -not $entryMap.ContainsKey([string]$validationTool.path) -or
            [string]$validationTool.sha256 -cne
                [string]$entryMap[[string]$validationTool.path].sha256 -or
            [string]$validationTool.signerThumbprint -cne
                [string]$document.driver.certificateThumbprint) {
            throw "The signed MediaGate validation tool binding is invalid."
        }
    }
    elseif ($validationTools.Count -ne 0) {
        throw "The Client role must not contain validation tools."
    }

    foreach ($driverBinding in @(
        @(
            [string]$document.driver.manifestPath,
            [string]$document.driver.manifestSha256
        ),
        @(
            [string]$document.driver.installerPath,
            [string]$document.driver.installerSha256
        ),
        @(
            [string]$document.driver.certificatePath,
            [string]$document.driver.certificateSha256
        )
    )) {
        if (-not $entryMap.ContainsKey($driverBinding[0]) -or
            [string]$entryMap[$driverBinding[0]].sha256 -cne
                $driverBinding[1]) {
            throw "A driver path/hash binding is invalid."
        }
    }

    return [PSCustomObject]@{
        Root = $rootIdentity.FullPath
        Path = $manifestIdentity.FullPath
        Sha256 = $manifestHash
        Length = [int64]$manifestIdentity.Identity.Length
        Document = $document
        Entries = $entries
        EntryMap = $entryMap
    }
}

function Assert-ComoteReleaseInventory {
    param(
        [Parameter(Mandatory)]
        $Release
    )

    $actualFileMap = New-Object `
        'Collections.Generic.Dictionary[string,object]' `
        ([StringComparer]::Ordinal)
    $actualDirectories = New-Object `
        'Collections.Generic.HashSet[string]' `
        ([StringComparer]::Ordinal)

    foreach ($item in @(
        Get-ChildItem `
            -LiteralPath $Release.Root `
            -Force `
            -Recurse `
            -ErrorAction Stop
    )) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "The release contains a reparse point: $($item.FullName)"
        }
        $relativePath = Get-ComoteRelativePath `
            -Root $Release.Root `
            -Child $item.FullName
        if ($item.PSIsContainer) {
            [void](Assert-ComoteOrdinaryLocalPath `
                -LiteralPath $item.FullName `
                -Directory $true `
                -Description "Release directory")
            [void]$actualDirectories.Add($relativePath)
            continue
        }

        $identity = Assert-ComoteOrdinaryLocalPath `
            -LiteralPath $item.FullName `
            -Directory $false `
            -Description "Release file"
        if ($actualFileMap.ContainsKey($relativePath)) {
            throw "The release contains a duplicate path: $relativePath"
        }
        $actualFileMap.Add($relativePath, $identity)
    }

    if (-not $actualFileMap.ContainsKey($script:ComotePreviewManifestName) -or
        $actualFileMap.Count -ne ($Release.Entries.Count + 1)) {
        throw "The release has an unexpected file count."
    }
    foreach ($entry in $Release.Entries) {
        $relativePath = [string]$entry.path
        if (-not $actualFileMap.ContainsKey($relativePath)) {
            throw "The release file is missing or mis-cased: $relativePath"
        }
        $identity = $actualFileMap[$relativePath]
        if ([int64]$identity.Identity.Length -ne [int64]$entry.length -or
            (Get-ComoteSha256 -LiteralPath $identity.FullPath) -cne
                [string]$entry.sha256) {
            throw "The release file size/hash is invalid: $relativePath"
        }
    }

    $expectedDirectories = Get-ComoteExpectedDirectorySet `
        -RelativeFiles @(
            @($Release.Entries | ForEach-Object { [string]$_.path }) +
            $script:ComotePreviewManifestName
        )
    if ($actualDirectories.Count -ne $expectedDirectories.Count) {
        throw "The release has an unexpected directory count."
    }
    foreach ($expectedDirectory in $expectedDirectories) {
        if (-not $actualDirectories.Contains($expectedDirectory)) {
            throw "The release directory is missing or mis-cased: $expectedDirectory"
        }
    }
}

function Read-ComotePhase2DriverManifest {
    param(
        [Parameter(Mandatory)]
        [string]$LiteralPath
    )

    $identity = Assert-ComoteOrdinaryLocalPath `
        -LiteralPath $LiteralPath `
        -Directory $false `
        -Description "Native driver manifest"
    $bytes = [IO.File]::ReadAllBytes($identity.FullPath)
    if ($bytes.Length -eq 0 -or $bytes.Length -gt 16384) {
        throw "The native driver manifest size is invalid."
    }
    foreach ($byte in $bytes) {
        if ($byte -gt 0x7F -or
            (($byte -lt 0x20) -and
                $byte -notin @(0x0A, 0x0D))) {
            throw "The native driver manifest is not strict ASCII text."
        }
    }
    $text = [Text.Encoding]::ASCII.GetString($bytes)
    if ($text.Contains("`r") -and $text -match '(?<!\r)\n|\r(?!\n)') {
        throw "The native driver manifest has mixed line endings."
    }
    $normalized = $text.Replace("`r`n", "`n").TrimEnd("`n")
    $lines = @($normalized.Split("`n"))
    $keys = @(
        "HardwareId",
        "RootInstanceId",
        "ServiceName",
        "Provider",
        "PackageFiles",
        "InfSize",
        "InfSha256",
        "CatSize",
        "CatSha256",
        "SysSize",
        "SysSha256"
    )
    if ($lines.Count -ne 12 -or
        $lines[0] -cne "COMOTE-PHASE2-PACKAGE-MANIFEST-V1") {
        throw "The native driver manifest header/count is invalid."
    }
    $values = @{}
    for ($index = 0; $index -lt $keys.Count; $index++) {
        $prefix = $keys[$index] + "="
        if (-not $lines[$index + 1].StartsWith(
                $prefix,
                [StringComparison]::Ordinal)) {
            throw "The native driver manifest order is invalid."
        }
        $values[$keys[$index]] = $lines[$index + 1].Substring(
            $prefix.Length
        )
    }
    if ($values.HardwareId -cne "ROOT\COMOTEVIRTUALHID_PHASE2" -or
        $values.RootInstanceId -cne
            "ROOT\COMOTEVIRTUALHID_PHASE2\COMOTE_PHASE2" -or
        $values.ServiceName -cne "ComoteVirtualHidPhase2" -or
        $values.Provider -cne "Comote" -or
        $values.PackageFiles -cne
            "ComoteVirtualHidPhase2.inf,ComoteVirtualHidPhase2.cat,ComoteVirtualHidPhase2.sys") {
        throw "The native driver manifest identities are invalid."
    }
    foreach ($sizeKey in @("InfSize", "CatSize", "SysSize")) {
        if ([string]$values[$sizeKey] -cnotmatch '^[1-9][0-9]*$') {
            throw "The native driver manifest size is invalid: $sizeKey"
        }
    }
    foreach ($hashKey in @("InfSha256", "CatSha256", "SysSha256")) {
        if ([string]$values[$hashKey] -cnotmatch '^[0-9A-F]{64}$') {
            throw "The native driver manifest hash is invalid: $hashKey"
        }
    }
    return [PSCustomObject]@{
        Path = $identity.FullPath
        Sha256 = Get-ComoteSha256 -LiteralPath $identity.FullPath
        Values = $values
    }
}

function Assert-ComoteDriverPackageBinding {
    param(
        [Parameter(Mandatory)]
        $Release
    )

    $manifestPath = Join-Path `
        $Release.Root `
        ([string]$Release.Document.driver.manifestPath).Replace('/', '\')
    $driverManifest = Read-ComotePhase2DriverManifest `
        -LiteralPath $manifestPath
    if ($driverManifest.Sha256 -cne
        [string]$Release.Document.driver.manifestSha256) {
        throw "The native driver manifest hash binding failed."
    }

    $packagePath = Join-Path `
        $Release.Root `
        ([string]$Release.Document.driver.packageDirectory).Replace('/', '\')
    [void](Assert-ComoteOrdinaryLocalPath `
        -LiteralPath $packagePath `
        -Directory $true `
        -Description "Native driver package")
    $actualNames = @(
        Get-ChildItem -LiteralPath $packagePath -Force |
            ForEach-Object { $_.Name }
    )
    $expectedNames = @(
        "ComoteVirtualHidPhase2.inf",
        "ComoteVirtualHidPhase2.cat",
        "ComoteVirtualHidPhase2.sys"
    )
    [Array]::Sort($actualNames, [StringComparer]::Ordinal)
    [Array]::Sort($expectedNames, [StringComparer]::Ordinal)
    if ($actualNames.Count -ne 3) {
        throw "The native driver package must contain exactly three files."
    }
    for ($index = 0; $index -lt 3; $index++) {
        if ($actualNames[$index] -cne $expectedNames[$index]) {
            throw "The native driver package inventory is not exact."
        }
    }

    $bindings = @(
        @("ComoteVirtualHidPhase2.inf", "InfSize", "InfSha256"),
        @("ComoteVirtualHidPhase2.cat", "CatSize", "CatSha256"),
        @("ComoteVirtualHidPhase2.sys", "SysSize", "SysSha256")
    )
    foreach ($binding in $bindings) {
        $path = Join-Path $packagePath $binding[0]
        $identity = Assert-ComoteOrdinaryLocalPath `
            -LiteralPath $path `
            -Directory $false `
            -Description "Native driver package file"
        if ([uint64]$identity.Identity.Length -ne
                [uint64]$driverManifest.Values[$binding[1]] -or
            (Get-ComoteSha256 -LiteralPath $path) -cne
                [string]$driverManifest.Values[$binding[2]]) {
            throw "The native driver manifest does not bind: $($binding[0])"
        }
    }
    return $driverManifest
}

function Test-ComoteCodeSigningEku {
    param(
        [Parameter(Mandatory)]
        [Security.Cryptography.X509Certificates.X509Certificate2]
        $Certificate
    )

    foreach ($extension in $Certificate.Extensions) {
        if ($extension -is
            [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]) {
            foreach ($oid in $extension.EnhancedKeyUsages) {
                if ([string]$oid.Value -eq
                    $script:ComotePreviewCodeSigningOid) {
                    return $true
                }
            }
        }
    }
    return $false
}

function Get-ComotePinnedCertificate {
    param(
        [Parameter(Mandatory)]
        $Release
    )

    $certificatePath = Join-Path `
        $Release.Root `
        ([string]$Release.Document.driver.certificatePath).Replace('/', '\')
    $identity = Assert-ComoteOrdinaryLocalPath `
        -LiteralPath $certificatePath `
        -Directory $false `
        -Description "Release test certificate"
    if ((Get-ComoteSha256 -LiteralPath $identity.FullPath) -cne
        [string]$Release.Document.driver.certificateSha256) {
        throw "The release test certificate SHA-256 is invalid."
    }
    $certificate = New-Object `
        Security.Cryptography.X509Certificates.X509Certificate2(
            $identity.FullPath
        )
    if ($certificate.HasPrivateKey -or
        $certificate.Thumbprint.ToUpperInvariant() -cne
            [string]$Release.Document.driver.certificateThumbprint -or
        [string]$certificate.Subject -cne
            [string]$Release.Document.driver.certificateSubject -or
        -not (Test-ComoteCodeSigningEku -Certificate $certificate) -or
        $certificate.NotBefore.ToUniversalTime() -gt [DateTime]::UtcNow -or
        $certificate.NotAfter.ToUniversalTime() -le [DateTime]::UtcNow) {
        $certificate.Dispose()
        throw "The release test certificate thumbprint/subject/EKU/time is invalid."
    }
    return [PSCustomObject]@{
        Path = $identity.FullPath
        Sha256 = Get-ComoteSha256 -LiteralPath $identity.FullPath
        Certificate = $certificate
    }
}

function Assert-ComotePinnedAuthenticodeSigner {
    param(
        [Parameter(Mandatory)]
        [string]$LiteralPath,

        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-F]{40}$')]
        [string]$Thumbprint,

        [Parameter(Mandatory)]
        [string]$Description,

        [switch]$RequireTrusted
    )

    $signature = Get-AuthenticodeSignature `
        -LiteralPath $LiteralPath `
        -ErrorAction Stop
    if ($null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.Thumbprint.ToUpperInvariant() -cne
            $Thumbprint) {
        throw "$Description signer thumbprint is not pinned."
    }
    $allowedStatuses = if ($RequireTrusted.IsPresent) {
        @([Management.Automation.SignatureStatus]::Valid)
    }
    else {
        @(
            [Management.Automation.SignatureStatus]::Valid,
            [Management.Automation.SignatureStatus]::NotTrusted
        )
    }
    if ($signature.Status -notin $allowedStatuses) {
        throw "$Description Authenticode signature is invalid: $($signature.Status)"
    }
    if ($RequireTrusted.IsPresent -and
        $signature.Status -ne
            [Management.Automation.SignatureStatus]::Valid) {
        throw "$Description is not trusted after pinned trust installation."
    }
}

function Assert-ComoteReleaseSigners {
    param(
        [Parameter(Mandatory)]
        $Release,

        [switch]$RequireTrusted
    )

    $thumbprint =
        [string]$Release.Document.driver.certificateThumbprint
    foreach ($application in @(
        @($Release.Document.cmt1Applications) +
        @($Release.Document.validationTools)
    )) {
        $applicationPath = Join-Path `
            $Release.Root `
            ([string]$application.path).Replace('/', '\')
        Assert-ComotePinnedAuthenticodeSigner `
            -LiteralPath $applicationPath `
            -Thumbprint $thumbprint `
            -Description ([string]$application.role + " application") `
            -RequireTrusted:$RequireTrusted

        $versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo(
            $applicationPath
        )
        if ([string]$versionInfo.OriginalFilename -cne
            [string]$application.originalFilename) {
            throw "The application role metadata is invalid: $($application.role)"
        }
    }

    $installerPath = Join-Path `
        $Release.Root `
        ([string]$Release.Document.driver.installerPath).Replace('/', '\')
    Assert-ComotePinnedAuthenticodeSigner `
        -LiteralPath $installerPath `
        -Thumbprint $thumbprint `
        -Description "Native driver installer" `
        -RequireTrusted:$RequireTrusted

    $catalogPath = Join-Path `
        $Release.Root `
        "Driver\Package\ComoteVirtualHidPhase2.cat"
    Assert-ComotePinnedAuthenticodeSigner `
        -LiteralPath $catalogPath `
        -Thumbprint $thumbprint `
        -Description "Native driver catalog" `
        -RequireTrusted:$RequireTrusted
}

function Assert-ComoteAdministrator {
    $principal = New-Object Security.Principal.WindowsPrincipal(
        [Security.Principal.WindowsIdentity]::GetCurrent()
    )
    if (-not $principal.IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Run elevated for this Comote preview operation."
    }
}

function Get-ComotePreviewTargetEnvironment {
    param(
        [Parameter(Mandatory)]
        [ValidateSet("validation-unified", "client-virtual-hid")]
        [string]$PackageRole
    )

    $computer = Get-CimInstance `
        -ClassName Win32_ComputerSystem `
        -ErrorAction Stop
    $operatingSystem = Get-CimInstance `
        -ClassName Win32_OperatingSystem `
        -ErrorAction Stop
    if ([int]$operatingSystem.ProductType -ne 1 -or
        [string]$operatingSystem.Caption -notmatch 'Windows 10' -or
        [string]$operatingSystem.OSArchitecture -notmatch '64' -or
        [string]$operatingSystem.BuildNumber -cne "19045") {
        throw "This preview requires Windows 10 22H2 workstation x64 build 19045."
    }
    if ($PackageRole -ceq "validation-unified" -and
        ([string]$computer.Manufacturer -cne "VMware, Inc." -or
            [string]$computer.Model -notmatch '^VMware' -or
            @(98, 99, 100, 101) -notcontains
                [int]$operatingSystem.OperatingSystemSKU)) {
        throw ("The unified package requires disposable VMware Windows 10 " +
            "Home-family validation target.")
    }
    $currentVersion = Get-ItemProperty `
        -LiteralPath "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion" `
        -ErrorAction Stop

    return [PSCustomObject]@{
        Computer = $computer
        OperatingSystem = $operatingSystem
        Ubr = [int]$currentVersion.UBR
    }
}

function Assert-ComotePreviewSecurityState {
    Initialize-ComotePreviewNativeTypes
    $information =
        New-Object Comote.VirtualHidPreview.SystemCodeIntegrityInformation
    $information.Length = [uint32](
        [Runtime.InteropServices.Marshal]::SizeOf($information)
    )
    $returnLength = 0
    $status =
        [Comote.VirtualHidPreview.NativeMethods]::NtQuerySystemInformation(
            103,
            [ref]$information,
            [int]$information.Length,
            [ref]$returnLength
        )
    if ($status -ne 0) {
        throw ("Unable to read active Code Integrity state: 0x{0:X8}" -f
            [uint32]$status)
    }
    if (-not [bool]($information.CodeIntegrityOptions -band 0x02)) {
        throw "The active boot session is not already in test-signing mode."
    }
    if ([bool]($information.CodeIntegrityOptions -band 0x400)) {
        throw "HVCI kernel-mode enforcement must already be inactive."
    }

    $secureBoot = $null
    try {
        $secureBoot = Confirm-SecureBootUEFI -ErrorAction Stop
    }
    catch [PlatformNotSupportedException] {
        $secureBoot = $false
    }
    catch {
        if ($_.Exception.Message -match '(?i)(not supported|unsupported)') {
            $secureBoot = $false
        }
        else {
            throw
        }
    }
    if ($secureBoot -ne $false) {
        throw "Secure Boot must already be disabled for this test-signed preview."
    }
}

function Get-ComoteRoleApproval {
    param(
        [Parameter(Mandatory)]
        [ValidateSet("validation-unified", "client-virtual-hid")]
        [string]$PackageRole,

        [switch]$AcknowledgeDisposableVm,

        [switch]$AcknowledgeTestSignedPreview,

        [AllowNull()]
        [string]$PreviewAcceptancePhrase
    )

    if ($PackageRole -ceq "validation-unified") {
        if (-not $AcknowledgeDisposableVm.IsPresent -or
            $AcknowledgeTestSignedPreview.IsPresent -or
            -not [string]::IsNullOrEmpty($PreviewAcceptancePhrase)) {
            throw "The unified package requires only -AcknowledgeDisposableVm."
        }
        return [PSCustomObject][ordered]@{
            kind = "switch"
            value = "AcknowledgeDisposableVm"
            acceptedUtc = [DateTime]::UtcNow.ToString("o")
        }
    }
    if ($AcknowledgeDisposableVm.IsPresent -or
        -not $AcknowledgeTestSignedPreview.IsPresent -or
        [string]$PreviewAcceptancePhrase -cne
            $script:ComotePreviewAcceptancePhrase) {
        throw ("The Client package requires -AcknowledgeTestSignedPreview and " +
            "the exact typed acceptance phrase.")
    }
    return [PSCustomObject][ordered]@{
        kind = "typed-phrase"
        value = $script:ComotePreviewAcceptancePhrase
        acceptedUtc = [DateTime]::UtcNow.ToString("o")
    }
}

function Assert-ComoteDisposableVmEnvironment {
    param(
        [Parameter(Mandatory)]
        [switch]$AcknowledgeDisposableVm,

        [switch]$RequireTestSigning
    )

    [void](Get-ComoteRoleApproval `
        -PackageRole "validation-unified" `
        -AcknowledgeDisposableVm:$AcknowledgeDisposableVm)
    Assert-ComoteAdministrator
    $environment = Get-ComotePreviewTargetEnvironment `
        -PackageRole "validation-unified"
    if ($RequireTestSigning.IsPresent) {
        Assert-ComotePreviewSecurityState
    }
    return $environment
}

function Assert-ComoteRoleInstallEnvironment {
    param(
        [Parameter(Mandatory)]
        [ValidateSet("validation-unified", "client-virtual-hid")]
        [string]$PackageRole,

        [switch]$AcknowledgeDisposableVm,

        [switch]$AcknowledgeTestSignedPreview,

        [AllowNull()]
        [string]$PreviewAcceptancePhrase
    )

    $approval = Get-ComoteRoleApproval `
        -PackageRole $PackageRole `
        -AcknowledgeDisposableVm:$AcknowledgeDisposableVm `
        -AcknowledgeTestSignedPreview:$AcknowledgeTestSignedPreview `
        -PreviewAcceptancePhrase $PreviewAcceptancePhrase
    Assert-ComoteAdministrator
    $environment = Get-ComotePreviewTargetEnvironment -PackageRole $PackageRole
    Assert-ComotePreviewSecurityState
    return [PSCustomObject]@{
        Computer = $environment.Computer
        OperatingSystem = $environment.OperatingSystem
        Ubr = [int]$environment.Ubr
        Approval = $approval
    }
}

function Assert-ComoteRoleCleanupEnvironment {
    param(
        [Parameter(Mandatory)]
        [ValidateSet("validation-unified", "client-virtual-hid")]
        [string]$PackageRole,

        [switch]$AcknowledgeDisposableVm,

        [switch]$AcknowledgeTestSignedPreview,

        [AllowNull()]
        [string]$PreviewAcceptancePhrase
    )

    if ($PackageRole -ceq "validation-unified") {
        if (-not $AcknowledgeDisposableVm.IsPresent -or
            $AcknowledgeTestSignedPreview.IsPresent -or
            -not [string]::IsNullOrEmpty($PreviewAcceptancePhrase)) {
            throw "Unified cleanup requires only -AcknowledgeDisposableVm."
        }
    }
    elseif ($AcknowledgeDisposableVm.IsPresent -or
        -not $AcknowledgeTestSignedPreview.IsPresent -or
        -not [string]::IsNullOrEmpty($PreviewAcceptancePhrase)) {
        throw "Client cleanup requires only -AcknowledgeTestSignedPreview."
    }
    Assert-ComoteAdministrator
    return Get-ComotePreviewTargetEnvironment -PackageRole $PackageRole
}

function Enter-ComotePreviewLock {
    $mutex = New-Object Threading.Mutex(
        $false,
        "Global\ComoteVirtualHidPreviewRelease"
    )
    $acquired = $false
    try {
        try {
            $acquired = $mutex.WaitOne(0)
        }
        catch [Threading.AbandonedMutexException] {
            $acquired = $true
        }
        if (-not $acquired) {
            throw "Another Comote preview release operation is active."
        }
        return $mutex
    }
    catch {
        if ($acquired) {
            try {
                $mutex.ReleaseMutex()
            }
            catch {
            }
        }
        $mutex.Dispose()
        throw
    }
}

function Exit-ComotePreviewLock {
    param(
        [Parameter(Mandatory)]
        [Threading.Mutex]$Mutex
    )

    try {
        $Mutex.ReleaseMutex()
    }
    finally {
        $Mutex.Dispose()
    }
}

function Set-ComoteProtectedDirectoryAcl {
    param(
        [Parameter(Mandatory)]
        [string]$LiteralPath,

        [switch]$AllowUsersReadExecute
    )

    if (-not (Test-Path -LiteralPath $LiteralPath -PathType Container)) {
        throw "Protected directory does not exist: $LiteralPath"
    }
    $systemSid = New-Object Security.Principal.SecurityIdentifier(
        [Security.Principal.WellKnownSidType]::LocalSystemSid,
        $null
    )
    $administratorsSid =
        New-Object Security.Principal.SecurityIdentifier(
            [Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid,
            $null
        )
    $usersSid = New-Object Security.Principal.SecurityIdentifier(
        [Security.Principal.WellKnownSidType]::BuiltinUsersSid,
        $null
    )
    $inheritance = (
        [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
        [Security.AccessControl.InheritanceFlags]::ObjectInherit
    )
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetOwner($administratorsSid)
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in @($systemSid, $administratorsSid)) {
        $rule = New-Object Security.AccessControl.FileSystemAccessRule(
            $sid,
            [Security.AccessControl.FileSystemRights]::FullControl,
            $inheritance,
            [Security.AccessControl.PropagationFlags]::None,
            [Security.AccessControl.AccessControlType]::Allow
        )
        [void]$acl.AddAccessRule($rule)
    }
    if ($AllowUsersReadExecute.IsPresent) {
        $rule = New-Object Security.AccessControl.FileSystemAccessRule(
            $usersSid,
            [Security.AccessControl.FileSystemRights]::ReadAndExecute,
            $inheritance,
            [Security.AccessControl.PropagationFlags]::None,
            [Security.AccessControl.AccessControlType]::Allow
        )
        [void]$acl.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $LiteralPath -AclObject $acl -ErrorAction Stop
    Assert-ComoteNoUntrustedWriteAcl -LiteralPath $LiteralPath
}

function Assert-ComoteNoUntrustedWriteAcl {
    param(
        [Parameter(Mandatory)]
        [string]$LiteralPath
    )

    $acl = Get-Acl -LiteralPath $LiteralPath -ErrorAction Stop
    $trustedSids = @(
        "S-1-5-18",
        "S-1-5-32-544"
    )
    try {
        $ownerSid = (New-Object Security.Principal.NTAccount(
            [string]$acl.Owner
        )).Translate(
            [Security.Principal.SecurityIdentifier]
        ).Value
    }
    catch {
        try {
            $ownerSid = (New-Object `
                Security.Principal.SecurityIdentifier(
                    [string]$acl.Owner
                )).Value
        }
        catch {
            throw "The protected-path owner could not be resolved: $LiteralPath"
        }
    }
    if ($trustedSids -notcontains $ownerSid) {
        throw "A non-administrator owns a protected path: $LiteralPath"
    }
    $dangerousRights = (
        [Security.AccessControl.FileSystemRights]::WriteData -bor
        [Security.AccessControl.FileSystemRights]::AppendData -bor
        [Security.AccessControl.FileSystemRights]::WriteExtendedAttributes -bor
        [Security.AccessControl.FileSystemRights]::WriteAttributes -bor
        [Security.AccessControl.FileSystemRights]::Delete -bor
        [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
        [Security.AccessControl.FileSystemRights]::ChangePermissions -bor
        [Security.AccessControl.FileSystemRights]::TakeOwnership
    )
    if (([Security.AccessControl.FileSystemRights]::ReadAndExecute -band
            $dangerousRights) -ne 0 -or
        ([Security.AccessControl.FileSystemRights]::Write -band
            $dangerousRights) -eq 0 -or
        ([Security.AccessControl.FileSystemRights]::Modify -band
            $dangerousRights) -eq 0 -or
        ([Security.AccessControl.FileSystemRights]::FullControl -band
            $dangerousRights) -eq 0) {
        throw "The protected-path mutation-rights mask is invalid."
    }
    foreach ($rule in $acl.Access) {
        if ($rule.AccessControlType -ne
                [Security.AccessControl.AccessControlType]::Allow -or
            ($rule.FileSystemRights -band $dangerousRights) -eq 0) {
            continue
        }
        try {
            $sid = $rule.IdentityReference.Translate(
                [Security.Principal.SecurityIdentifier]
            ).Value
        }
        catch {
            throw "An ACL identity could not be resolved: $LiteralPath"
        }
        if ($trustedSids -notcontains $sid) {
            throw "A non-administrator can modify a protected path: $LiteralPath"
        }
    }
}

function Get-ComoteInteractiveLocalUser {
    param(
        [Parameter(Mandatory)]
        [ValidatePattern('^[^\\/:*?"<>|]{1,64}\\[^\\/:*?"<>|]{1,64}$')]
        [string]$ControllerUser
    )

    $computer = Get-CimInstance `
        -ClassName Win32_ComputerSystem `
        -ErrorAction Stop
    if ([string]::IsNullOrWhiteSpace([string]$computer.UserName) -or
        -not $ControllerUser.Equals(
            [string]$computer.UserName,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "ControllerUser must explicitly name the current interactive user."
    }
    $parts = $ControllerUser.Split('\')
    if (-not $parts[0].Equals(
            $env:COMPUTERNAME,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "The preview controller must be an explicit local VM user."
    }
    $escapedName = $parts[1].Replace("'", "''")
    $escapedDomain = $parts[0].Replace("'", "''")
    $accounts = @(
        Get-CimInstance `
            -ClassName Win32_UserAccount `
            -Filter ("Domain='{0}' AND Name='{1}'" -f
                $escapedDomain,
                $escapedName) `
            -ErrorAction Stop
    )
    if ($accounts.Count -ne 1 -or
        -not [bool]$accounts[0].LocalAccount -or
        [bool]$accounts[0].Disabled) {
        throw "ControllerUser is not one enabled local account."
    }
    $account = New-Object Security.Principal.NTAccount(
        $parts[0],
        $parts[1]
    )
    $sid = $account.Translate(
        [Security.Principal.SecurityIdentifier]
    ).Value
    if ($sid -cne [string]$accounts[0].SID) {
        throw "ControllerUser SID resolution is inconsistent."
    }
    return [PSCustomObject]@{
        Account = "$($parts[0])\$($parts[1])"
        Name = $parts[1]
        Domain = $parts[0]
        Sid = $sid
        AdsiPath = "WinNT://$($parts[0])/$($parts[1]),user"
    }
}

function Get-ComoteLocalGroup {
    $computer = [ADSI]("WinNT://{0},computer" -f $env:COMPUTERNAME)
    $matches = @(
        $computer.Children |
            Where-Object {
                [string]$_.SchemaClassName -ieq "group" -and
                [string]$_.Name -ceq $script:ComotePreviewGroupName
            }
    )
    if ($matches.Count -gt 1) {
        throw "The local controller group identity is ambiguous."
    }
    return [PSCustomObject]@{
        Computer = $computer
        Group = if ($matches.Count -eq 1) { $matches[0] } else { $null }
    }
}

function Get-ComoteLocalGroupMemberSids {
    param(
        [Parameter(Mandatory)]
        $Group
    )

    $sids = @()
    foreach ($memberObject in @($Group.psbase.Invoke("Members"))) {
        $member = [ADSI]$memberObject
        $sidBytes = [byte[]]$member.psbase.Properties["objectSid"].Value
        $sids += (
            New-Object Security.Principal.SecurityIdentifier(
                $sidBytes,
                0
            )
        ).Value
    }
    return $sids
}

function Add-ComoteControllerGroupAndMember {
    param(
        [Parameter(Mandatory)]
        $User,

        [Parameter(Mandatory)]
        [bool]$CreateGroup,

        [Parameter(Mandatory)]
        [bool]$AddMembership
    )

    $state = Get-ComoteLocalGroup
    if ($CreateGroup) {
        if ($null -ne $state.Group) {
            throw "The controller group appeared after preflight."
        }
        $group = $state.Computer.Children.Add(
            "group",
            $script:ComotePreviewGroupName
        )
        $group.Put("Description", "Users allowed to control Comote virtual HID")
        $group.SetInfo()
        $state = Get-ComoteLocalGroup
    }
    if ($null -eq $state.Group) {
        throw "The controller group does not exist."
    }
    $memberSids = @(Get-ComoteLocalGroupMemberSids -Group $state.Group)
    if ($AddMembership) {
        if ($memberSids -contains $User.Sid) {
            throw "The controller membership appeared after preflight."
        }
        $state.Group.Add($User.AdsiPath)
        $memberSids = @(Get-ComoteLocalGroupMemberSids -Group $state.Group)
    }
    if ($memberSids.Count -ne 1 -or
        [string]$memberSids[0] -cne [string]$User.Sid) {
        throw "The controller group must contain only the intended user SID."
    }
}

function Get-ComoteCertificateFromStore {
    param(
        [Parameter(Mandatory)]
        [ValidateSet("Root", "TrustedPublisher")]
        [string]$StoreName,

        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-F]{40}$')]
        [string]$Thumbprint
    )

    $store = New-Object `
        Security.Cryptography.X509Certificates.X509Store(
            $StoreName,
            [Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine
        )
    try {
        $store.Open(
            [Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly
        )
        return @(
            $store.Certificates |
                Where-Object {
                    $_.Thumbprint.ToUpperInvariant() -ceq $Thumbprint
                } |
                ForEach-Object {
                    New-Object `
                        Security.Cryptography.X509Certificates.X509Certificate2(
                            $_
                        )
                }
        )
    }
    finally {
        $store.Close()
    }
}

function Add-ComotePinnedCertificateToStore {
    param(
        [Parameter(Mandatory)]
        [ValidateSet("Root", "TrustedPublisher")]
        [string]$StoreName,

        [Parameter(Mandatory)]
        [Security.Cryptography.X509Certificates.X509Certificate2]
        $Certificate
    )

    $existing = @(Get-ComoteCertificateFromStore `
        -StoreName $StoreName `
        -Thumbprint $Certificate.Thumbprint.ToUpperInvariant())
    if ($existing.Count -ne 0) {
        throw "The pinned certificate appeared in $StoreName after preflight."
    }
    $store = New-Object `
        Security.Cryptography.X509Certificates.X509Store(
            $StoreName,
            [Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine
        )
    try {
        $store.Open(
            [Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite
        )
        $store.Add($Certificate)
    }
    finally {
        $store.Close()
    }
    $added = @(Get-ComoteCertificateFromStore `
        -StoreName $StoreName `
        -Thumbprint $Certificate.Thumbprint.ToUpperInvariant())
    if ($added.Count -ne 1) {
        throw "The pinned certificate was not added exactly once to $StoreName."
    }
}

function Remove-ComotePinnedCertificateFromStore {
    param(
        [Parameter(Mandatory)]
        [ValidateSet("Root", "TrustedPublisher")]
        [string]$StoreName,

        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-F]{40}$')]
        [string]$Thumbprint,

        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-F]{64}$')]
        [string]$CertificateSha256,

        [Parameter(Mandatory)]
        [string]$Subject
    )

    $copies = @(Get-ComoteCertificateFromStore `
        -StoreName $StoreName `
        -Thumbprint $Thumbprint)
    if ($copies.Count -eq 0) {
        return
    }
    if ($copies.Count -ne 1) {
        throw "The pinned certificate is duplicated in $StoreName."
    }
    $copy = $copies[0]
    $rawHash = [BitConverter]::ToString(
        [Security.Cryptography.SHA256]::Create().ComputeHash($copy.RawData)
    ).Replace("-", "")
    if ($rawHash -cne $CertificateSha256 -or
        [string]$copy.Subject -cne $Subject -or
        -not (Test-ComoteCodeSigningEku -Certificate $copy)) {
        throw "The certificate in $StoreName does not match the receipt."
    }
    $store = New-Object `
        Security.Cryptography.X509Certificates.X509Store(
            $StoreName,
            [Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine
        )
    try {
        $store.Open(
            [Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite
        )
        $store.Remove($copy)
    }
    finally {
        $store.Close()
        $copy.Dispose()
    }
}

function Invoke-ComoteNativeProcess {
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,

        [string[]]$Arguments = @()
    )

    [void](Assert-ComoteOrdinaryLocalPath `
        -LiteralPath $FilePath `
        -Directory $false `
        -Description "Native executable")
    $priorPreference = $ErrorActionPreference
    $output = ""
    $exitCode = $null
    try {
        $ErrorActionPreference = "Continue"
        $global:LASTEXITCODE = $null
        $output = (& $FilePath @Arguments 2>&1 | Out-String)
        $exitCode = $global:LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $priorPreference
    }
    if ($null -eq $exitCode) {
        throw "Native process did not return an exit code: $FilePath"
    }
    return [PSCustomObject]@{
        ExitCode = [int]$exitCode
        Output = $output
    }
}

function Invoke-ComoteNativeDriverInstaller {
    param(
        [Parameter(Mandatory)]
        [ValidateSet("install", "status", "remove")]
        [string]$Command,

        [Parameter(Mandatory)]
        [string]$InstallerPath,

        [Parameter(Mandatory)]
        [string]$ManifestPath,

        [string]$PackagePath
    )

    $arguments = @($Command)
    if ($Command -eq "install") {
        if ([string]::IsNullOrWhiteSpace($PackagePath)) {
            throw "Native install requires the exact package path."
        }
        $arguments += @("--package", $PackagePath)
    }
    $arguments += @("--manifest", $ManifestPath)
    $result = Invoke-ComoteNativeProcess `
        -FilePath $InstallerPath `
        -Arguments $arguments
    $lines = @(
        $result.Output.Replace("`r`n", "`n").Split("`n") |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )
    if ($lines.Count -eq 0) {
        throw "The native driver installer returned no result line."
    }
    $lastLine = $lines[$lines.Count - 1]
    if ($lastLine -cnotmatch
        '^COMOTE_INSTALLER_RESULT code=([0-9]+) state=([a-z-]+) message="[^"\r\n]*"$') {
        throw "The native driver installer result line is malformed."
    }
    if ([int]$Matches[1] -ne $result.ExitCode) {
        throw "The native driver installer result code disagrees with its exit code."
    }
    return [PSCustomObject]@{
        ExitCode = $result.ExitCode
        State = $Matches[2]
        ResultLine = $lastLine
        Output = $result.Output
    }
}

function Get-ComoteBrokerService {
    return @(
        Get-CimInstance `
            -ClassName Win32_Service `
            -Filter ("Name='{0}'" -f $script:ComotePreviewServiceName) `
            -ErrorAction Stop
    )
}

function Assert-ComoteBrokerServiceIdentity {
    param(
        [Parameter(Mandatory)]
        $Service,

        [Parameter(Mandatory)]
        [string]$BinaryPath,

        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-F]{64}$')]
        [string]$BinarySha256
    )

    $expectedCommand = '"{0}"' -f [IO.Path]::GetFullPath($BinaryPath)
    if ([string]$Service.Name -cne $script:ComotePreviewServiceName -or
        [string]$Service.PathName -cne $expectedCommand -or
        [string]$Service.StartName -notin @(
            "LocalSystem",
            "NT AUTHORITY\LocalSystem"
        ) -or
        [string]$Service.StartMode -cne "Auto") {
        throw "The Broker service configuration differs from the receipt."
    }
    [void](Assert-ComoteOrdinaryLocalPath `
        -LiteralPath $BinaryPath `
        -Directory $false `
        -Description "Broker service binary")
    Assert-ComoteNoUntrustedWriteAcl -LiteralPath $BinaryPath
    if ((Get-ComoteSha256 -LiteralPath $BinaryPath) -cne $BinarySha256) {
        throw "The Broker service binary hash differs from the receipt."
    }
}

function New-ComoteBrokerService {
    param(
        [Parameter(Mandatory)]
        [string]$BinaryPath,

        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-F]{64}$')]
        [string]$BinarySha256
    )

    if (@(Get-ComoteBrokerService).Count -ne 0) {
        throw "The Broker service appeared after preflight."
    }
    Assert-ComoteNoUntrustedWriteAcl -LiteralPath $BinaryPath
    if ((Get-ComoteSha256 -LiteralPath $BinaryPath) -cne $BinarySha256) {
        throw "The Broker binary changed before service creation."
    }
    $creation = Invoke-CimMethod `
        -ClassName Win32_Service `
        -MethodName Create `
        -Arguments @{
            Name = $script:ComotePreviewServiceName
            DisplayName = "Comote Input Broker"
            PathName = ('"{0}"' -f [IO.Path]::GetFullPath($BinaryPath))
            ServiceType = [byte]16
            ErrorControl = [byte]1
            StartMode = "Automatic"
            StartName = "LocalSystem"
        } `
        -ErrorAction Stop
    if ([int]$creation.ReturnValue -ne 0) {
        throw "Broker service creation failed: $($creation.ReturnValue)"
    }

    $scPath = Join-Path $env:SystemRoot "System32\sc.exe"
    $aclResult = Invoke-ComoteNativeProcess `
        -FilePath $scPath `
        -Arguments @(
            "sdset",
            $script:ComotePreviewServiceName,
            $script:ComotePreviewServiceSddl
        )
    if ($aclResult.ExitCode -ne 0) {
        throw "The restrictive Broker service DACL could not be applied."
    }
    $showResult = Invoke-ComoteNativeProcess `
        -FilePath $scPath `
        -Arguments @("sdshow", $script:ComotePreviewServiceName)
    if ($showResult.ExitCode -ne 0 -or
        $showResult.Output.IndexOf(
            $script:ComotePreviewServiceSddl,
            [StringComparison]::Ordinal
        ) -lt 0) {
        throw "The restrictive Broker service DACL could not be verified."
    }

    $services = @(Get-ComoteBrokerService)
    if ($services.Count -ne 1) {
        throw "The Broker service was not created exactly once."
    }
    Assert-ComoteBrokerServiceIdentity `
        -Service $services[0] `
        -BinaryPath $BinaryPath `
        -BinarySha256 $BinarySha256
}

function Start-ComoteBrokerService {
    Start-Service `
        -Name $script:ComotePreviewServiceName `
        -ErrorAction Stop
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        Start-Sleep -Milliseconds 250
        $services = @(Get-ComoteBrokerService)
        if ($services.Count -eq 1 -and
            [string]$services[0].State -ceq "Running") {
            return
        }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "The Broker service did not reach Running state."
}

function Stop-ComoteBrokerServiceExact {
    param(
        [Parameter(Mandatory)]
        [string]$BinaryPath,

        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-F]{64}$')]
        [string]$BinarySha256
    )

    $services = @(Get-ComoteBrokerService)
    if ($services.Count -eq 0) {
        return
    }
    if ($services.Count -ne 1) {
        throw "The Broker service identity is ambiguous."
    }
    Assert-ComoteBrokerServiceIdentity `
        -Service $services[0] `
        -BinaryPath $BinaryPath `
        -BinarySha256 $BinarySha256
    if ([string]$services[0].State -ne "Stopped") {
        Stop-Service `
            -Name $script:ComotePreviewServiceName `
            -Force `
            -ErrorAction Stop
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        do {
            Start-Sleep -Milliseconds 250
            $services = @(Get-ComoteBrokerService)
            if ($services.Count -eq 1 -and
                [string]$services[0].State -ceq "Stopped") {
                break
            }
        } while ([DateTime]::UtcNow -lt $deadline)
        if ($services.Count -ne 1 -or
            [string]$services[0].State -cne "Stopped") {
            throw "The Broker service did not stop."
        }
    }
}

function Delete-ComoteBrokerServiceExact {
    param(
        [Parameter(Mandatory)]
        [string]$BinaryPath,

        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-F]{64}$')]
        [string]$BinarySha256
    )

    $services = @(Get-ComoteBrokerService)
    if ($services.Count -eq 0) {
        return
    }
    if ($services.Count -ne 1) {
        throw "The Broker service identity is ambiguous."
    }
    Assert-ComoteBrokerServiceIdentity `
        -Service $services[0] `
        -BinaryPath $BinaryPath `
        -BinarySha256 $BinarySha256
    if ([string]$services[0].State -cne "Stopped") {
        throw "The receipt-owned Broker service is not stopped."
    }
    $scPath = Join-Path $env:SystemRoot "System32\sc.exe"
    $deleteResult = Invoke-ComoteNativeProcess `
        -FilePath $scPath `
        -Arguments @("delete", $script:ComotePreviewServiceName)
    if ($deleteResult.ExitCode -ne 0) {
        throw "The receipt-owned Broker service could not be deleted."
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        Start-Sleep -Milliseconds 250
        if (@(Get-ComoteBrokerService).Count -eq 0) {
            return
        }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "The Broker service remains pending deletion."
}

function Stop-AndDeleteComoteBrokerService {
    param(
        [Parameter(Mandatory)]
        [string]$BinaryPath,

        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-F]{64}$')]
        [string]$BinarySha256
    )

    Stop-ComoteBrokerServiceExact `
        -BinaryPath $BinaryPath `
        -BinarySha256 $BinarySha256
    Delete-ComoteBrokerServiceExact `
        -BinaryPath $BinaryPath `
        -BinarySha256 $BinarySha256
}

function Assert-ComoteReceiptSchema {
    param(
        [Parameter(Mandatory)]
        [PSObject]$Receipt
    )

    Assert-ComoteExactProperties `
        -InputObject $Receipt `
        -Expected @(
            "schemaVersion",
            "releaseId",
            "packageRole",
            "releaseManifestSha256",
            "transactionId",
            "status",
            "createdUtc",
            "updatedUtc",
            "approval",
            "target",
            "controller",
            "ownership",
            "certificate",
            "certificateStores",
            "service",
            "driver",
            "paths",
            "installedFiles",
            "installedDirectories"
        ) `
        -Description "Protected install receipt"
    $packageRole = [string]$Receipt.packageRole
    if ([int]$Receipt.schemaVersion -ne 2 -or
        [string]$Receipt.releaseId -notmatch
            '^[A-Za-z0-9][A-Za-z0-9._-]{2,63}$' -or
        $packageRole -cnotin @(
            "validation-unified",
            "client-virtual-hid"
        ) -or
        [string]$Receipt.releaseManifestSha256 -cnotmatch
            '^[0-9A-F]{64}$' -or
        [string]$Receipt.transactionId -cnotmatch
            '^[0-9a-f]{32}$' -or
        [string]$Receipt.status -notin @(
            "installing",
            "installed",
            "uninstalling",
            "recovery-required"
        )) {
        throw "The protected install receipt identity/status is invalid."
    }
    Assert-ComoteExactProperties `
        -InputObject $Receipt.approval `
        -Expected @("kind", "value", "acceptedUtc") `
        -Description "Receipt preview approval"
    if ([string]$Receipt.approval.acceptedUtc -cnotmatch
            '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}Z$' -or
        ($packageRole -ceq "validation-unified" -and
            ([string]$Receipt.approval.kind -cne "switch" -or
                [string]$Receipt.approval.value -cne
                    "AcknowledgeDisposableVm")) -or
        ($packageRole -ceq "client-virtual-hid" -and
            ([string]$Receipt.approval.kind -cne "typed-phrase" -or
                [string]$Receipt.approval.value -cne
                    $script:ComotePreviewAcceptancePhrase))) {
        throw "The protected receipt preview approval is invalid."
    }
    Assert-ComoteExactProperties `
        -InputObject $Receipt.target `
        -Expected @(
            "manufacturer",
            "model",
            "productType",
            "architecture",
            "buildNumber",
            "ubr",
            "editionSku"
        ) `
        -Description "Receipt target"
    if ([string]::IsNullOrWhiteSpace(
            [string]$Receipt.target.manufacturer) -or
        [string]::IsNullOrWhiteSpace([string]$Receipt.target.model) -or
        [int]$Receipt.target.productType -ne 1 -or
        [string]$Receipt.target.architecture -cne "x64" -or
        [string]$Receipt.target.buildNumber -cne "19045" -or
        [int]$Receipt.target.ubr -lt 0 -or
        ($packageRole -ceq "validation-unified" -and
            ([string]$Receipt.target.manufacturer -cne "VMware, Inc." -or
                [string]$Receipt.target.model -notmatch '^VMware' -or
                @(98, 99, 100, 101) -notcontains
                    [int]$Receipt.target.editionSku))) {
        throw "The protected receipt target is invalid for its package role."
    }
    Assert-ComoteExactProperties `
        -InputObject $Receipt.controller `
        -Expected @("account", "sid") `
        -Description "Receipt controller"
    Assert-ComoteExactProperties `
        -InputObject $Receipt.ownership `
        -Expected @(
            "installRoot",
            "stateRoot",
            "group",
            "membership",
            "brokerLogRoot"
        ) `
        -Description "Receipt ownership"
    Assert-ComoteExactProperties `
        -InputObject $Receipt.certificate `
        -Expected @("thumbprint", "subject", "sha256") `
        -Description "Receipt certificate"
    foreach ($store in @($Receipt.certificateStores)) {
        Assert-ComoteExactProperties `
            -InputObject $store `
            -Expected @("name", "owned") `
            -Description "Receipt certificate store"
        if ([string]$store.name -notin @("Root", "TrustedPublisher")) {
            throw "The receipt names an unsupported certificate store."
        }
    }
    if (@($Receipt.certificateStores).Count -ne 2) {
        throw "The receipt must describe two certificate stores."
    }
    Assert-ComoteExactProperties `
        -InputObject $Receipt.service `
        -Expected @(
            "name",
            "binaryPath",
            "binarySha256",
            "createAttempted"
        ) `
        -Description "Receipt service"
    Assert-ComoteExactProperties `
        -InputObject $Receipt.driver `
        -Expected @(
            "installAttempted",
            "installerPath",
            "installerSha256",
            "manifestPath",
            "manifestSha256",
            "packagePath"
        ) `
        -Description "Receipt driver"
    Assert-ComoteExactProperties `
        -InputObject $Receipt.paths `
        -Expected @(
            "installRoot",
            "stateRoot",
            "receiptPath",
            "brokerLogRoot"
        ) `
        -Description "Receipt paths"
    foreach ($file in @($Receipt.installedFiles)) {
        Assert-ComoteExactProperties `
            -InputObject $file `
            -Expected @("relativePath", "length", "sha256") `
            -Description "Receipt installed file"
        Assert-ComoteSafeRelativePath `
            -RelativePath ([string]$file.relativePath)
        if ([int64]$file.length -le 0 -or
            [string]$file.sha256 -cnotmatch '^[0-9A-F]{64}$') {
            throw "A receipt installed-file entry is invalid."
        }
    }
    foreach ($directory in @($Receipt.installedDirectories)) {
        Assert-ComoteExactProperties `
            -InputObject $directory `
            -Expected @("relativePath") `
            -Description "Receipt installed directory"
        Assert-ComoteSafeRelativePath `
            -RelativePath ([string]$directory.relativePath)
    }
}

function Write-ComoteReceipt {
    param(
        [Parameter(Mandatory)]
        [PSObject]$Receipt
    )

    $Receipt.updatedUtc = [DateTime]::UtcNow.ToString("o")
    Assert-ComoteReceiptSchema -Receipt $Receipt
    Write-ComoteJsonAtomically `
        -LiteralPath ([string]$Receipt.paths.receiptPath) `
        -InputObject $Receipt `
        -Depth 16
    Assert-ComoteNoUntrustedWriteAcl `
        -LiteralPath ([string]$Receipt.paths.receiptPath)
}

function Read-ComoteProtectedReceipt {
    param(
        [Parameter(Mandatory)]
        [ValidatePattern('^[0-9A-Fa-f]{64}$')]
        [string]$ExpectedReleaseManifestSha256,

        [Parameter(Mandatory)]
        [ValidateSet("validation-unified", "client-virtual-hid")]
        [string]$ExpectedPackageRole
    )

    $paths = Get-ComotePreviewPaths
    [void](Assert-ComoteOrdinaryLocalPath `
        -LiteralPath $paths.StateRoot `
        -Directory $true `
        -Description "Preview state directory")
    Assert-ComoteNoUntrustedWriteAcl -LiteralPath $paths.StateRoot
    $stateItems = @(
        Get-ChildItem -LiteralPath $paths.StateRoot -Force -ErrorAction Stop
    )
    if ($stateItems.Count -ne 1 -or
        $stateItems[0].Name -cne $script:ComotePreviewReceiptName -or
        $stateItems[0].PSIsContainer) {
        throw "The protected state directory inventory is not exact."
    }
    [void](Assert-ComoteOrdinaryLocalPath `
        -LiteralPath $paths.ReceiptPath `
        -Directory $false `
        -Description "Protected install receipt")
    Assert-ComoteNoUntrustedWriteAcl -LiteralPath $paths.ReceiptPath
    $receipt = Read-ComoteJson `
        -LiteralPath $paths.ReceiptPath `
        -Description "Protected install receipt"
    Assert-ComoteReceiptSchema -Receipt $receipt
    if ([string]$receipt.releaseManifestSha256 -cne
            $ExpectedReleaseManifestSha256.ToUpperInvariant() -or
        [string]$receipt.packageRole -cne $ExpectedPackageRole) {
        throw "The protected receipt does not match the expected release."
    }
    foreach ($binding in @(
        @([string]$receipt.paths.installRoot, $paths.InstallRoot),
        @([string]$receipt.paths.stateRoot, $paths.StateRoot),
        @([string]$receipt.paths.receiptPath, $paths.ReceiptPath),
        @([string]$receipt.paths.brokerLogRoot, $paths.BrokerLogRoot)
    )) {
        if (-not [IO.Path]::GetFullPath($binding[0]).Equals(
                [IO.Path]::GetFullPath($binding[1]),
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "The protected receipt contains a noncanonical path."
        }
    }
    return $receipt
}

function Assert-ComoteOwnedInstallTree {
    param(
        [Parameter(Mandatory)]
        [PSObject]$Receipt,

        [switch]$RequireComplete
    )

    $root = [string]$Receipt.paths.installRoot
    if (-not (Test-Path -LiteralPath $root)) {
        if ($RequireComplete.IsPresent) {
            throw "The receipt-owned install root is missing."
        }
        return
    }
    [void](Assert-ComoteOrdinaryLocalPath `
        -LiteralPath $root `
        -Directory $true `
        -Description "Receipt-owned install root")
    Assert-ComoteNoUntrustedWriteAcl -LiteralPath $root

    $fileMap = New-Object `
        'Collections.Generic.Dictionary[string,object]' `
        ([StringComparer]::Ordinal)
    foreach ($file in @($Receipt.installedFiles)) {
        if ($fileMap.ContainsKey([string]$file.relativePath)) {
            throw "The receipt duplicates an installed file path."
        }
        $fileMap.Add([string]$file.relativePath, $file)
    }
    $directorySet = New-Object `
        'Collections.Generic.HashSet[string]' `
        ([StringComparer]::Ordinal)
    foreach ($directory in @($Receipt.installedDirectories)) {
        if (-not $directorySet.Add([string]$directory.relativePath)) {
            throw "The receipt duplicates an installed directory path."
        }
    }

    $seenFiles = New-Object `
        'Collections.Generic.HashSet[string]' `
        ([StringComparer]::Ordinal)
    $seenDirectories = New-Object `
        'Collections.Generic.HashSet[string]' `
        ([StringComparer]::Ordinal)
    foreach ($item in @(
        Get-ChildItem -LiteralPath $root -Force -Recurse -ErrorAction Stop
    )) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "The installed tree contains a reparse point."
        }
        $relativePath = Get-ComoteRelativePath `
            -Root $root `
            -Child $item.FullName
        if ($item.PSIsContainer) {
            if (-not $directorySet.Contains($relativePath)) {
                throw "The installed tree contains an unowned directory."
            }
            [void](Assert-ComoteOrdinaryLocalPath `
                -LiteralPath $item.FullName `
                -Directory $true `
                -Description "Receipt-owned directory")
            [void]$seenDirectories.Add($relativePath)
        }
        else {
            if (-not $fileMap.ContainsKey($relativePath)) {
                throw "The installed tree contains an unowned file."
            }
            $identity = Assert-ComoteOrdinaryLocalPath `
                -LiteralPath $item.FullName `
                -Directory $false `
                -Description "Receipt-owned file"
            $expected = $fileMap[$relativePath]
            if ([int64]$identity.Identity.Length -ne
                    [int64]$expected.length -or
                (Get-ComoteSha256 -LiteralPath $item.FullName) -cne
                    [string]$expected.sha256) {
                throw "A receipt-owned file has changed: $relativePath"
            }
            [void]$seenFiles.Add($relativePath)
        }
    }
    if ($RequireComplete.IsPresent -and
        ($seenFiles.Count -ne $fileMap.Count -or
            $seenDirectories.Count -ne $directorySet.Count)) {
        throw "The installed tree is incomplete."
    }
}

function Remove-ComoteOwnedInstallTree {
    param(
        [Parameter(Mandatory)]
        [PSObject]$Receipt
    )

    $root = [string]$Receipt.paths.installRoot
    if (-not (Test-Path -LiteralPath $root)) {
        return
    }
    Assert-ComoteOwnedInstallTree -Receipt $Receipt
    foreach ($file in @($Receipt.installedFiles)) {
        $path = Join-Path `
            $root `
            ([string]$file.relativePath).Replace('/', '\')
        if ([IO.File]::Exists($path)) {
            [IO.File]::Delete($path)
        }
    }
    $directories = @(
        $Receipt.installedDirectories |
            ForEach-Object { [string]$_.relativePath } |
            Sort-Object Length -Descending
    )
    foreach ($relativeDirectory in $directories) {
        $path = Join-Path $root $relativeDirectory.Replace('/', '\')
        if ([IO.Directory]::Exists($path)) {
            if (@(
                    Get-ChildItem -LiteralPath $path -Force -ErrorAction Stop
                ).Count -ne 0) {
                throw "A receipt-owned directory is not empty: $path"
            }
            [IO.Directory]::Delete($path, $false)
        }
    }
    if (@(
            Get-ChildItem -LiteralPath $root -Force -ErrorAction Stop
        ).Count -ne 0) {
        throw "The receipt-owned install root is not empty."
    }
    [IO.Directory]::Delete($root, $false)
}

function Remove-ComoteOwnedGroupState {
    param(
        [Parameter(Mandatory)]
        [PSObject]$Receipt
    )

    $state = Get-ComoteLocalGroup
    if ($null -eq $state.Group) {
        return
    }
    $memberSids = @(Get-ComoteLocalGroupMemberSids -Group $state.Group)
    if ([bool]$Receipt.ownership.group) {
        $allowed = @()
        if ([bool]$Receipt.ownership.membership) {
            $allowed += [string]$Receipt.controller.sid
        }
        foreach ($memberSid in $memberSids) {
            if ($allowed -notcontains $memberSid) {
                throw "The receipt-owned group contains an unowned member."
            }
        }
    }
    if ([bool]$Receipt.ownership.membership -and
        $memberSids -contains [string]$Receipt.controller.sid) {
        $userPath = "WinNT://{0}/{1},user" -f
            ([string]$Receipt.controller.account).Split('\')[0],
            ([string]$Receipt.controller.account).Split('\')[1]
        $state.Group.Remove($userPath)
    }
    if ([bool]$Receipt.ownership.group) {
        $memberSids = @(Get-ComoteLocalGroupMemberSids -Group $state.Group)
        if ($memberSids.Count -ne 0) {
            throw "The receipt-owned group is not empty."
        }
        $state.Computer.Children.Remove(
            "group",
            $script:ComotePreviewGroupName
        )
    }
}

function Invoke-ComoteReceiptOwnedRemoval {
    param(
        [Parameter(Mandatory)]
        [PSObject]$Receipt
    )

    $receiptPath = [string]$Receipt.paths.receiptPath
    $failure = $null
    try {
        Assert-ComoteOwnedInstallTree `
            -Receipt $Receipt `
            -RequireComplete:([string]$Receipt.status -eq "installed")

        $services = @(Get-ComoteBrokerService)
        if ($services.Count -gt 1) {
            throw "The Broker service identity is ambiguous."
        }
        if ($services.Count -eq 1) {
            Assert-ComoteBrokerServiceIdentity `
                -Service $services[0] `
                -BinaryPath ([string]$Receipt.service.binaryPath) `
                -BinarySha256 ([string]$Receipt.service.binarySha256)
        }

        $groupState = Get-ComoteLocalGroup
        if ($null -ne $groupState.Group -and
            [bool]$Receipt.ownership.group) {
            $memberSids = @(
                Get-ComoteLocalGroupMemberSids -Group $groupState.Group
            )
            foreach ($memberSid in $memberSids) {
                if ($memberSid -cne [string]$Receipt.controller.sid) {
                    throw "The receipt-owned group has an unexpected member."
                }
            }
        }

        foreach ($storeReceipt in @($Receipt.certificateStores)) {
            if (-not [bool]$storeReceipt.owned) {
                continue
            }
            $copies = @(Get-ComoteCertificateFromStore `
                -StoreName ([string]$storeReceipt.name) `
                -Thumbprint ([string]$Receipt.certificate.thumbprint))
            if ($copies.Count -gt 1) {
                throw "A receipt-owned certificate is duplicated."
            }
            foreach ($copy in $copies) {
                $rawHash = [BitConverter]::ToString(
                    [Security.Cryptography.SHA256]::Create().ComputeHash(
                        $copy.RawData
                    )
                ).Replace("-", "")
                if ($rawHash -cne [string]$Receipt.certificate.sha256 -or
                    [string]$copy.Subject -cne
                        [string]$Receipt.certificate.subject) {
                    throw "A receipt-owned certificate identity has changed."
                }
            }
        }

        $Receipt.status = "uninstalling"
        Write-ComoteReceipt -Receipt $Receipt

        if ([bool]$Receipt.service.createAttempted) {
            Stop-ComoteBrokerServiceExact `
                -BinaryPath ([string]$Receipt.service.binaryPath) `
                -BinarySha256 ([string]$Receipt.service.binarySha256)
        }

        if ([bool]$Receipt.driver.installAttempted) {
            $removeResult = Invoke-ComoteNativeDriverInstaller `
                -Command remove `
                -InstallerPath ([string]$Receipt.driver.installerPath) `
                -ManifestPath ([string]$Receipt.driver.manifestPath)
            if ($removeResult.ExitCode -in @(23, 36)) {
                throw ("Native removal requires recovery/reboot; inputs and " +
                    "receipt were preserved. " + $removeResult.ResultLine)
            }
            if ($removeResult.ExitCode -notin @(0, 20)) {
                throw "Native driver removal failed: $($removeResult.ResultLine)"
            }
            $statusResult = Invoke-ComoteNativeDriverInstaller `
                -Command status `
                -InstallerPath ([string]$Receipt.driver.installerPath) `
                -ManifestPath ([string]$Receipt.driver.manifestPath)
            if ($statusResult.ExitCode -ne 20 -or
                $statusResult.State -cne "not-installed") {
                throw "Native driver state is not proven clean after removal."
            }
        }

        if ([bool]$Receipt.service.createAttempted) {
            Delete-ComoteBrokerServiceExact `
                -BinaryPath ([string]$Receipt.service.binaryPath) `
                -BinarySha256 ([string]$Receipt.service.binarySha256)
        }

        Remove-ComoteOwnedGroupState -Receipt $Receipt

        foreach ($storeReceipt in @($Receipt.certificateStores)) {
            if ([bool]$storeReceipt.owned) {
                Remove-ComotePinnedCertificateFromStore `
                    -StoreName ([string]$storeReceipt.name) `
                    -Thumbprint ([string]$Receipt.certificate.thumbprint) `
                    -CertificateSha256 ([string]$Receipt.certificate.sha256) `
                    -Subject ([string]$Receipt.certificate.subject)
            }
        }

        Remove-ComoteOwnedInstallTree -Receipt $Receipt
        if ([IO.File]::Exists($receiptPath)) {
            [IO.File]::Delete($receiptPath)
        }
        $stateRoot = [string]$Receipt.paths.stateRoot
        if ([IO.Directory]::Exists($stateRoot)) {
            if (@(
                    Get-ChildItem `
                        -LiteralPath $stateRoot `
                        -Force `
                        -ErrorAction Stop
                ).Count -ne 0) {
                throw "The protected state directory is not empty."
            }
            [IO.Directory]::Delete($stateRoot, $false)
        }
    }
    catch {
        $failure = $_
        if ([IO.File]::Exists($receiptPath)) {
            try {
                $Receipt.status = "recovery-required"
                Write-ComoteReceipt -Receipt $Receipt
            }
            catch {
            }
        }
        throw $failure
    }
}
