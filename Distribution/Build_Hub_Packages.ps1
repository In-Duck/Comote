param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$version = "1.6.0-preview.16"
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
if ($LASTEXITCODE -ne 0) { throw "Manager publish failed: $LASTEXITCODE" }


dotnet publish (Join-Path $root "Host\Host.csproj") `
    -c $Configuration -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false `
    -o $clientOut
if ($LASTEXITCODE -ne 0) { throw "Client publish failed: $LASTEXITCODE" }


$fakerInputUrl =
    "https://github.com/Ryochan7/FakerInput/releases/download/" +
    "v0.1.1/FakerInput_Setup_0.1.1_x64.msi"
$downloadTemp = if ($env:RUNNER_TEMP) {
    $env:RUNNER_TEMP
} else {
    [System.IO.Path]::GetTempPath()
}
$fakerInputPath = Join-Path $downloadTemp `
    "FakerInput_Setup_0.1.1_x64.msi"
$expectedFakerInputSha256 =
    "4C0AEFB7340051A91D606776243298B5CD1143EF5508BBAE6800C474F9ED0840"
Invoke-WebRequest -Uri $fakerInputUrl -OutFile $fakerInputPath
$actualFakerInputSha256 =
    (Get-FileHash -Algorithm SHA256 -LiteralPath $fakerInputPath).Hash
if ($actualFakerInputSha256 -ne $expectedFakerInputSha256) {
    throw "FakerInput installer SHA-256 mismatch."
}
$fakerSignature = Get-AuthenticodeSignature -LiteralPath $fakerInputPath
if ($fakerSignature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
    throw "FakerInput installer signature is not valid: $($fakerSignature.Status)"
}
Copy-Item -LiteralPath $fakerInputPath -Destination $clientOut
Copy-Item -LiteralPath `
    (Join-Path $root "ThirdParty\FakerInput-LICENSE.txt") `
    -Destination (Join-Path $clientOut "FakerInput-LICENSE.txt")

Rename-Item -LiteralPath (Join-Path $managerOut "Viewer.exe") `
    -NewName "ComoteManager.exe"
Rename-Item -LiteralPath (Join-Path $clientOut "Host.exe") `
    -NewName "ComoteClient.exe"


Copy-Item -LiteralPath `
    (Join-Path $root "docs\ACCOUNT_CONNECTION_GUIDE.md") `
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
    version = "1.6.0.16"
    client_package_url = "REPLACE_WITH_HTTPS_PACKAGE_URL"
    client_package_sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $zip).Hash
}
$updateManifest | ConvertTo-Json | Set-Content `
    -LiteralPath (Join-Path $artifacts "Comote-$version-client-update.json") `
    -Encoding UTF8

Write-Host "Hub package: $zip"
Write-Host "Unpacked: $stage"
