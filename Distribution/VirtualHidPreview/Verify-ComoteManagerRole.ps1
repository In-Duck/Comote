#Requires -Version 5.1
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function ConvertTo-ComoteManagerNativePath {
    param([Parameter(Mandatory)][string]$LiteralPath)

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

function Initialize-ComoteManagerNativeTypes {
    if ($null -ne ("Comote.ManagerRole.NativeMethods" -as [Type])) {
        return
    }
    Add-Type -TypeDefinition @"
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Comote.ManagerRole
{
    public sealed class FileIdentity
    {
        public string FinalPath { get; set; }
        public uint Attributes { get; set; }
        public uint NumberOfLinks { get; set; }
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
            string fileName, uint desiredAccess, uint shareMode,
            IntPtr securityAttributes, uint creationDisposition,
            uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(
            SafeFileHandle file, out BY_HANDLE_FILE_INFORMATION information);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandleW(
            SafeFileHandle file, StringBuilder path, uint pathLength,
            uint flags);

        public static FileIdentity Inspect(string path)
        {
            const uint FILE_READ_ATTRIBUTES = 0x80;
            const uint FILE_SHARE_READ = 0x1;
            const uint FILE_SHARE_WRITE = 0x2;
            const uint FILE_SHARE_DELETE = 0x4;
            const uint OPEN_EXISTING = 3;
            const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

            using (SafeFileHandle handle = CreateFileW(
                path, FILE_READ_ATTRIBUTES,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT,
                IntPtr.Zero))
            {
                if (handle.IsInvalid)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                BY_HANDLE_FILE_INFORMATION information;
                if (!GetFileInformationByHandle(handle, out information))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                StringBuilder finalPath = new StringBuilder(32768);
                uint written = GetFinalPathNameByHandleW(
                    handle, finalPath, (uint)finalPath.Capacity, 0);
                if (written == 0 || written >= finalPath.Capacity)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                return new FileIdentity {
                    FinalPath = finalPath.ToString(),
                    Attributes = information.FileAttributes,
                    NumberOfLinks = information.NumberOfLinks
                };
            }
        }
    }
}
"@
}

function Assert-ComoteManagerOrdinaryPath {
    param(
        [Parameter(Mandatory)]
        [string]$LiteralPath,

        [Parameter(Mandatory)]
        [bool]$Directory,

        [Parameter(Mandatory)]
        [string]$Description
    )

    if ($LiteralPath.StartsWith("\\", [StringComparison]::Ordinal) -or
        $LiteralPath.StartsWith("\\?\", [StringComparison]::Ordinal) -or
        $LiteralPath.StartsWith("\\.\", [StringComparison]::Ordinal)) {
        throw "$Description must use a normal fixed local path."
    }
    $fullPath = [IO.Path]::GetFullPath($LiteralPath)
    if (($Directory -and
            -not (Test-Path -LiteralPath $fullPath -PathType Container)) -or
        (-not $Directory -and
            -not (Test-Path -LiteralPath $fullPath -PathType Leaf))) {
        throw "$Description was not found."
    }
    $root = [IO.Path]::GetPathRoot($fullPath)
    $drive = New-Object IO.DriveInfo($root)
    if ($root -cnotmatch '^[A-Za-z]:\\$' -or
        -not $drive.IsReady -or
        $drive.DriveType -ne [IO.DriveType]::Fixed -or
        [string]$drive.DriveFormat -cne "NTFS") {
        throw "$Description must be on a ready fixed NTFS volume."
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
            throw "$Description has a reparse-point ancestor."
        }
        if ($cursor.TrimEnd('\') -ieq $root.TrimEnd('\')) {
            break
        }
        $cursor = [IO.Path]::GetDirectoryName($cursor.TrimEnd('\'))
    }
    if (-not $Directory) {
        Initialize-ComoteManagerNativeTypes
        $identity = [Comote.ManagerRole.NativeMethods]::Inspect($fullPath)
        if (($identity.Attributes -band [uint32]0x400) -ne 0 -or
            ($identity.Attributes -band [uint32]0x10) -ne 0 -or
            $identity.NumberOfLinks -ne 1) {
            throw "$Description must be an ordinary single-link file."
        }
        $finalPath = [IO.Path]::GetFullPath(
            (ConvertTo-ComoteManagerNativePath `
                -LiteralPath $identity.FinalPath)
        )
        if (-not $finalPath.Equals(
                $fullPath,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "$Description final path differs from the requested path."
        }
    }
    return $fullPath
}

function Assert-ComoteManagerSafeRelativePath {
    param(
        [Parameter(Mandatory)]
        [string]$RelativePath
    )

    if ([string]::IsNullOrWhiteSpace($RelativePath) -or
        [IO.Path]::IsPathRooted($RelativePath) -or
        $RelativePath -match '\\' -or
        $RelativePath.Split('/') -contains ".." -or
        $RelativePath -match '(^|/)(?:\.|\s*$)' -or
        $RelativePath.IndexOfAny([IO.Path]::GetInvalidPathChars()) -ge 0) {
        throw "The Manager manifest contains an unsafe relative path."
    }
}

function Assert-ComoteManagerExactProperties {
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
        throw "$Description property count is invalid."
    }
    for ($index = 0; $index -lt $Expected.Count; $index++) {
        if ([string]$actual[$index] -cne [string]$Expected[$index]) {
            throw "$Description property order/name is invalid."
        }
    }
}

$roleRoot = Assert-ComoteManagerOrdinaryPath `
    -LiteralPath $PSScriptRoot `
    -Directory $true `
    -Description "Manager role root"
$manifestPath = Assert-ComoteManagerOrdinaryPath `
    -LiteralPath (Join-Path $roleRoot "manager-role-manifest.json") `
    -Directory $false `
    -Description "Detached Manager manifest"
$manifestSidecar = Assert-ComoteManagerOrdinaryPath `
    -LiteralPath "$manifestPath.sha256" `
    -Directory $false `
    -Description "Detached Manager manifest checksum"
$manifestHash = (
    Get-FileHash -Algorithm SHA256 -LiteralPath $manifestPath -ErrorAction Stop
).Hash.ToUpperInvariant()
$expectedSidecar = "{0} *manager-role-manifest.json`r`n" -f $manifestHash
if ([IO.File]::ReadAllText($manifestSidecar) -cne $expectedSidecar) {
    throw "The detached Manager manifest checksum is invalid."
}
try {
    $manifest = [IO.File]::ReadAllText(
        $manifestPath,
        (New-Object Text.UTF8Encoding($false, $true))
    ) | ConvertFrom-Json -ErrorAction Stop
}
catch {
    throw "The detached Manager manifest is not strict UTF-8 JSON."
}
Assert-ComoteManagerExactProperties `
    -InputObject $manifest `
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
    -Description "Detached Manager manifest"
Assert-ComoteManagerExactProperties `
    -InputObject $manifest.application `
    -Expected @(
        "role",
        "path",
        "originalFilename",
        "sha256",
        "signerThumbprint"
    ) `
    -Description "Manager application identity"
Assert-ComoteManagerExactProperties `
    -InputObject $manifest.runtimePolicy `
    -Expected @(
        "frameworkVersion",
        "runtimePackVersion",
        "coreclrLength",
        "coreclrSha256",
        "coreclrFileVersion",
        "coreclrProductVersion"
    ) `
    -Description "Manager runtime policy"
$expectedCoreclrVersion =
    "10,0,1026,32716 @Commit: f7d90799ce4ef09a0bb257852a57248d2a8fb8dd"
if ([int]$manifest.schemaVersion -ne 1 -or
    [string]$manifest.releaseId -notmatch
        '^[A-Za-z0-9][A-Za-z0-9._-]{2,63}$' -or
    [string]$manifest.packageRole -cne "manager-portable" -or
    [string]$manifest.sourceReleaseManifestSha256 -cnotmatch
        '^[0-9A-F]{64}$' -or
    [string]$manifest.unifiedProvisionalReportSha256 -cnotmatch
        '^[0-9A-F]{64}$' -or
    [string]$manifest.runtimePolicy.frameworkVersion -cne "10.0.10" -or
    [string]$manifest.runtimePolicy.runtimePackVersion -cne "10.0.10" -or
    [int64]$manifest.runtimePolicy.coreclrLength -ne 4614952 -or
    [string]$manifest.runtimePolicy.coreclrSha256 -cne
        "58859F85A30CC71313B281898E7CFBDBB9ECCB95AE2A3F865329EFD47EBF31BB" -or
    [string]$manifest.runtimePolicy.coreclrFileVersion -cne
        $expectedCoreclrVersion -or
    [string]$manifest.runtimePolicy.coreclrProductVersion -cne
        $expectedCoreclrVersion -or
    [string]$manifest.application.role -cne "manager" -or
    [string]$manifest.application.path -cne
        "App/Manager/ComoteManager.exe" -or
    [string]$manifest.application.originalFilename -cne "Viewer.dll" -or
    [string]$manifest.application.sha256 -cnotmatch '^[0-9A-F]{64}$' -or
    [string]$manifest.application.signerThumbprint -cnotmatch
        '^[0-9A-F]{40}$') {
    throw "The detached Manager manifest identity is invalid."
}

$entryMap = New-Object `
    'Collections.Generic.Dictionary[string,object]' `
    ([StringComparer]::Ordinal)
foreach ($entry in @($manifest.files)) {
    Assert-ComoteManagerExactProperties `
        -InputObject $entry `
        -Expected @("path", "length", "sha256") `
        -Description "Manager file entry"
    $relative = [string]$entry.path
    Assert-ComoteManagerSafeRelativePath -RelativePath $relative
    if ($relative -ceq "manager-role-manifest.json" -or
        $relative -ceq "manager-role-manifest.json.sha256" -or
        [int64]$entry.length -le 0 -or
        [string]$entry.sha256 -cnotmatch '^[0-9A-F]{64}$' -or
        $entryMap.ContainsKey($relative)) {
        throw "A Manager file entry is invalid or duplicated."
    }
    $entryMap.Add($relative, $entry)
    $filePath = Assert-ComoteManagerOrdinaryPath `
        -LiteralPath (Join-Path $roleRoot $relative.Replace('/', '\')) `
        -Directory $false `
        -Description "Manager role file"
    $identity = Get-Item -LiteralPath $filePath -Force -ErrorAction Stop
    if ([int64]$identity.Length -ne [int64]$entry.length -or
        (Get-FileHash `
            -Algorithm SHA256 `
            -LiteralPath $filePath `
            -ErrorAction Stop).Hash.ToUpperInvariant() -cne
                [string]$entry.sha256) {
        throw "A Manager role file differs from its detached manifest."
    }
}
if ($entryMap.Count -lt 4 -or
    -not $entryMap.ContainsKey("START COMOTE MANAGER.cmd") -or
    -not $entryMap.ContainsKey("Verify-ComoteManagerRole.ps1") -or
    -not $entryMap.ContainsKey("ROLE_README.txt") -or
    -not $entryMap.ContainsKey([string]$manifest.application.path) -or
    [string]$entryMap[[string]$manifest.application.path].sha256 -cne
        [string]$manifest.application.sha256) {
    throw "The Manager role inventory omits a required bound file."
}
$actualFiles = @(
    Get-ChildItem `
        -LiteralPath $roleRoot `
        -File `
        -Force `
        -Recurse `
        -ErrorAction Stop |
        ForEach-Object {
            $_.FullName.Substring($roleRoot.TrimEnd('\').Length + 1).
                Replace('\', '/')
        }
)
if ($actualFiles.Count -ne $entryMap.Count + 2) {
    throw "The Manager role contains an unexpected file."
}
foreach ($actualFile in $actualFiles) {
    if ($actualFile -cne "manager-role-manifest.json" -and
        $actualFile -cne "manager-role-manifest.json.sha256" -and
        -not $entryMap.ContainsKey($actualFile)) {
        throw "The Manager role contains an unmanifested file."
    }
}

$applicationPath = Join-Path `
    $roleRoot `
    ([string]$manifest.application.path).Replace('/', '\')
$signature = Get-AuthenticodeSignature `
    -LiteralPath $applicationPath `
    -ErrorAction Stop
if ($null -eq $signature.SignerCertificate -or
    $signature.SignerCertificate.Thumbprint.ToUpperInvariant() -cne
        [string]$manifest.application.signerThumbprint -or
    $signature.Status -notin @(
        [Management.Automation.SignatureStatus]::Valid,
        [Management.Automation.SignatureStatus]::NotTrusted
    )) {
    throw "The Manager Authenticode signature/thumbprint is invalid."
}
$version = [Diagnostics.FileVersionInfo]::GetVersionInfo($applicationPath)
if ([string]$version.OriginalFilename -cne
    [string]$manifest.application.originalFilename) {
    throw "The Manager executable original filename is invalid."
}

Write-Host "Comote Manager role verification passed." -ForegroundColor Green
