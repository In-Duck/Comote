#Requires -Version 5.1
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$root = $PSScriptRoot
$paths = [ordered]@{
    Common = Join-Path $root "VirtualHidPreview.Common.ps1"
    Build = Join-Path $root "Build-VirtualHidPreviewRelease.ps1"
    Install = Join-Path $root "Install-ComoteVirtualHidPreview.ps1"
    Uninstall = Join-Path $root "Uninstall-ComoteVirtualHidPreview.ps1"
    NewRelease = Join-Path $root "New-VirtualHidPreviewReleaseInVm.ps1"
    Promotion = Join-Path $root "Invoke-VirtualHidPreviewVmPromotion.ps1"
    Exporter = Join-Path $root "Export-VirtualHidPreviewRolePackages.ps1"
    RegressionGate = Join-Path `
        $root `
        "Invoke-VirtualHidPreviewRegressionGate.ps1"
    SourceInventory = Join-Path `
        $root `
        "Get-VirtualHidPreviewSourceInventory.ps1"
    Test = Join-Path $root "Test-VirtualHidPreviewBoundary.ps1"
    Readme = Join-Path $root "README.md"
    DirectDependenciesNotice = Join-Path `
        $root `
        "THIRD_PARTY_NOTICES\DOTNET_DIRECT_DEPENDENCIES.md"
    FfmpegNotice = Join-Path `
        $root `
        "THIRD_PARTY_NOTICES\FFMPEG.md"
    FfmpegReceipt = Join-Path `
        $root `
        "THIRD_PARTY_NOTICES\FFMPEG_ASSET_RECEIPT.json"
    MediaGateLock = Join-Path `
        $root `
        "MediaGate\packages.lock.json"
    FfmpegManifest = Join-Path `
        (Split-Path -Parent (Split-Path -Parent $root)) `
        "ffmpeg\manifest.json"
}
foreach ($entry in $paths.GetEnumerator()) {
    if (-not (Test-Path -LiteralPath $entry.Value -PathType Leaf)) {
        throw "Required preview release file is missing: $($entry.Value)"
    }
}

$sources = [ordered]@{}
foreach ($entry in $paths.GetEnumerator()) {
    $sources[$entry.Key] = Get-Content `
        -LiteralPath $entry.Value `
        -Raw `
        -ErrorAction Stop
}

function Assert-ContainsText {
    param(
        [Parameter(Mandatory)]
        [string]$Text,

        [Parameter(Mandatory)]
        [string]$Token,

        [Parameter(Mandatory)]
        [string]$Description
    )

    if ($Text.IndexOf($Token, [StringComparison]::Ordinal) -lt 0) {
        throw "Missing $Description ($Token)."
    }
}

function Assert-NotContainsText {
    param(
        [Parameter(Mandatory)]
        [string]$Text,

        [Parameter(Mandatory)]
        [string]$Token,

        [Parameter(Mandatory)]
        [string]$Description
    )

    if ($Text.IndexOf(
            $Token,
            [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Forbidden $Description is present ($Token)."
    }
}

$scriptFiles = @(
    Get-ChildItem `
        -LiteralPath $root `
        -Filter "*.ps1" `
        -File `
        -Force `
        -ErrorAction Stop |
        Sort-Object Name
)
if ($scriptFiles.Count -lt 10) {
    throw "The preview PowerShell inventory is unexpectedly small."
}
foreach ($scriptFile in $scriptFiles) {
    $scriptPath = $scriptFile.FullName
    $bytes = [IO.File]::ReadAllBytes($scriptPath)
    foreach ($byte in $bytes) {
        if ($byte -gt 0x7F) {
            throw "Operational PowerShell must remain ASCII: $scriptPath"
        }
    }
    $tokens = $null
    $parseErrors = $null
    [void][Management.Automation.Language.Parser]::ParseFile(
        $scriptPath,
        [ref]$tokens,
        [ref]$parseErrors
    )
    if ($parseErrors.Count -ne 0) {
        throw "PowerShell parse errors were found: $scriptPath"
    }
}

$mutationSources = [ordered]@{
    Common = $sources.Common
    Build = $sources.Build
    Install = $sources.Install
    Uninstall = $sources.Uninstall
    NewRelease = $sources.NewRelease
    Promotion = $sources.Promotion
    Exporter = $sources.Exporter
    RegressionGate = $sources.RegressionGate
}

function Test-ComoteMutationBoundary {
    param(
        [Parameter(Mandatory)]
        [string]$Candidate
    )

    $tokens = $null
    $parseErrors = $null
    $ast = [Management.Automation.Language.Parser]::ParseInput(
        $Candidate,
        [ref]$tokens,
        [ref]$parseErrors
    )
    if ($parseErrors.Count -ne 0) {
        return $false
    }
    $commands = @(
        $ast.FindAll(
            {
                param($node)
                $node -is [Management.Automation.Language.CommandAst]
            },
            $true
        )
    )
    foreach ($command in $commands) {
        $commandName = $command.GetCommandName()
        if ([string]::IsNullOrWhiteSpace($commandName)) {
            continue
        }
        if ($commandName -match
                '^(?i:bcdedit|certutil|pnputil|devcon|devgen)(?:\.exe)?$' -or
            $commandName -match
                '^(?i:UpdateDriverForPlugAndPlayDevices)$' -or
            $commandName -match
                '^(?i:Set|Enable|Disable)-SecureBoot' -or
            $commandName -match
                '^(?i:New|Register|Set|Start|Stop|Enable|Disable|Unregister)-ScheduledTask$' -or
            $commandName -match '^(?i:schtasks)(?:\.exe)?$' -or
            $commandName -match '^(?i:New|Set)-NetFirewallRule$' -or
            ($commandName -match '^(?i:netsh)(?:\.exe)?$' -and
                $command.Extent.Text -match '(?i)(?:advfirewall|firewall)')) {
            return $false
        }
        if ($commandName -ieq "Remove-Item") {
            $parameters = @(
                $command.CommandElements |
                    Where-Object {
                        $_ -is [Management.Automation.Language.CommandParameterAst]
                    } |
                    ForEach-Object { $_.ParameterName }
            )
            if ($parameters -icontains "Recurse" -or
                $command.Extent.Text -match '[*?]') {
                return $false
            }
        }
        if ($commandName -in @("Set-Item", "Set-ItemProperty") -and
            $command.Extent.Text -match
                '(?i)CurrentControlSet\\Control\\DeviceGuard') {
            return $false
        }
    }
    $strings = @(
        $ast.FindAll(
            {
                param($node)
                $node -is
                    [Management.Automation.Language.StringConstantExpressionAst] -or
                $node -is
                    [Management.Automation.Language.ExpandableStringExpressionAst]
            },
            $true
        )
    )
    foreach ($stringNode in $strings) {
        if ([string]$stringNode.Value -match
            '^(?i:bcdedit|certutil|pnputil|devcon|devgen)(?:\.exe)?$') {
            return $false
        }
    }
    if ($Candidate -match
            '(?is)\[IO\.Directory\]::Delete\([^\)]*,\s*\$true\s*\)' -or
        $Candidate -match '(?i)AppData[\\/]Broker') {
        return $false
    }
    return $true
}

foreach ($entry in $mutationSources.GetEnumerator()) {
    if (-not (Test-ComoteMutationBoundary -Candidate $entry.Value)) {
        throw "The mutation boundary failed for: $($entry.Key)"
    }
    if ($entry.Key -cne "Promotion" -and
        $entry.Value -match '(?i)verifier\.exe') {
        throw "Only promotion may invoke verifier.exe: $($entry.Key)"
    }
}

$broadDeleteMutation = $sources.Common +
    "`nRemove-Item C:\ProgramData\Comote -Recurse -Force"
if (Test-ComoteMutationBoundary -Candidate $broadDeleteMutation) {
    throw "Negative mutation: broad recursive deletion was not rejected."
}
$bootMutation = $sources.Common +
    "`n" + "bcd" + "edit.exe /set testsigning on"
if (Test-ComoteMutationBoundary -Candidate $bootMutation) {
    throw "Negative mutation: a test-signing BCD change was not rejected."
}
$trustMutation = $sources.Common +
    "`n" + "cert" + "util.exe -addstore Root adjacent.cer"
if (Test-ComoteMutationBoundary -Candidate $trustMutation) {
    throw "Negative mutation: unpinned certificate trust was not rejected."
}
$firewallMutation = $sources.Common +
    "`nNew-NetFirewallRule -DisplayName Comote -Direction Inbound"
if (Test-ComoteMutationBoundary -Candidate $firewallMutation) {
    throw "Negative mutation: a firewall-rule change was not rejected."
}
$netshFirewallMutation = $sources.Common +
    "`nnetsh.exe advfirewall firewall add rule name=Comote dir=in action=allow"
if (Test-ComoteMutationBoundary -Candidate $netshFirewallMutation) {
    throw "Negative mutation: a netsh firewall change was not rejected."
}
$servicePathMutation = $sources.Common.Replace(
    '"App/Broker/Comote.InputBroker.exe"',
    '"AppData/Broker/Comote.InputBroker.exe"'
)
if ($servicePathMutation -eq $sources.Common) {
    throw "The user-writable service-path negative mutation was not constructed."
}
if ($servicePathMutation -notmatch
    '(?i)AppData/Broker/Comote\.InputBroker\.exe') {
    throw "Negative mutation: user-writable service path was not detected."
}
if (Test-ComoteMutationBoundary -Candidate $servicePathMutation) {
    throw "Negative mutation: user-writable service path was not rejected."
}

foreach ($token in @(
    "COMOTE-PHASE2-PACKAGE-MANIFEST-V1",
    "ComoteVirtualHidPhase2.inf",
    "ComoteVirtualHidPhase2.cat",
    "ComoteVirtualHidPhase2.sys",
    "NumberOfLinks -ne 1",
    "GetFinalPathNameByHandleW",
    "DriveType]::Fixed",
    "DriveFormat",
    "NTFS",
    "Root",
    "TrustedPublisher",
    "1.3.6.1.5.5.7.3.3",
    "D:P(A;;GA;;;SY)(A;;GA;;;BA)",
    "COMOTE_INSTALLER_RESULT",
    "ExitCode -in @(23, 36)",
    "Stop-ComoteBrokerServiceExact",
    "Delete-ComoteBrokerServiceExact",
    "packageRole",
    "validation-unified",
    "client-virtual-hid",
    "runtimePolicy",
    "validationTools",
    "10.0.10",
    "58859F85A30CC71313B281898E7CFBDBB9ECCB95AE2A3F865329EFD47EBF31BB",
    "START HERE - Manager Hub.cmd",
    "START HERE - Client Virtual HID.cmd",
    "THIRD_PARTY_NOTICES/DOTNET_DIRECT_DEPENDENCIES.md",
    "THIRD_PARTY_NOTICES/NUGET_SBOM.json",
    "THIRD_PARTY_NOTICES/FFMPEG.md",
    "THIRD_PARTY_NOTICES/FFMPEG_ASSET_RECEIPT.json",
    "THIRD_PARTY_NOTICES/FFmpeg/LICENSE.LGPLv3.txt",
    "THIRD_PARTY_NOTICES/FFmpeg/SOURCE_OFFER.md",
    "Validation/REGRESSION_GATE.json",
    "Validation/MEDIA_GATE.json",
    "Validation/Comote.MediaGate.exe",
    "Validation/NuGetLocks/InputCore/packages.lock.json",
    "Validation/NuGetLocks/Distribution/VirtualHidPreview/MediaGate/packages.lock.json",
    "App/Client/ThirdParty/FFmpeg/manifest.json",
    "App/Manager/ThirdParty/FFmpeg/manifest.json",
    "[IO.File]::Delete",
    "[IO.Directory]::Delete"
)) {
    Assert-ContainsText `
        -Text $sources.Common `
        -Token $token `
        -Description "common fail-closed contract"
}

foreach ($token in @(
    '"THIRD_PARTY_NOTICES/"',
    "Get-ComoteLocalGroupMemberSids",
    "existingMemberSids.Count -gt 1",
    "empty or contain only the exact ControllerUser SID",
    "Add-ComoteControllerGroupAndMember"
)) {
    Assert-ContainsText `
        -Text $sources.Install `
        -Token $token `
        -Description "installer protected-notice/controller-group contract"
}

$installOrder = [ordered]@{
    Inventory = $sources.Install.IndexOf(
        "Assert-ComoteReleaseInventory",
        [StringComparison]::Ordinal
    )
    VmGuard = $sources.Install.IndexOf(
        "Assert-ComoteRoleInstallEnvironment",
        [StringComparison]::Ordinal
    )
    CertificatePin = $sources.Install.IndexOf(
        "Get-ComotePinnedCertificate",
        [StringComparison]::Ordinal
    )
    Receipt = $sources.Install.IndexOf(
        "Write-ComoteReceipt -Receipt `$receipt",
        [StringComparison]::Ordinal
    )
    Group = $sources.Install.IndexOf(
        "Add-ComoteControllerGroupAndMember",
        [StringComparison]::Ordinal
    )
    CertificateInstall = $sources.Install.IndexOf(
        "Add-ComotePinnedCertificateToStore",
        [StringComparison]::Ordinal
    )
    DriverInstall = $sources.Install.IndexOf(
        "-Command install",
        [StringComparison]::Ordinal
    )
    ServiceCreate = $sources.Install.IndexOf(
        "New-ComoteBrokerService",
        [StringComparison]::Ordinal
    )
}
$prior = -1
foreach ($entry in $installOrder.GetEnumerator()) {
    if ($entry.Value -le $prior) {
        throw "Install gate order is invalid at: $($entry.Key)"
    }
    $prior = $entry.Value
}

$removalStart = $sources.Common.IndexOf(
    "function Invoke-ComoteReceiptOwnedRemoval",
    [StringComparison]::Ordinal
)
if ($removalStart -lt 0) {
    throw "The receipt-owned removal function is missing."
}
$removalSource = $sources.Common.Substring($removalStart)
$removalOrder = [ordered]@{
    ServiceStop = $removalSource.IndexOf(
        "Stop-ComoteBrokerServiceExact",
        [StringComparison]::Ordinal
    )
    DriverRemove = $removalSource.IndexOf(
        "-Command remove",
        [StringComparison]::Ordinal
    )
    RecoveryGate = $removalSource.IndexOf(
        "ExitCode -in @(23, 36)",
        [StringComparison]::Ordinal
    )
    DriverStatus = $removalSource.IndexOf(
        "-Command status",
        [StringComparison]::Ordinal
    )
    ServiceDelete = $removalSource.IndexOf(
        "Delete-ComoteBrokerServiceExact",
        [StringComparison]::Ordinal
    )
}
$prior = -1
foreach ($entry in $removalOrder.GetEnumerator()) {
    if ($entry.Value -le $prior) {
        throw "Removal gate order is invalid at: $($entry.Key)"
    }
    $prior = $entry.Value
}

foreach ($token in @(
    "--runtime win-x64",
    "--self-contained true",
    "--no-restore",
    "-p:RestoreLockedMode=true",
    "-p:RuntimeFrameworkVersion=10.0.10",
    "-p:TargetLatestRuntimePatch=false",
    "-p:PublishSingleFile=true",
    "App\Client",
    "App\Manager",
    "App\Broker",
    "driverPackageRoot",
    "ComoteDriverInstaller.exe",
    "ExpectedSigningReceiptSha256",
    "ExpectedDriverManifestSha256",
    "ExpectedPinnedInstallerSha256",
    "ExpectedRegressionGateSha256",
    "ExpectedNuGetSbomSha256",
    "ExpectedSourceInventorySha256",
    "ExpectedCodeSigningCertificateThumbprint",
    "UNPINNED-INSTALL-MUST-REJECT",
    "Start Comote Client Virtual HID.cmd",
    "--manager-hub --virtual-hid",
    "Start Comote Manager Hub.cmd",
    '"%~dp0ComoteManager.exe" --manager-hub',
    "START HERE - Manager Hub.cmd",
    "START HERE - Client Virtual HID.cmd",
    "%ProgramFiles%\Comote\VirtualHidPreview\",
    "App\Manager\ComoteManager.exe",
    "App\Client\ComoteClient.exe",
    "if not exist",
    "exit /b 1",
    "VMware validation-only unified bundle",
    'packageRole = "validation-unified"',
    "runtimePolicy",
    "validationTools",
    "Validation/Comote.MediaGate.exe",
    "REGRESSION_GATE.json",
    "MEDIA_GATE.json",
    "NuGetLocks",
    "NUGET_SBOM.json",
    "FFMPEG_ASSET_RECEIPT.json",
    "LICENSE.SIPSorceryMedia.FFmpeg.LGPL-2.1.txt",
    ".release-manifest.sha256",
    "zipPath.sha256",
    "No driver was installed, loaded, or tested"
)) {
    Assert-ContainsText `
        -Text $sources.Build `
        -Token $token `
        -Description "host-safe release build contract"
}
Assert-NotContainsText `
    -Text $sources.Build `
    -Token "--manager-hub --virtual-hid --setup" `
    -Description "forced Client setup launcher argument"

$launcherGenerationSource = $sources.Build + "`n" + $sources.Exporter
foreach ($forbiddenLauncherFlag in @(
    "--setup",
    "--access-key",
    "--password",
    "--allow-remote-tasks"
)) {
    Assert-NotContainsText `
        -Text $launcherGenerationSource `
        -Token $forbiddenLauncherFlag `
        -Description "generated launcher command-line flag"
}

foreach ($token in @(
    "COMOTE-VIRTUAL-HID-PREVIEW-SOURCE-V1",
    "ExpectedSourceInventorySha256",
    "Get-ComoteSourceInventoryHash",
    "global.json",
    "NuGet.config",
    "Directory.Build.props",
    "Directory.Build.targets",
    "Directory.Build.rsp",
    "Directory.Packages.props",
    "Directory.Packages.targets",
    "MSBuild.rsp",
    "InputCore.SelfTest",
    "HostInputSelfTest",
    "ViewerLifecycleSelfTest",
    "RemoteFileSenderSelfTest",
    "SecureChannelSelfTest",
    "HubTransportSelfTest"
)) {
    Assert-ContainsText `
        -Text ($sources.SourceInventory + "`n" + $sources.NewRelease) `
        -Token $token `
        -Description "isolated source-inventory contract"
}

foreach ($token in @(
    "Invoke-VirtualHidPreviewRegressionGate.ps1",
    "REGRESSION_GATE.json",
    "NUGET_SBOM.json",
    "MEDIA_GATE.json",
    "-ExpectedRegressionGateSha256",
    "-ExpectedNuGetSbomSha256",
    "-ExpectedSourceInventorySha256"
)) {
    Assert-ContainsText `
        -Text $sources.NewRelease `
        -Token $token `
        -Description "pre-driver gate-to-build handoff contract"
}

foreach ($token in @(
    'requiredSdkVersion = "10.0.302"',
    'requiredRuntimeVersion = "10.0.10"',
    "--locked-mode",
    "--no-restore",
    "-p:RestoreLockedMode=true",
    "InputCore.SelfTest",
    "HostInputSelfTest",
    "InputBroker",
    "ViewerLifecycleSelfTest",
    "RemoteFileSenderSelfTest",
    "SecureChannelSelfTest",
    "HubTransportSelfTest",
    "All 15 InputCore self-tests passed.",
    "Host input pure self-test passed.",
    "InputBroker pure self-test passed.",
    "PASS: clipboard STA worker can rejoin after timeout",
    "All RemoteFileSender self-tests passed.",
    "6/6 secure-channel tests passed.",
    "15/15 hardened hub transport tests passed.",
    "Test-Phase2Boundary.ps1",
    "Test-InstallerBoundary.ps1",
    "Test-Phase2FinalEvidenceParser.ps1",
    "Test-Phase2FinalE2EBoundary.ps1",
    "Test-FFmpegLgplBoundary.ps1",
    "Test-VirtualHidPreviewBoundary.ps1",
    "--include-transitive",
    "--vulnerable",
    '"--format", "json"',
    "contentHash",
    "nupkgSha512",
    "licenseExpression",
    "provenanceUrls",
    "NUGET_SBOM.json",
    "REGRESSION_GATE.json",
    "MEDIA_GATE.json",
    "FFMPEG_ASSET_RECEIPT.json",
    "--acknowledge-release-vm",
    "noDriverDeviceInputOrSystemMutation",
    "RuntimeFrameworkVersion",
    "TargetLatestRuntimePatch",
    "RollForward",
    "4614952",
    "58859F85A30CC71313B281898E7CFBDBB9ECCB95AE2A3F865329EFD47EBF31BB",
    "private static void InstallService",
    "DataProtectionScope.LocalMachine",
    "DefaultPassword"
)) {
    Assert-ContainsText `
        -Text $sources.RegressionGate `
        -Token $token `
        -Description "pre-driver regression/SBOM contract"
}
Assert-NotContainsText `
    -Text $sources.RegressionGate `
    -Token "--force-evaluate" `
    -Description "lock-file force evaluation"

Assert-ContainsText `
    -Text $sources.MediaGateLock `
    -Token '"net10.0-windows7.0/win-x64": {}' `
    -Description "MediaGate source-locked win-x64 target"

foreach ($token in @(
    "NUGET_SBOM.json",
    "mandatory resolved authority",
    "packages.lock.json",
    "contentHash",
    ".nupkg.sha512",
    "zero direct or transitive vulnerable packages",
    "Microsoft.Extensions.Hosting.WindowsServices | 10.0.10"
)) {
    Assert-ContainsText `
        -Text $sources.DirectDependenciesNotice `
        -Token $token `
        -Description "completed locked NuGet notice"
}

foreach ($token in @(
    "n8.1.2-32-gcfa62de001-20260730",
    "exact seven",
    "six-file topology",
    "FFMPEG_ASSET_RECEIPT.json",
    "Validation/REGRESSION_GATE.json",
    "Validation/Comote.MediaGate.exe",
    "validationTools",
    "out-of-band-hash-bound"
)) {
    Assert-ContainsText `
        -Text $sources.FfmpegNotice `
        -Token $token `
        -Description "frozen FFmpeg notice/gate contract"
}

foreach ($token in @(
    '"schemaVersion": 2',
    '"component": "FFmpeg"',
    '"build": "n8.1.2-32-gcfa62de001-20260730"',
    '"license": "LGPL-3.0-or-later"',
    '"gplEnabled": false',
    '"name": "avcodec-62.dll"',
    '"name": "avdevice-62.dll"',
    '"name": "avfilter-11.dll"',
    '"name": "avformat-62.dll"',
    '"name": "avutil-60.dll"',
    '"name": "swresample-6.dll"',
    '"name": "swscale-9.dll"'
)) {
    Assert-ContainsText `
        -Text $sources.FfmpegReceipt `
        -Token $token `
        -Description "external FFmpeg receipt identity"
}

$ffmpegManifest = $sources.FfmpegManifest |
    ConvertFrom-Json -ErrorAction Stop
$ffmpegReceipt = $sources.FfmpegReceipt |
    ConvertFrom-Json -ErrorAction Stop
$receiptCanonical = [PSCustomObject][ordered]@{
    schemaVersion = $ffmpegReceipt.schemaVersion
    build = $ffmpegReceipt.build
    license = $ffmpegReceipt.license
    architecture = $ffmpegReceipt.architecture
    redistributable = $ffmpegReceipt.redistributable
    gplEnabled = $ffmpegReceipt.gplEnabled
    softwareH264Fallback = $ffmpegReceipt.softwareH264Fallback
    encoderOptions = $ffmpegReceipt.encoderOptions
    archive = $ffmpegReceipt.archive
    publisherChecksums = $ffmpegReceipt.publisherChecksums
    source = $ffmpegReceipt.source
    managedComponents = $ffmpegReceipt.managedComponents
    abiMajors = $ffmpegReceipt.abiMajors
    files = $ffmpegReceipt.files
}
if ([string]$ffmpegReceipt.component -cne "FFmpeg" -or
    ($receiptCanonical | ConvertTo-Json -Depth 20 -Compress) -cne
        ($ffmpegManifest | ConvertTo-Json -Depth 20 -Compress)) {
    throw "The external FFmpeg receipt does not mirror the canonical manifest."
}

foreach ($token in @(
    "Export-VirtualHidPreviewRolePackages.ps1",
    "export-role-candidate",
    "candidateIndexPath",
    "candidateIndexSha256",
    "candidateRoleHashes",
    "promotedIndexPath",
    "promotedIndexSha256"
)) {
    Assert-ContainsText `
        -Text $sources.Promotion `
        -Token $token `
        -Description "promotion role-export contract"
}

foreach ($token in @(
    "Assert-ComoteCandidateRoleOutput",
    "Assert-ComotePromotedRoleIndex",
    "PROMOTED-ROLE-INDEX.json",
    "promoted-role-packages",
    "client-virtual-hid",
    ".partial-",
    "[IO.Directory]::Move"
)) {
    Assert-ContainsText `
        -Text $sources.Exporter `
        -Token $token `
        -Description "atomic role-package export contract"
}

$verifierStandardPattern =
    '(?s)-Arguments\s+@\(\s*"/standard",\s*"/driver",\s*' +
        '"ComoteVirtualHidPhase2\.sys"\s*\)'
$verifierBootPattern =
    '(?s)-Arguments\s+@\(\s*"/bootmode",\s*"oneboot"\s*\)'
$verifierResetPattern =
    '(?s)-Arguments\s+@\(\s*"/reset"\s*\)'
if ($sources.Promotion -cnotmatch $verifierStandardPattern -or
    $sources.Promotion -cnotmatch $verifierBootPattern -or
    $sources.Promotion -cnotmatch $verifierResetPattern) {
    throw "The exact oneboot Driver Verifier command contract is missing."
}
$verifierDriverTargets = [regex]::Matches(
    $sources.Promotion,
    '(?s)"/driver"\s*,\s*"([^"]+)"'
)
if ($verifierDriverTargets.Count -ne 1 -or
    $verifierDriverTargets[0].Groups[1].Value -cne
        "ComoteVirtualHidPhase2.sys") {
    throw "Driver Verifier names an unexpected driver target."
}

foreach ($token in @(
    "Read-ComoteProtectedReceipt",
    "Invoke-ComoteReceiptOwnedRemoval",
    "Only changes owned by the protected receipt",
    "intentionally retained",
    "TESTSIGNING, Secure Boot, and HVCI were not changed"
)) {
    Assert-ContainsText `
        -Text $sources.Uninstall `
        -Token $token `
        -Description "receipt-owned uninstall contract"
}

foreach ($token in @(
    "must already",
    "out-of-band",
    "Sign out",
    "access key",
    "never enable TESTSIGNING",
    "never change Secure Boot",
    "HVCI or memory-integrity",
    "VM-only",
    "VMware validation-only",
    "two-machine",
    "Client+Broker+driver role ZIP",
    "Windows DPAPI",
    "Get-VirtualHidPreviewSourceInventory.ps1",
    "New-VirtualHidPreviewReleaseInVm.ps1",
    "Invoke-VirtualHidPreviewVmPromotion.ps1",
    "oneboot",
    "20/not-installed",
    "secure desktop",
    "Ctrl+Alt+Del",
    "Unicode text-injection",
    "seven-library shared LGPL set",
    "direct/transitive",
    "10.0.302",
    "10.0.10",
    "locked",
    "zero direct/transitive vulnerable packages",
    "NUGET_SBOM.json",
    "REGRESSION_GATE.json",
    "MEDIA_GATE.json",
    "supported/ESU test environment",
    "exact singleton",
    "Authenticated Users",
    "account/session",
    "FFMPEG_ASSET_RECEIPT.json",
    "validationTools",
    "Broker logs",
    "Windows 10 Home",
    "19045",
    "any UBR",
    "OS logs",
    "23",
    "36"
)) {
    Assert-ContainsText `
        -Text $sources.Readme `
        -Token $token `
        -Description "release safety documentation"
}

Write-Host "Comote Virtual HID preview static boundary passed." `
    -ForegroundColor Green
Write-Host "No project was built or published."
Write-Host "No certificate, service, group, BCD, device, or driver state was changed."
