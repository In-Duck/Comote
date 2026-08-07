#Requires -Version 5.1
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$preflightPath = Join-Path $PSScriptRoot "Invoke-Phase1RuntimePreflight.ps1"
if (-not (Test-Path -LiteralPath $preflightPath -PathType Leaf)) {
    throw "Runtime preflight script was not found."
}

$preflight = Get-Content -LiteralPath $preflightPath -Raw
foreach ($requiredText in @(
    "Test-ComoteVirtualMachine",
    "Windows 10",
    "19045",
    "Confirm-SecureBootUEFI",
    "Get-BitLockerVolume",
    'bcdedit.exe /enum "{current}"',
    "Get-FileHash",
    '$manifestDocument -is [Array]',
    'foreach ($entry in $manifestEntries)',
    "Get-AuthenticodeSignature",
    "SignatureStatus]::NotSigned",
    "60EE21C9DF1C019DE8B259CF1E265CDCF61EB9BC",
    "signtool.exe",
    "devgen.exe",
    "No system state was changed"
)) {
    if ($preflight.IndexOf(
            $requiredText,
            [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Required runtime preflight gate is missing: $requiredText"
    }
}

foreach ($forbiddenText in @(
    "New-SelfSignedCertificate",
    "Import-Certificate",
    "Set-AuthenticodeSignature",
    "bcdedit.exe /set",
    "pnputil.exe",
    '& $signTool',
    '& $devGen',
    "Remove-Item",
    "New-Service",
    "Start-Service",
    "verifier.exe"
)) {
    if ($preflight.IndexOf(
            $forbiddenText,
            [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Runtime preflight must remain read-only: $forbiddenText"
    }
}

Write-Host "Phase 1 runtime preflight boundary verified."
