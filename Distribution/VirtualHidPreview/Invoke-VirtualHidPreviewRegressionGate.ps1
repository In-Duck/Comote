#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [switch]$AcknowledgeDisposableVm,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$SnapshotName,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$SourceRoot,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedSourceInventorySha256,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$ReleaseHandoffRoot
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot "VirtualHidPreview.Common.ps1")

$requiredSdkVersion = "10.0.302"
$requiredRuntimeVersion = "10.0.10"
$includedRoots = @(
    "ApiCheck",
    "Driver",
    "Host",
    "Viewer",
    "InputCore",
    "InputCore.SelfTest",
    "HostInputSelfTest",
    "InputBroker",
    "ViewerLifecycleSelfTest",
    "RemoteFileSenderSelfTest",
    "SecureChannelSelfTest",
    "HubTransportSelfTest",
    "ffmpeg",
    "Distribution/VirtualHidPreview"
)
$excludedSegments = @(".git", ".vs", "artifacts", "bin", "obj")
$backupSuffixPattern =
    '(?i)(?:\.bak|\.backup|\.codex-backup|' +
    '\.servicecredential-backup|\.autoclipboard-backup|[-.]backup)$'

function Get-ComoteGateRootInputs {
    param(
        [Parameter(Mandatory)]
        [string]$Root
    )

    $allowed = @(
        ".editorconfig",
        "global.json",
        "NuGet.config",
        "nuget.config",
        "Directory.Build.props",
        "Directory.Build.targets",
        "Directory.Build.rsp",
        "Directory.Packages.props",
        "Directory.Packages.targets",
        "MSBuild.rsp"
    )
    $result = @()
    foreach ($file in @(
        Get-ChildItem `
            -LiteralPath $Root `
            -File `
            -Force `
            -ErrorAction Stop
    )) {
        $candidate =
            $file.Name -ieq ".editorconfig" -or
            $file.Name -ieq "global.json" -or
            $file.Name -ieq "nuget.config" -or
            $file.Name -ieq "MSBuild.rsp" -or
            $file.Name -imatch '^Directory\.(?:Build|Packages)\.' -or
            $file.Extension -in @(".props", ".targets", ".rsp")
        if (-not $candidate) {
            continue
        }
        if ($allowed -cnotcontains $file.Name) {
            throw "Unhandled or incorrectly cased root build input: $($file.Name)"
        }
        [void](Assert-ComoteOrdinaryLocalPath `
            -LiteralPath $file.FullName `
            -Directory $false `
            -Description "Regression root build input")
        $result += $file
    }
    if (@(
            $result |
                Where-Object {
                    $_.Name -in @("NuGet.config", "nuget.config")
                }
        ).Count -gt 1) {
        throw "NuGet.config and nuget.config are ambiguous at the source root."
    }
    return @($result | Sort-Object Name)
}

function Get-ComoteGateSourceRecords {
    param(
        [Parameter(Mandatory)]
        [string]$Root
    )

    $records = @()
    foreach ($file in @(Get-ComoteGateRootInputs -Root $Root)) {
        $records += [PSCustomObject][ordered]@{
            path = $file.Name
            sourcePath = $file.FullName
            length = [int64]$file.Length
            sha256 = Get-ComoteSha256 -LiteralPath $file.FullName
        }
    }
    foreach ($includedRoot in $includedRoots) {
        $path = Join-Path $Root $includedRoot.Replace('/', '\')
        if (-not (Test-Path -LiteralPath $path -PathType Container)) {
            throw "Required regression source directory is missing: $includedRoot"
        }
        foreach ($file in @(
            Get-ChildItem `
                -LiteralPath $path `
                -File `
                -Force `
                -Recurse `
                -ErrorAction Stop
        )) {
            $relative = $file.FullName.Substring(
                $Root.TrimEnd('\').Length + 1
            ).Replace('\', '/')
            if ($file.Name -match $backupSuffixPattern) {
                throw "Regression gate rejects backup artifact: $relative"
            }
            $segments = @($relative.Split('/'))
            if (@($segments | Where-Object {
                    $excludedSegments -contains $_
                }).Count -ne 0) {
                continue
            }
            [void](Assert-ComoteOrdinaryLocalPath `
                -LiteralPath $file.FullName `
                -Directory $false `
                -Description "Regression source file")
            $records += [PSCustomObject][ordered]@{
                path = $relative
                sourcePath = $file.FullName
                length = [int64]$file.Length
                sha256 = Get-ComoteSha256 -LiteralPath $file.FullName
            }
        }
    }
    return @($records | Sort-Object path)
}

function Get-ComoteGateSourceInventoryHash {
    param(
        [Parameter(Mandatory)]
        [object[]]$Records
    )

    $lines = @(
        "COMOTE-VIRTUAL-HID-PREVIEW-SOURCE-V1"
        foreach ($record in $Records) {
            "{0}|{1}|{2}" -f
                $record.path,
                $record.length,
                $record.sha256
        }
    )
    $bytes = (New-Object Text.ASCIIEncoding).GetBytes(
        (($lines -join "`r`n") + "`r`n")
    )
    return [BitConverter]::ToString(
        [Security.Cryptography.SHA256]::Create().ComputeHash($bytes)
    ).Replace("-", "")
}

function Get-ComoteGateRequiredSourceText {
    param(
        [Parameter(Mandatory)]
        [object[]]$Records,

        [Parameter(Mandatory)]
        [string]$RelativePath
    )

    $matches = @(
        $Records |
            Where-Object { [string]$_.path -ceq $RelativePath }
    )
    if ($matches.Count -ne 1) {
        throw "Required source must be inventoried exactly once: $RelativePath"
    }
    try {
        return [IO.File]::ReadAllText(
            [string]$matches[0].sourcePath,
            (New-Object Text.UTF8Encoding($false, $true))
        )
    }
    catch {
        throw "Required source is not strict UTF-8: $RelativePath"
    }
}

function Get-ComoteGateRequiredSourceSection {
    param(
        [Parameter(Mandatory)]
        [string]$Text,

        [Parameter(Mandatory)]
        [string]$StartToken,

        [Parameter(Mandatory)]
        [string]$EndToken,

        [Parameter(Mandatory)]
        [string]$Description
    )

    $start = $Text.IndexOf($StartToken, [StringComparison]::Ordinal)
    if ($start -lt 0 -or
        $Text.IndexOf(
            $StartToken,
            $start + $StartToken.Length,
            [StringComparison]::Ordinal) -ge 0) {
        throw "$Description start boundary is not an exact singleton."
    }
    $end = $Text.IndexOf(
        $EndToken,
        $start + $StartToken.Length,
        [StringComparison]::Ordinal)
    if ($end -lt 0) {
        throw "$Description end boundary was not found."
    }
    return $Text.Substring($start, $end - $start)
}

function Assert-ComoteGateClipboardSourceContractLegacyUnused {
    param([Parameter(Mandatory)][object[]]$Records)

    foreach ($record in @(
        $Records |
            Where-Object {
                [string]$_.path.StartsWith(
                    "Viewer/",
                    [StringComparison]::Ordinal)
            }
    )) {
        $bytes = [IO.File]::ReadAllBytes([string]$record.sourcePath)
        $ascii = [Text.Encoding]::ASCII.GetString($bytes)
        if ($ascii.IndexOf(
                "AutoClipboard",
                [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw "Viewer source contains forbidden AutoClipboard material."
        }
        if ([string]$record.path -match
                '^Viewer/(?:.*/)?(?:AppSettings|SettingsWindow)[^/]*$') {
            try {
                $settingsText = [IO.File]::ReadAllText(
                    [string]$record.sourcePath,
                    (New-Object Text.UTF8Encoding($false, $true))
                )
            }
            catch {
                throw "Viewer settings source is not strict UTF-8."
            }
            if ($settingsText -match
                '(?im)^[^\r\n]*Clipboard[^\r\n]*' +
                '(?:=\s*true\b|DefaultValue\s*\(\s*true\s*\))') {
                throw "Viewer settings contain default-on clipboard behavior."
            }
        }
    }

    $protocol = Get-ComoteGateRequiredSourceText `
        -Records $Records `
        -RelativePath "InputCore/RemoteClipboardConsentProtocol.cs"
    foreach ($token in @(
        "public static class RemoteClipboardConsentProtocol",
        "public const byte MessageType = 0x24;",
        "public const byte Disabled = 0;",
        "public const byte Enabled = 1;",
        "public const int MessageSize = 2;",
        "public static byte[] Create(bool enabled)",
        "public static bool TryParse(",
        "payload.Length != MessageSize",
        "payload[1] is not Disabled and not Enabled"
    )) {
        if ([regex]::Matches(
                $protocol,
                [regex]::Escape($token)).Count -ne 1) {
            throw "The shared clipboard consent protocol is not exact."
        }
    }

    $host = Get-ComoteGateRequiredSourceText `
        -Records $Records `
        -RelativePath "Host/WebRTCManager.cs"
    $viewer = Get-ComoteGateRequiredSourceText `
        -Records $Records `
        -RelativePath "Viewer/VideoReceiver.cs"
    if ([regex]::Matches(
            $host,
            [regex]::Escape("RemoteClipboardConsentProtocol")).Count -lt 2 -or
        [regex]::Matches(
            $viewer,
            [regex]::Escape("RemoteClipboardConsentProtocol")).Count -lt 2) {
        throw "Host and Viewer must consume the shared consent protocol."
    }

    $hostConnection = Get-ComoteGateRequiredSourceSection `
        -Text $host `
        -StartToken "connection.onconnectionstatechange += (state) =>" `
        -EndToken "connection.ondatachannel += (channel) =>" `
        -Description "Host connection callback"
    if ($hostConnection.IndexOf(
            "StartClipboardMonitoring(",
            [StringComparison]::Ordinal) -ge 0) {
        throw "Host connection callback starts clipboard monitoring."
    }
    if ([regex]::Matches(
            $host,
            [regex]::Escape("StartClipboardMonitoring(")).Count -ne 2) {
        throw "Host clipboard monitoring has an unexpected start call site."
    }
    $hostApply = Get-ComoteGateRequiredSourceSection `
        -Text $host `
        -StartToken "private bool ApplyClipboardConsent(" `
        -EndToken "private bool IsClipboardSessionEnabledCandidate(" `
        -Description "Host clipboard consent application"
    if ($hostApply -cnotmatch
        '(?s)if\s*\(enabled\).*StartClipboardMonitoring\(\).*' +
        'else.*StopClipboardMonitoring\(\)') {
        throw "Host monitoring is not conditional on explicit consent."
    }
    $hostMessage = Get-ComoteGateRequiredSourceSection `
        -Text $host `
        -StartToken "case MSG_CLIPBOARD:" `
        -EndToken "default:" `
        -Description "Host clipboard message"
    $hostGuard = $hostMessage.IndexOf(
        "TryGetClipboardConsentEpoch(",
        [StringComparison]::Ordinal)
    $hostSet = $hostMessage.IndexOf(
        "SetClipboardOnSta(",
        [StringComparison]::Ordinal)
    if ($hostGuard -lt 0 -or $hostSet -le $hostGuard) {
        throw "Host clipboard write is not preceded by a session guard."
    }
    $hostRevoke = Get-ComoteGateRequiredSourceSection `
        -Text $host `
        -StartToken "private bool RevokeControlSession(" `
        -EndToken "private void ClosePeerConnectionForReplacement(" `
        -Description "Host clipboard revocation"
    foreach ($token in @(
        "_clipboardSessionEnabled = false;",
        "_clipboardConsentEpoch++;",
        "_lastClipboardText = null;",
        "StopClipboardMonitoring();"
    )) {
        if ($hostRevoke.IndexOf($token, [StringComparison]::Ordinal) -lt 0) {
            throw "Host revocation does not reset clipboard consent state."
        }
    }

    $viewerMessage = Get-ComoteGateRequiredSourceSection `
        -Text $viewer `
        -StartToken "case MSG_CLIPBOARD" `
        -EndToken "default:" `
        -Description "Viewer clipboard receive"
    $viewerEnabled = $viewerMessage.IndexOf(
        "_clipboardSessionEnabled",
        [StringComparison]::Ordinal)
    $viewerRequested = $viewerMessage.IndexOf(
        "_clipboardConsentRequested",
        [StringComparison]::Ordinal)
    $viewerCallback = $viewerMessage.IndexOf(
        "OnClipboardReceived?.Invoke(text)",
        [StringComparison]::Ordinal)
    if ($viewerEnabled -lt 0 -or $viewerRequested -lt 0 -or
        $viewerCallback -le $viewerEnabled -or
        $viewerCallback -le $viewerRequested) {
        throw "Viewer clipboard receive lacks both consent guards."
    }
    $viewerSend = Get-ComoteGateRequiredSourceSection `
        -Text $viewer `
        -StartToken "public void SendClipboard(string text)" `
        -EndToken "public void SendMonitorSwitch()" `
        -Description "Viewer clipboard send"
    if ($viewerSend.IndexOf(
            "if (!ClipboardSessionEnabled)",
            [StringComparison]::Ordinal) -lt 0 -or
        $viewerSend.IndexOf(
            "msg[0] = MSG_CLIPBOARD;",
            [StringComparison]::Ordinal) -le
        $viewerSend.IndexOf(
            "if (!ClipboardSessionEnabled)",
            [StringComparison]::Ordinal)) {
        throw "Viewer clipboard send lacks an active-session guard."
    }
    foreach ($sectionSpec in @(
        @(
            "private void RevokeControlLocked(bool closeChannels)",
            "private void TrySendReleaseAllLocked()",
            "Viewer control revocation"
        ),
        @(
            "private bool CloseInputChannelForProtocolViolationLocked(",
            "private bool CloseFileChannelLocked(",
            "Viewer input-channel close"
        ),
        @(
            "public void Reset()",
            "public async Task StartAsync(",
            "Viewer reset"
        )
    )) {
        $section = Get-ComoteGateRequiredSourceSection `
            -Text $viewer `
            -StartToken ([string]$sectionSpec[0]) `
            -EndToken ([string]$sectionSpec[1]) `
            -Description ([string]$sectionSpec[2])
        if ($section.IndexOf(
                "_clipboardConsentRequested = false;",
                [StringComparison]::Ordinal) -lt 0 -or
            $section.IndexOf(
                "_clipboardSessionEnabled = false;",
                [StringComparison]::Ordinal) -lt 0) {
            throw "$($sectionSpec[2]) does not clear clipboard consent."
        }
        if ([string]$sectionSpec[2] -ceq "Viewer reset" -and
            $section.IndexOf(
                "NotifyClipboardConsentChanged(false);",
                [StringComparison]::Ordinal) -lt 0) {
            throw "Viewer reset does not publish clipboard revocation."
        }
    }
    $viewerConnection = Get-ComoteGateRequiredSourceSection `
        -Text $viewer `
        -StartToken "private void HandleConnectionStateChanged(" `
        -EndToken "private void RevokeControlLocked(bool closeChannels)" `
        -Description "Viewer terminal connection handling"
    if ($viewerConnection.IndexOf(
            "RTCPeerConnectionState.disconnected",
            [StringComparison]::Ordinal) -lt 0 -or
        $viewerConnection.IndexOf(
            "RTCPeerConnectionState.failed",
            [StringComparison]::Ordinal) -lt 0 -or
        $viewerConnection.IndexOf(
            "RTCPeerConnectionState.closed",
            [StringComparison]::Ordinal) -lt 0 -or
        $viewerConnection.IndexOf(
            "RevokeControlLocked(closeChannels: true);",
            [StringComparison]::Ordinal) -lt 0) {
        throw "Viewer disconnect does not revoke clipboard consent."
    }

    return [PSCustomObject][ordered]@{
        backupArtifactCount = 0
        clipboardDefaultOff = $true
        explicitClipboardConsentProtocol = $true
    }
}

function Assert-ComoteGateClipboardSourceContract {
    param([Parameter(Mandatory)][object[]]$Records)

    foreach ($record in @($Records | Where-Object {
        [string]$_.path.StartsWith("Viewer/", [StringComparison]::Ordinal)
    })) {
        $bytes = [IO.File]::ReadAllBytes([string]$record.sourcePath)
        $ascii = [Text.Encoding]::ASCII.GetString($bytes)
        if ($ascii.IndexOf(
                "AutoClipboard", [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw "Viewer source contains forbidden AutoClipboard material."
        }
    }

    $sources = @{
        Protocol = Get-ComoteGateRequiredSourceText `
            -Records $Records `
            -RelativePath "InputCore/RemoteClipboardConsentProtocol.cs"
        Host = Get-ComoteGateRequiredSourceText `
            -Records $Records `
            -RelativePath "Host/WebRTCManager.cs"
        Transition = Get-ComoteGateRequiredSourceText `
            -Records $Records `
            -RelativePath "Host/ClipboardMonitoringTransition.cs"
        Worker = Get-ComoteGateRequiredSourceText `
            -Records $Records `
            -RelativePath "Host/ClipboardStaWorker.cs"
        Receiver = Get-ComoteGateRequiredSourceText `
            -Records $Records `
            -RelativePath "Viewer/VideoReceiver.cs"
        Consent = Get-ComoteGateRequiredSourceText `
            -Records $Records `
            -RelativePath "Viewer/ClipboardConsentState.cs"
        Main = Get-ComoteGateRequiredSourceText `
            -Records $Records `
            -RelativePath "Viewer/MainWindow.xaml.cs"
    }
    foreach ($token in @(
        "public const byte MessageType = 0x24;",
        "public const byte Disabled = 0;",
        "public const byte Enabled = 1;",
        "public const int MessageSize = 2;",
        "public static byte[] Create(bool enabled)",
        "public static bool TryParse("
    )) {
        if ([regex]::Matches(
                $sources.Protocol, [regex]::Escape($token)).Count -ne 1) {
            throw "The shared clipboard consent protocol is not exact."
        }
    }
    foreach ($binding in @(
        @($sources.Consent, "ClipboardConsentSnapshot(", "final snapshot"),
        @($sources.Consent, "private long _epoch;", "monotonic epoch"),
        @($sources.Consent, "IncrementEpochLocked();", "epoch transition"),
        @($sources.Consent, "TryAcquireActiveLease(", "consent lease"),
        @($sources.Consent, "snapshot.Epoch <= Epoch", "stale projection"),
        @($sources.Receiver, "TryCaptureActiveEpoch(", "receiver epoch capture"),
        @($sources.Receiver, "IsClipboardConsentEpochActive(", "receiver recheck"),
        @($sources.Receiver, "TryApplyClipboardWithConsentLease(", "receiver lease"),
        @($sources.Host, "TryAcquireClipboardLease(", "host lease"),
        @($sources.Host, "IsClipboardSessionEnabled(", "host recheck"),
        @($sources.Host, "ApplyClipboardMonitoringTransition(", "transition apply"),
        @($sources.Transition,
            "transition.Epoch == currentEpoch", "stale transition epoch"),
        @($sources.Transition,
            "transition.Enabled == currentEnabled", "stale transition state"),
        @($sources.Worker, "TimeSpan.FromSeconds(5)", "bounded STA join"),
        @($sources.Worker, "_pendingClipboardSet = action;", "coalesced set"),
        @($sources.Worker, "_pendingPoll ??= action;", "coalesced poll"),
        @($sources.Worker, "_thread.Join(_disposeJoinTimeout)", "STA rejoin"),
        @($sources.Main, "_clipboardConsentUi.TryApply(snapshot)", "UI snapshot"),
        @($sources.Main,
            "receiver.TryApplyClipboardWithConsentLease(", "UI receive lease"),
        @($sources.Main, "ReferenceEquals(_receiver, receiver)", "UI receiver recheck"),
        @($sources.Main, "_connectedHostId", "UI host recheck")
    )) {
        if ([string]$binding[0].IndexOf(
                [string]$binding[1], [StringComparison]::Ordinal) -lt 0) {
            throw "The clipboard $($binding[2]) contract is missing."
        }
    }
    if ([regex]::Matches(
            $sources.Host,
            [regex]::Escape("_clipboardWorker = new ClipboardStaWorker();")
        ).Count -ne 1 -or
        [regex]::Matches(
            $sources.Worker,
            [regex]::Escape("new Thread(Run)")
        ).Count -ne 1) {
        throw "Clipboard work is not confined to one bounded STA worker."
    }
    $send = Get-ComoteGateRequiredSourceSection `
        -Text $sources.Receiver `
        -StartToken "public bool SendClipboard(string text)" `
        -EndToken "public void SendMonitorSwitch()" `
        -Description "Atomic Viewer clipboard send"
    $lockIndex = $send.IndexOf("lock (_lifecycleGate)",
        [StringComparison]::Ordinal)
    $consentIndex = $send.IndexOf("_clipboardConsent.Enabled",
        [StringComparison]::Ordinal)
    $readyIndex = $send.IndexOf("IsInputChannelReadyLocked()",
        [StringComparison]::Ordinal)
    $sendIndex = $send.IndexOf("SendSecuredInputPayload(msg)",
        [StringComparison]::Ordinal)
    if ($lockIndex -lt 0 -or $consentIndex -le $lockIndex -or
        $readyIndex -le $lockIndex -or $sendIndex -le $consentIndex -or
        $sendIndex -le $readyIndex) {
        throw "Viewer SendClipboard is not an atomic consent/send operation."
    }
    return [PSCustomObject][ordered]@{
        backupArtifactCount = 0
        clipboardDefaultOff = $true
        explicitClipboardConsentProtocol = $true
    }
}

function Get-ComoteGateTextSha256 {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Text
    )

    return [BitConverter]::ToString(
        [Security.Cryptography.SHA256]::Create().ComputeHash(
            (New-Object Text.UTF8Encoding($false)).GetBytes($Text)
        )
    ).Replace("-", "")
}

function Invoke-ComoteGateProcess {
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,

        [Parameter(Mandatory)]
        [string[]]$Arguments,

        [Parameter(Mandatory)]
        [string]$WorkingDirectory,

        [Parameter(Mandatory)]
        [string]$Description
    )

    [void](Assert-ComoteOrdinaryLocalPath `
        -LiteralPath $FilePath `
        -Directory $false `
        -Description "$Description executable")
    [void](Assert-ComoteOrdinaryLocalPath `
        -LiteralPath $WorkingDirectory `
        -Directory $true `
        -Description "$Description working directory")
    Push-Location -LiteralPath $WorkingDirectory
    try {
        $result = Invoke-ComoteNativeProcess `
            -FilePath $FilePath `
            -Arguments $Arguments
    }
    finally {
        Pop-Location
    }
    if ($result.ExitCode -ne 0) {
        throw "$Description returned exit code $($result.ExitCode)."
    }
    return [PSCustomObject][ordered]@{
        Output = [string]$result.Output
        OutputSha256 = Get-ComoteGateTextSha256 `
            -Text ([string]$result.Output)
    }
}

function Assert-ComoteGateOutput {
    param(
        [Parameter(Mandatory)]
        [string]$Output,

        [Parameter(Mandatory)]
        [string]$SuccessToken,

        [Parameter(Mandatory)]
        [ValidateRange(0, 1000)]
        [int]$ExpectedPassLines,

        [Parameter(Mandatory)]
        [string]$PassLinePattern,

        [Parameter(Mandatory)]
        [string]$Description
    )

    if ([regex]::Matches(
            $Output,
            [regex]::Escape($SuccessToken)
        ).Count -ne 1 -or
        $Output -match '(?im)^\s*(?:FAIL|FAILED|ERROR)[: ]') {
        throw "$Description did not emit its exact success summary."
    }
    if ($ExpectedPassLines -gt 0 -and
        [regex]::Matches(
            $Output,
            $PassLinePattern,
            [Text.RegularExpressions.RegexOptions]::Multiline
        ).Count -ne $ExpectedPassLines) {
        throw "$Description emitted an unexpected PASS count."
    }
}

function Get-ComoteGateProjectSourceHash {
    param(
        [Parameter(Mandatory)]
        [string]$ProjectPath
    )

    $projectDirectory = [IO.Path]::GetDirectoryName($ProjectPath)
    $records = @(
        Get-ChildItem `
            -LiteralPath $projectDirectory `
            -File `
            -Force `
            -Recurse `
            -ErrorAction Stop |
            Where-Object {
                $_.FullName -notmatch '(?i)\\(?:bin|obj)\\'
            } |
            ForEach-Object {
                [void](Assert-ComoteOrdinaryLocalPath `
                    -LiteralPath $_.FullName `
                    -Directory $false `
                    -Description "Regression project source")
                [PSCustomObject]@{
                    path = Get-ComoteRelativePath `
                        -Root $projectDirectory `
                        -Child $_.FullName
                    length = [int64]$_.Length
                    sha256 = Get-ComoteSha256 -LiteralPath $_.FullName
                }
            } |
            Sort-Object path
    )
    $lines = @(
        "COMOTE-REGRESSION-PROJECT-SOURCE-V1"
        foreach ($record in $records) {
            "{0}|{1}|{2}" -f
                $record.path,
                $record.length,
                $record.sha256
        }
    )
    return Get-ComoteGateTextSha256 `
        -Text (($lines -join "`r`n") + "`r`n")
}

function Get-ComoteGateExecutableHash {
    param(
        [Parameter(Mandatory)]
        [string]$ProjectPath,

        [Parameter(Mandatory)]
        [string]$ExecutableName
    )

    $root = Join-Path `
        ([IO.Path]::GetDirectoryName($ProjectPath)) `
        "bin\Release"
    $matches = @(
        Get-ChildItem `
            -LiteralPath $root `
            -Filter $ExecutableName `
            -File `
            -Recurse `
            -ErrorAction Stop
    )
    if ($matches.Count -ne 1) {
        throw "Expected one built executable: $ExecutableName"
    }
    [void](Assert-ComoteOrdinaryLocalPath `
        -LiteralPath $matches[0].FullName `
        -Directory $false `
        -Description "Regression built executable")
    return Get-ComoteSha256 -LiteralPath $matches[0].FullName
}

function Read-ComoteGateJson {
    param(
        [Parameter(Mandatory)]
        [string]$LiteralPath,

        [Parameter(Mandatory)]
        [string]$Description
    )

    $identity = Assert-ComoteOrdinaryLocalPath `
        -LiteralPath $LiteralPath `
        -Directory $false `
        -Description $Description
    if ([uint64]$identity.Identity.Length -gt 67108864) {
        throw "$Description exceeds the regression size limit."
    }
    try {
        return [IO.File]::ReadAllText(
            $identity.FullPath,
            (New-Object Text.UTF8Encoding($false, $true))
        ) | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "$Description is not strict UTF-8 JSON."
    }
}

function Assert-ComoteGateApiCheckContract {
    param([Parameter(Mandatory)][string]$Root)

    $projectPath = Join-Path $Root "ApiCheck\ApiCheck.csproj"
    $settings = New-Object Xml.XmlReaderSettings
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $reader = [Xml.XmlReader]::Create($projectPath, $settings)
    try {
        $document = New-Object Xml.XmlDocument
        $document.XmlResolver = $null
        $document.Load($reader)
    }
    finally {
        $reader.Dispose()
    }
    foreach ($binding in @(
        @("TargetFramework", "net10.0-windows"),
        @("RuntimeIdentifiers", "win-x64"),
        @("RuntimeFrameworkVersion", "10.0.10"),
        @("TargetLatestRuntimePatch", "false"),
        @("RollForward", "Disable"),
        @("RestorePackagesWithLockFile", "true")
    )) {
        $nodes = @($document.GetElementsByTagName([string]$binding[0]))
        if ($nodes.Count -ne 1 -or
            [string]$nodes[0].InnerText.Trim() -cne
                [string]$binding[1]) {
            throw "ApiCheck has an invalid exact runtime/restore property."
        }
    }
    $references = @($document.GetElementsByTagName("PackageReference"))
    if ($references.Count -ne 4) {
        throw "ApiCheck must have exactly four direct package references."
    }
    $referenceMap = New-Object `
        'Collections.Generic.Dictionary[string,string]' `
        ([StringComparer]::Ordinal)
    foreach ($reference in $references) {
        $name = [string]$reference.GetAttribute("Include")
        $version = [string]$reference.GetAttribute("Version")
        if ([string]::IsNullOrWhiteSpace($name) -or
            $referenceMap.ContainsKey($name)) {
            throw "ApiCheck has an invalid direct package reference."
        }
        $referenceMap.Add($name, $version)
    }
    foreach ($binding in @(
        @("Microsoft.Extensions.DependencyInjection.Abstractions", "10.0.10"),
        @("Microsoft.Extensions.Logging.Abstractions", "10.0.10"),
        @("SIPSorcery", "10.0.12"),
        @("SIPSorceryMedia.FFmpeg", "10.0.12")
    )) {
        $name = [string]$binding[0]
        $version = [string]$binding[1]
        if (-not $referenceMap.ContainsKey($name) -or
            [string]$referenceMap[$name] -cne $version) {
            throw "ApiCheck does not pin $name to $version."
        }
    }

    $lock = Read-ComoteGateJson `
        -LiteralPath (Join-Path $Root "ApiCheck\packages.lock.json") `
        -Description "ApiCheck lock"
    $targets = @($lock.dependencies.PSObject.Properties.Name)
    if ($targets.Count -ne 2 -or
        [string]$targets[0] -cne "net10.0-windows7.0" -or
        [string]$targets[1] -cne "net10.0-windows7.0/win-x64") {
        throw "ApiCheck lock has an invalid exact target graph."
    }
    $graph = $lock.dependencies.'net10.0-windows7.0'
    foreach ($binding in @(
        @("Microsoft.Extensions.DependencyInjection.Abstractions", "10.0.10"),
        @("Microsoft.Extensions.Logging.Abstractions", "10.0.10"),
        @("SIPSorcery", "10.0.12"),
        @("SIPSorceryMedia.FFmpeg", "10.0.12")
    )) {
        $name = [string]$binding[0]
        $version = [string]$binding[1]
        $entry = $graph.PSObject.Properties[$name].Value
        if ($null -eq $entry -or
            [string]$entry.type -cne "Direct" -or
            [string]$entry.resolved -cne $version -or
            [string]$entry.contentHash -notmatch '^[A-Za-z0-9+/]+={0,2}$') {
            throw "ApiCheck lock does not pin the exact $name dependency."
        }
    }
    if ([string]$graph.'SIPSorceryMedia.FFmpeg'.dependencies.'FFmpeg.AutoGen' -cne
            "8.1.0" -or
        [string]$graph.'SIPSorceryMedia.FFmpeg'.dependencies.
            'SIPSorceryMedia.Abstractions' -cne "10.0.12") {
        throw "ApiCheck FFmpeg wrapper dependency closure is invalid."
    }
}

function Get-ComoteGateArtifactEntries {
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [Parameter(Mandatory)]
        [ValidateSet("lock", "assets")]
        [string]$Kind
    )

    $result = @()
    foreach ($includedRoot in $includedRoots) {
        $searchRoot = Join-Path $Root $includedRoot.Replace('/', '\')
        $filter = if ($Kind -ceq "lock") {
            "packages.lock.json"
        }
        else {
            "project.assets.json"
        }
        foreach ($file in @(
            Get-ChildItem `
                -LiteralPath $searchRoot `
                -Filter $filter `
                -File `
                -Force `
                -Recurse `
                -ErrorAction Stop
        )) {
            if ($Kind -ceq "assets" -and
                $file.Directory.Name -cne "obj") {
                continue
            }
            [void](Assert-ComoteOrdinaryLocalPath `
                -LiteralPath $file.FullName `
                -Directory $false `
                -Description "Regression $Kind artifact")
            $result += [PSCustomObject][ordered]@{
                path = Get-ComoteRelativePath `
                    -Root $Root `
                    -Child $file.FullName
                length = [int64]$file.Length
                sha256 = Get-ComoteSha256 -LiteralPath $file.FullName
            }
        }
    }
    return @($result | Sort-Object path -Unique)
}

function Test-ComoteGateHttpsUrl {
    param(
        [AllowNull()]
        [string]$Value
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $false
    }
    $uri = $null
    return [Uri]::TryCreate(
            $Value.Trim(),
            [UriKind]::Absolute,
            [ref]$uri
        ) -and
        $uri.Scheme -ceq "https" -and
        -not [string]::IsNullOrWhiteSpace($uri.Host)
}

function Get-ComoteGateNuspecMetadata {
    param(
        [Parameter(Mandatory)]
        [string]$NuspecPath
    )

    [void](Assert-ComoteOrdinaryLocalPath `
        -LiteralPath $NuspecPath `
        -Directory $false `
        -Description "Restored NuGet nuspec")
    $settings = New-Object Xml.XmlReaderSettings
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $reader = [Xml.XmlReader]::Create($NuspecPath, $settings)
    try {
        $document = New-Object Xml.XmlDocument
        $document.XmlResolver = $null
        $document.Load($reader)
    }
    finally {
        $reader.Dispose()
    }
    $metadata = @(
        $document.DocumentElement.ChildNodes |
            Where-Object { $_.LocalName -ceq "metadata" }
    )
    if ($metadata.Count -ne 1) {
        throw "A restored nuspec has ambiguous metadata."
    }
    $licenseNodes = @(
        $metadata[0].ChildNodes |
            Where-Object { $_.LocalName -ceq "license" }
    )
    if ($licenseNodes.Count -gt 1) {
        throw "A restored nuspec has ambiguous license metadata."
    }
    $licenseExpression = $null
    if ($licenseNodes.Count -eq 1 -and
        [string]$licenseNodes[0].GetAttribute("type") -ceq "expression") {
        $candidate = [string]$licenseNodes[0].InnerText.Trim()
        if ($candidate -match '^[A-Za-z0-9.+() -]+$' -and
            $candidate -notmatch
                '(?i)(?:LicenseRef|NOASSERTION|UNKNOWN|SEE LICENSE|PROPRIETARY)') {
            $licenseExpression = $candidate
        }
    }
    $licenseUrlNodes = @(
        $metadata[0].ChildNodes |
            Where-Object { $_.LocalName -ceq "licenseUrl" }
    )
    if ($licenseUrlNodes.Count -gt 1) {
        throw "A restored nuspec has ambiguous license URL metadata."
    }
    $licenseUrl = $null
    if ($licenseUrlNodes.Count -eq 1 -and
        (Test-ComoteGateHttpsUrl `
            -Value ([string]$licenseUrlNodes[0].InnerText))) {
        $licenseUrl = [string]$licenseUrlNodes[0].InnerText.Trim()
    }
    if ([string]::IsNullOrWhiteSpace($licenseExpression) -and
        [string]::IsNullOrWhiteSpace($licenseUrl)) {
        throw "A restored package lacks a clear license expression or HTTPS URL."
    }

    $provenanceCandidates = @()
    foreach ($repository in @(
        $metadata[0].ChildNodes |
            Where-Object { $_.LocalName -ceq "repository" }
    )) {
        $provenanceCandidates += [string]$repository.GetAttribute("url")
    }
    foreach ($nodeName in @("projectUrl", "source")) {
        $provenanceCandidates += @(
            $metadata[0].ChildNodes |
                Where-Object { $_.LocalName -ceq $nodeName } |
                ForEach-Object { [string]$_.InnerText }
        )
    }
    $provenanceUrls = @(
        $provenanceCandidates |
            Where-Object { Test-ComoteGateHttpsUrl -Value $_ } |
            ForEach-Object { $_.Trim() } |
            Sort-Object -Unique
    )
    if ($provenanceUrls.Count -eq 0) {
        throw "A restored package lacks an HTTPS project/repository/source URL."
    }
    return [PSCustomObject][ordered]@{
        licenseExpression = $licenseExpression
        licenseUrl = $licenseUrl
        provenanceUrls = $provenanceUrls
    }
}

function New-ComoteGateNuGetSbom {
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [Parameter(Mandatory)]
        [hashtable]$RoleProjectPaths,

        [Parameter(Mandatory)]
        [string]$SourceInventorySha256,

        [Parameter(Mandatory)]
        [PSObject]$RuntimePolicy,

        [Parameter(Mandatory)]
        [object[]]$LockFiles,

        [Parameter(Mandatory)]
        [string]$OutputPath
    )

    $packages = New-Object `
        'Collections.Generic.Dictionary[string,object]' `
        ([StringComparer]::OrdinalIgnoreCase)
    $packageFolders = New-Object `
        'Collections.Generic.HashSet[string]' `
        ([StringComparer]::OrdinalIgnoreCase)
    foreach ($role in @($RoleProjectPaths.Keys | Sort-Object)) {
        $projectPath = [string]$RoleProjectPaths[$role]
        $lockPath = Join-Path `
            ([IO.Path]::GetDirectoryName($projectPath)) `
            "packages.lock.json"
        $assetsPath = Join-Path `
            ([IO.Path]::GetDirectoryName($projectPath)) `
            "obj\project.assets.json"
        $lock = Read-ComoteGateJson `
            -LiteralPath $lockPath `
            -Description "$role NuGet lock"
        $assets = Read-ComoteGateJson `
            -LiteralPath $assetsPath `
            -Description "$role project assets"
        $folderProperty = $assets.PSObject.Properties["packageFolders"]
        if ($null -eq $folderProperty) {
            throw "$role assets omit packageFolders."
        }
        foreach ($folder in @($assets.packageFolders.PSObject.Properties.Name)) {
            [void]$packageFolders.Add([IO.Path]::GetFullPath($folder))
        }
        $dependencyProperty = $lock.PSObject.Properties["dependencies"]
        if ($null -eq $dependencyProperty) {
            throw "$role lock omits dependencies."
        }
        foreach ($framework in $lock.dependencies.PSObject.Properties) {
            foreach ($packageProperty in $framework.Value.PSObject.Properties) {
                $entry = $packageProperty.Value
                $typeProperty = $entry.PSObject.Properties["type"]
                if ($null -eq $typeProperty) {
                    throw "A NuGet lock dependency omits type."
                }
                $type = [string]$entry.type
                if ($type -ceq "Project") {
                    continue
                }
                if ($type -notin @("Direct", "Transitive", "CentralTransitive")) {
                    throw "A NuGet lock dependency has an unsupported type."
                }
                $resolved = [string]$entry.resolved
                $contentHash = [string]$entry.contentHash
                if ($resolved -notmatch '^[0-9A-Za-z][0-9A-Za-z.+-]*$' -or
                    $contentHash -notmatch '^[A-Za-z0-9+/]+={0,2}$') {
                    throw "A NuGet lock dependency lacks version/SHA512 identity."
                }
                $name = [string]$packageProperty.Name
                $key = "{0}|{1}" -f $name, $resolved
                if (-not $packages.ContainsKey($key)) {
                    $packages.Add($key, [PSCustomObject]@{
                        name = $name
                        resolvedVersion = $resolved
                        contentHash = $contentHash
                        dependencyTypes = New-Object `
                            'Collections.Generic.HashSet[string]' `
                            ([StringComparer]::Ordinal)
                        usedByRoles = New-Object `
                            'Collections.Generic.HashSet[string]' `
                            ([StringComparer]::Ordinal)
                        roleDependencies = New-Object `
                            'Collections.Generic.HashSet[string]' `
                            ([StringComparer]::Ordinal)
                    })
                }
                $package = $packages[$key]
                if ([string]$package.contentHash -cne $contentHash) {
                    throw "A NuGet package has inconsistent SHA512 lock identities."
                }
                $kind = if ($type -ceq "Direct") { "direct" } else { "transitive" }
                [void]$package.dependencyTypes.Add($kind)
                [void]$package.usedByRoles.Add($role)
                [void]$package.roleDependencies.Add("$role|$kind")
            }
        }
    }

    $sbomPackages = @()
    foreach ($package in @($packages.Values | Sort-Object name, resolvedVersion)) {
        if ([string]$package.name -like "Microsoft.Extensions.*" -and
            [string]$package.resolvedVersion -match '^10\.0\.(\d+)(?:[-+].*)?$' -and
            [int]$Matches[1] -lt 10) {
            throw "Microsoft.Extensions.* 10.0.x must be at least 10.0.10."
        }
        $packageDirectories = @()
        foreach ($folder in $packageFolders) {
            $candidate = Join-Path `
                $folder `
                (([string]$package.name).ToLowerInvariant() + "\" +
                    ([string]$package.resolvedVersion).ToLowerInvariant())
            if (Test-Path -LiteralPath $candidate -PathType Container) {
                $packageDirectories += [IO.Path]::GetFullPath($candidate)
            }
        }
        $packageDirectories = @($packageDirectories | Sort-Object -Unique)
        if ($packageDirectories.Count -ne 1) {
            throw "A locked NuGet package cache location is ambiguous."
        }
        [void](Assert-ComoteOrdinaryLocalPath `
            -LiteralPath $packageDirectories[0] `
            -Directory $true `
            -Description "Restored NuGet package directory")
        $nuspecs = @(
            Get-ChildItem `
                -LiteralPath $packageDirectories[0] `
                -Filter "*.nuspec" `
                -File `
                -Force `
                -ErrorAction Stop
        )
        $shaFiles = @(
            Get-ChildItem `
                -LiteralPath $packageDirectories[0] `
                -Filter "*.nupkg.sha512" `
                -File `
                -Force `
                -ErrorAction Stop
        )
        if ($nuspecs.Count -ne 1 -or $shaFiles.Count -ne 1) {
            throw "A restored NuGet package lacks exact nuspec/SHA512 files."
        }
        $sha512 = [IO.File]::ReadAllText(
            $shaFiles[0].FullName,
            (New-Object Text.UTF8Encoding($false, $true))
        ).Trim()
        if ($sha512 -cne [string]$package.contentHash) {
            throw "A restored NuGet package SHA512 differs from its lock."
        }
        $metadata = Get-ComoteGateNuspecMetadata `
            -NuspecPath $nuspecs[0].FullName
        $roleDependencies = @(
            foreach ($binding in @($package.roleDependencies | Sort-Object)) {
                $parts = $binding.Split('|')
                [PSCustomObject][ordered]@{
                    role = $parts[0]
                    dependencyType = $parts[1]
                }
            }
        )
        $sbomPackages += [PSCustomObject][ordered]@{
            name = [string]$package.name
            resolvedVersion = [string]$package.resolvedVersion
            contentHash = [string]$package.contentHash
            nupkgSha512 = $sha512
            dependencyTypes = @($package.dependencyTypes | Sort-Object)
            usedByRoles = @($package.usedByRoles | Sort-Object)
            roleDependencies = $roleDependencies
            licenseExpression = $metadata.licenseExpression
            licenseUrl = $metadata.licenseUrl
            provenanceUrls = @($metadata.provenanceUrls)
            nuspecSha256 = Get-ComoteSha256 -LiteralPath $nuspecs[0].FullName
            sha512FileSha256 = Get-ComoteSha256 `
                -LiteralPath $shaFiles[0].FullName
        }
    }
    if ($sbomPackages.Count -eq 0) {
        throw "The release NuGet SBOM is empty."
    }
    if ($LockFiles.Count -ne 13) {
        throw "The NuGet SBOM must bind exactly 13 canonical lock files."
    }
    foreach ($lockFile in $LockFiles) {
        Assert-ComoteExactProperties `
            -InputObject $lockFile `
            -Expected @("path", "length", "sha256") `
            -Description "NuGet SBOM lock binding"
        if ([string]$lockFile.path -notmatch '(^|/)packages\.lock\.json$' -or
            [int64]$lockFile.length -le 0 -or
            [string]$lockFile.sha256 -cnotmatch '^[0-9A-F]{64}$') {
            throw "The NuGet SBOM contains an invalid lock binding."
        }
    }
    $document = [PSCustomObject][ordered]@{
        schemaVersion = 1
        sourceInventorySha256 = $SourceInventorySha256
        runtimePolicy = $RuntimePolicy
        lockFiles = $LockFiles
        packageCount = $sbomPackages.Count
        packages = $sbomPackages
    }
    Write-ComoteJsonAtomically `
        -LiteralPath $OutputPath `
        -InputObject $document `
        -Depth 20
    return [PSCustomObject][ordered]@{
        Path = $OutputPath
        Sha256 = Get-ComoteSha256 -LiteralPath $OutputPath
        PackageCount = $sbomPackages.Count
    }
}

function Assert-ComoteGateRuntimeIdentifierLocks {
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [Parameter(Mandatory)]
        [object[]]$Projects
    )

    $ridProjects = @($Projects | Where-Object { [bool]$_.ridRestore })
    if ($ridProjects.Count -ne 6) {
        throw "The exact six-project RID-lock set is incomplete."
    }
    $expectedRidProjects = @(
        "ApiCheck", "InputCore", "InputBroker",
        "Host", "Viewer", "MediaGate"
    )
    $actualRidProjects = @($ridProjects | ForEach-Object name | Sort-Object)
    if (($actualRidProjects -join "|") -cne
        (@($expectedRidProjects | Sort-Object) -join "|")) {
        throw "The exact six-project RID-lock identity is invalid."
    }
    foreach ($project in $ridProjects) {
        $projectPath = Join-Path `
            $Root `
            ([string]$project.path).Replace('/', '\')
        $settings = New-Object Xml.XmlReaderSettings
        $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
        $settings.XmlResolver = $null
        $reader = [Xml.XmlReader]::Create($projectPath, $settings)
        try {
            $document = New-Object Xml.XmlDocument
            $document.XmlResolver = $null
            $document.Load($reader)
        }
        finally {
            $reader.Dispose()
        }
        $plural = @($document.GetElementsByTagName("RuntimeIdentifiers"))
        $singular = @($document.GetElementsByTagName("RuntimeIdentifier"))
        if ($plural.Count -ne 1 -or
            [string]$plural[0].InnerText.Trim() -cne "win-x64" -or
            $singular.Count -ne 0) {
            throw ("$($project.name) must declare only the exact " +
                "RuntimeIdentifiers win-x64 property.")
        }
        $lockPath = Join-Path `
            ([IO.Path]::GetDirectoryName($projectPath)) `
            "packages.lock.json"
        $lock = Read-ComoteGateJson `
            -LiteralPath $lockPath `
            -Description "$($project.name) RID lock"
        if (@($lock.dependencies.PSObject.Properties.Name) -cnotcontains
            "net10.0-windows7.0/win-x64") {
            throw "$($project.name) lock omits the exact win-x64 RID target."
        }
    }
}

function Get-ComoteGateRuntimePolicy {
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [Parameter(Mandatory)]
        [object[]]$Projects
    )

    $packageFolders = New-Object `
        'Collections.Generic.HashSet[string]' `
        ([StringComparer]::OrdinalIgnoreCase)
    foreach ($project in @($Projects | Where-Object publishRestore)) {
        $projectPath = Join-Path $Root ([string]$project.path).Replace('/', '\')
        $settings = New-Object Xml.XmlReaderSettings
        $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
        $settings.XmlResolver = $null
        $reader = [Xml.XmlReader]::Create($projectPath, $settings)
        try {
            $document = New-Object Xml.XmlDocument
            $document.XmlResolver = $null
            $document.Load($reader)
        }
        finally {
            $reader.Dispose()
        }
        foreach ($propertyBinding in @(
            @("RuntimeFrameworkVersion", "10.0.10"),
            @("TargetLatestRuntimePatch", "false"),
            @("RollForward", "Disable")
        )) {
            $nodes = @($document.GetElementsByTagName($propertyBinding[0]))
            if ($nodes.Count -ne 1 -or
                [string]$nodes[0].InnerText.Trim() -cne
                    [string]$propertyBinding[1]) {
                throw "$($project.name) does not pin its exact runtime policy."
            }
        }

        $projectDirectory = [IO.Path]::GetDirectoryName($projectPath)
        $lock = Read-ComoteGateJson `
            -LiteralPath (Join-Path $projectDirectory "packages.lock.json") `
            -Description "$($project.name) runtime lock"
        if (@($lock.dependencies.PSObject.Properties.Name) -cnotcontains
            "net10.0-windows7.0/win-x64") {
            throw "$($project.name) lock omits the exact win-x64 RID target."
        }
        $assets = Read-ComoteGateJson `
            -LiteralPath (Join-Path $projectDirectory "obj\project.assets.json") `
            -Description "$($project.name) runtime assets"
        $framework = @(
            $assets.project.frameworks.PSObject.Properties |
                Where-Object {
                    [string]$_.Name -ceq "net10.0-windows7.0"
                }
        )
        if ($framework.Count -ne 1 -or
            $null -eq $framework[0].Value.PSObject.Properties[
                "downloadDependencies"]) {
            throw "$($project.name) assets omit runtime download dependencies."
        }
        $downloads = @($framework[0].Value.downloadDependencies)
        if ($downloads.Count -lt 1) {
            throw "$($project.name) runtime download dependency set is empty."
        }
        foreach ($download in $downloads) {
            if ([string]$download.name -notmatch
                    '^Microsoft\..*\.Runtime\.win-x64$' -or
                [string]$download.version -cne "[10.0.10, 10.0.10]") {
                throw "$($project.name) has an unpinned runtime download."
            }
        }
        $requiredDownloads = @(
            "Microsoft.NETCore.App.Runtime.win-x64",
            "Microsoft.WindowsDesktop.App.Runtime.win-x64"
        )
        foreach ($requiredDownload in $requiredDownloads) {
            if (@(
                    $downloads |
                        Where-Object {
                            [string]$_.name -ceq $requiredDownload
                        }
                ).Count -ne 1) {
                throw "$($project.name) omits runtime $requiredDownload."
            }
        }
        foreach ($folder in @($assets.packageFolders.PSObject.Properties.Name)) {
            [void]$packageFolders.Add([IO.Path]::GetFullPath($folder))
        }
    }

    $coreclrCandidates = @()
    foreach ($folder in $packageFolders) {
        $candidate = Join-Path `
            $folder `
            "microsoft.netcore.app.runtime.win-x64\10.0.10\runtimes\win-x64\native\coreclr.dll"
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            $coreclrCandidates += [IO.Path]::GetFullPath($candidate)
        }
    }
    $coreclrCandidates = @($coreclrCandidates | Sort-Object -Unique)
    if ($coreclrCandidates.Count -ne 1) {
        throw "The restored 10.0.10 coreclr.dll location is ambiguous."
    }
    $coreclr = Assert-ComoteOrdinaryLocalPath `
        -LiteralPath $coreclrCandidates[0] `
        -Directory $false `
        -Description "Restored 10.0.10 coreclr.dll"
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo(
        $coreclr.FullPath
    )
    $expectedVersion =
        "10,0,1026,32716 @Commit: f7d90799ce4ef09a0bb257852a57248d2a8fb8dd"
    if ([int64]$coreclr.Identity.Length -ne 4614952 -or
        (Get-ComoteSha256 -LiteralPath $coreclr.FullPath) -cne
            "58859F85A30CC71313B281898E7CFBDBB9ECCB95AE2A3F865329EFD47EBF31BB" -or
        [string]$version.FileVersion -cne $expectedVersion -or
        [string]$version.ProductVersion -cne $expectedVersion) {
        throw "The restored .NET 10.0.10 coreclr.dll identity is invalid."
    }
    return [PSCustomObject][ordered]@{
        frameworkVersion = "10.0.10"
        runtimePackVersion = "10.0.10"
        coreclrLength = [int64]$coreclr.Identity.Length
        coreclrSha256 = Get-ComoteSha256 -LiteralPath $coreclr.FullPath
        coreclrFileVersion = [string]$version.FileVersion
        coreclrProductVersion = [string]$version.ProductVersion
    }
}

if ($SnapshotName.Length -lt 3 -or
    $SnapshotName.Length -gt 128 -or
    $SnapshotName -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{2,127}$') {
    throw "SnapshotName must name the exact clean VMware snapshot."
}
$expectedSourceHash = $ExpectedSourceInventorySha256.ToUpperInvariant()
$root = [IO.Path]::GetFullPath($SourceRoot)
[void](Assert-ComoteOrdinaryLocalPath `
    -LiteralPath $root `
    -Directory $true `
    -Description "Isolated regression source root")
$sourceRecords = @(Get-ComoteGateSourceRecords -Root $root)
if ($sourceRecords.Count -lt 80 -or
    (Get-ComoteGateSourceInventoryHash -Records $sourceRecords) -cne
        $expectedSourceHash) {
    throw "The isolated regression source inventory is not exactly pinned."
}
[void](Assert-ComoteGateApiCheckContract -Root $root)
$sourceHygiene = Assert-ComoteGateClipboardSourceContract `
    -Records $sourceRecords
$hostSourceText = @(
    Get-ChildItem `
        -LiteralPath (Join-Path $root "Host") `
        -Filter "*.cs" `
        -File `
        -Force `
        -ErrorAction Stop |
        ForEach-Object { [IO.File]::ReadAllText($_.FullName) }
) -join "`n"
foreach ($forbiddenHostToken in @(
    "--install",
    "--uninstall",
    "--nogui",
    "private static void InstallService",
    'FileName = "sc.exe"',
    "DataProtectionScope.LocalMachine",
    "DefaultPassword"
)) {
    if ($hostSourceText.IndexOf(
            $forbiddenHostToken,
            [StringComparison]::OrdinalIgnoreCase
        ) -ge 0) {
        throw "Host contains forbidden legacy service/password behavior."
    }
}
$environment = Assert-ComoteDisposableVmEnvironment `
    -AcknowledgeDisposableVm:$AcknowledgeDisposableVm `
    -RequireTestSigning

$handoffRoot = [IO.Path]::GetFullPath($ReleaseHandoffRoot)
if (Test-Path -LiteralPath $handoffRoot) {
    [void](Assert-ComoteOrdinaryLocalPath `
        -LiteralPath $handoffRoot `
        -Directory $true `
        -Description "Regression release handoff")
    if (@(
            Get-ChildItem `
                -LiteralPath $handoffRoot `
                -Force `
                -ErrorAction Stop
        ).Count -ne 0) {
        throw "The regression release handoff must start empty."
    }
}
else {
    [void](Assert-ComoteOrdinaryLocalPath `
        -LiteralPath ([IO.Path]::GetDirectoryName($handoffRoot)) `
        -Directory $true `
        -Description "Regression handoff parent")
    [IO.Directory]::CreateDirectory($handoffRoot) | Out-Null
}
$regressionPath = Join-Path $handoffRoot "REGRESSION_GATE.json"
$sbomPath = Join-Path $handoffRoot "NUGET_SBOM.json"
$mediaEvidencePath = Join-Path $handoffRoot "MEDIA_GATE.json"
foreach ($newArtifact in @(
    $regressionPath,
    $sbomPath,
    $mediaEvidencePath
)) {
    if (Test-Path -LiteralPath $newArtifact) {
        throw "A regression output artifact already exists."
    }
}

$dotnetCommand = Get-Command `
    -Name "dotnet.exe" `
    -CommandType Application `
    -ErrorAction Stop |
    Select-Object -First 1
if ($null -eq $dotnetCommand) {
    throw "dotnet.exe was not found."
}
$dotnetPath = [IO.Path]::GetFullPath([string]$dotnetCommand.Path)
[void](Assert-ComoteOrdinaryLocalPath `
    -LiteralPath $dotnetPath `
    -Directory $false `
    -Description "Pinned dotnet host")
$dotnetHash = Get-ComoteSha256 -LiteralPath $dotnetPath

$sdkList = Invoke-ComoteGateProcess `
    -FilePath $dotnetPath `
    -Arguments @("--list-sdks") `
    -WorkingDirectory $root `
    -Description "Installed SDK inventory"
$sdkMatches = @(
    $sdkList.Output.Replace("`r`n", "`n").Split("`n") |
        Where-Object {
            $_ -match ('^' + [regex]::Escape($requiredSdkVersion) +
                '\s+\[[^\]]+\]$')
        }
)
if ($sdkMatches.Count -ne 1) {
    throw "The exact .NET SDK 10.0.302 is not installed once."
}
$globalJsonPath = Join-Path $root "global.json"
$sourceGlobal = @(
    $sourceRecords |
        Where-Object { [string]$_.path -ceq "global.json" }
)
if ($sourceGlobal.Count -ne 1) {
    throw "The source inventory must contain exact global.json once."
}
$globalJson = Read-ComoteGateJson `
    -LiteralPath $globalJsonPath `
    -Description "Source global.json"
Assert-ComoteExactProperties `
    -InputObject $globalJson `
    -Expected @("sdk") `
    -Description "Source global.json"
Assert-ComoteExactProperties `
    -InputObject $globalJson.sdk `
    -Expected @("version", "rollForward", "allowPrerelease") `
    -Description "Source global.json SDK"
if ([string]$globalJson.sdk.version -cne $requiredSdkVersion -or
    [string]$globalJson.sdk.rollForward -cne "disable" -or
    [bool]$globalJson.sdk.allowPrerelease -ne $false) {
    throw "Source global.json does not pin the exact release SDK."
}
$globalJsonHash = Get-ComoteSha256 -LiteralPath $globalJsonPath
$selectedSdk = Invoke-ComoteGateProcess `
    -FilePath $dotnetPath `
    -Arguments @("--version") `
    -WorkingDirectory $root `
    -Description "Selected SDK verification"
if ($selectedSdk.Output.Trim() -cne $requiredSdkVersion) {
    throw "global.json did not select exact SDK 10.0.302."
}
$runtimeList = Invoke-ComoteGateProcess `
    -FilePath $dotnetPath `
    -Arguments @("--list-runtimes") `
    -WorkingDirectory $root `
    -Description "Installed runtime inventory"
$runtimeRecords = @()
foreach ($line in @(
    $runtimeList.Output.Replace("`r`n", "`n").Split("`n") |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
)) {
    if ($line -notmatch '^(\S+)\s+(\S+)\s+\[([^\]]+)\]$') {
        throw "The installed runtime inventory format is not exact."
    }
    $runtimeRoot = [IO.Path]::GetFullPath([string]$Matches[3])
    [void](Assert-ComoteOrdinaryLocalPath `
        -LiteralPath $runtimeRoot `
        -Directory $true `
        -Description "Installed runtime root")
    $runtimeRecords += [PSCustomObject][ordered]@{
        name = [string]$Matches[1]
        version = [string]$Matches[2]
        path = $runtimeRoot
    }
}
foreach ($requiredRuntime in @(
    "Microsoft.NETCore.App",
    "Microsoft.WindowsDesktop.App"
)) {
    if (@(
            $runtimeRecords |
                Where-Object {
                    [string]$_.name -ceq $requiredRuntime -and
                    [string]$_.version -ceq $requiredRuntimeVersion
                }
        ).Count -ne 1) {
        throw "The exact installed runtime is missing: $requiredRuntime 10.0.10"
    }
}
$runtimeRecords = @($runtimeRecords | Sort-Object name, version, path)

$projects = @(
    [PSCustomObject][ordered]@{
        name = "ApiCheck"
        path = "ApiCheck/ApiCheck.csproj"
        executable = "ApiCheck.exe"
        ridRestore = $true
        publishRestore = $false
    },
    [PSCustomObject][ordered]@{
        name = "InputCore"
        path = "InputCore/Comote.InputCore.csproj"
        executable = ""
        ridRestore = $true
        publishRestore = $false
    },
    [PSCustomObject][ordered]@{
        name = "InputCore.SelfTest"
        path = "InputCore.SelfTest/Comote.InputCore.SelfTest.csproj"
        executable = "Comote.InputCore.SelfTest.exe"
        ridRestore = $false
        publishRestore = $false
    },
    [PSCustomObject][ordered]@{
        name = "HostInputSelfTest"
        path = "HostInputSelfTest/Comote.HostInputSelfTest.csproj"
        executable = "Comote.HostInputSelfTest.exe"
        ridRestore = $false
        publishRestore = $false
    },
    [PSCustomObject][ordered]@{
        name = "InputBroker"
        path = "InputBroker/Comote.InputBroker.csproj"
        executable = "Comote.InputBroker.exe"
        ridRestore = $true
        publishRestore = $true
    },
    [PSCustomObject][ordered]@{
        name = "ViewerLifecycleSelfTest"
        path = "ViewerLifecycleSelfTest/Comote.ViewerLifecycleSelfTest.csproj"
        executable = "Comote.ViewerLifecycleSelfTest.exe"
        ridRestore = $false
        publishRestore = $false
    },
    [PSCustomObject][ordered]@{
        name = "RemoteFileSenderSelfTest"
        path = "RemoteFileSenderSelfTest/Comote.RemoteFileSenderSelfTest.csproj"
        executable = "Comote.RemoteFileSenderSelfTest.exe"
        ridRestore = $false
        publishRestore = $false
    },
    [PSCustomObject][ordered]@{
        name = "SecureChannelSelfTest"
        path = "SecureChannelSelfTest/Comote.SecureChannelSelfTest.csproj"
        executable = "Comote.SecureChannelSelfTest.exe"
        ridRestore = $false
        publishRestore = $false
    },
    [PSCustomObject][ordered]@{
        name = "HubTransportSelfTest"
        path = "HubTransportSelfTest/Comote.HubTransportSelfTest.csproj"
        executable = "Comote.HubTransportSelfTest.exe"
        ridRestore = $false
        publishRestore = $false
    },
    [PSCustomObject][ordered]@{
        name = "Host"
        path = "Host/Host.csproj"
        executable = "Host.exe"
        ridRestore = $true
        publishRestore = $true
    },
    [PSCustomObject][ordered]@{
        name = "Viewer"
        path = "Viewer/Viewer.csproj"
        executable = "Viewer.exe"
        ridRestore = $true
        publishRestore = $true
    },
    [PSCustomObject][ordered]@{
        name = "VirtualHidE2E"
        path = "Driver/FinalValidation/VirtualHidE2E/Comote.VirtualHidE2E.csproj"
        executable = "Comote.VirtualHidE2E.exe"
        ridRestore = $false
        publishRestore = $false
    },
    [PSCustomObject][ordered]@{
        name = "MediaGate"
        path = "Distribution/VirtualHidPreview/MediaGate/Comote.MediaGate.csproj"
        executable = "Comote.MediaGate.exe"
        ridRestore = $true
        publishRestore = $true
    }
)
if ($projects.Count -ne 13) {
    throw "The canonical NuGet project graph must contain exactly 13 projects."
}

$sourceLockHashes = @{}
foreach ($record in @(
    $sourceRecords |
        Where-Object { [string]$_.path -match '(^|/)packages\.lock\.json$' }
)) {
    $sourceLockHashes[[string]$record.path] = [string]$record.sha256
}
$projectReports = @()
foreach ($project in $projects) {
    Assert-ComoteSafeRelativePath -RelativePath ([string]$project.path)
    $projectPath = Join-Path $root ([string]$project.path).Replace('/', '\')
    [void](Assert-ComoteOrdinaryLocalPath `
        -LiteralPath $projectPath `
        -Directory $false `
        -Description "$($project.name) project")
    $projectSha256 = Get-ComoteSha256 -LiteralPath $projectPath
    $sourceSha256 = Get-ComoteGateProjectSourceHash `
        -ProjectPath $projectPath
    $lockRelative = ([string]$project.path).Substring(
        0,
        ([string]$project.path).LastIndexOf('/') + 1
    ) + "packages.lock.json"
    $sourceLocked = $sourceLockHashes.ContainsKey($lockRelative)
    $restoreArguments = @("restore", $projectPath)
    if ([bool]$project.ridRestore) {
        $restoreArguments += @("--runtime", "win-x64")
    }
    if ([bool]$project.publishRestore) {
        $restoreArguments += @(
            "-p:SelfContained=true",
            "-p:PublishSingleFile=true",
            "-p:RuntimeFrameworkVersion=$requiredRuntimeVersion",
            "-p:TargetLatestRuntimePatch=false"
        )
    }
    if (-not $sourceLocked) {
        throw "$($project.name) packages.lock.json is not source-inventoried."
    }
    $restoreArguments += "--locked-mode"
    $lockOrigin = "source"
    $evaluation = Invoke-ComoteGateProcess `
        -FilePath $dotnetPath `
        -Arguments $restoreArguments `
        -WorkingDirectory $root `
        -Description "$($project.name) restore evaluation"
    $lockedArguments = @("restore", $projectPath, "--locked-mode")
    if ([bool]$project.ridRestore) {
        $lockedArguments += @("--runtime", "win-x64")
    }
    if ([bool]$project.publishRestore) {
        $lockedArguments += @(
            "-p:SelfContained=true",
            "-p:PublishSingleFile=true",
            "-p:RuntimeFrameworkVersion=$requiredRuntimeVersion",
            "-p:TargetLatestRuntimePatch=false"
        )
    }
    $locked = Invoke-ComoteGateProcess `
        -FilePath $dotnetPath `
        -Arguments $lockedArguments `
        -WorkingDirectory $root `
        -Description "$($project.name) locked restore verification"
    foreach ($sourceLock in $sourceLockHashes.GetEnumerator()) {
        $sourceLockPath = Join-Path $root $sourceLock.Key.Replace('/', '\')
        if ((Get-ComoteSha256 -LiteralPath $sourceLockPath) -cne
            [string]$sourceLock.Value) {
            throw "A source-inventoried packages.lock.json was rewritten."
        }
    }
    $generatedLockPath = Join-Path `
        ([IO.Path]::GetDirectoryName($projectPath)) `
        "packages.lock.json"
    [void](Assert-ComoteOrdinaryLocalPath `
        -LiteralPath $generatedLockPath `
        -Directory $false `
        -Description "$($project.name) packages.lock.json")
    $projectReports += [PSCustomObject][ordered]@{
        name = [string]$project.name
        path = [string]$project.path
        projectSha256 = $projectSha256
        sourceSha256 = $sourceSha256
        publishRestore = [bool]$project.publishRestore
        lockOrigin = $lockOrigin
        lockSha256 = Get-ComoteSha256 -LiteralPath $generatedLockPath
        restoreOutputSha256 = $evaluation.OutputSha256
        lockedRestoreOutputSha256 = $locked.OutputSha256
    }
}
$ridLockResult = Assert-ComoteGateRuntimeIdentifierLocks `
    -Root $root `
    -Projects $projects
$runtimePolicy = Get-ComoteGateRuntimePolicy `
    -Root $root `
    -Projects $projects

$testSpecs = @(
    [PSCustomObject]@{
        name = "InputCore.SelfTest"
        expectedPassCount = 15
        passPattern = '^PASS: '
        successToken = "All 15 InputCore self-tests passed."
        applicationArguments = @()
        sourceTokens = @()
        outputTokens = @()
    },
    [PSCustomObject]@{
        name = "HostInputSelfTest"
        expectedPassCount = 5
        passPattern = '^$'
        successToken = "Host input pure self-test passed."
        applicationArguments = @()
        sourceTokens = @(
            "RunProtocolTests();",
            "RunFactoryTests();",
            "RunCoordinateTests();",
            "RunKeyboardStateTests();",
            "RunInboundFileTransferTests();",
            "RunSystemCommandExecutorTests();"
        )
        outputTokens = @()
    },
    [PSCustomObject]@{
        name = "InputBroker"
        expectedPassCount = 2
        passPattern = '^$'
        successToken = "InputBroker pure self-test passed."
        applicationArguments = @("--self-test")
        sourceTokens = @(
            "TestDriverResponses();",
            "TestComputerNameNormalization();"
        )
        outputTokens = @()
    },
    [PSCustomObject]@{
        name = "ViewerLifecycleSelfTest"
        expectedPassCount = 14
        passPattern = '^PASS: '
        successToken = "PASS: clipboard STA worker can rejoin after timeout"
        applicationArguments = @()
        sourceTokens = @()
        outputTokens = @(
            "PASS: generation rejects stale callbacks",
            "PASS: answer is accepted once",
            "PASS: termination revokes connectivity",
            "PASS: left and right modifiers are independent",
            "PASS: modifier reset clears all sides",
            "PASS: clipboard revoke invalidates an in-flight epoch",
            "PASS: unexpected clipboard enable acknowledgement stays disabled",
            "PASS: clipboard acknowledgement transition invalidates stale epoch",
            "PASS: clipboard revoke does not wait for an acquired lease",
            "PASS: clipboard projection rejects stale snapshots",
            "PASS: rapid clipboard toggles preserve the newest request",
            "PASS: host clipboard monitor rejects stale transitions",
            "PASS: clipboard STA worker coalesces pending work",
            "PASS: clipboard STA worker can rejoin after timeout"
        )
    },
    [PSCustomObject]@{
        name = "RemoteFileSenderSelfTest"
        expectedPassCount = 4
        passPattern = '^PASS: '
        successToken = "All RemoteFileSender self-tests passed."
        applicationArguments = @()
        sourceTokens = @()
        outputTokens = @()
    },
    [PSCustomObject]@{
        name = "SecureChannelSelfTest"
        expectedPassCount = 6
        passPattern = '^PASS '
        successToken = "6/6 secure-channel tests passed."
        applicationArguments = @()
        sourceTokens = @()
        outputTokens = @()
    },
    [PSCustomObject]@{
        name = "HubTransportSelfTest"
        expectedPassCount = 15
        passPattern = '^PASS '
        successToken = "15/15 hardened hub transport tests passed."
        applicationArguments = @()
        sourceTokens = @()
        outputTokens = @()
    }
)
$testReports = @()
foreach ($spec in $testSpecs) {
    $project = @(
        $projects |
            Where-Object { [string]$_.name -ceq [string]$spec.name }
    )
    if ($project.Count -ne 1) {
        throw "A regression test project mapping is ambiguous."
    }
    $projectPath = Join-Path $root ([string]$project[0].path).Replace('/', '\')
    if (@($spec.sourceTokens).Count -gt 0) {
        $sourceText = @(
            Get-ChildItem `
                -LiteralPath ([IO.Path]::GetDirectoryName($projectPath)) `
                -Filter "*.cs" `
                -File `
                -Force `
                -Recurse `
                -ErrorAction Stop |
                ForEach-Object {
                    [IO.File]::ReadAllText($_.FullName)
                }
        ) -join "`n"
        foreach ($sourceToken in @($spec.sourceTokens)) {
            if ([regex]::Matches(
                    $sourceText,
                    [regex]::Escape([string]$sourceToken)
                ).Count -ne 1) {
                throw "$($spec.name) does not contain its exact suite set."
            }
        }
    }
    $runArguments = @(
        "run",
        "--project", $projectPath,
        "--configuration", "Release",
        "--no-restore",
        "-p:RestoreLockedMode=true"
    )
    if (@($spec.applicationArguments).Count -gt 0) {
        $runArguments += "--"
        $runArguments += @($spec.applicationArguments)
    }
    $run = Invoke-ComoteGateProcess `
        -FilePath $dotnetPath `
        -Arguments $runArguments `
        -WorkingDirectory $root `
        -Description "$($spec.name) pure regression suite"
    $passLineCount = if ([string]$spec.passPattern -ceq '^$') {
        0
    }
    else {
        [regex]::Matches(
            $run.Output,
            [string]$spec.passPattern,
            [Text.RegularExpressions.RegexOptions]::Multiline
        ).Count
    }
    Assert-ComoteGateOutput `
        -Output $run.Output `
        -SuccessToken ([string]$spec.successToken) `
        -ExpectedPassLines $passLineCount `
        -PassLinePattern ([string]$spec.passPattern) `
        -Description "$($spec.name) pure regression suite"
    if ([string]$spec.passPattern -cne '^$' -and
        $passLineCount -ne [int]$spec.expectedPassCount) {
        throw "$($spec.name) did not produce its exact PASS count."
    }
    foreach ($outputToken in @($spec.outputTokens)) {
        if ([regex]::Matches(
                $run.Output,
                [regex]::Escape([string]$outputToken)
            ).Count -ne 1) {
            throw "$($spec.name) omitted an exact PASS result."
        }
    }
    $projectReport = @(
        $projectReports |
            Where-Object { [string]$_.name -ceq [string]$spec.name }
    )[0]
    $testReports += [PSCustomObject][ordered]@{
        name = [string]$spec.name
        projectPath = [string]$project[0].path
        projectSha256 = [string]$projectReport.projectSha256
        sourceSha256 = [string]$projectReport.sourceSha256
        expectedPassCount = [int]$spec.expectedPassCount
        successToken = [string]$spec.successToken
        executableSha256 = Get-ComoteGateExecutableHash `
            -ProjectPath $projectPath `
            -ExecutableName ([string]$project[0].executable)
        outputSha256 = [string]$run.OutputSha256
    }
}

$observerProject = @(
    $projects |
        Where-Object { [string]$_.name -ceq "VirtualHidE2E" }
)[0]
$observerProjectPath = Join-Path `
    $root `
    ([string]$observerProject.path).Replace('/', '\')
$observerBuild = Invoke-ComoteGateProcess `
    -FilePath $dotnetPath `
    -Arguments @(
        "build", $observerProjectPath,
        "--configuration", "Release",
        "--no-restore",
        "-p:RestoreLockedMode=true"
    ) `
    -WorkingDirectory $root `
    -Description "FinalValidation observer locked build"
$observerHash = Get-ComoteGateExecutableHash `
    -ProjectPath $observerProjectPath `
    -ExecutableName ([string]$observerProject.executable)

$mediaProject = @(
    $projects |
        Where-Object { [string]$_.name -ceq "MediaGate" }
)[0]
$mediaProjectPath = Join-Path `
    $root `
    ([string]$mediaProject.path).Replace('/', '\')
$mediaPublishRoot = Join-Path `
    ([IO.Path]::GetDirectoryName($mediaProjectPath)) `
    "bin\Release\release-gate-publish"
if (Test-Path -LiteralPath $mediaPublishRoot) {
    throw "The MediaGate publish output must start absent."
}
$mediaPublish = Invoke-ComoteGateProcess `
    -FilePath $dotnetPath `
    -Arguments @(
        "publish", $mediaProjectPath,
        "--configuration", "Release",
        "--runtime", "win-x64",
        "--self-contained", "true",
        "--no-restore",
        "-p:RestoreLockedMode=true",
        "-p:PublishSingleFile=true",
        "-p:RuntimeFrameworkVersion=$requiredRuntimeVersion",
        "-p:TargetLatestRuntimePatch=false",
        "-p:DebugType=None",
        "-p:DebugSymbols=false",
        "--output", $mediaPublishRoot
    ) `
    -WorkingDirectory $root `
    -Description "MediaGate self-contained publish"
$mediaExecutable = Join-Path $mediaPublishRoot "Comote.MediaGate.exe"
[void](Assert-ComoteOrdinaryLocalPath `
    -LiteralPath $mediaExecutable `
    -Directory $false `
    -Description "Published MediaGate executable")
$ffmpegReceiptPath = Join-Path `
    $root `
    "Distribution\VirtualHidPreview\THIRD_PARTY_NOTICES\FFMPEG_ASSET_RECEIPT.json"
[void](Assert-ComoteOrdinaryLocalPath `
    -LiteralPath $ffmpegReceiptPath `
    -Directory $false `
    -Description "Pinned FFmpeg asset receipt")
$ffmpegReceiptHash = Get-ComoteSha256 -LiteralPath $ffmpegReceiptPath
$mediaRun = Invoke-ComoteGateProcess `
    -FilePath $mediaExecutable `
    -Arguments @(
        "--acknowledge-release-vm",
        "--receipt", $ffmpegReceiptPath,
        "--output", $mediaEvidencePath
    ) `
    -WorkingDirectory $root `
    -Description "FFmpeg MediaGate"
Assert-ComoteGateOutput `
    -Output $mediaRun.Output `
    -SuccessToken "Comote MediaGate passed." `
    -ExpectedPassLines 0 `
    -PassLinePattern '^$' `
    -Description "FFmpeg MediaGate"
$mediaEvidence = Read-ComoteGateJson `
    -LiteralPath $mediaEvidencePath `
    -Description "MediaGate evidence"
if ([int]$mediaEvidence.schemaVersion -ne 1 -or
    [string]$mediaEvidence.status -cne "passed" -or
    [string]$mediaEvidence.environment.runtimeVersion -cne
        $requiredRuntimeVersion -or
    [string]$mediaEvidence.receipt.sha256 -cne $ffmpegReceiptHash -or
    [bool]$mediaEvidence.mediaProbe.usedSyntheticFramesOnly -ne $true -or
    [int]$mediaEvidence.mediaProbe.submittedFrames -le 0 -or
    [int]$mediaEvidence.mediaProbe.encodedPackets -le 0 -or
    [int]$mediaEvidence.mediaProbe.decodedFrames -le 0 -or
    ($null -ne $mediaEvidence.PSObject.Properties["error"] -and
        $null -ne $mediaEvidence.error)) {
    throw "MediaGate evidence did not prove the exact runtime/media gate."
}
$mediaReport = [PSCustomObject][ordered]@{
    projectPath = [string]$mediaProject.path
    publishOutputSha256 = [string]$mediaPublish.OutputSha256
    executableSha256 = Get-ComoteSha256 -LiteralPath $mediaExecutable
    runOutputSha256 = [string]$mediaRun.OutputSha256
    evidencePath = "MEDIA_GATE.json"
    evidenceSha256 = Get-ComoteSha256 -LiteralPath $mediaEvidencePath
    ffmpegReceiptSha256 = $ffmpegReceiptHash
    runtimeVersion = $requiredRuntimeVersion
}

$powershellPath = Join-Path $PSHOME "powershell.exe"
[void](Assert-ComoteOrdinaryLocalPath `
    -LiteralPath $powershellPath `
    -Directory $false `
    -Description "Boundary Windows PowerShell")
$boundarySpecs = @(
    [PSCustomObject]@{
        group = "phase2"
        path = "Driver/ComoteVirtualHidPhase2/Test-Phase2Boundary.ps1"
        token = "Phase 2 source and build boundaries verified."
    },
    [PSCustomObject]@{
        group = "installer"
        path = "Driver/Installer/Test-InstallerBoundary.ps1"
        token = "Comote Phase 2 hardened installer boundary verified."
    },
    [PSCustomObject]@{
        group = "final-validation"
        path = "Driver/FinalValidation/Test-Phase2FinalEvidenceParser.ps1"
        token = "Phase 2 final E2E evidence parser self-test passed."
    },
    [PSCustomObject]@{
        group = "final-validation"
        path = "Driver/FinalValidation/Test-Phase2FinalE2EBoundary.ps1"
        token = "Phase 2 final E2E source boundary verified."
    },
    [PSCustomObject]@{
        group = "ffmpeg"
        path = "ffmpeg/Test-FFmpegLgplBoundary.ps1"
        token = "FFmpeg LGPL and ABI boundary verified."
    },
    [PSCustomObject]@{
        group = "packaging"
        path = "Distribution/VirtualHidPreview/Test-VirtualHidPreviewBoundary.ps1"
        token = "Comote Virtual HID preview static boundary passed."
    }
)
$boundaryReports = @()
foreach ($spec in $boundarySpecs) {
    Assert-ComoteSafeRelativePath -RelativePath ([string]$spec.path)
    $scriptPath = Join-Path $root ([string]$spec.path).Replace('/', '\')
    [void](Assert-ComoteOrdinaryLocalPath `
        -LiteralPath $scriptPath `
        -Directory $false `
        -Description "$($spec.group) boundary script")
    $boundary = Invoke-ComoteGateProcess `
        -FilePath $powershellPath `
        -Arguments @(
            "-NoLogo",
            "-NoProfile",
            "-NonInteractive",
            "-ExecutionPolicy", "Bypass",
            "-File", $scriptPath
        ) `
        -WorkingDirectory $root `
        -Description "$($spec.group) boundary"
    Assert-ComoteGateOutput `
        -Output $boundary.Output `
        -SuccessToken ([string]$spec.token) `
        -ExpectedPassLines 0 `
        -PassLinePattern '^$' `
        -Description "$($spec.group) boundary"
    $boundaryReports += [PSCustomObject][ordered]@{
        group = [string]$spec.group
        scriptPath = [string]$spec.path
        scriptSha256 = Get-ComoteSha256 -LiteralPath $scriptPath
        successToken = [string]$spec.token
        outputSha256 = [string]$boundary.OutputSha256
    }
}
if (@($boundaryReports.group | Sort-Object -Unique).Count -ne 5 -or
    $boundaryReports.Count -ne 6) {
    throw "The exact five boundary groups/six scripts did not execute."
}

$vulnerabilityReports = @()
$totalTopLevelVulnerable = 0
$totalTransitiveVulnerable = 0
foreach ($project in $projects) {
    $projectPath = Join-Path $root ([string]$project.path).Replace('/', '\')
    $scan = Invoke-ComoteGateProcess `
        -FilePath $dotnetPath `
        -Arguments @(
            "list", $projectPath, "package",
            "--include-transitive",
            "--vulnerable",
            "--format", "json",
            "--no-restore"
        ) `
        -WorkingDirectory $root `
        -Description "$($project.name) vulnerability scan"
    try {
        $scanJson = $scan.Output |
            ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "$($project.name) vulnerability output is not valid JSON."
    }
    if ($null -eq $scanJson.PSObject.Properties["projects"] -or
        @($scanJson.projects).Count -lt 1) {
        throw "$($project.name) vulnerability JSON omits projects."
    }
    $topLevelCount = 0
    $transitiveCount = 0
    foreach ($scanProject in @($scanJson.projects)) {
        if ($null -eq $scanProject.PSObject.Properties["frameworks"]) {
            throw "$($project.name) vulnerability JSON omits frameworks."
        }
        foreach ($framework in @($scanProject.frameworks)) {
            if ($null -ne $framework.PSObject.Properties[
                    "topLevelPackages"]) {
                $topLevelCount += @($framework.topLevelPackages).Count
            }
            if ($null -ne $framework.PSObject.Properties[
                    "transitivePackages"]) {
                $transitiveCount += @($framework.transitivePackages).Count
            }
        }
    }
    if ($topLevelCount -ne 0 -or $transitiveCount -ne 0) {
        throw "$($project.name) has a vulnerable NuGet dependency."
    }
    $totalTopLevelVulnerable += $topLevelCount
    $totalTransitiveVulnerable += $transitiveCount
    $vulnerabilityReports += [PSCustomObject][ordered]@{
        projectPath = [string]$project.path
        topLevelVulnerableCount = $topLevelCount
        transitiveVulnerableCount = $transitiveCount
        outputSha256 = [string]$scan.OutputSha256
    }
}
if ($totalTopLevelVulnerable -ne 0 -or
    $totalTransitiveVulnerable -ne 0) {
    throw "The release vulnerability count is not zero."
}

$lockEntries = @(Get-ComoteGateArtifactEntries -Root $root -Kind lock)
$assetsEntries = @(Get-ComoteGateArtifactEntries -Root $root -Kind assets)
if ($lockEntries.Count -ne 13 -or
    $assetsEntries.Count -ne 13) {
    throw "The locked restore artifact inventory is incomplete."
}
foreach ($project in $projects) {
    $directory = ([string]$project.path).Substring(
        0,
        ([string]$project.path).LastIndexOf('/') + 1
    )
    if (@(
            $lockEntries |
                Where-Object {
                    [string]$_.path -ceq
                        ($directory + "packages.lock.json")
                }
        ).Count -ne 1 -or
        @(
            $assetsEntries |
                Where-Object {
                    [string]$_.path -ceq
                        ($directory + "obj/project.assets.json")
                }
        ).Count -ne 1) {
        throw "A release/test project lacks exact lock/assets evidence."
    }
}
foreach ($sourceLock in $sourceLockHashes.GetEnumerator()) {
    $sourceLockPath = Join-Path $root $sourceLock.Key.Replace('/', '\')
    if ((Get-ComoteSha256 -LiteralPath $sourceLockPath) -cne
        [string]$sourceLock.Value) {
        throw "A source-inventoried packages.lock.json changed during the gate."
    }
}

$roleProjects = @{
    client = Join-Path $root "Host\Host.csproj"
    manager = Join-Path $root "Viewer\Viewer.csproj"
    broker = Join-Path $root "InputBroker\Comote.InputBroker.csproj"
}
$sbom = New-ComoteGateNuGetSbom `
    -Root $root `
    -RoleProjectPaths $roleProjects `
    -SourceInventorySha256 $expectedSourceHash `
    -RuntimePolicy $runtimePolicy `
    -LockFiles $lockEntries `
    -OutputPath $sbomPath

$observerProjectReport = @(
    $projectReports |
        Where-Object { [string]$_.name -ceq "VirtualHidE2E" }
)[0]
$report = [PSCustomObject][ordered]@{
    schemaVersion = 1
    status = "passed"
    completedUtc = [DateTime]::UtcNow.ToString("o")
    snapshotName = $SnapshotName
    sourceInventorySha256 = $expectedSourceHash
    sourceHygiene = $sourceHygiene
    environment = [PSCustomObject][ordered]@{
        manufacturer = [string]$environment.Computer.Manufacturer
        model = [string]$environment.Computer.Model
        osBuild = [string]$environment.OperatingSystem.BuildNumber
        osUbr = [int]$environment.Ubr
        testSigningActive = $true
    }
    toolchain = [PSCustomObject][ordered]@{
        dotnetPath = $dotnetPath
        dotnetSha256 = $dotnetHash
        sdkVersion = $requiredSdkVersion
        sdkListOutputSha256 = [string]$sdkList.OutputSha256
        selectedSdkOutputSha256 = [string]$selectedSdk.OutputSha256
        globalJsonSha256 = $globalJsonHash
        runtimeVersion = $requiredRuntimeVersion
        runtimeListOutputSha256 = [string]$runtimeList.OutputSha256
        installedRuntimes = $runtimeRecords
        powershellPath = $powershellPath
        powershellSha256 = Get-ComoteSha256 -LiteralPath $powershellPath
    }
    runtimePolicy = $runtimePolicy
    projects = $projectReports
    lockFiles = $lockEntries
    assetsFiles = $assetsEntries
    pureTests = $testReports
    observer = [PSCustomObject][ordered]@{
        projectPath = [string]$observerProject.path
        projectSha256 = [string]$observerProjectReport.projectSha256
        sourceSha256 = [string]$observerProjectReport.sourceSha256
        executableSha256 = $observerHash
        buildOutputSha256 = [string]$observerBuild.OutputSha256
    }
    mediaGate = $mediaReport
    boundaryGroups = 5
    boundaryScripts = $boundaryReports
    vulnerabilityScans = $vulnerabilityReports
    topLevelVulnerableCount = $totalTopLevelVulnerable
    transitiveVulnerableCount = $totalTransitiveVulnerable
    vulnerabilityCount = 0
    nugetSbom = [PSCustomObject][ordered]@{
        path = "NUGET_SBOM.json"
        sha256 = [string]$sbom.Sha256
        packageCount = [int]$sbom.PackageCount
    }
    noDriverDeviceInputOrSystemMutation = $true
}
Write-ComoteJsonAtomically `
    -LiteralPath $regressionPath `
    -InputObject $report `
    -Depth 20
$regressionHash = Get-ComoteSha256 -LiteralPath $regressionPath

Write-Host ""
Write-Host "Virtual HID pre-driver regression gate passed." `
    -ForegroundColor Green
Write-Host "Regression SHA-256: $regressionHash"
Write-Host "NuGet SBOM SHA-256: $($sbom.Sha256)"
Write-Host "Vulnerable NuGet packages: 0"
Write-Host "No driver, device, input, certificate, service, or system state changed."
