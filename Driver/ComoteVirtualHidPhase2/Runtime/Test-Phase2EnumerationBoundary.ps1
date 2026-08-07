#Requires -Version 5.1
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$requiredFiles = [ordered]@{
    Common = "Phase2Runtime.Common.ps1"
    EnumerationCommon = "Phase2Enumeration.Common.ps1"
    Preflight = "Test-Phase2EnumerationPreflight.ps1"
    Install = "Install-Phase2Enumeration.ps1"
    Installed = "Test-Phase2InstalledState.ps1"
    Remove = "Remove-Phase2Enumeration.ps1"
    Recovery = "Repair-Phase2EnumerationState.ps1"
    SigningBoundary = "Test-Phase2TestSigningBoundary.ps1"
}
$sources = @{}
foreach ($entry in $requiredFiles.GetEnumerator()) {
    $path = Join-Path $PSScriptRoot $entry.Value
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required Phase 2 enumeration gate is missing: $path"
    }
    $source = Get-Content -LiteralPath $path -Raw
    if ($source -match '[^\x00-\x7F]') {
        throw "Runtime PowerShell must remain ASCII-compatible: $path"
    }
    $sources[$entry.Key] = $source
}

function Assert-ContainsAll {
    param(
        [Parameter(Mandatory)]
        [string]$Source,

        [Parameter(Mandatory)]
        [string[]]$Required,

        [Parameter(Mandatory)]
        [string]$Description
    )
    foreach ($item in $Required) {
        if ($Source.IndexOf(
                $item,
                [StringComparison]::OrdinalIgnoreCase) -lt 0) {
            throw "$Description is missing: $item"
        }
    }
}

function Assert-ContainsNone {
    param(
        [Parameter(Mandatory)]
        [string]$Source,

        [Parameter(Mandatory)]
        [string[]]$Forbidden,

        [Parameter(Mandatory)]
        [string]$Description
    )
    foreach ($item in $Forbidden) {
        if ($Source.IndexOf(
                $item,
                [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw "$Description crossed its boundary: $item"
        }
    }
}

Assert-ContainsAll `
    -Source $sources.Common `
    -Description "Phase 2 common runtime" `
    -Required @(
        "Assert-ComotePhase2RuntimeEnvironment",
        "Test-ComotePhase2VirtualMachine",
        "Write-ComotePhase2JsonAtomically",
        ".replace-backup",
        "Read-ComotePhase2JsonDocument",
        '$global:LASTEXITCODE = $null',
        "AbandonedMutexException",
        "Get-ComotePhase2NamedCertificates",
        "Get-ComotePhase2ActiveCodeIntegrityState",
        "ROOT\COMOTEVIRTUALHID_PHASE2",
        "-ErrorAction Stop"
    )

Assert-ContainsAll `
    -Source $sources.Preflight `
    -Description "Phase 2 enumeration preflight" `
    -Required @(
        "Assert-ComotePhase2RuntimeEnvironment",
        "Assert-ComotePhase2SigningPrerequisites",
        "Assert-ComotePhase2NoInstalledDevice",
        "Get-ComotePhase2TestSigningState",
        "Get-ComotePhase2ActiveCodeIntegrityState",
        "Get-ComotePhase2NamedCertificates",
        "test-signing-preparation.json",
        "enumeration-installation.json",
        "enumeration-transaction.json",
        "Get-FileHash",
        "Get-AuthenticodeSignature",
        '"verify", "/pa", "/c"',
        "Get-ComotePhase2DriverPackages",
        "Get-ComotePhase2RootDevices",
        "-ErrorAction Stop",
        "No package was staged and no device was created"
    )
Assert-ContainsNone `
    -Source $sources.Preflight `
    -Description "Phase 2 enumeration preflight" `
    -Forbidden @(
        "New-SelfSignedCertificate",
        "Import-Certificate",
        "Remove-Item",
        "Write-ComotePhase2JsonAtomically",
        "pnputil.exe",
        "DeviceIoControl",
        "Restart-Computer",
        "shutdown.exe"
    )

Assert-ContainsAll `
    -Source $sources.EnumerationCommon `
    -Description "Phase 2 enumeration common runtime" `
    -Required @(
        "Assert-ComotePhase2DriverPackageIdentity",
        '[version]"0.2.0.0"',
        "Get-ComotePhase2RootDevices",
        "-ErrorAction Stop",
        "Assert-ComotePhase2InstalledState",
        "Remove-ComotePhase2OrphanedService",
        "Assert-ComotePhase2EnumerationTransaction",
        "Invoke-ComotePhase2ExactEnumerationCleanup",
        "More than one Phase 2 root device exists",
        "More than one Phase 2 package exists",
        '"/delete-driver"',
        '"/uninstall"',
        "Write-ComotePhase2JsonAtomically",
        '$children.Mice.Count -ne 2',
        "A Phase 2 PnP device remained after exact cleanup"
    )
Assert-ContainsNone `
    -Source $sources.EnumerationCommon `
    -Description "Phase 2 exact cleanup" `
    -Forbidden @(
        '"/force"',
        '"/reboot"',
        "Restart-Computer",
        "shutdown.exe",
        "verifier.exe",
        "DeviceIoControl"
    )

Assert-ContainsAll `
    -Source $sources.Install `
    -Description "Phase 2 install runtime" `
    -Required @(
        "Test-Phase2EnumerationPreflight.ps1",
        "ValidateOnly",
        "enumeration-transaction.json",
        'operation = "install"',
        'status = "install-prepared"',
        "Write-ComotePhase2JsonAtomically",
        "Enter-ComotePhase2RuntimeLock",
        "Exit-ComotePhase2RuntimeLock",
        "Assert-ComotePhase2InstalledState",
        '"/add-driver"',
        '"/instanceid", "COMOTE_PHASE2"',
        '"/hardwareid", "ROOT\COMOTEVIRTUALHID_PHASE2"',
        '@("/add-driver", $infPath, "/install")',
        "Assert-ComotePhase2DriverPackageIdentity",
        'Value "package-staged"',
        'Value "device-enumerated"',
        '$linkedMice.Count -eq 2',
        '$linkedMice.Count -ne 2',
        "installed-enumerated",
        "Invoke-ComotePhase2ExactEnumerationCleanup",
        "rollback-complete",
        "Repair-Phase2EnumerationState.ps1"
    )
$installValidate = $sources.Install.IndexOf(
    'if ($ValidateOnly.IsPresent)',
    [StringComparison]::OrdinalIgnoreCase
)
$installJournal = $sources.Install.IndexOf(
    'operation = "install"',
    $installValidate,
    [StringComparison]::OrdinalIgnoreCase
)
$installStage = $sources.Install.IndexOf(
    '"/add-driver"',
    $installJournal,
    [StringComparison]::OrdinalIgnoreCase
)
$installCreate = $sources.Install.IndexOf(
    '"/instanceid", "COMOTE_PHASE2"',
    $installStage,
    [StringComparison]::OrdinalIgnoreCase
)
$installReceipt = $sources.Install.IndexOf(
    '-LiteralPath $installReceiptPath',
    $installCreate,
    [StringComparison]::OrdinalIgnoreCase
)
if ($installValidate -lt 0 -or
    $installJournal -lt $installValidate -or
    $installStage -lt $installJournal -or
    $installCreate -lt $installStage -or
    $installReceipt -lt $installCreate) {
    throw "ValidateOnly, journal, stage, device, and receipt order is invalid."
}
Assert-ContainsNone `
    -Source $sources.Install `
    -Description "Phase 2 installation" `
    -Forbidden @(
        "New-SelfSignedCertificate",
        '"/force"',
        '"/reboot"',
        "Restart-Computer",
        "shutdown.exe",
        "verifier.exe",
        "DeviceIoControl",
        "ComoteVirtualHidProbe.exe",
        "Set-Content"
    )

Assert-ContainsAll `
    -Source $sources.Installed `
    -Description "Phase 2 installed-state audit" `
    -Required @(
        "Assert-ComotePhase2RuntimeEnvironment",
        "Assert-ComotePhase2InstalledState",
        "recoverySnapshotName",
        "manufacturer",
        "osBuildNumber",
        "certificateNotAfterUtc",
        "Get-ComotePhase2CertificateCopies",
        "artifacts\phase2-test-signed",
        "Get-FileHash",
        "Get-AuthenticodeSignature",
        "The loaded Phase 2 driver does not match the signed package.",
        "probeLaunched",
        "inputReportsSubmitted"
    )
Assert-ContainsNone `
    -Source $sources.Installed `
    -Description "Phase 2 installed-state audit" `
    -Forbidden @(
        "pnputil.exe",
        "devgen.exe",
        "DeviceIoControl",
        "Write-ComotePhase2JsonAtomically",
        "Remove-Item"
    )

Assert-ContainsAll `
    -Source $sources.Remove `
    -Description "Phase 2 removal runtime" `
    -Required @(
        "Assert-ComotePhase2InstalledState",
        "ValidateOnly",
        "enumeration-transaction.json",
        'operation = "remove"',
        'status = "remove-prepared"',
        "Write-ComotePhase2JsonAtomically",
        "Enter-ComotePhase2RuntimeLock",
        "Exit-ComotePhase2RuntimeLock",
        "Invoke-ComotePhase2ExactEnumerationCleanup",
        "history\enumeration-",
        "Repair-Phase2EnumerationState.ps1",
        "active receipt was archived and removed"
    )
$removeValidate = $sources.Remove.IndexOf(
    'if ($ValidateOnly.IsPresent)',
    [StringComparison]::OrdinalIgnoreCase
)
$removeJournal = $sources.Remove.IndexOf(
    'operation = "remove"',
    $removeValidate,
    [StringComparison]::OrdinalIgnoreCase
)
$removeMutation = $sources.Remove.IndexOf(
    "Invoke-ComotePhase2ExactEnumerationCleanup",
    $removeJournal,
    [StringComparison]::OrdinalIgnoreCase
)
if ($removeValidate -lt 0 -or
    $removeJournal -lt $removeValidate -or
    $removeMutation -lt $removeJournal) {
    throw "Removal ValidateOnly, journal, and mutation order is invalid."
}
Assert-ContainsNone `
    -Source $sources.Remove `
    -Description "Phase 2 removal" `
    -Forbidden @(
        '"/force"',
        '"/reboot"',
        "Restart-Computer",
        "shutdown.exe",
        "verifier.exe",
        "DeviceIoControl",
        "Set-Content"
    )

Assert-ContainsAll `
    -Source $sources.Recovery `
    -Description "Phase 2 enumeration recovery" `
    -Required @(
        "Assert-ComotePhase2EnumerationTransaction",
        "Invoke-ComotePhase2ExactEnumerationCleanup",
        "Read-ComotePhase2JsonDocument",
        "Write-ComotePhase2JsonAtomically",
        "Enter-ComotePhase2RuntimeLock",
        "Exit-ComotePhase2RuntimeLock",
        "recovered-from-install-receipt",
        "history",
        "removed-legacy",
        "state exists without an exact transaction or receipt",
        "no target was mutated"
    )
Assert-ContainsNone `
    -Source $sources.Recovery `
    -Description "Phase 2 enumeration recovery" `
    -Forbidden @(
        '"/force"',
        '"/reboot"',
        "Restart-Computer",
        "shutdown.exe",
        "verifier.exe",
        "DeviceIoControl"
    )

Write-Host "Phase 2 enumeration transaction and recovery boundaries verified." `
    -ForegroundColor Green