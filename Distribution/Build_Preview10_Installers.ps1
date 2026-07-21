param(
    [string]$PackagePath = "",
    [string]$InnoCompiler = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($PackagePath)) {
    $PackagePath = Join-Path $root `
        "artifacts\Comote-1.6.0-preview.21-win-x64.zip"
}

if (-not (Test-Path -LiteralPath $PackagePath)) {
    throw "Preview 19 package not found: $PackagePath"
}

if ([string]::IsNullOrWhiteSpace($InnoCompiler)) {
    $candidates = @(
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe",
        ("$env:LOCALAPPDATA\Programs\Antigravity\resources\app" +
            "\node_modules\innosetup\bin\ISCC.exe"),
        ("$env:LOCALAPPDATA\Programs\Antigravity IDE\resources\app" +
            "\node_modules\innosetup\bin\ISCC.exe")
    )
    $InnoCompiler = $candidates |
        Where-Object { Test-Path -LiteralPath $_ } |
        Select-Object -First 1
}

if ([string]::IsNullOrWhiteSpace($InnoCompiler) -or
    -not (Test-Path -LiteralPath $InnoCompiler)) {
    throw "Inno Setup compiler was not found."
}

$publishRoot = Join-Path $root "publish\Preview10"
if (Test-Path -LiteralPath $publishRoot) {
    $resolvedPublish = (Resolve-Path -LiteralPath $publishRoot).Path
    $resolvedRoot = (Resolve-Path -LiteralPath $root).Path
    $expectedParent = Join-Path $resolvedRoot "publish"
    if (-not $resolvedPublish.StartsWith(
        $expectedParent,
        [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe publish path: $resolvedPublish"
    }
    Remove-Item -LiteralPath $publishRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null
Expand-Archive -LiteralPath $PackagePath -DestinationPath $publishRoot -Force

$clientExe = Join-Path $publishRoot "Client\ComoteClient.exe"
if (-not (Test-Path -LiteralPath $clientExe)) {
    throw "Client executable is missing from the package."
}

$outputDir = Join-Path $root "artifacts\installers"
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

$clientScript = Join-Path $PSScriptRoot `
    "Client\Client_Offline_Installer.iss"
& $InnoCompiler $clientScript
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup compilation failed with exit code $LASTEXITCODE."
}

$installer = Join-Path $outputDir `
    "ComoteClient_Setup_1.6.0-preview.21_Offline.exe"
if (-not (Test-Path -LiteralPath $installer)) {
    throw "Installer output was not created."
}

$hash = Get-FileHash -Algorithm SHA256 -LiteralPath $installer
[ordered]@{
    version = "1.6.0-preview.21"
    file = Split-Path $installer -Leaf
    bytes = (Get-Item -LiteralPath $installer).Length
    sha256 = $hash.Hash
} | ConvertTo-Json | Set-Content `
    -LiteralPath (Join-Path $outputDir "SHA256.json") `
    -Encoding UTF8

Write-Host "Client installer: $installer"
Write-Host "Size: $((Get-Item -LiteralPath $installer).Length) bytes"
Write-Host "SHA256: $($hash.Hash)"


