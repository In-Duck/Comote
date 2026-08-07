[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$ManifestPath,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$PackageDirectory,

    [string]$OutputHeader,

    [string]$ProjectPath,

    [switch]$ValidateOnly,

    [switch]$Build
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($OutputHeader)) {
    $OutputHeader = Join-Path $PSScriptRoot "ComoteReleaseManifestPin.h"
}
if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
    $ProjectPath = Join-Path $PSScriptRoot "ComoteDriverInstaller.vcxproj"
}

if (-not ("ComoteReleaseFileIdentity" -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

public sealed class ComoteReleaseFileIdentityResult
{
    public string FinalPath { get; set; }
    public uint NumberOfLinks { get; set; }
    public uint FileAttributes { get; set; }
    public ulong FileSize { get; set; }
    public string Sha256 { get; set; }
    public byte[] Bytes { get; set; }
}

public static class ComoteReleaseFileIdentity
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

    public static ComoteReleaseFileIdentityResult Inspect(
        string path,
        bool directory,
        bool captureBytes)
    {
        const uint GENERIC_READ = 0x80000000;
        const uint FILE_READ_ATTRIBUTES = 0x80;
        const uint FILE_SHARE_READ = 0x1;
        const uint FILE_SHARE_WRITE = 0x2;
        const uint FILE_SHARE_DELETE = 0x4;
        const uint OPEN_EXISTING = 3;
        const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
        uint flags = FILE_FLAG_OPEN_REPARSE_POINT;
        if (directory)
        {
            flags |= FILE_FLAG_BACKUP_SEMANTICS;
        }
        uint desiredAccess = directory ? FILE_READ_ATTRIBUTES : GENERIC_READ;
        uint shareMode = directory
            ? FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE
            : FILE_SHARE_READ;
        using (SafeFileHandle handle = CreateFileW(
            path,
            desiredAccess,
            shareMode,
            IntPtr.Zero,
            OPEN_EXISTING,
            flags,
            IntPtr.Zero))
        {
            if (handle.IsInvalid)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Unable to open release input by handle.");
            }
            BY_HANDLE_FILE_INFORMATION information;
            if (!GetFileInformationByHandle(handle, out information))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Unable to inspect release input identity.");
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
                    "Unable to resolve release input final path.");
            }

            string sha256 = null;
            byte[] captured = null;
            if (!directory)
            {
                using (FileStream stream = new FileStream(
                    handle,
                    FileAccess.Read,
                    4096,
                    false))
                {
                    using (SHA256 algorithm = SHA256.Create())
                    {
                        if (captureBytes)
                        {
                            if (stream.Length > 16384)
                            {
                                throw new InvalidDataException(
                                    "Release manifest exceeds 16 KiB.");
                            }
                            using (MemoryStream memory = new MemoryStream())
                            {
                                stream.CopyTo(memory);
                                captured = memory.ToArray();
                            }
                            sha256 = BitConverter.ToString(
                                algorithm.ComputeHash(captured)).Replace("-", "");
                        }
                        else
                        {
                            sha256 = BitConverter.ToString(
                                algorithm.ComputeHash(stream)).Replace("-", "");
                        }
                    }
                }
            }
            return new ComoteReleaseFileIdentityResult
            {
                FinalPath = finalPath.ToString(),
                NumberOfLinks = information.NumberOfLinks,
                FileAttributes = information.FileAttributes,
                FileSize = ((ulong)information.FileSizeHigh << 32) |
                    information.FileSizeLow,
                Sha256 = sha256,
                Bytes = captured
            };
        }
    }
}
"@
}
function ConvertFrom-NativeFinalPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$LiteralPath
    )

    if ($LiteralPath.StartsWith("\\?\UNC\", [StringComparison]::OrdinalIgnoreCase)) {
        return "\\" + $LiteralPath.Substring(8)
    }
    if ($LiteralPath.StartsWith("\\?\", [StringComparison]::OrdinalIgnoreCase)) {
        return $LiteralPath.Substring(4)
    }
    return $LiteralPath
}

function Assert-LocalFixedPathIdentity {
    param(
        [Parameter(Mandatory = $true)]
        [string]$LiteralPath,

        [Parameter(Mandatory = $true)]
        [bool]$Directory,

        [Parameter(Mandatory = $true)]
        [string]$Description,

        [switch]$CaptureBytes
    )

    if ($LiteralPath.StartsWith("\\", [StringComparison]::Ordinal) -or
        $LiteralPath.StartsWith("\\?\", [StringComparison]::Ordinal) -or
        $LiteralPath.StartsWith("\\.\", [StringComparison]::Ordinal)) {
        throw "$Description must use a normal local drive path."
    }
    $fullPath = [IO.Path]::GetFullPath($LiteralPath)
    if ($fullPath.StartsWith("\\", [StringComparison]::Ordinal)) {
        throw "$Description must not use a UNC or device path: $fullPath"
    }
    $root = [IO.Path]::GetPathRoot($fullPath)
    if ($root -cnotmatch "^[A-Za-z]:\\$") {
        throw "$Description does not resolve to a local drive root: $fullPath"
    }
    $drive = [IO.DriveInfo]::new($root)
    if ($drive.DriveType -ne [IO.DriveType]::Fixed) {
        throw "$Description must reside on a fixed local volume: $fullPath"
    }

    $cursor = if ($Directory) { $fullPath } else { Split-Path -Parent $fullPath }
    while (-not [string]::IsNullOrEmpty($cursor)) {
        $item = Get-Item -LiteralPath $cursor -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$Description has a reparse-point ancestor: $cursor"
        }
        if ($cursor.TrimEnd('\') -ieq $root.TrimEnd('\')) {
            break
        }
        $cursor = Split-Path -Parent $cursor
    }

    $identity = [ComoteReleaseFileIdentity]::Inspect(
        $fullPath,
        $Directory,
        [bool]$CaptureBytes
    )
    $fileAttributeDirectory = [uint32]0x10
    $fileAttributeReparsePoint = [uint32]0x400
    if (($identity.FileAttributes -band $fileAttributeReparsePoint) -ne 0 -or
        ($Directory -and
            ($identity.FileAttributes -band $fileAttributeDirectory) -eq 0) -or
        (-not $Directory -and
            ($identity.FileAttributes -band $fileAttributeDirectory) -ne 0)) {
        throw "$Description handle attributes are not an ordinary requested object."
    }
    $finalPath = [IO.Path]::GetFullPath(
        (ConvertFrom-NativeFinalPath -LiteralPath $identity.FinalPath)
    )
    if (-not $finalPath.Equals(
            $fullPath,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description final path differs from the requested path."
    }
    if (-not $Directory -and $identity.NumberOfLinks -ne 1) {
        throw "$Description must have exactly one hard-link name."
    }
    return [PSCustomObject]@{
        FullPath = $fullPath
        Identity = $identity
    }
}
if ($ValidateOnly -and $Build) {
    throw "-ValidateOnly and -Build cannot be used together."
}

function Get-FullExistingFilePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$LiteralPath,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    if (-not (Test-Path -LiteralPath $LiteralPath -PathType Leaf)) {
        throw "$Description was not found: $LiteralPath"
    }
    $validated = Assert-LocalFixedPathIdentity `
        -LiteralPath $LiteralPath `
        -Directory $false `
        -Description $Description
    $fullPath = [string]$validated.FullPath
    $item = Get-Item -LiteralPath $fullPath -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Description must not be a reparse point: $fullPath"
    }
    return $fullPath
}

function Get-FullExistingDirectoryPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$LiteralPath,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    if (-not (Test-Path -LiteralPath $LiteralPath -PathType Container)) {
        throw "$Description was not found: $LiteralPath"
    }
    $validated = Assert-LocalFixedPathIdentity `
        -LiteralPath $LiteralPath `
        -Directory $true `
        -Description $Description
    return [string]$validated.FullPath
}

function Assert-NormalOutputPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$LiteralPath
    )

    if ($LiteralPath.StartsWith("\\", [StringComparison]::Ordinal) -or
        $LiteralPath.StartsWith("\\?\", [StringComparison]::Ordinal) -or
        $LiteralPath.StartsWith("\\.\", [StringComparison]::Ordinal)) {
        throw "The output header must use a normal local drive path."
    }
    $fullPath = [IO.Path]::GetFullPath($LiteralPath)
    $parentPath = Split-Path -Parent $fullPath
    if (-not (Test-Path -LiteralPath $parentPath -PathType Container)) {
        throw "The output-header directory does not exist: $parentPath"
    }
    [void](Assert-LocalFixedPathIdentity `
        -LiteralPath $parentPath `
        -Directory $true `
        -Description "Output-header directory")

    if (Test-Path -LiteralPath $fullPath) {
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            throw "The output-header path is not a regular file: $fullPath"
        }
        [void](Assert-LocalFixedPathIdentity `
            -LiteralPath $fullPath `
            -Directory $false `
            -Description "Output header")
    }
    return $fullPath
}
function Read-StrictReleaseManifest {
    param(
        [Parameter(Mandatory = $true)]
        [string]$LiteralPath
    )

    $validatedManifest = Assert-LocalFixedPathIdentity `
        -LiteralPath $LiteralPath `
        -Directory $false `
        -Description "Final release manifest" `
        -CaptureBytes
    $bytes = [byte[]]$validatedManifest.Identity.Bytes
    if ($bytes.Length -eq 0) {
        throw "The release manifest is empty."
    }

    foreach ($byte in $bytes) {
        if ($byte -gt 0x7F) {
            throw "The release manifest must be strict 7-bit ASCII without a BOM."
        }
        if (($byte -lt 0x20) -and
            ($byte -ne 0x0A) -and
            ($byte -ne 0x0D)) {
            throw ("The release manifest contains a forbidden control byte: " +
                "0x{0:X2}" -f $byte)
        }
    }

    $text = [Text.Encoding]::ASCII.GetString($bytes)
    if ($text.Contains("`r") -and -not $text.Contains("`r`n")) {
        throw "The release manifest contains a bare carriage return."
    }
    if ([Text.RegularExpressions.Regex]::IsMatch($text, "`r(?!`n)")) {
        throw "The release manifest contains mixed or bare carriage returns."
    }

    $normalized = $text.Replace("`r`n", "`n")
    if ($normalized.EndsWith("`n", [StringComparison]::Ordinal)) {
        $normalized = $normalized.Substring(0, $normalized.Length - 1)
    }
    if ($normalized.Contains("`n`n")) {
        throw "The release manifest must not contain blank lines."
    }

    $lines = @($normalized.Split([char]"`n"))
    $expectedKeys = @(
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

    if ($lines.Count -ne 12) {
        throw "The release manifest must contain exactly 12 logical lines."
    }
    if ($lines[0] -cne "COMOTE-PHASE2-PACKAGE-MANIFEST-V1") {
        throw "The release manifest header is invalid."
    }

    $values = [ordered]@{}
    for ($index = 0; $index -lt $expectedKeys.Count; $index++) {
        $expectedKey = $expectedKeys[$index]
        $line = $lines[$index + 1]
        $separator = $line.IndexOf("=")
        if ($separator -le 0) {
            throw "Malformed release-manifest line: $line"
        }

        $key = $line.Substring(0, $separator)
        $value = $line.Substring($separator + 1)
        if ($key -cne $expectedKey) {
            throw ("Unexpected release-manifest key or order. Expected " +
                "'$expectedKey', received '$key'.")
        }
        if ([string]::IsNullOrEmpty($value)) {
            throw "Release-manifest value is empty: $key"
        }
        $values[$key] = $value
    }

    $fixedValues = [ordered]@{
        HardwareId = "ROOT\COMOTEVIRTUALHID_PHASE2"
        RootInstanceId = "ROOT\COMOTEVIRTUALHID_PHASE2\COMOTE_PHASE2"
        ServiceName = "ComoteVirtualHidPhase2"
        Provider = "Comote"
        PackageFiles = "ComoteVirtualHidPhase2.inf,ComoteVirtualHidPhase2.cat,ComoteVirtualHidPhase2.sys"
    }
    foreach ($entry in $fixedValues.GetEnumerator()) {
        if ($values[$entry.Key] -cne $entry.Value) {
            throw ("Release-manifest identity mismatch for {0}." -f
                $entry.Key)
        }
    }

    foreach ($hashKey in @("InfSha256", "CatSha256", "SysSha256")) {
        if ($values[$hashKey] -cnotmatch "^[0-9A-F]{64}$") {
            throw "$hashKey must contain exactly 64 uppercase hexadecimal characters."
        }
        if ($values[$hashKey] -ceq ("0" * 64)) {
            throw "$hashKey must not be the all-zero placeholder."
        }
    }

    foreach ($sizeKey in @("InfSize", "CatSize", "SysSize")) {
        if ($values[$sizeKey] -cnotmatch "^[1-9][0-9]*$") {
            throw "$sizeKey must be a strict nonzero decimal integer."
        }

        try {
            [void][uint64]::Parse(
                $values[$sizeKey],
                [Globalization.NumberStyles]::None,
                [Globalization.CultureInfo]::InvariantCulture
            )
        }
        catch {
            throw "$sizeKey is outside the unsigned 64-bit range."
        }
    }

    $manifestHash = [string]$validatedManifest.Identity.Sha256

    return [PSCustomObject]@{
        Bytes = $bytes
        Values = $values
        Sha256 = $manifestHash
    }
}

function Assert-ExactPackage {
    param(
        [Parameter(Mandatory = $true)]
        [string]$LiteralPath,

        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$ManifestValues
    )

    $expectedFiles = [ordered]@{
        "ComoteVirtualHidPhase2.inf" = @{
            SizeKey = "InfSize"
            HashKey = "InfSha256"
        }
        "ComoteVirtualHidPhase2.cat" = @{
            SizeKey = "CatSize"
            HashKey = "CatSha256"
        }
        "ComoteVirtualHidPhase2.sys" = @{
            SizeKey = "SysSize"
            HashKey = "SysSha256"
        }
    }

    $children = @(Get-ChildItem -LiteralPath $LiteralPath -Force)
    if ($children.Count -ne $expectedFiles.Count) {
        throw ("The package directory must contain exactly the three " +
            "manifested Phase 2 files.")
    }

    foreach ($child in $children) {
        if ($child.PSIsContainer) {
            throw "The package directory must not contain subdirectories."
        }
        if (($child.Attributes -band
                [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Package files must not be reparse points: $($child.Name)"
        }
        if (-not $expectedFiles.Contains($child.Name)) {
            throw "The package directory contains an unexpected file: $($child.Name)"
        }
        if ($expectedFiles.Keys -cnotcontains $child.Name) {
            throw "Package filename casing is not canonical: $($child.Name)"
        }
    }

    foreach ($entry in $expectedFiles.GetEnumerator()) {
        $validatedFile = Assert-LocalFixedPathIdentity `
            -LiteralPath (Join-Path $LiteralPath $entry.Key) `
            -Directory $false `
            -Description "Release package file $($entry.Key)"
        $filePath = [string]$validatedFile.FullPath
        $expectedSize = [uint64]::Parse(
            $ManifestValues[$entry.Value.SizeKey],
            [Globalization.NumberStyles]::None,
            [Globalization.CultureInfo]::InvariantCulture
        )
        $actualSize = [uint64]$validatedFile.Identity.FileSize
        if ($actualSize -ne $expectedSize) {
            throw ("Package size mismatch for {0}: expected {1}, actual {2}." -f
                $entry.Key,
                $expectedSize,
                $actualSize)
        }

        $actualHash = [string]$validatedFile.Identity.Sha256
        $expectedHash = $ManifestValues[$entry.Value.HashKey]
        if ($actualHash -cne $expectedHash) {
            throw ("Package SHA-256 mismatch for {0}: expected {1}, actual {2}." -f
                $entry.Key,
                $expectedHash,
                $actualHash)
        }
    }
}
function New-PinnedHeaderText {
    param(
        [Parameter(Mandatory = $true)]
        [ValidatePattern("^[0-9A-F]{64}$")]
        [string]$ManifestSha256
    )

    $byteLiterals = for ($index = 0; $index -lt 64; $index += 2) {
        "0x$($ManifestSha256.Substring($index, 2))"
    }
    $rows = for ($row = 0; $row -lt 4; $row++) {
        "    " + (($byteLiterals[($row * 8)..(($row * 8) + 7)]) -join ", ")
    }
    $arrayText = $rows -join ",`r`n"

    return @"
#pragma once

#include <array>
#include <cstdint>

// Generated from the exact final authenticated release manifest.
// Do not edit this file manually. Regenerate it with
// Build-ComoteReleasePinnedInstaller.ps1 before the installer Release build.
namespace comote::release_manifest_pin
{
inline constexpr bool kPinned = true;
inline constexpr char kState[] = "PINNED";
inline constexpr char kSha256Hex[] =
    "$ManifestSha256";
inline constexpr std::array<std::uint8_t, 32> kSha256 = {
$arrayText};
} // namespace comote::release_manifest_pin
"@
}

function Write-AsciiFileAtomically {
    param(
        [Parameter(Mandatory = $true)]
        [string]$LiteralPath,

        [Parameter(Mandatory = $true)]
        [string]$Content
    )

    $temporaryPath = "$LiteralPath.tmp-$([Guid]::NewGuid().ToString('N'))"
    try {
        $encoding = [Text.ASCIIEncoding]::new()
        [IO.File]::WriteAllText($temporaryPath, $Content, $encoding)

        if (Test-Path -LiteralPath $LiteralPath) {
            [IO.File]::Replace(
                $temporaryPath,
                $LiteralPath,
                $null,
                $true
            )
        }
        else {
            [IO.File]::Move($temporaryPath, $LiteralPath)
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }
}

function Find-MSBuild {
    $programFilesX86 = [Environment]::GetEnvironmentVariable(
        "ProgramFiles(x86)"
    )
    $vswhereCandidates = @(
        (Join-Path $programFilesX86 `
            "Microsoft Visual Studio\Installer\vswhere.exe"),
        (Join-Path $env:ProgramFiles `
            "Microsoft Visual Studio\Installer\vswhere.exe")
    )

    $vswhere = $vswhereCandidates |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        Select-Object -First 1
    if ([string]::IsNullOrEmpty($vswhere)) {
        throw "vswhere.exe was not found. Install Visual Studio C++ build tools."
    }

    $installationPath = & $vswhere `
        -latest `
        -products "*" `
        -requires Microsoft.Component.MSBuild `
        -property installationPath
    if ($LASTEXITCODE -ne 0 -or
        [string]::IsNullOrWhiteSpace($installationPath)) {
        throw "Visual Studio with MSBuild was not found."
    }

    $msbuildCandidates = @(
        (Join-Path $installationPath "MSBuild\Current\Bin\amd64\MSBuild.exe"),
        (Join-Path $installationPath "MSBuild\Current\Bin\MSBuild.exe")
    )
    $msbuild = $msbuildCandidates |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        Select-Object -First 1
    if ([string]::IsNullOrEmpty($msbuild)) {
        throw "MSBuild.exe was not found below: $installationPath"
    }

    return $msbuild
}

$resolvedManifest = Get-FullExistingFilePath `
    -LiteralPath $ManifestPath `
    -Description "Final release manifest"
$resolvedPackage = Get-FullExistingDirectoryPath `
    -LiteralPath $PackageDirectory `
    -Description "Final release package directory"
$resolvedProject = Get-FullExistingFilePath `
    -LiteralPath $ProjectPath `
    -Description "Installer project"
$resolvedHeader = Assert-NormalOutputPath -LiteralPath $OutputHeader
$manifest = Read-StrictReleaseManifest -LiteralPath $resolvedManifest
Assert-ExactPackage `
    -LiteralPath $resolvedPackage `
    -ManifestValues $manifest.Values
$headerText = New-PinnedHeaderText -ManifestSha256 $manifest.Sha256

Write-Host "Release manifest validation passed."
Write-Host "Release package validation passed."
Write-Host "Manifest SHA-256: $($manifest.Sha256)"
Write-Host "Manifest: $resolvedManifest"
Write-Host "Package: $resolvedPackage"
Write-Host "Pin header: $resolvedHeader"
Write-Host "Installer project: $resolvedProject"

if ($ValidateOnly) {
    Write-Host ""
    Write-Host "ValidateOnly: no header was written and no build was started."
    exit 0
}

Write-AsciiFileAtomically `
    -LiteralPath $resolvedHeader `
    -Content $headerText

$writtenHash = (
    Get-FileHash -Algorithm SHA256 -LiteralPath $resolvedHeader
).Hash
Write-Host "Pinned header written atomically."
Write-Host "Pinned header SHA-256: $writtenHash"
Write-Host ""
Write-Host "Required release order:"
Write-Host "  1. Freeze and authenticate the exact final package manifest."
Write-Host "  2. Generate this pin header from that exact manifest."
Write-Host "  3. Build the x64 Release installer."
Write-Host "  4. Verify the installer Authenticode signature and package together."

$buildArguments = @(
    $resolvedProject,
    "/nologo",
    "/m:1",
    "/t:Rebuild",
    "/p:Configuration=Release",
    "/p:Platform=x64"
)

if (-not $Build) {
    Write-Host ""
    Write-Host "Build not requested. Review the pin, then run with -Build or:"
    Write-Host ('  msbuild.exe "{0}" {1}' -f
        $resolvedProject,
        (($buildArguments[1..($buildArguments.Count - 1)]) -join " "))
    exit 0
}

$msbuild = Find-MSBuild
Write-Host ""
Write-Host "Starting the explicitly requested x64 Release installer build:"
Write-Host "  $msbuild"
& $msbuild @buildArguments
$buildExitCode = $LASTEXITCODE
if ($buildExitCode -ne 0) {
    throw "The installer build failed with exit code $buildExitCode."
}

Write-Host "Pinned x64 Release installer build completed."
