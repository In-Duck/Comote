#Requires -Version 5.1
[CmdletBinding()]
param(
    [switch]$AcknowledgeDisposableVm,

    [switch]$AcknowledgeTestSignedPreview,

    [AllowNull()]
    [string]$PreviewAcceptancePhrase,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedReleaseManifestSha256
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot "VirtualHidPreview.Common.ps1")

$release = Get-ComoteReleaseManifest `
    -PackageRoot $PSScriptRoot `
    -ExpectedManifestSha256 $ExpectedReleaseManifestSha256
$environment = Assert-ComoteRoleCleanupEnvironment `
    -PackageRole ([string]$release.Document.packageRole) `
    -AcknowledgeDisposableVm:$AcknowledgeDisposableVm `
    -AcknowledgeTestSignedPreview:$AcknowledgeTestSignedPreview `
    -PreviewAcceptancePhrase $PreviewAcceptancePhrase
$operationLock = Enter-ComotePreviewLock
try {
    $receipt = Read-ComoteProtectedReceipt `
        -ExpectedReleaseManifestSha256 $ExpectedReleaseManifestSha256 `
        -ExpectedPackageRole ([string]$release.Document.packageRole)

    if ([string]$receipt.target.manufacturer -cne
            [string]$environment.Computer.Manufacturer -or
        [string]$receipt.target.model -cne
            [string]$environment.Computer.Model -or
        [int]$receipt.target.productType -ne
            [int]$environment.OperatingSystem.ProductType -or
        [string]$receipt.target.architecture -cne "x64" -or
        [string]$receipt.target.buildNumber -cne
            [string]$environment.OperatingSystem.BuildNumber -or
        [int]$receipt.target.editionSku -ne
            [int]$environment.OperatingSystem.OperatingSystemSKU) {
        throw "The protected receipt belongs to a different target machine."
    }

    Invoke-ComoteReceiptOwnedRemoval -Receipt $receipt

    Write-Host ""
    Write-Host "Comote Virtual HID preview removed." -ForegroundColor Green
    Write-Host "Only changes owned by the protected receipt were removed."
    Write-Host ("Windows Event Log, SetupAPI logs, native OS logs, and the " +
        "protected Broker log directory were intentionally retained.")
    Write-Host "TESTSIGNING, Secure Boot, and HVCI were not changed."
    Write-Host ("Cleanup remained available without requiring their current " +
        "state to match installation time.")
}
finally {
    Exit-ComotePreviewLock -Mutex $operationLock
}
