#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$SourceRoot
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$root = [IO.Path]::GetFullPath($SourceRoot)
if (-not (Test-Path -LiteralPath $root -PathType Container)) {
    throw "SourceRoot was not found."
}

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
        -LiteralPath $root `
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
    if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Root build input is a reparse point: $($file.Name)"
    }
    $rootInputFiles += $file
}
if (@(
        $rootInputFiles |
            Where-Object { $_.Name -in @("NuGet.config", "nuget.config") }
    ).Count -gt 1) {
    throw "NuGet.config and nuget.config are ambiguous at the source root."
}
foreach ($file in $rootInputFiles) {
    $records += [PSCustomObject][ordered]@{
        path = $file.Name
        length = [int64]$file.Length
        sha256 = (
            Get-FileHash `
                -Algorithm SHA256 `
                -LiteralPath $file.FullName
        ).Hash.ToUpperInvariant()
    }
}
foreach ($includedRoot in $includedRoots) {
    $path = Join-Path $root $includedRoot.Replace('/', '\')
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
        $relative = $file.FullName.Substring($root.TrimEnd('\').Length + 1).
            Replace('\', '/')
        if ($file.Name -match $backupSuffixPattern) {
            throw "Source inventory rejects backup artifact: $relative"
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
        if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Source inventory contains a reparse point: $relative"
        }
        $records += [PSCustomObject][ordered]@{
            path = $relative
            length = [int64]$file.Length
            sha256 = (
                Get-FileHash `
                    -Algorithm SHA256 `
                    -LiteralPath $file.FullName
            ).Hash.ToUpperInvariant()
        }
    }
}
$records = @($records | Sort-Object path)
if ($records.Count -lt 50) {
    throw "The release source inventory is unexpectedly small."
}
$lines = @(
    "COMOTE-VIRTUAL-HID-PREVIEW-SOURCE-V1"
    foreach ($record in $records) {
        "{0}|{1}|{2}" -f
            $record.path,
            $record.length,
            $record.sha256
    }
)
$canonical = ($lines -join "`r`n") + "`r`n"
$bytes = (New-Object Text.ASCIIEncoding).GetBytes($canonical)
$sha256 = [BitConverter]::ToString(
    [Security.Cryptography.SHA256]::Create().ComputeHash($bytes)
).Replace("-", "")

Write-Host "Source inventory SHA-256: $sha256"
Write-Host "Source files: $($records.Count)"
Write-Host "No source, build output, certificate, service, device, or driver state changed."
