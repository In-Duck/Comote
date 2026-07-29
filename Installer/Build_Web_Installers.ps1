param(
    [switch]$UseLocalPackages,
    [string]$ReleaseConfig = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $root "artifacts\installers"
$buildRoot = Join-Path $output "build"
$compiler = Join-Path $env:WINDIR `
    "Microsoft.NET\Framework64\v4.0.30319\csc.exe"

if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw ".NET Framework C# compiler was not found: $compiler"
}

if ([string]::IsNullOrWhiteSpace($ReleaseConfig)) {
    $ReleaseConfig = Join-Path $PSScriptRoot "release-config.json"
}

if ($UseLocalPackages) {
    [xml]$project = Get-Content (Join-Path $root "Host\Host.csproj")
    $version = [string]$project.Project.PropertyGroup.Version
    $assemblyVersion = [string]$project.Project.PropertyGroup.FileVersion
    if ([string]::IsNullOrWhiteSpace($version) -or
        [string]::IsNullOrWhiteSpace($assemblyVersion)) {
        throw "Host project version metadata is missing."
    }

    $clientPackage = Join-Path $root `
        "artifacts\ComoteClient-$version-win-x64.zip"
    $managerPackage = Join-Path $root `
        "artifacts\ComoteManager-$version-win-x64.zip"
    foreach ($package in @($clientPackage, $managerPackage)) {
        if (-not (Test-Path -LiteralPath $package -PathType Leaf)) {
            throw "Release package is missing: $package"
        }
    }

    $tag = "v$version"
    $client = [pscustomobject]@{
        url = "https://github.com/In-Duck/Comote/releases/download/$tag/" +
            [IO.Path]::GetFileName($clientPackage)
        sha256 = (Get-FileHash -Algorithm SHA256 `
            -LiteralPath $clientPackage).Hash
        bytes = (Get-Item -LiteralPath $clientPackage).Length
    }
    $manager = [pscustomobject]@{
        url = "https://github.com/In-Duck/Comote/releases/download/$tag/" +
            [IO.Path]::GetFileName($managerPackage)
        sha256 = (Get-FileHash -Algorithm SHA256 `
            -LiteralPath $managerPackage).Hash
        bytes = (Get-Item -LiteralPath $managerPackage).Length
    }
} else {
    if (-not (Test-Path -LiteralPath $ReleaseConfig -PathType Leaf)) {
        throw "Installer release config is missing: $ReleaseConfig"
    }
    $config = Get-Content -LiteralPath $ReleaseConfig -Raw |
        ConvertFrom-Json
    $version = [string]$config.version
    $assemblyVersion = [string]$config.assembly_version
    $client = $config.client
    $manager = $config.manager
}

foreach ($value in @(
    $version,
    $assemblyVersion,
    $client.url,
    $client.sha256,
    $manager.url,
    $manager.sha256
)) {
    if ([string]::IsNullOrWhiteSpace([string]$value)) {
        throw "Installer release metadata contains an empty value."
    }
}
foreach ($package in @($client, $manager)) {
    $uri = $null
    if (-not [Uri]::TryCreate(
            [string]$package.url,
            [UriKind]::Absolute,
            [ref]$uri) -or
        $uri.Scheme -ne "https" -or
        $uri.Host -ne "github.com" -or
        -not $uri.AbsolutePath.StartsWith(
            "/In-Duck/Comote/releases/download/",
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Installer package URL is not an approved GitHub release URL."
    }
    if ([string]$package.sha256 -notmatch "^[A-Fa-f0-9]{64}$") {
        throw "Installer package SHA-256 is invalid."
    }
    if ([long]$package.bytes -le 0 -or
        [long]$package.bytes -gt 500MB) {
        throw "Installer package size is invalid."
    }
}
if ($assemblyVersion -notmatch "^\d+\.\d+\.\d+\.\d+$") {
    throw "Installer assembly version is invalid: $assemblyVersion"
}

New-Item -ItemType Directory -Path $output -Force | Out-Null
if (Test-Path -LiteralPath $buildRoot) {
    $resolvedBuild = (Resolve-Path -LiteralPath $buildRoot).Path
    $resolvedOutput = (Resolve-Path -LiteralPath $output).Path
    if (-not $resolvedBuild.StartsWith(
        $resolvedOutput,
        [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe installer build path: $resolvedBuild"
    }
    Remove-Item -LiteralPath $buildRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null

function Convert-ToCSharpLiteral([string]$value) {
    return $value.Replace("\", "\\").Replace('"', '\"')
}

$generatedConfig = Join-Path $buildRoot "InstallerBuildConfiguration.cs"
$generatedSource = @"
namespace ComoteInstaller
{
    internal static class InstallerBuildConfiguration
    {
        internal const string Version = "$(Convert-ToCSharpLiteral $version)";
        internal const string ClientPackageUrl = "$(Convert-ToCSharpLiteral ([string]$client.url))";
        internal const string ClientPackageSha256 = "$(([string]$client.sha256).ToUpperInvariant())";
        internal const long ClientPackageBytes = $([long]$client.bytes)L;
        internal const string ManagerPackageUrl = "$(Convert-ToCSharpLiteral ([string]$manager.url))";
        internal const string ManagerPackageSha256 = "$(([string]$manager.sha256).ToUpperInvariant())";
        internal const long ManagerPackageBytes = $([long]$manager.bytes)L;
    }
}
"@
[IO.File]::WriteAllText(
    $generatedConfig,
    $generatedSource,
    [Text.UTF8Encoding]::new($true))

$manifestTemplate = Get-Content `
    -LiteralPath (Join-Path $PSScriptRoot "Installer.manifest") `
    -Raw
$generatedManifest = Join-Path $buildRoot "Installer.manifest"
$manifestSource = $manifestTemplate -replace `
    'version="\d+\.\d+\.\d+\.\d+"', `
    "version=`"$assemblyVersion`""
[IO.File]::WriteAllText(
    $generatedManifest,
    $manifestSource,
    [Text.UTF8Encoding]::new($true))

$source = Join-Path $PSScriptRoot "Bootstrapper.cs"
$references = @(
    "/reference:System.dll",
    "/reference:System.Core.dll",
    "/reference:System.Drawing.dll",
    "/reference:System.Net.Http.dll",
    "/reference:System.IO.Compression.dll",
    "/reference:System.IO.Compression.FileSystem.dll",
    "/reference:System.Windows.Forms.dll"
)
$products = @(
    @{
        Define = "CLIENT"
        Name = "ComoteClient_Setup.exe"
        Icon = Join-Path $root "Host\Kymote.ico"
    },
    @{
        Define = "MANAGER"
        Name = "ComoteManager_Setup.exe"
        Icon = Join-Path $root "Viewer\Kymote.ico"
    }
)

foreach ($product in $products) {
    $target = Join-Path $output $product.Name
    & $compiler `
        /nologo `
        /target:winexe `
        /platform:x64 `
        /optimize+ `
        /checked+ `
        /warn:4 `
        "/define:$($product.Define)" `
        "/win32icon:$($product.Icon)" `
        "/win32manifest:$generatedManifest" `
        "/out:$target" `
        @references `
        $source `
        $generatedConfig
    if ($LASTEXITCODE -ne 0) {
        throw "$($product.Name) compilation failed: $LASTEXITCODE"
    }

    $verification = Start-Process `
        -FilePath $target `
        -ArgumentList "/verify" `
        -WindowStyle Hidden `
        -Wait `
        -PassThru
    if ($verification.ExitCode -ne 0) {
        throw "$($product.Name) configuration verification failed."
    }
    if ((Get-Item -LiteralPath $target).Length -gt 1MB) {
        throw "$($product.Name) exceeds the 1 MB bootstrapper size limit."
    }
}

$manifestEntries = foreach ($product in $products) {
    $path = Join-Path $output $product.Name
    $file = Get-Item -LiteralPath $path
    [pscustomobject][ordered]@{
        version = $version
        file = $file.Name
        bytes = $file.Length
        sha256 = (Get-FileHash `
            -Algorithm SHA256 `
            -LiteralPath $file.FullName).Hash
    }
}

$manifestEntries |
    ConvertTo-Json |
    Set-Content `
        -LiteralPath (Join-Path $output "web-installers.sha256.json") `
        -Encoding UTF8

$manifestEntries | Format-Table file, bytes, sha256 -AutoSize
