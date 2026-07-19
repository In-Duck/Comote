param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$version = "1.4.0-demo.1"
$artifacts = Join-Path $root "artifacts"
$stage = Join-Path $artifacts "Comote-$version"
$viewerOut = Join-Path $stage "Viewer"
$hostOut = Join-Path $stage "Host"

if (Test-Path -LiteralPath $stage) {
    Remove-Item -LiteralPath $stage -Recurse -Force
}

New-Item -ItemType Directory -Path $viewerOut -Force | Out-Null
New-Item -ItemType Directory -Path $hostOut -Force | Out-Null

dotnet publish (Join-Path $root "Viewer\Viewer.csproj") `
    -c $Configuration -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false `
    -o $viewerOut

dotnet publish (Join-Path $root "Host\Host.csproj") `
    -c $Configuration -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false `
    -o $hostOut

$viewerExe = Join-Path $viewerOut "Viewer.exe"
$hostExe = Join-Path $hostOut "Host.exe"
Rename-Item -LiteralPath $viewerExe -NewName "ComoteViewer.exe"
Rename-Item -LiteralPath $hostExe -NewName "ComoteHost.exe"

@"
@echo off
start "" "%~dp0Viewer\ComoteViewer.exe" --demo
"@ | Set-Content -LiteralPath (Join-Path $stage "Comote Viewer Demo.cmd") -Encoding Ascii

Copy-Item -LiteralPath (Join-Path $root "docs\DEMO_TEST_GUIDE.md") `
    -Destination (Join-Path $stage "README.md")

$manifest = @(
    Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $viewerOut "ComoteViewer.exe")
    Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $hostOut "ComoteHost.exe")
) | Select-Object @{Name="File";Expression={Split-Path $_.Path -Leaf}}, Algorithm, Hash
$manifest | ConvertTo-Json | Set-Content `
    -LiteralPath (Join-Path $stage "SHA256.json") -Encoding UTF8

$zip = Join-Path $artifacts "Comote-$version-win-x64.zip"
if (Test-Path -LiteralPath $zip) {
    Remove-Item -LiteralPath $zip -Force
}
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip -CompressionLevel Optimal

Write-Host "Demo package: $zip"
Write-Host "Unpacked: $stage"
