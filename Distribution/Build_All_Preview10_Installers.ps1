param(
    [string]$PackagePath = "",
    [string]$InnoCompiler = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

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

$clientBuild = Join-Path $PSScriptRoot `
    "Build_Preview10_Installers.ps1"
& $clientBuild `
    -PackagePath $PackagePath `
    -InnoCompiler $InnoCompiler

$managerScript = Join-Path $PSScriptRoot `
    "Manager\Manager_Offline_Installer.iss"
& $InnoCompiler $managerScript
if ($LASTEXITCODE -ne 0) {
    throw "Manager installer compilation failed: $LASTEXITCODE"
}

$outputDir = Join-Path $root "artifacts\installers"
$installers = @(
    Join-Path $outputDir `
        "ComoteClient_Setup_1.6.0-preview.20_Offline.exe"
    Join-Path $outputDir `
        "ComoteManager_Setup_1.6.0-preview.20_Offline.exe"
)

foreach ($installer in $installers) {
    if (-not (Test-Path -LiteralPath $installer)) {
        throw "Installer output was not created: $installer"
    }
}

$manifest = $installers | ForEach-Object {
    $file = Get-Item -LiteralPath $_
    [ordered]@{
        version = "1.6.0-preview.20"
        file = $file.Name
        bytes = $file.Length
        sha256 = (Get-FileHash `
            -Algorithm SHA256 `
            -LiteralPath $file.FullName).Hash
    }
}
$manifest | ConvertTo-Json | Set-Content `
    -LiteralPath (Join-Path $outputDir "SHA256.json") `
    -Encoding UTF8

$manifest | Format-Table file, bytes, sha256 -AutoSize
