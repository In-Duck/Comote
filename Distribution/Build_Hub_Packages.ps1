param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$version = "1.6.0-preview.11"
$artifacts = Join-Path $root "artifacts"
$stage = Join-Path $artifacts "Comote-$version"
$managerOut = Join-Path $stage "Manager"
$clientOut = Join-Path $stage "Client"

if (Test-Path -LiteralPath $stage) {
    $resolvedStage = (Resolve-Path -LiteralPath $stage).Path
    $resolvedRoot = (Resolve-Path -LiteralPath $root).Path
    if (-not $resolvedStage.StartsWith(
        (Join-Path $resolvedRoot "artifacts"),
        [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe stage path: $resolvedStage"
    }
    Remove-Item -LiteralPath $resolvedStage -Recurse -Force
}

New-Item -ItemType Directory -Path $managerOut -Force | Out-Null
New-Item -ItemType Directory -Path $clientOut -Force | Out-Null

dotnet publish (Join-Path $root "Viewer\Viewer.csproj") `
    -c $Configuration -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false `
    -o $managerOut

dotnet publish (Join-Path $root "Host\Host.csproj") `
    -c $Configuration -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false `
    -o $clientOut

Rename-Item -LiteralPath (Join-Path $managerOut "Viewer.exe") `
    -NewName "ComoteManager.exe"
Rename-Item -LiteralPath (Join-Path $clientOut "Host.exe") `
    -NewName "ComoteClient.exe"

Copy-Item -LiteralPath `
    (Join-Path $root "Distribution\Open_Comote_Manager_Hub_Port_45820.cmd") `
    -Destination `
    (Join-Path $managerOut "Open Manager Hub Firewall as Administrator.cmd")
Copy-Item -LiteralPath `
    (Join-Path $root "docs\HUB_CONNECTION_TEST_GUIDE.md") `
    -Destination (Join-Path $stage "README.md")

Copy-Item -LiteralPath `
    (Join-Path $root "docs\CLIENT_AUTO_UPDATE_GUIDE.md") `
    -Destination (Join-Path $stage "CLIENT_AUTO_UPDATE_GUIDE.md")

$manifest = @(
    Get-FileHash -Algorithm SHA256 -LiteralPath `
        (Join-Path $managerOut "ComoteManager.exe")
    Get-FileHash -Algorithm SHA256 -LiteralPath `
        (Join-Path $clientOut "ComoteClient.exe")
) | Select-Object `
    @{Name="File";Expression={Split-Path $_.Path -Leaf}},
    Algorithm,
    Hash
$manifest | ConvertTo-Json | Set-Content `
    -LiteralPath (Join-Path $stage "SHA256.json") -Encoding UTF8

$zip = Join-Path $artifacts "Comote-$version-win-x64.zip"
if (Test-Path -LiteralPath $zip) {
    Remove-Item -LiteralPath $zip -Force
}
Compress-Archive `
    -Path (Join-Path $stage "*") `
    -DestinationPath $zip `
    -CompressionLevel Optimal

$updateManifest = [ordered]@{
    version = "1.6.0.11"
    client_package_url = "REPLACE_WITH_HTTPS_PACKAGE_URL"
    client_package_sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $zip).Hash
}
$updateManifest | ConvertTo-Json | Set-Content `
    -LiteralPath (Join-Path $artifacts "Comote-$version-client-update.json") `
    -Encoding UTF8

Write-Host "Hub package: $zip"
Write-Host "Unpacked: $stage"
