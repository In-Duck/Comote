#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [switch]$AcknowledgeDisposableVm,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$SnapshotName,

    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{2,63}$')]
    [string]$ReleaseId,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$SourceRoot,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedSourceInventorySha256,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$IsolatedWorkRoot,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$SignToolPath,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$OutputDirectory,

    [Parameter(Mandatory)]
    [ValidatePattern('^[^\\/:*?"<>|]{1,64}\\[^\\/:*?"<>|]{1,64}$')]
    [string]$ControllerUser,

    [ValidateRange(1024, 65535)]
    [int]$HubPort = 45820,

    [switch]$AcknowledgeHubSmoke,

    [string]$HubSmokeConfirmationPhrase = "",

    [switch]$AcknowledgeInputGeneration,

    [string]$InputConfirmationPhrase = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot "VirtualHidPreview.Common.ps1")

$script:PromotionPhases = @(
    "pre-install-gates",
    "installing",
    "await-controller-logon",
    "await-hub-smoke",
    "normal-e2e",
    "await-normal-reboot",
    "await-s1-resume",
    "await-cold-start",
    "configure-verifier",
    "await-verifier-reboot",
    "cleanup-unified",
    "await-post-verifier-clean-reboot",
    "export-role-candidate",
    "await-manager-role-start",
    "install-client-role",
    "await-client-role-logon",
    "client-role-e2e",
    "cleanup-client-role",
    "final-report",
    "promote-index",
    "complete"
)
$script:HubAttestationPhrase =
    "I CONFIRM CLIENT ONLINE VIDEO VISIBLE AUTHENTICATED INPUT ACTIVE " +
    "HARMLESS INPUT PERFORMED"
$script:InputAttestationPhrase = "RUN COMOTE VIRTUAL HID E2E IN VM"
$script:ClientAcceptancePhrase =
    "I ACCEPT COMOTE TEST-SIGNED VIRTUAL HID PREVIEW"

function Get-ComotePromotionPaths {
    $programData = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::CommonApplicationData
    )
    $root = Join-Path $programData "ComoteVirtualHidPreviewPromotion"
    return [PSCustomObject][ordered]@{
        Root = $root
        State = Join-Path $root "promotion-state.json"
        Tools = Join-Path $root "Tools"
        Output = Join-Path $root "Output"
        Evidence = Join-Path $root "Evidence"
        RoleTests = Join-Path $root "RoleTests"
    }
}

function Get-ComotePromotionArgumentBinding {
    return [PSCustomObject][ordered]@{
        acknowledgeDisposableVm = $true
        snapshotName = $SnapshotName
        releaseId = $ReleaseId
        sourceRoot = [IO.Path]::GetFullPath($SourceRoot)
        sourceInventorySha256 =
            $ExpectedSourceInventorySha256.ToUpperInvariant()
        isolatedWorkRoot = [IO.Path]::GetFullPath($IsolatedWorkRoot)
        signToolPath = [IO.Path]::GetFullPath($SignToolPath)
        outputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
        controllerUser = $ControllerUser
        hubPort = $HubPort
    }
}

function Get-ComoteToolInventoryHash {
    param([Parameter(Mandatory)][object[]]$Records)

    $lines = @("COMOTE-PROMOTION-TOOL-INVENTORY-V1")
    foreach ($record in $Records) {
        Assert-ComoteExactProperties `
            -InputObject $record `
            -Expected @("kind", "path", "length", "sha256") `
            -Description "Promotion tool inventory record"
        $hashValue = if ($null -eq $record.sha256) {
            "-"
        }
        else {
            [string]$record.sha256
        }
        $lines += "{0}|{1}|{2}|{3}" -f
            [string]$record.kind,
            [string]$record.path,
            [int64]$record.length,
            $hashValue
    }
    $bytes = (New-Object Text.ASCIIEncoding).GetBytes(
        (($lines -join "`r`n") + "`r`n")
    )
    return [BitConverter]::ToString(
        [Security.Cryptography.SHA256]::Create().ComputeHash($bytes)
    ).Replace("-", "")
}

function Get-ComoteToolInventory {
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [switch]$RequireProtectedAcl
    )

    $rootIdentity = Assert-ComoteOrdinaryLocalPath `
        -LiteralPath $Root `
        -Directory $true `
        -Description "Promotion tool root"
    if ($RequireProtectedAcl.IsPresent) {
        Assert-ComoteNoUntrustedWriteAcl -LiteralPath $rootIdentity.FullPath
    }
    $records = @()
    $excludedSegments = @(".git", ".vs", "artifacts", "bin", "obj")
    $backupSuffixPattern =
        '(?i)(?:\.bak|\.backup|\.codex-backup|' +
        '\.servicecredential-backup|\.autoclipboard-backup|[-.]backup)$'
    $relativeSet = New-Object `
        'Collections.Generic.HashSet[string]' `
        ([StringComparer]::Ordinal)
    foreach ($item in @(
        Get-ChildItem `
            -LiteralPath $rootIdentity.FullPath `
            -Force `
            -Recurse `
            -ErrorAction Stop
    )) {
        $relative = Get-ComoteRelativePath `
            -Root $rootIdentity.FullPath `
            -Child $item.FullName
        Assert-ComoteSafeRelativePath -RelativePath $relative
        $segments = @($relative.Split('/'))
        if (@($segments | Where-Object { $excludedSegments -ccontains $_ }).Count -gt 0) {
            continue
        }
        if ([string]$item.Name -match $backupSuffixPattern -or
            [string]$item.Name -match '(?i)(?:^|\.)partial-') {
            throw "Promotion tool input contains a backup or partial artifact."
        }
        if (-not $relativeSet.Add($relative)) {
            throw "Promotion tool inventory contains a duplicated path."
        }
        if ($item.PSIsContainer) {
            [void](Assert-ComoteOrdinaryLocalPath `
                -LiteralPath $item.FullName `
                -Directory $true `
                -Description "Promotion tool directory")
            $record = [PSCustomObject][ordered]@{
                kind = "directory"
                path = $relative
                length = [int64]0
                sha256 = $null
            }
        }
        else {
            $identity = Assert-ComoteOrdinaryLocalPath `
                -LiteralPath $item.FullName `
                -Directory $false `
                -Description "Promotion tool file"
            $record = [PSCustomObject][ordered]@{
                kind = "file"
                path = $relative
                length = [int64]$identity.Identity.Length
                sha256 = Get-ComoteSha256 -LiteralPath $identity.FullPath
            }
        }
        if ($RequireProtectedAcl.IsPresent) {
            Assert-ComoteNoUntrustedWriteAcl -LiteralPath $item.FullName
        }
        $records += $record
    }
    $records = @($records | Sort-Object path, kind)
    if ($records.Count -lt 15) {
        throw "Promotion tool inventory is unexpectedly small."
    }
    return $records
}

function Copy-ComoteProtectedToolTree {
    param(
        [Parameter(Mandatory)]
        [string]$Source,

        [Parameter(Mandatory)]
        [string]$Destination,

        [Parameter(Mandatory)]
        [object[]]$Inventory
    )

    if (Test-Path -LiteralPath $Destination) {
        throw "The protected promotion tool root must start absent."
    }
    [IO.Directory]::CreateDirectory($Destination) | Out-Null
    Set-ComoteProtectedDirectoryAcl -LiteralPath $Destination
    foreach ($record in @($Inventory | Where-Object kind -ceq "directory")) {
        [IO.Directory]::CreateDirectory(
            (Join-Path $Destination ([string]$record.path).Replace('/', '\'))
        ) | Out-Null
    }
    foreach ($record in @($Inventory | Where-Object kind -ceq "file")) {
        $sourcePath = Join-Path `
            $Source `
            ([string]$record.path).Replace('/', '\')
        $destinationPath = Join-Path `
            $Destination `
            ([string]$record.path).Replace('/', '\')
        [IO.Directory]::CreateDirectory(
            [IO.Path]::GetDirectoryName($destinationPath)
        ) | Out-Null
        [IO.File]::Copy($sourcePath, $destinationPath, $false)
    }
    Set-ComoteProtectedDirectoryAcl -LiteralPath $Destination
}

function Assert-ComoteToolTrees {
    param(
        [Parameter(Mandatory)]$State,
        [Parameter(Mandatory)][string]$CurrentRoot
    )

    $expectedJson = $State.toolInventory |
        ConvertTo-Json -Depth 8 -Compress
    foreach ($binding in @(
        @($CurrentRoot, $false, "current"),
        @([string]$State.protectedToolRoot, $true, "protected")
    )) {
        $actual = Get-ComoteToolInventory `
            -Root ([string]$binding[0]) `
            -RequireProtectedAcl:([bool]$binding[1])
        if ((Get-ComoteToolInventoryHash -Records $actual) -cne
                [string]$State.toolInventorySha256 -or
            ($actual | ConvertTo-Json -Depth 8 -Compress) -cne
                $expectedJson) {
            throw "The $($binding[2]) promotion tool tree changed."
        }
    }
}

function Assert-ComotePromotionStateSchema {
    param([Parameter(Mandatory)][PSObject]$State)

    Assert-ComoteExactProperties `
        -InputObject $State `
        -Expected @(
            "schemaVersion",
            "phase",
            "createdUtc",
            "updatedUtc",
            "arguments",
            "protectedToolRoot",
            "protectedOutputRoot",
            "toolInventorySha256",
            "toolInventory",
            "packageRoot",
            "releaseManifestSha256",
            "regressionReportPath",
            "regressionReportSha256",
            "sbomPath",
            "sbomSha256",
            "mediaEvidencePath",
            "mediaEvidenceSha256",
            "runtimePolicy",
            "signingSnapshotName",
            "preInstallMediaGate",
            "installEvidence",
            "hubSmoke",
            "bootEvidence",
            "e2eRuns",
            "verifierConfiguration",
            "verifierEvidence",
            "unifiedCleanup",
            "provisionalReportPath",
            "provisionalReportSha256",
            "candidateRoot",
            "candidateIndexPath",
            "candidateIndexSha256",
            "candidateRoleHashes",
            "managerRoleRoot",
            "managerRoleTest",
            "clientRoleRoot",
            "clientManifestSha256",
            "clientInstallEvidence",
            "clientRoleTest",
            "clientCleanup",
            "finalReportPath",
            "finalReportSha256",
            "promotedIndexPath",
            "promotedIndexSha256"
        ) `
        -Description "Protected promotion state"
    Assert-ComoteExactProperties `
        -InputObject $State.arguments `
        -Expected @(
            "acknowledgeDisposableVm",
            "snapshotName",
            "releaseId",
            "sourceRoot",
            "sourceInventorySha256",
            "isolatedWorkRoot",
            "signToolPath",
            "outputDirectory",
            "controllerUser",
            "hubPort"
        ) `
        -Description "Protected promotion arguments"
    Assert-ComoteExactProperties `
        -InputObject $State.bootEvidence `
        -Expected @(
            "normalBootBefore",
            "normalBootAfter",
            "s1CheckpointUtc",
            "s1BootMarker",
            "s1Evidence",
            "coldCheckpointUtc",
            "coldBootBefore",
            "coldBootAfter",
            "coldEvidence",
            "verifierBootBefore",
            "verifierBootAfter",
            "postCleanupCheckpointUtc",
            "postCleanupBootBefore",
            "postCleanupBootAfter"
        ) `
        -Description "Protected boot evidence"
    if ([int]$State.schemaVersion -ne 2 -or
        $script:PromotionPhases -cnotcontains [string]$State.phase -or
        [string]$State.toolInventorySha256 -cnotmatch '^[0-9A-F]{64}$' -or
        @($State.toolInventory).Count -lt 15) {
        throw "Protected promotion state identity is invalid."
    }
    $expectedArguments = Get-ComotePromotionArgumentBinding
    if (($State.arguments | ConvertTo-Json -Depth 8 -Compress) -cne
        ($expectedArguments | ConvertTo-Json -Depth 8 -Compress)) {
        throw "Protected promotion state arguments differ from this command."
    }
}

function Write-ComotePromotionState {
    param(
        [Parameter(Mandatory)][PSObject]$State,
        [Parameter(Mandatory)][string]$StatePath
    )

    $State.updatedUtc = [DateTime]::UtcNow.ToString("o")
    Assert-ComotePromotionStateSchema -State $State
    Write-ComoteJsonAtomically `
        -LiteralPath $StatePath `
        -InputObject $State `
        -Depth 20
    Assert-ComoteNoUntrustedWriteAcl -LiteralPath $StatePath
}

function Move-ComotePromotionPhase {
    param(
        [Parameter(Mandatory)][PSObject]$State,
        [Parameter(Mandatory)][string]$ExpectedCurrent,
        [Parameter(Mandatory)][string]$Next,
        [Parameter(Mandatory)][string]$StatePath
    )

    $currentIndex = [Array]::IndexOf(
        $script:PromotionPhases,
        $ExpectedCurrent
    )
    if ([string]$State.phase -cne $ExpectedCurrent -or
        $currentIndex -lt 0 -or
        $currentIndex + 1 -ge $script:PromotionPhases.Count -or
        [string]$script:PromotionPhases[$currentIndex + 1] -cne $Next) {
        throw "The promotion phase transition is not the exact next phase."
    }
    $State.phase = $Next
    Write-ComotePromotionState -State $State -StatePath $StatePath
}

function New-ComotePromotionState {
    param(
        [Parameter(Mandatory)]$Paths,
        [Parameter(Mandatory)][object[]]$ToolInventory,
        [Parameter(Mandatory)][string]$ToolInventorySha256
    )

    return [PSCustomObject][ordered]@{
        schemaVersion = 2
        phase = "pre-install-gates"
        createdUtc = [DateTime]::UtcNow.ToString("o")
        updatedUtc = [DateTime]::UtcNow.ToString("o")
        arguments = Get-ComotePromotionArgumentBinding
        protectedToolRoot = $Paths.Tools
        protectedOutputRoot = $Paths.Output
        toolInventorySha256 = $ToolInventorySha256
        toolInventory = $ToolInventory
        packageRoot = $null
        releaseManifestSha256 = $null
        regressionReportPath = $null
        regressionReportSha256 = $null
        sbomPath = $null
        sbomSha256 = $null
        mediaEvidencePath = $null
        mediaEvidenceSha256 = $null
        runtimePolicy = $null
        signingSnapshotName = $null
        preInstallMediaGate = $null
        installEvidence = $null
        hubSmoke = $null
        bootEvidence = [PSCustomObject][ordered]@{
            normalBootBefore = $null
            normalBootAfter = $null
            s1CheckpointUtc = $null
            s1BootMarker = $null
            s1Evidence = $null
            coldCheckpointUtc = $null
            coldBootBefore = $null
            coldBootAfter = $null
            coldEvidence = $null
            verifierBootBefore = $null
            verifierBootAfter = $null
            postCleanupCheckpointUtc = $null
            postCleanupBootBefore = $null
            postCleanupBootAfter = $null
        }
        e2eRuns = @()
        verifierConfiguration = $null
        verifierEvidence = $null
        unifiedCleanup = $null
        provisionalReportPath = $null
        provisionalReportSha256 = $null
        candidateRoot = $null
        candidateIndexPath = $null
        candidateIndexSha256 = $null
        candidateRoleHashes = $null
        managerRoleRoot = $null
        managerRoleTest = $null
        clientRoleRoot = $null
        clientManifestSha256 = $null
        clientInstallEvidence = $null
        clientRoleTest = $null
        clientCleanup = $null
        finalReportPath = $null
        finalReportSha256 = $null
        promotedIndexPath = $null
        promotedIndexSha256 = $null
    }
}

function Initialize-ComotePromotionState {
    param([Parameter(Mandatory)]$Paths)

    if (Test-Path -LiteralPath $Paths.Root) {
        throw "Promotion root exists without its exact schema-2 state."
    }
    $parent = [IO.Path]::GetDirectoryName($Paths.Root)
    [void](Assert-ComoteOrdinaryLocalPath `
        -LiteralPath $parent `
        -Directory $true `
        -Description "Promotion state parent")
    $partialLeaf = ([IO.Path]::GetFileName($Paths.Root)) + ".partial-*"
    $retainedPartials = @(Get-ChildItem `
        -LiteralPath $parent `
        -Force `
        -Filter $partialLeaf `
        -ErrorAction Stop)
    if ($retainedPartials.Count -ne 0) {
        throw ("A retained protected-initialization partial blocks retry: " +
            ($retainedPartials.FullName -join ", "))
    }
    $partialRoot = Join-Path `
        $parent `
        (([IO.Path]::GetFileName($Paths.Root)) + ".partial-" +
            [Guid]::NewGuid().ToString("N"))
    $published = $false
    try {
        [IO.Directory]::CreateDirectory($partialRoot) | Out-Null
        Set-ComoteProtectedDirectoryAcl -LiteralPath $partialRoot
        $partialTools = Join-Path $partialRoot "Tools"
        foreach ($directory in @(
            (Join-Path $partialRoot "Evidence"),
            (Join-Path $partialRoot "RoleTests")
        )) {
            [IO.Directory]::CreateDirectory($directory) | Out-Null
        }

        $inventory = Get-ComoteToolInventory -Root $PSScriptRoot
        $inventoryHash = Get-ComoteToolInventoryHash -Records $inventory
        Copy-ComoteProtectedToolTree `
            -Source $PSScriptRoot `
            -Destination $partialTools `
            -Inventory $inventory
        Set-ComoteProtectedDirectoryAcl -LiteralPath $partialRoot
        $protectedInventory = Get-ComoteToolInventory `
            -Root $partialTools `
            -RequireProtectedAcl
        if ((Get-ComoteToolInventoryHash -Records $protectedInventory) -cne
                $inventoryHash -or
            ($protectedInventory | ConvertTo-Json -Depth 8 -Compress) -cne
                ($inventory | ConvertTo-Json -Depth 8 -Compress)) {
            throw "Protected promotion tool copy failed exact verification."
        }
        $state = New-ComotePromotionState `
            -Paths $Paths `
            -ToolInventory $inventory `
            -ToolInventorySha256 $inventoryHash
        $partialState = Join-Path $partialRoot "promotion-state.json"
        Write-ComotePromotionState `
            -State $state `
            -StatePath $partialState
        [IO.Directory]::Move($partialRoot, $Paths.Root)
        $published = $true
        Assert-ComoteNoUntrustedWriteAcl -LiteralPath $Paths.Root
        Assert-ComoteNoUntrustedWriteAcl -LiteralPath $Paths.State
        return $state
    }
    finally {
        if (-not $published -and
            (Test-Path -LiteralPath $partialRoot -PathType Container)) {
            Write-Warning "Incomplete protected initialization retained: $partialRoot"
        }
    }
}

function Get-ComoteBootMarker {
    $os = Get-CimInstance `
        -ClassName Win32_OperatingSystem `
        -ErrorAction Stop
    return ([DateTime]$os.LastBootUpTime).ToUniversalTime().ToString("o")
}

function Write-ComoteHashSidecar {
    param([Parameter(Mandatory)][string]$LiteralPath)

    $hash = Get-ComoteSha256 -LiteralPath $LiteralPath
    [IO.File]::WriteAllText(
        "$LiteralPath.sha256",
        ("{0} *{1}`r`n" -f $hash, [IO.Path]::GetFileName($LiteralPath)),
        (New-Object Text.ASCIIEncoding)
    )
    return $hash
}

function Initialize-ComotePromotionNativeTypes {
    if ("Comote.Promotion.Native.ProcessInspector" -as [type]) {
        return
    }
    Add-Type -TypeDefinition @"
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace Comote.Promotion.Native
{
    public sealed class ProcessTokenEvidence
    {
        public string Path { get; set; }
        public string OwnerSid { get; set; }
        public bool IsElevated { get; set; }
        public string ElevationType { get; set; }
        public string AuthenticationId { get; set; }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TokenElevation { public int TokenIsElevated; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Luid { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TokenStatistics
    {
        public Luid TokenId;
        public Luid AuthenticationId;
        public long ExpirationTime;
        public int TokenType;
        public int ImpersonationLevel;
        public uint DynamicCharged;
        public uint DynamicAvailable;
        public uint GroupCount;
        public uint PrivilegeCount;
        public Luid ModifiedId;
    }

    public static class ProcessInspector
    {
        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const uint TokenQuery = 0x0008;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(
            uint access, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true,
            CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(
            IntPtr process, int flags, StringBuilder path, ref int size);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(
            IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(
            IntPtr token, int informationClass, IntPtr information,
            int informationLength, out int returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("shell32.dll", SetLastError = true,
            CharSet = CharSet.Unicode)]
        private static extern IntPtr CommandLineToArgvW(
            string commandLine, out int argumentCount);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LocalFree(IntPtr memory);

        private static T ReadToken<T>(IntPtr token, int informationClass)
            where T : struct
        {
            int size = Marshal.SizeOf(typeof(T));
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                int returned;
                if (!GetTokenInformation(token, informationClass,
                    buffer, size, out returned))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                return (T)Marshal.PtrToStructure(buffer, typeof(T));
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        public static ProcessTokenEvidence InspectProcess(int processId)
        {
            IntPtr process = OpenProcess(
                ProcessQueryLimitedInformation, false, processId);
            if (process == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            IntPtr token = IntPtr.Zero;
            try
            {
                var path = new StringBuilder(32768);
                int length = path.Capacity;
                if (!QueryFullProcessImageName(process, 0, path, ref length))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if (!OpenProcessToken(process, TokenQuery, out token))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                string owner;
                using (var identity = new WindowsIdentity(token))
                    owner = identity.User.Value;
                int elevationType = ReadToken<int>(token, 18);
                TokenElevation elevation = ReadToken<TokenElevation>(token, 20);
                TokenStatistics statistics = ReadToken<TokenStatistics>(token, 10);
                string typeName = elevationType == 1
                    ? "TokenElevationTypeDefault"
                    : elevationType == 2
                        ? "TokenElevationTypeFull"
                        : elevationType == 3
                            ? "TokenElevationTypeLimited"
                            : "TokenElevationTypeUnknown";
                return new ProcessTokenEvidence
                {
                    Path = path.ToString(),
                    OwnerSid = owner,
                    IsElevated = elevation.TokenIsElevated != 0,
                    ElevationType = typeName,
                    AuthenticationId = String.Format(
                        "{0:X8}:{1:X8}",
                        unchecked((uint)statistics.AuthenticationId.HighPart),
                        statistics.AuthenticationId.LowPart)
                };
            }
            finally
            {
                if (token != IntPtr.Zero) CloseHandle(token);
                CloseHandle(process);
            }
        }

        public static string[] ParseCommandLine(string commandLine)
        {
            int count;
            IntPtr argv = CommandLineToArgvW(commandLine, out count);
            if (argv == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var result = new string[count];
                for (int index = 0; index < count; index++)
                {
                    IntPtr value = Marshal.ReadIntPtr(
                        argv, index * IntPtr.Size);
                    result[index] = Marshal.PtrToStringUni(value);
                }
                return result;
            }
            finally { LocalFree(argv); }
        }
    }
}
"@
}

function Get-ComoteControllerSid {
    $account = New-Object Security.Principal.NTAccount($ControllerUser)
    return [string]$account.Translate(
        [Security.Principal.SecurityIdentifier]
    ).Value
}

function Assert-ComoteControllerToken {
    Initialize-ComotePromotionNativeTypes
    $expectedSid = Get-ComoteControllerSid
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        if ([string]$identity.User.Value -cne $expectedSid) {
            throw "The current interactive token is not the bound controller."
        }
        $group = New-Object Security.Principal.NTAccount(
            $env:COMPUTERNAME,
            $script:ComotePreviewGroupName
        )
        $groupSid = [Security.Principal.SecurityIdentifier]$group.Translate(
            [Security.Principal.SecurityIdentifier]
        )
        $principal = New-Object Security.Principal.WindowsPrincipal($identity)
        if (-not $principal.IsInRole($groupSid)) {
            throw ("The current token does not contain the controller group. " +
                "Sign out and sign in, then rerun this command.")
        }
        $native = [Comote.Promotion.Native.ProcessInspector]::InspectProcess($PID)
        return [PSCustomObject][ordered]@{
            userSid = $expectedSid
            groupSid = [string]$groupSid.Value
            processId = [int]$PID
            isElevated = [bool]$native.IsElevated
            elevationType = [string]$native.ElevationType
            authenticationId = [string]$native.AuthenticationId
        }
    }
    finally {
        $identity.Dispose()
    }
}

function Assert-ComoteRuntimePolicy {
    param([Parameter(Mandatory)]$RuntimePolicy)

    Assert-ComoteExactProperties `
        -InputObject $RuntimePolicy `
        -Expected @(
            "frameworkVersion", "runtimePackVersion", "coreclrLength",
            "coreclrSha256", "coreclrFileVersion", "coreclrProductVersion"
        ) `
        -Description "Runtime policy"
    if ([string]$RuntimePolicy.frameworkVersion -cne "10.0.10" -or
        [string]$RuntimePolicy.runtimePackVersion -cne "10.0.10" -or
        [int64]$RuntimePolicy.coreclrLength -ne 4614952 -or
        [string]$RuntimePolicy.coreclrSha256 -cne
            "58859F85A30CC71313B281898E7CFBDBB9ECCB95AE2A3F865329EFD47EBF31BB" -or
        [string]$RuntimePolicy.coreclrFileVersion -cne
            "10,0,1026,32716 @Commit: f7d90799ce4ef09a0bb257852a57248d2a8fb8dd" -or
        [string]$RuntimePolicy.coreclrProductVersion -cne
            "10,0,1026,32716 @Commit: f7d90799ce4ef09a0bb257852a57248d2a8fb8dd") {
        throw "The release runtime is not the exact 10.0.10 policy."
    }
}

function Get-ComoteLoadedRuntimeEvidence {
    param(
        [Parameter(Mandatory)][int]$ProcessId,
        [Parameter(Mandatory)]$RuntimePolicy
    )

    $process = Get-Process -Id $ProcessId -ErrorAction Stop
    $modules = @($process.Modules | Where-Object ModuleName -ceq "coreclr.dll")
    if ($modules.Count -ne 1) {
        throw "The process does not have exactly one loaded coreclr.dll."
    }
    $path = [string]$modules[0].FileName
    $identity = Assert-ComoteOrdinaryLocalPath `
        -LiteralPath $path `
        -Directory $false `
        -Description "Loaded coreclr.dll"
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($path)
    if ([int64]$identity.Identity.Length -ne
            [int64]$RuntimePolicy.coreclrLength -or
        (Get-ComoteSha256 -LiteralPath $path) -cne
            [string]$RuntimePolicy.coreclrSha256 -or
        [string]$version.FileVersion -cne
            [string]$RuntimePolicy.coreclrFileVersion -or
        [string]$version.ProductVersion -cne
            [string]$RuntimePolicy.coreclrProductVersion) {
        throw "Loaded coreclr.dll differs from the release runtime policy."
    }
    return [PSCustomObject][ordered]@{
        path = $path
        length = [int64]$identity.Identity.Length
        sha256 = [string]$RuntimePolicy.coreclrSha256
        fileVersion = [string]$version.FileVersion
        productVersion = [string]$version.ProductVersion
    }
}

function Get-ComoteExactProcessEvidence {
    param(
        [Parameter(Mandatory)][int]$ProcessId,
        [Parameter(Mandatory)][string]$ExpectedPath,
        [Parameter(Mandatory)][string[]]$ExpectedArguments,
        [Parameter(Mandatory)]$Application,
        [Parameter(Mandatory)]$RuntimePolicy,
        [Parameter(Mandatory)][string]$ExpectedOwnerSid,
        [AllowNull()][Nullable[bool]]$ExpectedElevated,
        [switch]$RequireLimited,
        [switch]$RequireTrustedSigner,
        [switch]$RequireProtectedRuntimeAcl
    )

    Initialize-ComotePromotionNativeTypes
    $cim = @(Get-CimInstance `
        -ClassName Win32_Process `
        -Filter ("ProcessId={0}" -f $ProcessId) `
        -ErrorAction Stop)
    if ($cim.Count -ne 1) {
        throw "The expected process identity is no longer present."
    }
    $native = [Comote.Promotion.Native.ProcessInspector]::InspectProcess($ProcessId)
    $expectedFullPath = [IO.Path]::GetFullPath($ExpectedPath)
    if (-not [IO.Path]::GetFullPath([string]$native.Path).Equals(
            $expectedFullPath, [StringComparison]::OrdinalIgnoreCase) -or
        -not [IO.Path]::GetFullPath([string]$cim[0].ExecutablePath).Equals(
            $expectedFullPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The process executable path is not exact."
    }
    $arguments = @(
        [Comote.Promotion.Native.ProcessInspector]::ParseCommandLine(
            [string]$cim[0].CommandLine
        )
    )
    if (($arguments | ConvertTo-Json -Compress) -cne
        ($ExpectedArguments | ConvertTo-Json -Compress)) {
        throw "The process argv is not the exact allowed vector."
    }
    if ([string]$native.OwnerSid -cne $ExpectedOwnerSid -or
        ($null -ne $ExpectedElevated -and
            [bool]$native.IsElevated -ne [bool]$ExpectedElevated) -or
        ($RequireLimited.IsPresent -and
            [string]$native.ElevationType -cne "TokenElevationTypeLimited")) {
        throw "The process owner or token elevation is not exact."
    }
    if ((Get-ComoteSha256 -LiteralPath $expectedFullPath) -cne
            [string]$Application.sha256 -or
        [string][Diagnostics.FileVersionInfo]::GetVersionInfo(
            $expectedFullPath).OriginalFilename -cne
            [string]$Application.originalFilename) {
        throw "The process executable differs from its authenticated manifest."
    }
    Assert-ComotePinnedAuthenticodeSigner `
        -LiteralPath $expectedFullPath `
        -Thumbprint ([string]$Application.signerThumbprint) `
        -Description "Running packaged application" `
        -RequireTrusted:$RequireTrustedSigner
    $runtime = Get-ComoteLoadedRuntimeEvidence `
        -ProcessId $ProcessId `
        -RuntimePolicy $RuntimePolicy
    if ($RequireProtectedRuntimeAcl.IsPresent) {
        Assert-ComoteNoUntrustedWriteAcl -LiteralPath $runtime.path
        Assert-ComoteNoUntrustedWriteAcl `
            -LiteralPath ([IO.Path]::GetDirectoryName($runtime.path))
    }
    return [PSCustomObject][ordered]@{
        processId = $ProcessId
        path = $expectedFullPath
        sha256 = [string]$Application.sha256
        originalFilename = [string]$Application.originalFilename
        signerThumbprint = [string]$Application.signerThumbprint
        ownerSid = [string]$native.OwnerSid
        isElevated = [bool]$native.IsElevated
        elevationType = [string]$native.ElevationType
        authenticationId = [string]$native.AuthenticationId
        argv = $arguments
        commandLineSha256 = [BitConverter]::ToString(
            [Security.Cryptography.SHA256]::Create().ComputeHash(
                [Text.Encoding]::UTF8.GetBytes([string]$cim[0].CommandLine)
            )
        ).Replace("-", "")
        runtime = $runtime
    }
}

function Get-ComoteSingletonProcessId {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$ExpectedPath
    )

    $processes = @(Get-CimInstance `
        -ClassName Win32_Process `
        -Filter ("Name='{0}'" -f $Name.Replace("'", "''")) `
        -ErrorAction Stop)
    $matches = @($processes | Where-Object {
        -not [string]::IsNullOrWhiteSpace([string]$_.ExecutablePath) -and
        [IO.Path]::GetFullPath([string]$_.ExecutablePath).Equals(
            [IO.Path]::GetFullPath($ExpectedPath),
            [StringComparison]::OrdinalIgnoreCase)
    })
    if ($processes.Count -ne 1 -or $matches.Count -ne 1) {
        throw "The exact packaged process is not a singleton."
    }
    return [int]$matches[0].ProcessId
}

function Test-ComotePrivateIPv4 {
    param([Parameter(Mandatory)][string]$Address)

    $parsed = $null
    if (-not [Net.IPAddress]::TryParse($Address, [ref]$parsed) -or
        $parsed.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) {
        return $false
    }
    $bytes = $parsed.GetAddressBytes()
    return $bytes[0] -eq 10 -or
        ($bytes[0] -eq 172 -and $bytes[1] -ge 16 -and $bytes[1] -le 31) -or
        ($bytes[0] -eq 192 -and $bytes[1] -eq 168)
}

function Get-ComoteVmwareNatRouteEvidence {
    param([Parameter(Mandatory)]$Environment)

    if ([string]$Environment.Computer.Manufacturer -cne "VMware, Inc." -or
        [string]$Environment.Computer.Model -notmatch '^VMware') {
        throw "The default route is not being sampled in the VMware VM."
    }
    $routes = @(Get-NetRoute `
        -AddressFamily IPv4 `
        -DestinationPrefix "0.0.0.0/0" `
        -ErrorAction Stop |
        Where-Object { [string]$_.State -ceq "Alive" })
    if ($routes.Count -lt 1) {
        throw "The VM has no live IPv4 default route."
    }
    $routeBindings = @()
    foreach ($route in $routes) {
        $interfaces = @(Get-NetIPInterface `
            -AddressFamily IPv4 `
            -InterfaceIndex ([int]$route.InterfaceIndex) `
            -ErrorAction Stop |
            Where-Object { [string]$_.ConnectionState -ceq "Connected" })
        if ($interfaces.Count -ne 1) {
            throw "A live default route lacks one connected IPv4 interface."
        }
        $routeBindings += [PSCustomObject]@{
            route = $route
            interface = $interfaces[0]
            effectiveMetric = [int64]$route.RouteMetric +
                [int64]$interfaces[0].InterfaceMetric
        }
    }
    $routeBindings = @($routeBindings |
        Sort-Object effectiveMetric,
            @{ Expression = { [int]$_.route.RouteMetric } },
            @{ Expression = { [int]$_.route.InterfaceIndex } })
    $bestMetric = [int64]$routeBindings[0].effectiveMetric
    $best = @($routeBindings | Where-Object {
        [int64]$_.effectiveMetric -eq $bestMetric
    })
    if ($best.Count -ne 1 -or
        -not (Test-ComotePrivateIPv4 `
            -Address ([string]$best[0].route.NextHop))) {
        throw "The VM does not have one RFC1918 NAT default route."
    }
    $adapter = Get-NetAdapter `
        -InterfaceIndex ([int]$best[0].route.InterfaceIndex) `
        -ErrorAction Stop
    if ([string]$adapter.Status -cne "Up") {
        throw "The VMware NAT default-route adapter is not up."
    }
    $unicastAddresses = @(Get-NetIPAddress `
        -AddressFamily IPv4 `
        -InterfaceIndex ([int]$best[0].route.InterfaceIndex) `
        -ErrorAction Stop |
        Where-Object {
            [string]$_.AddressState -ceq "Preferred" -and
            (Test-ComotePrivateIPv4 -Address ([string]$_.IPAddress))
        } |
        ForEach-Object { [string]$_.IPAddress } |
        Sort-Object -Unique)
    if ($unicastAddresses.Count -lt 1) {
        throw "The VMware NAT interface has no preferred RFC1918 address."
    }
    return [PSCustomObject][ordered]@{
        destinationPrefix = "0.0.0.0/0"
        nextHop = [string]$best[0].route.NextHop
        interfaceIndex = [int]$best[0].route.InterfaceIndex
        interfaceGuid = [string]$adapter.InterfaceGuid
        interfaceDescription = [string]$adapter.InterfaceDescription
        routeMetric = [int]$best[0].route.RouteMetric
        interfaceMetric = [int]$best[0].interface.InterfaceMetric
        interfaceUnicastAddresses = $unicastAddresses
    }
}

function Get-ComoteReciprocalHubTuple {
    param(
        [Parameter(Mandatory)][int]$ClientProcessId,
        [Parameter(Mandatory)][int]$ManagerProcessId,
        [Parameter(Mandatory)][int]$Port,
        [Parameter(Mandatory)]$RouteEvidence
    )

    $connections = @(Get-NetTCPConnection -State Established -ErrorAction Stop)
    $clients = @($connections | Where-Object {
        [int]$_.OwningProcess -eq $ClientProcessId -and
        [int]$_.RemotePort -eq $Port
    })
    $managers = @($connections | Where-Object {
        [int]$_.OwningProcess -eq $ManagerProcessId -and
        [int]$_.LocalPort -eq $Port
    })
    $pairs = @()
    foreach ($client in $clients) {
        foreach ($manager in $managers) {
            if ([string]$client.LocalAddress -ceq
                    [string]$manager.RemoteAddress -and
                [int]$client.LocalPort -eq [int]$manager.RemotePort -and
                [string]$client.RemoteAddress -ceq
                    [string]$manager.LocalAddress) {
                $pairs += [PSCustomObject][ordered]@{
                    clientAddress = [string]$client.LocalAddress
                    clientPort = [int]$client.LocalPort
                    managerAddress = [string]$client.RemoteAddress
                    managerPort = [int]$client.RemotePort
                    clientProcessId = $ClientProcessId
                    managerProcessId = $ManagerProcessId
                }
            }
        }
    }
    $listeners = @(Get-NetTCPConnection -State Listen -ErrorAction Stop |
        Where-Object {
            [int]$_.OwningProcess -eq $ManagerProcessId -and
            [int]$_.LocalPort -eq $Port
        })
    if ($clients.Count -ne 1 -or
        $managers.Count -ne 1 -or
        $pairs.Count -ne 1 -or
        $listeners.Count -ne 1) {
        throw "Hub smoke does not have one reciprocal singleton TCP tuple."
    }
    $allowedAddresses = @($RouteEvidence.interfaceUnicastAddresses)
    if (-not (Test-ComotePrivateIPv4 -Address $pairs[0].clientAddress) -or
        -not (Test-ComotePrivateIPv4 -Address $pairs[0].managerAddress) -or
        $allowedAddresses -cnotcontains [string]$pairs[0].clientAddress -or
        $allowedAddresses -cnotcontains [string]$pairs[0].managerAddress) {
        throw "Hub smoke is not bound to the attested VMware NAT interface."
    }
    return $pairs[0]
}

function Stop-ComoteExactProcess {
    param(
        [Parameter(Mandatory)][int]$ProcessId,
        [Parameter(Mandatory)][string]$ExpectedPath
    )

    Initialize-ComotePromotionNativeTypes
    $native = [Comote.Promotion.Native.ProcessInspector]::InspectProcess($ProcessId)
    if (-not [IO.Path]::GetFullPath([string]$native.Path).Equals(
            [IO.Path]::GetFullPath($ExpectedPath),
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to stop a process whose exact identity changed."
    }
    $process = Get-Process -Id $ProcessId -ErrorAction Stop
    $process.Kill()
    if (-not $process.WaitForExit(10000)) {
        throw "The exact packaged UI process did not exit."
    }
}

function Assert-ComoteProtectedTreeAcl {
    param([Parameter(Mandatory)][string]$Root)

    [void](Assert-ComoteOrdinaryLocalPath `
        -LiteralPath $Root `
        -Directory $true `
        -Description "Protected evidence tree")
    Assert-ComoteNoUntrustedWriteAcl -LiteralPath $Root
    foreach ($item in @(Get-ChildItem `
        -LiteralPath $Root `
        -Force `
        -Recurse `
        -ErrorAction Stop)) {
        [void](Assert-ComoteOrdinaryLocalPath `
            -LiteralPath $item.FullName `
            -Directory:([bool]$item.PSIsContainer) `
            -Description "Protected evidence item")
        Assert-ComoteNoUntrustedWriteAcl -LiteralPath $item.FullName
    }
}

function Assert-ComoteAsciiHashSidecar {
    param(
        [Parameter(Mandatory)][string]$LiteralPath,
        [Parameter(Mandatory)][string]$TargetPath
    )

    [void](Assert-ComoteOrdinaryLocalPath `
        -LiteralPath $LiteralPath `
        -Directory $false `
        -Description "Out-of-band SHA-256 sidecar")
    $bytes = [IO.File]::ReadAllBytes($LiteralPath)
    if (@($bytes | Where-Object { $_ -gt 127 }).Count -ne 0) {
        throw "A SHA-256 sidecar is not ASCII."
    }
    $expectedHash = Get-ComoteSha256 -LiteralPath $TargetPath
    $expected = "{0} *{1}`r`n" -f
        $expectedHash,
        [IO.Path]::GetFileName($TargetPath)
    $actual = (New-Object Text.ASCIIEncoding).GetString($bytes)
    if ($actual -cne $expected) {
        throw "An out-of-band SHA-256 sidecar is not exact."
    }
    return $expectedHash
}

function Get-ComoteStreamSha256 {
    param([Parameter(Mandatory)][IO.Stream]$Stream)

    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString(
            $algorithm.ComputeHash($Stream)
        ).Replace("-", "")
    }
    finally {
        $algorithm.Dispose()
    }
}

function Assert-ComoteZipMatchesDirectory {
    param(
        [Parameter(Mandatory)][string]$ZipPath,
        [Parameter(Mandatory)][string]$Directory
    )

    [void](Assert-ComoteOrdinaryLocalPath `
        -LiteralPath $ZipPath `
        -Directory $false `
        -Description "Outer release ZIP")
    $root = (Assert-ComoteOrdinaryLocalPath `
        -LiteralPath $Directory `
        -Directory $true `
        -Description "Outer release stage").FullPath
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $expected = New-Object `
        'Collections.Generic.Dictionary[string,object]' `
        ([StringComparer]::Ordinal)
    $directories = New-Object `
        'Collections.Generic.Dictionary[string,bool]' `
        ([StringComparer]::Ordinal)
    $expectedCase = New-Object `
        'Collections.Generic.HashSet[string]' `
        ([StringComparer]::OrdinalIgnoreCase)
    foreach ($item in @(Get-ChildItem `
        -LiteralPath $Directory `
        -Force `
        -Recurse `
        -ErrorAction Stop)) {
        $relative = Get-ComoteRelativePath `
            -Root $root `
            -Child $item.FullName
        Assert-ComoteSafeRelativePath -RelativePath $relative
        if (-not $expectedCase.Add($relative)) {
            throw "The unified stage has a case-colliding path."
        }
        $identity = Assert-ComoteOrdinaryLocalPath `
            -LiteralPath $item.FullName `
            -Directory:([bool]$item.PSIsContainer) `
            -Description "Outer release stage item"
        if ($item.PSIsContainer) {
            $directories.Add($relative, (@(Get-ChildItem `
                -LiteralPath $item.FullName `
                -Force `
                -ErrorAction Stop).Count -eq 0))
            continue
        }
        $expected.Add($relative, [PSCustomObject]@{
            length = [int64]$identity.Identity.Length
            sha256 = Get-ComoteSha256 -LiteralPath $item.FullName
        })
    }
    $archive = [IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        $seen = New-Object `
            'Collections.Generic.HashSet[string]' `
            ([StringComparer]::Ordinal)
        $seenCase = New-Object `
            'Collections.Generic.HashSet[string]' `
            ([StringComparer]::OrdinalIgnoreCase)
        $seenDirectories = New-Object `
            'Collections.Generic.HashSet[string]' `
            ([StringComparer]::Ordinal)
        foreach ($entry in $archive.Entries) {
            $relative = [string]$entry.FullName.Replace('\', '/').TrimEnd('/')
            if ([string]::IsNullOrWhiteSpace($relative)) {
                throw "The outer release ZIP contains an empty entry."
            }
            Assert-ComoteSafeRelativePath -RelativePath $relative
            if (-not $seenCase.Add($relative)) {
                throw "The outer release ZIP has a case-colliding entry."
            }
            if ([string]$entry.FullName.EndsWith(
                    "/", [StringComparison]::Ordinal) -or
                [string]::IsNullOrEmpty([string]$entry.Name)) {
                if (-not $directories.ContainsKey($relative) -or
                    -not $seenDirectories.Add($relative)) {
                    throw "The outer release ZIP has an unexpected directory."
                }
                continue
            }
            if (-not $seen.Add($relative) -or
                -not $expected.ContainsKey($relative) -or
                [int64]$entry.Length -ne [int64]$expected[$relative].length) {
                throw "The outer release ZIP inventory is not exact."
            }
            $stream = $entry.Open()
            try {
                $hash = Get-ComoteStreamSha256 -Stream $stream
            }
            finally {
                $stream.Dispose()
            }
            if ($hash -cne [string]$expected[$relative].sha256) {
                throw "The outer release ZIP bytes differ from the stage."
            }
        }
        if ($seen.Count -ne $expected.Count) {
            throw "The outer release ZIP omits staged files."
        }
        foreach ($pair in $directories.GetEnumerator()) {
            if ([bool]$pair.Value -and
                -not $seenDirectories.Contains([string]$pair.Key)) {
                throw "The outer release ZIP omits an empty staged directory."
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Assert-ComoteRegressionEvidence {
    param(
        [Parameter(Mandatory)]$Report,
        [Parameter(Mandatory)]$Sbom,
        [Parameter(Mandatory)]$Media,
        [Parameter(Mandatory)]$Release,
        [Parameter(Mandatory)][string]$SbomHash,
        [Parameter(Mandatory)][string]$MediaHash,
        [Parameter(Mandatory)][string]$FfmpegReceiptHash
    )

    Assert-ComoteExactProperties `
        -InputObject $Report `
        -Expected @(
            "schemaVersion", "status", "completedUtc", "snapshotName",
            "sourceInventorySha256", "sourceHygiene", "environment",
            "toolchain", "runtimePolicy", "projects", "lockFiles",
            "assetsFiles", "pureTests", "observer", "mediaGate",
            "boundaryGroups", "boundaryScripts", "vulnerabilityScans",
            "topLevelVulnerableCount", "transitiveVulnerableCount",
            "vulnerabilityCount", "nugetSbom",
            "noDriverDeviceInputOrSystemMutation"
        ) `
        -Description "Bundled regression report"
    Assert-ComoteExactProperties `
        -InputObject $Sbom `
        -Expected @(
            "schemaVersion", "sourceInventorySha256", "runtimePolicy",
            "lockFiles", "packageCount", "packages"
        ) `
        -Description "Bundled NuGet SBOM"
    Assert-ComoteExactProperties `
        -InputObject $Media `
        -Expected @(
            "schemaVersion", "status", "startedUtc", "completedUtc",
            "gateAssemblyVersion", "environment", "receipt", "ffmpeg",
            "mediaProbe", "error"
        ) `
        -Description "Bundled media evidence"
    if ([int]$Report.schemaVersion -ne 1 -or
        [string]$Report.status -cne "passed" -or
        [string]$Report.snapshotName -cne $SnapshotName -or
        [string]$Report.sourceInventorySha256 -cne
            $ExpectedSourceInventorySha256.ToUpperInvariant() -or
        @($Report.projects).Count -ne 13 -or
        @($Report.lockFiles).Count -ne 13 -or
        @($Report.assetsFiles).Count -ne 13 -or
        [int]$Report.sourceHygiene.backupArtifactCount -ne 0 -or
        @($Report.vulnerabilityScans).Count -ne 13 -or
        [int]$Report.topLevelVulnerableCount -ne 0 -or
        [int]$Report.transitiveVulnerableCount -ne 0 -or
        [int]$Report.vulnerabilityCount -ne 0 -or
        [bool]$Report.noDriverDeviceInputOrSystemMutation -ne $true -or
        [string]$Report.nugetSbom.sha256 -cne $SbomHash -or
        [string]$Report.mediaGate.evidenceSha256 -cne $MediaHash -or
        [string]$Report.observer.executableSha256 -cnotmatch
            '^[0-9A-F]{64}$' -or
        [int]$Sbom.schemaVersion -ne 1 -or
        [string]$Sbom.sourceInventorySha256 -cne
            $ExpectedSourceInventorySha256.ToUpperInvariant() -or
        @($Sbom.lockFiles).Count -ne 13 -or
        [int]$Sbom.packageCount -le 0 -or
        [int]$Media.schemaVersion -ne 1 -or
        [string]$Media.status -cne "passed" -or
        [string]$Media.environment.runtimeVersion -cne "10.0.10" -or
        [string]$Media.receipt.sha256 -cne $FfmpegReceiptHash -or
        $null -ne $Media.error) {
        throw "Bundled pre-install evidence is not exact."
    }
    foreach ($scan in @($Report.vulnerabilityScans)) {
        if ([int]$scan.topLevelVulnerableCount -ne 0 -or
            [int]$scan.transitiveVulnerableCount -ne 0) {
            throw "A bundled vulnerability scan has a nonzero nested count."
        }
    }
    foreach ($runtime in @(
        $Report.runtimePolicy,
        $Sbom.runtimePolicy,
        $Release.Document.runtimePolicy
    )) {
        Assert-ComoteRuntimePolicy -RuntimePolicy $runtime
    }
    $canonicalRuntime = $Release.Document.runtimePolicy |
        ConvertTo-Json -Depth 8 -Compress
    if (($Report.runtimePolicy | ConvertTo-Json -Depth 8 -Compress) -cne
            $canonicalRuntime -or
        ($Sbom.runtimePolicy | ConvertTo-Json -Depth 8 -Compress) -cne
            $canonicalRuntime) {
        throw "Regression, SBOM, and release runtime policies differ."
    }
}

function Get-ComoteAuthenticatedUnifiedOutput {
    param([Parameter(Mandatory)][string]$OutputRoot)

    $stageName = "Comote-{0}-win-x64" -f $ReleaseId
    $packageRoot = Join-Path $OutputRoot $stageName
    $manifestPath = Join-Path `
        $packageRoot `
        $script:ComotePreviewManifestName
    [void](Assert-ComoteOrdinaryLocalPath `
        -LiteralPath $OutputRoot `
        -Directory $true `
        -Description "Protected unified output")
    $manifestHash = Get-ComoteSha256 -LiteralPath $manifestPath
    [void](Assert-ComoteAsciiHashSidecar `
        -LiteralPath (Join-Path `
            $OutputRoot `
            "$stageName.release-manifest.sha256") `
        -TargetPath $manifestPath)
    $zipPath = Join-Path $OutputRoot "$stageName.zip"
    $zipHash = Assert-ComoteAsciiHashSidecar `
        -LiteralPath "$zipPath.sha256" `
        -TargetPath $zipPath
    Assert-ComoteZipMatchesDirectory `
        -ZipPath $zipPath `
        -Directory $packageRoot

    $release = Get-ComoteReleaseManifest `
        -PackageRoot $packageRoot `
        -ExpectedManifestSha256 $manifestHash
    Assert-ComoteReleaseInventory -Release $release
    [void](Assert-ComoteDriverPackageBinding -Release $release)
    Assert-ComoteReleaseSigners -Release $release
    if ([string]$release.Document.releaseId -cne $ReleaseId -or
        [string]$release.Document.packageRole -cne "validation-unified") {
        throw "The protected unified package identity is invalid."
    }

    $bindings = @(
        @("Validation/REGRESSION_GATE.json", "Regression"),
        @("THIRD_PARTY_NOTICES/NUGET_SBOM.json", "Sbom"),
        @("Validation/MEDIA_GATE.json", "Media"),
        @("THIRD_PARTY_NOTICES/FFMPEG_ASSET_RECEIPT.json", "Ffmpeg")
    )
    $documents = @{}
    foreach ($binding in $bindings) {
        $relative = [string]$binding[0]
        if (-not $release.EntryMap.ContainsKey($relative)) {
            throw "The release omits authenticated evidence: $relative"
        }
        $path = Join-Path $packageRoot $relative.Replace('/', '\')
        $entry = $release.EntryMap[$relative]
        if ((Get-ComoteSha256 -LiteralPath $path) -cne
            [string]$entry.sha256) {
            throw "Bundled evidence differs from the release manifest."
        }
        $documents[[string]$binding[1]] = [PSCustomObject]@{
            Path = $path
            Hash = [string]$entry.sha256
            Document = if ([string]$binding[1] -ceq "Ffmpeg") {
                $null
            }
            else {
                Read-ComoteJson `
                    -LiteralPath $path `
                    -Description "Bundled release evidence"
            }
        }
    }
    Assert-ComoteRegressionEvidence `
        -Report $documents.Regression.Document `
        -Sbom $documents.Sbom.Document `
        -Media $documents.Media.Document `
        -Release $release `
        -SbomHash ([string]$documents.Sbom.Hash) `
        -MediaHash ([string]$documents.Media.Hash) `
        -FfmpegReceiptHash ([string]$documents.Ffmpeg.Hash)

    $evidenceRoot = Join-Path `
        ([IO.Path]::GetDirectoryName($OutputRoot)) `
        "Evidence"
    $reportEnvironment = Get-ComotePreviewTargetEnvironment `
        -PackageRole "validation-unified"
    $mediaTools = @($release.Document.validationTools | Where-Object {
        [string]$_.role -ceq "media-gate"
    })
    if ($mediaTools.Count -ne 1) {
        throw "The release does not contain one validation MediaGate."
    }
    $generatedReports = @{}
    $signingSnapshotName = $null
    foreach ($reportBinding in @(
        @("$stageName.build-report.json", "unified-build-report.json", "build"),
        @(
            "phase2-release-preparation-report.json",
            "unified-preparation-report.json",
            "preparation"
        )
    )) {
        $sourceReport = Join-Path $OutputRoot ([string]$reportBinding[0])
        $savedReport = Join-Path $evidenceRoot ([string]$reportBinding[1])
        $sourceExists = Test-Path -LiteralPath $sourceReport -PathType Leaf
        $savedExists = Test-Path -LiteralPath $savedReport -PathType Leaf
        if ($sourceExists -and $savedExists) {
            throw "A generated release report exists in both crash locations."
        }
        if (-not $sourceExists -and -not $savedExists) {
            throw ("The protected unified output omits its exact " +
                "$($reportBinding[2]) report.")
        }
        $reportPath = if ($sourceExists) { $sourceReport } else { $savedReport }
        $generatedReport = Read-ComoteJson `
            -LiteralPath $reportPath `
            -Description "Generated unified release report"
        if ([string]$reportBinding[2] -ceq "build") {
            Assert-ComoteExactProperties `
                -InputObject $generatedReport `
                -Expected @(
                    "schemaVersion", "releaseId", "completedUtc",
                    "vmManufacturer", "vmModel", "osBuild", "osUbr",
                    "sourceInventorySha256", "regressionGateSha256",
                    "nugetSbomSha256", "mediaGateSha256",
                    "ffmpegAssetReceiptSha256", "dotnetSdkVersion",
                    "selfContainedRuntimeVersion", "validationMediaGateSha256",
                    "signingReceiptSha256", "driverManifestSha256",
                    "pinnedInstallerInputSha256", "releaseManifestSha256",
                    "zipFile", "zipSha256", "certificateThumbprint", "note"
                ) `
                -Description "Generated unified build report"
            if ([int]$generatedReport.schemaVersion -ne 1 -or
                [string]$generatedReport.releaseId -cne $ReleaseId -or
                [string]$generatedReport.vmManufacturer -cne
                    [string]$reportEnvironment.Computer.Manufacturer -or
                [string]$generatedReport.vmModel -cne
                    [string]$reportEnvironment.Computer.Model -or
                [string]$generatedReport.osBuild -cne
                    [string]$reportEnvironment.OperatingSystem.BuildNumber -or
                [int]$generatedReport.osUbr -ne
                    [int]$reportEnvironment.Ubr -or
                [string]$generatedReport.sourceInventorySha256 -cne
                    $ExpectedSourceInventorySha256.ToUpperInvariant() -or
                [string]$generatedReport.regressionGateSha256 -cne
                    [string]$documents.Regression.Hash -or
                [string]$generatedReport.nugetSbomSha256 -cne
                    [string]$documents.Sbom.Hash -or
                [string]$generatedReport.mediaGateSha256 -cne
                    [string]$documents.Media.Hash -or
                [string]$generatedReport.ffmpegAssetReceiptSha256 -cne
                    [string]$documents.Ffmpeg.Hash -or
                [string]$generatedReport.releaseManifestSha256 -cne
                    $manifestHash -or
                [string]$generatedReport.zipFile -cne "$stageName.zip" -or
                [string]$generatedReport.zipSha256 -cne $zipHash -or
                [string]$generatedReport.selfContainedRuntimeVersion -cne
                    "10.0.10" -or
                [string]$generatedReport.dotnetSdkVersion -cne "10.0.302" -or
                [string]$generatedReport.validationMediaGateSha256 -cne
                    [string]$mediaTools[0].sha256 -or
                [string]$generatedReport.signingReceiptSha256 -cnotmatch
                    '^[0-9A-F]{64}$' -or
                [string]$generatedReport.driverManifestSha256 -cne
                    [string]$release.Document.driver.manifestSha256 -or
                [string]$generatedReport.pinnedInstallerInputSha256 -cnotmatch
                    '^[0-9A-F]{64}$' -or
                [string]$generatedReport.certificateThumbprint -cne
                    [string]$release.Document.driver.certificateThumbprint -or
                [string]$generatedReport.note -cne
                    "No driver was installed, loaded, or tested by this build step.") {
                throw "The generated unified build report is not exact."
            }
        }
        else {
            Assert-ComoteExactProperties `
                -InputObject $generatedReport `
                -Expected @(
                    "schemaVersion", "releaseId", "completedUtc",
                    "snapshotName", "sourceInventorySha256",
                    "regressionGateSha256", "nugetSbomSha256",
                    "mediaGateSha256", "signingReceiptSha256",
                    "driverManifestSha256", "pinnedInstallerInputSha256",
                    "certificateThumbprint", "isolatedWorkRoot",
                    "outputDirectory", "testSigningChangedByWorkflow", "note"
                ) `
                -Description "Generated unified preparation report"
            if ([int]$generatedReport.schemaVersion -ne 1 -or
                [string]$generatedReport.releaseId -cne $ReleaseId -or
                [string]$generatedReport.snapshotName -cne $SnapshotName -or
                [string]$generatedReport.sourceInventorySha256 -cne
                    $ExpectedSourceInventorySha256.ToUpperInvariant() -or
                [string]$generatedReport.regressionGateSha256 -cne
                    [string]$documents.Regression.Hash -or
                [string]$generatedReport.nugetSbomSha256 -cne
                    [string]$documents.Sbom.Hash -or
                [string]$generatedReport.mediaGateSha256 -cne
                    [string]$documents.Media.Hash -or
                [string]$generatedReport.signingReceiptSha256 -cnotmatch
                    '^[0-9A-F]{64}$' -or
                [string]$generatedReport.driverManifestSha256 -cnotmatch
                    '^[0-9A-F]{64}$' -or
                [string]$generatedReport.pinnedInstallerInputSha256 -cnotmatch
                    '^[0-9A-F]{64}$' -or
                -not [IO.Path]::GetFullPath(
                    [string]$generatedReport.isolatedWorkRoot
                ).Equals(
                    [IO.Path]::GetFullPath($IsolatedWorkRoot),
                    [StringComparison]::OrdinalIgnoreCase) -or
                -not [IO.Path]::GetFullPath(
                    [string]$generatedReport.outputDirectory
                ).Equals(
                    [IO.Path]::GetFullPath($OutputRoot),
                    [StringComparison]::OrdinalIgnoreCase) -or
                [bool]$generatedReport.testSigningChangedByWorkflow -ne $false) {
                throw "The generated unified preparation report is not exact."
            }
            if ([string]$generatedReport.certificateThumbprint -cne
                    [string]$release.Document.driver.certificateThumbprint -or
                [string]$generatedReport.driverManifestSha256 -cne
                    [string]$release.Document.driver.manifestSha256 -or
                [string]$generatedReport.note -cne
                    "Fresh build/sign/package only; no driver was installed or loaded.") {
                throw "The preparation identity/certificate/note is not exact."
            }
            $signingSnapshotName = [string]$generatedReport.snapshotName
        }
        $generatedReports[[string]$reportBinding[2]] = $generatedReport
        $generatedReportHash = Get-ComoteSha256 -LiteralPath $reportPath
        if ($sourceExists) {
            [IO.File]::Move($sourceReport, $savedReport)
            if ((Get-ComoteSha256 -LiteralPath $savedReport) -cne
                $generatedReportHash) {
                throw "A generated release report changed during atomic move."
            }
        }
        Assert-ComoteNoUntrustedWriteAcl -LiteralPath $savedReport
    }
    if ($generatedReports.Count -ne 2 -or
        [string]::IsNullOrWhiteSpace($signingSnapshotName)) {
        throw "The two generated release reports are not a complete pair."
    }
    $buildReport = $generatedReports.build
    $preparationReport = $generatedReports.preparation
    foreach ($field in @(
        "sourceInventorySha256", "regressionGateSha256", "nugetSbomSha256",
        "mediaGateSha256", "signingReceiptSha256", "driverManifestSha256",
        "pinnedInstallerInputSha256", "certificateThumbprint"
    )) {
        if ([string]$buildReport.$field -cne
            [string]$preparationReport.$field) {
            throw "Build/preparation reports disagree on $field."
        }
    }
    $topItems = @(Get-ChildItem `
        -LiteralPath $OutputRoot `
        -Force `
        -ErrorAction Stop)
    $expectedTopNames = @(
        $stageName,
        "$stageName.zip",
        "$stageName.zip.sha256",
        "$stageName.release-manifest.sha256"
    )
    if ($topItems.Count -ne 4 -or
        @($topItems | Where-Object {
            $expectedTopNames -cnotcontains [string]$_.Name
        }).Count -ne 0) {
        throw "The protected unified output top-level inventory is not exact."
    }
    Assert-ComoteProtectedTreeAcl -Root $OutputRoot
    return [PSCustomObject]@{
        OutputRoot = $OutputRoot
        PackageRoot = $packageRoot
        ManifestPath = $manifestPath
        ManifestSha256 = $manifestHash
        ZipPath = $zipPath
        ZipSha256 = $zipHash
        Release = $release
        RegressionPath = [string]$documents.Regression.Path
        RegressionSha256 = [string]$documents.Regression.Hash
        Regression = $documents.Regression.Document
        SbomPath = [string]$documents.Sbom.Path
        SbomSha256 = [string]$documents.Sbom.Hash
        MediaPath = [string]$documents.Media.Path
        MediaSha256 = [string]$documents.Media.Hash
        FfmpegReceiptPath = [string]$documents.Ffmpeg.Path
        FfmpegReceiptSha256 = [string]$documents.Ffmpeg.Hash
        SigningSnapshotName = $signingSnapshotName
    }
}

function Assert-ComoteBoundArtifactState {
    param([Parameter(Mandatory)]$State)

    if ($null -eq $State.packageRoot) {
        foreach ($name in @(
            "releaseManifestSha256", "regressionReportPath",
            "regressionReportSha256", "sbomPath", "sbomSha256",
            "mediaEvidencePath", "mediaEvidenceSha256", "runtimePolicy",
            "signingSnapshotName"
        )) {
            if ($null -ne $State.$name) {
                throw "Uncommitted release evidence appears in protected state."
            }
        }
        return $null
    }
    $artifacts = Get-ComoteAuthenticatedUnifiedOutput `
        -OutputRoot ([string]$State.protectedOutputRoot)
    if (-not [IO.Path]::GetFullPath([string]$State.packageRoot).Equals(
            [IO.Path]::GetFullPath($artifacts.PackageRoot),
            [StringComparison]::OrdinalIgnoreCase) -or
        [string]$State.releaseManifestSha256 -cne
            [string]$artifacts.ManifestSha256 -or
        [string]$State.regressionReportPath -cne
            [string]$artifacts.RegressionPath -or
        [string]$State.regressionReportSha256 -cne
            [string]$artifacts.RegressionSha256 -or
        [string]$State.sbomPath -cne [string]$artifacts.SbomPath -or
        [string]$State.sbomSha256 -cne [string]$artifacts.SbomSha256 -or
        [string]$State.mediaEvidencePath -cne
            [string]$artifacts.MediaPath -or
        [string]$State.mediaEvidenceSha256 -cne
            [string]$artifacts.MediaSha256 -or
        ($State.runtimePolicy | ConvertTo-Json -Depth 8 -Compress) -cne
            ($artifacts.Release.Document.runtimePolicy |
                ConvertTo-Json -Depth 8 -Compress) -or
        [string]$State.signingSnapshotName -cne
            [string]$artifacts.SigningSnapshotName) {
        throw "Protected state release evidence changed."
    }
    if ($null -ne $State.preInstallMediaGate) {
        $gate = $State.preInstallMediaGate
        $gateDocument = Read-ComoteJson `
            -LiteralPath ([string]$gate.path) `
            -Description "Final signed pre-install MediaGate evidence"
        if ((Get-ComoteSha256 -LiteralPath ([string]$gate.path)) -cne
                [string]$gate.sha256 -or
            [string]$gateDocument.status -cne "passed" -or
            [string]$gateDocument.environment.runtimeVersion -cne "10.0.10" -or
            [string]$gateDocument.receipt.sha256 -cne
                [string]$artifacts.FfmpegReceiptSha256 -or
            $null -ne $gateDocument.error) {
            throw "Final signed pre-install MediaGate evidence changed."
        }
    }
    return $artifacts
}

function Invoke-ComoteFinalPreInstallMediaGate {
    param(
        [Parameter(Mandatory)]$Artifacts,
        [Parameter(Mandatory)]$Paths
    )

    $application = @($Artifacts.Release.Document.validationTools |
        Where-Object { [string]$_.role -ceq "media-gate" })
    if ($application.Count -ne 1) {
        throw "The release has no exact signed MediaGate application."
    }
    $executable = Join-Path `
        $Artifacts.PackageRoot `
        ([string]$application[0].path).Replace('/', '\')
    if ((Get-ComoteSha256 -LiteralPath $executable) -cne
            [string]$application[0].sha256 -or
        [string][Diagnostics.FileVersionInfo]::GetVersionInfo(
            $executable).OriginalFilename -cne
            [string]$application[0].originalFilename) {
        throw "The final signed MediaGate executable is not manifest-bound."
    }
    Assert-ComotePinnedAuthenticodeSigner `
        -LiteralPath $executable `
        -Thumbprint ([string]$application[0].signerThumbprint) `
        -Description "Final signed pre-install MediaGate"
    $output = Join-Path $Paths.Evidence "pre-install-media-gate.json"
    if (-not (Test-Path -LiteralPath $output -PathType Leaf)) {
        $result = Invoke-ComoteNativeProcess `
            -FilePath $executable `
            -Arguments @(
                "--acknowledge-release-vm",
                "--receipt", $Artifacts.FfmpegReceiptPath,
                "--output", $output
            )
        if ($result.ExitCode -ne 0) {
            throw "The final signed pre-install MediaGate failed."
        }
    }
    $document = Read-ComoteJson `
        -LiteralPath $output `
        -Description "Final signed pre-install MediaGate evidence"
    if ([string]$document.status -cne "passed" -or
        [string]$document.environment.runtimeVersion -cne "10.0.10" -or
        [string]$document.receipt.sha256 -cne
            [string]$Artifacts.FfmpegReceiptSha256 -or
        $null -ne $document.error) {
        throw "The final signed pre-install MediaGate evidence is invalid."
    }
    Assert-ComoteNoUntrustedWriteAcl -LiteralPath $output
    return [PSCustomObject][ordered]@{
        path = $output
        sha256 = Get-ComoteSha256 -LiteralPath $output
        executablePath = $executable
        executableSha256 = [string]$application[0].sha256
        signerThumbprint = [string]$application[0].signerThumbprint
        runtimeVersion = "10.0.10"
        ffmpegReceiptSha256 = [string]$Artifacts.FfmpegReceiptSha256
    }
}

function Write-ComoteNativeValidationReceipt {
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][string]$RecoverySnapshotName,
        [Parameter(Mandatory)][string]$SigningSnapshotName,
        [switch]$AllowMissingSigningReceipt
    )

    $runtimeRoot = Join-Path `
        $RepositoryRoot `
        "Driver\ComoteVirtualHidPhase2\Runtime"
    . (Join-Path $runtimeRoot "Phase2Runtime.Common.ps1")
    . (Join-Path $runtimeRoot "Phase2Enumeration.Common.ps1")
    $phase2Root = Split-Path -Parent $runtimeRoot
    $signingReceiptPath = Join-Path `
        $phase2Root `
        "artifacts\phase2-runtime-state\test-signing-preparation.json"
    if (Test-Path -LiteralPath $signingReceiptPath -PathType Leaf) {
        $signingReceipt = Read-ComotePhase2JsonDocument `
            -LiteralPath $signingReceiptPath `
            -Description "Phase 2 signing receipt"
        if ([string]$signingReceipt.snapshotName -cne $SigningSnapshotName) {
            throw "The Phase 2 signing snapshot differs from protected state."
        }
    }
    elseif (-not $AllowMissingSigningReceipt.IsPresent) {
        throw "The bound Phase 2 signing receipt is missing before cleanup."
    }
    $packages = @(Get-ComotePhase2DriverPackages)
    if ($packages.Count -ne 1) {
        throw "The native installation does not have one Phase 2 package."
    }
    $publishedInf = Assert-ComotePhase2DriverPackageIdentity `
        -Package $packages[0] `
        -ExpectedPublishedInfName ""
    $roots = @(Get-ComotePhase2RootDevices)
    if ($roots.Count -ne 1 -or
        [int]$roots[0].ConfigManagerErrorCode -ne 0) {
        throw "The native installation does not have one healthy Phase 2 root."
    }
    $rootInstanceId = [string]$roots[0].PNPDeviceID
    $children = Get-ComotePhase2InputChildren `
        -RootInstanceId $rootInstanceId
    if ($children.Keyboards.Count -ne 1 -or
        $children.Mice.Count -ne 2) {
        throw "The native installation does not expose exact VHF children."
    }
    $computer = Get-CimInstance `
        -ClassName Win32_ComputerSystem `
        -ErrorAction Stop
    $os = Get-CimInstance `
        -ClassName Win32_OperatingSystem `
        -ErrorAction Stop
    $receipt = [PSCustomObject][ordered]@{
        schemaVersion = 1
        status = "installed-enumerated"
        recoverySnapshotName = $RecoverySnapshotName
        signingSnapshotName = $SigningSnapshotName
        manufacturer = [string]$computer.Manufacturer
        model = [string]$computer.Model
        osBuildNumber = [string]$os.BuildNumber
        serviceName = "ComoteVirtualHidPhase2"
        publishedInfName = $publishedInf
        rootDeviceInstanceId = $rootInstanceId
        keyboardInstanceIds = @([string]$children.Keyboards[0].InstanceId)
        mouseInstanceIds = @($children.Mice |
            ForEach-Object { [string]$_.InstanceId } |
            Sort-Object)
        probeLaunched = $false
        inputReportsSubmitted = 0
        source = "native-release-installer-compatibility-evidence"
    }
    $receiptPath = Join-Path `
        $phase2Root `
        "artifacts\phase2-runtime-state\enumeration-installation.json"
    Write-ComotePhase2JsonAtomically `
        -LiteralPath $receiptPath `
        -InputObject $receipt
    return $receiptPath
}

function Get-ComoteRegressionArtifactSnapshot {
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)]$Regression
    )

    if (@($Regression.lockFiles).Count -ne 13 -or
        @($Regression.assetsFiles).Count -ne 13) {
        throw "Regression evidence does not bind 13 lock/assets pairs."
    }
    $records = @()
    foreach ($kind in @("lockFiles", "assetsFiles")) {
        foreach ($entry in @($Regression.$kind)) {
            Assert-ComoteExactProperties `
                -InputObject $entry `
                -Expected @("path", "length", "sha256") `
                -Description "Regression artifact binding"
            $relative = [string]$entry.path
            Assert-ComoteSafeRelativePath -RelativePath $relative
            $path = Join-Path $RepositoryRoot $relative.Replace('/', '\')
            $identity = Assert-ComoteOrdinaryLocalPath `
                -LiteralPath $path `
                -Directory $false `
                -Description "Locked E2E restore artifact"
            if ([int64]$identity.Identity.Length -ne [int64]$entry.length -or
                (Get-ComoteSha256 -LiteralPath $path) -cne
                    [string]$entry.sha256) {
                throw "A locked E2E restore artifact differs from regression."
            }
            $records += [PSCustomObject][ordered]@{
                kind = $kind
                path = $relative
                length = [int64]$entry.length
                sha256 = [string]$entry.sha256
            }
        }
    }
    $canonical = $records | ConvertTo-Json -Depth 5 -Compress
    $bytes = (New-Object Text.ASCIIEncoding).GetBytes($canonical)
    return [PSCustomObject][ordered]@{
        records = $records
        sha256 = [BitConverter]::ToString(
            [Security.Cryptography.SHA256]::Create().ComputeHash($bytes)
        ).Replace("-", "")
    }
}

function Invoke-ComotePromotionE2E {
    param(
        [Parameter(Mandatory)][string]$Label,
        [Parameter(Mandatory)]$Artifacts
    )

    if (-not $AcknowledgeInputGeneration.IsPresent -or
        $InputConfirmationPhrase -cne $script:InputAttestationPhrase) {
        throw "The exact real-input E2E acknowledgement is required."
    }
    $repositoryRoot = [string]$State.arguments.isolatedWorkRoot
    $beforeArtifacts = Get-ComoteRegressionArtifactSnapshot `
        -RepositoryRoot $repositoryRoot `
        -Regression $Artifacts.Regression
    [void](Write-ComoteNativeValidationReceipt `
        -RepositoryRoot $repositoryRoot `
        -RecoverySnapshotName $SnapshotName `
        -SigningSnapshotName ([string]$State.signingSnapshotName) `
        -AllowMissingSigningReceipt:($null -ne $State.unifiedCleanup))
    $summaryRoot = Join-Path `
        $repositoryRoot `
        "Driver\FinalValidation\artifacts\phase2-final-e2e"
    $before = @()
    if (Test-Path -LiteralPath $summaryRoot -PathType Container) {
        $before = @(Get-ChildItem `
            -LiteralPath $summaryRoot `
            -Filter "validation-summary.json" `
            -File `
            -Recurse `
            -ErrorAction Stop |
            ForEach-Object { [IO.Path]::GetFullPath($_.FullName) })
    }
    $broker = @($Artifacts.Release.Document.cmt1Applications |
        Where-Object { [string]$_.role -ceq "broker" })
    if ($broker.Count -ne 1) {
        throw "The release has no exact Broker application."
    }
    $hadRestoreLockedMode = Test-Path Env:\RestoreLockedMode
    $savedRestoreLockedMode = $env:RestoreLockedMode
    try {
        $env:RestoreLockedMode = "true"
        & (Join-Path `
            $repositoryRoot `
            "Driver\FinalValidation\Invoke-Phase2FinalE2E.ps1") `
            -AcknowledgeDisposableVm `
            -RecoverySnapshotName $SnapshotName `
            -AcknowledgeInputGeneration `
            -ConfirmationPhrase $script:InputAttestationPhrase `
            -ExpectedBrokerSha256 ([string]$broker[0].sha256)
        if ($LASTEXITCODE -notin @($null, 0)) {
            throw "Final E2E returned a nonzero process exit code."
        }
    }
    finally {
        if ($hadRestoreLockedMode) {
            $env:RestoreLockedMode = $savedRestoreLockedMode
        }
        else {
            Remove-Item Env:\RestoreLockedMode -ErrorAction SilentlyContinue
        }
    }
    $after = @(Get-ChildItem `
        -LiteralPath $summaryRoot `
        -Filter "validation-summary.json" `
        -File `
        -Recurse `
        -ErrorAction Stop |
        ForEach-Object { [IO.Path]::GetFullPath($_.FullName) })
    $newReports = @($after | Where-Object { $before -cnotcontains $_ })
    if ($newReports.Count -ne 1) {
        throw "Final E2E did not produce exactly one new summary."
    }
    $report = Read-ComoteJson `
        -LiteralPath $newReports[0] `
        -Description "Final E2E summary"
    [void](Assert-ComoteOrdinaryLocalPath `
        -LiteralPath $newReports[0] `
        -Directory $false `
        -Description "New Final E2E summary")
    Assert-ComoteExactProperties `
        -InputObject $report `
        -Expected @(
            "schemaVersion", "runId", "status", "startedUtc", "completedUtc",
            "recoverySnapshotName", "manufacturer", "model", "osBuildNumber",
            "osUbr", "brokerServiceName", "brokerServiceState",
            "brokerExecutablePath", "brokerExecutableSha256",
            "expectedBrokerSha256", "appReportPath", "configSha256",
            "observerSha256", "recoveryStateSha256", "cleanupExitCode",
            "buildExitCode", "applicationExitCode", "evidenceTestCount",
            "rawInputEventCount", "appReportSha256", "failureType",
            "failureMessage"
        ) `
        -Description "Final E2E summary"
    if ([int]$report.schemaVersion -ne 1 -or
        [string]$report.status -cne "passed" -or
        [string]$report.recoverySnapshotName -cne $SnapshotName -or
        [int]$report.buildExitCode -ne 0 -or
        [int]$report.applicationExitCode -ne 0 -or
        [int]$report.cleanupExitCode -ne 0 -or
        [int]$report.evidenceTestCount -le 0 -or
        [int]$report.rawInputEventCount -le 0 -or
        [string]$report.observerSha256 -cne
            [string]$Artifacts.Regression.observer.executableSha256 -or
        [string]$report.brokerExecutableSha256 -cne
            [string]$broker[0].sha256 -or
        [string]$report.expectedBrokerSha256 -cne
            [string]$broker[0].sha256 -or
        [string]$report.brokerServiceName -cne "ComoteInputBroker" -or
        [string]$report.brokerServiceState -cne "Running" -or
        [string]$report.configSha256 -cnotmatch '^[0-9A-F]{64}$' -or
        [string]$report.recoveryStateSha256 -cnotmatch '^[0-9A-F]{64}$' -or
        -not [string]::IsNullOrEmpty([string]$report.failureType) -or
        -not [string]::IsNullOrEmpty([string]$report.failureMessage) -or
        [string]$report.appReportSha256 -cnotmatch '^[0-9A-F]{64}$') {
        throw "Final E2E evidence did not pass every exact gate."
    }
    $runRoot = [IO.Path]::GetDirectoryName($newReports[0]).TrimEnd('\')
    $appReportPath = [IO.Path]::GetFullPath([string]$report.appReportPath)
    if (-not $appReportPath.StartsWith(
            $runRoot + "\", [StringComparison]::OrdinalIgnoreCase)) {
        throw "The Final E2E app report is outside its new run root."
    }
    [void](Assert-ComoteOrdinaryLocalPath `
        -LiteralPath $appReportPath `
        -Directory $false `
        -Description "Final E2E application report")
    if ((Get-ComoteSha256 -LiteralPath $appReportPath) -cne
        [string]$report.appReportSha256) {
        throw "The Final E2E application report hash is not authentic."
    }
    $afterArtifacts = Get-ComoteRegressionArtifactSnapshot `
        -RepositoryRoot $repositoryRoot `
        -Regression $Artifacts.Regression
    if ([string]$beforeArtifacts.sha256 -cne
        [string]$afterArtifacts.sha256) {
        throw "Final E2E changed a locked restore artifact."
    }
    return [PSCustomObject][ordered]@{
        label = $Label
        completedUtc = [string]$report.completedUtc
        path = $newReports[0]
        sha256 = Get-ComoteSha256 -LiteralPath $newReports[0]
        appReportPath = $appReportPath
        appReportSha256 = [string]$report.appReportSha256
        observerSha256 = [string]$report.observerSha256
        brokerSha256 = [string]$broker[0].sha256
        buildExitCode = [int]$report.buildExitCode
        applicationExitCode = [int]$report.applicationExitCode
        cleanupExitCode = [int]$report.cleanupExitCode
        evidenceTestCount = [int]$report.evidenceTestCount
        rawInputEventCount = [int]$report.rawInputEventCount
        restoreArtifactSnapshotSha256 = [string]$afterArtifacts.sha256
    }
}

function Add-ComoteE2ERun {
    param(
        [Parameter(Mandatory)]$State,
        [Parameter(Mandatory)]$Evidence
    )

    if (@($State.e2eRuns | Where-Object {
        [string]$_.label -ceq [string]$Evidence.label
    }).Count -ne 0) {
        throw "The protected state already contains this E2E label."
    }
    $State.e2eRuns = @($State.e2eRuns) + $Evidence
}

function Get-ComoteSystemEventsAfter {
    param(
        [Parameter(Mandatory)][DateTime]$CheckpointUtc,
        [Parameter(Mandatory)][string]$Provider,
        [Parameter(Mandatory)][int]$Id
    )

    return @(Get-WinEvent `
        -FilterHashtable @{
            LogName = "System"
            ProviderName = $Provider
            Id = $Id
            StartTime = $CheckpointUtc.ToLocalTime()
        } `
        -ErrorAction Stop |
        Where-Object { $_.TimeCreated.ToUniversalTime() -gt $CheckpointUtc } |
        Sort-Object TimeCreated, RecordId)
}

function Get-ComoteS1ResumeEvidence {
    param([Parameter(Mandatory)][DateTime]$CheckpointUtc)

    $sleepEvents = @(Get-ComoteSystemEventsAfter `
        -CheckpointUtc $CheckpointUtc `
        -Provider "Microsoft-Windows-Kernel-Power" `
        -Id 42)
    $matchingSleep = @()
    foreach ($event in $sleepEvents) {
        $xml = New-Object Xml.XmlDocument
        $xml.XmlResolver = $null
        $xml.LoadXml($event.ToXml())
        $values = @{}
        foreach ($node in @($xml.SelectNodes(
            "/*[local-name()='Event']/*[local-name()='EventData']/*[local-name()='Data']"
        ))) {
            $values[[string]$node.GetAttribute("Name")] =
                [string]$node.InnerText
        }
        if ([string]$values.TargetState -ceq "1" -and
            [string]$values.EffectiveState -ceq "1") {
            $matchingSleep += $event
        }
    }
    $resumeEvents = @(Get-ComoteSystemEventsAfter `
        -CheckpointUtc $CheckpointUtc `
        -Provider "Microsoft-Windows-Power-Troubleshooter" `
        -Id 1)
    $pairs = @()
    foreach ($sleep in $matchingSleep) {
        foreach ($resume in $resumeEvents) {
            if ($resume.TimeCreated -gt $sleep.TimeCreated) {
                $pairs += [PSCustomObject]@{
                    sleep = $sleep
                    resume = $resume
                }
            }
        }
    }
    if ($pairs.Count -ne 1) {
        throw "The checkpoint has no singleton S1 sleep/resume event pair."
    }
    return [PSCustomObject][ordered]@{
        targetState = 1
        effectiveState = 1
        sleepRecordId = [int64]$pairs[0].sleep.RecordId
        sleepUtc = $pairs[0].sleep.TimeCreated.ToUniversalTime().ToString("o")
        resumeRecordId = [int64]$pairs[0].resume.RecordId
        resumeUtc = $pairs[0].resume.TimeCreated.ToUniversalTime().ToString("o")
    }
}

function Get-ComoteColdStartEvidence {
    param(
        [Parameter(Mandatory)][DateTime]$CheckpointUtc,
        [Parameter(Mandatory)][string]$BeforeBoot,
        [Parameter(Mandatory)][string]$AfterBoot
    )

    $events = @(Get-ComoteSystemEventsAfter `
        -CheckpointUtc $CheckpointUtc `
        -Provider "EventLog" `
        -Id 6006)
    if ($events.Count -ne 1 -or $AfterBoot -ceq $BeforeBoot) {
        throw "The checkpoint has no singleton clean shutdown/cold boot evidence."
    }
    return [PSCustomObject][ordered]@{
        eventId = 6006
        recordId = [int64]$events[0].RecordId
        shutdownUtc = $events[0].TimeCreated.ToUniversalTime().ToString("o")
        bootBefore = $BeforeBoot
        bootAfter = $AfterBoot
    }
}

function Invoke-ComoteHubSmoke {
    param(
        [Parameter(Mandatory)]$Artifacts,
        [Parameter(Mandatory)]$InstallReceipt,
        [Parameter(Mandatory)]$Environment
    )

    if (-not $AcknowledgeHubSmoke.IsPresent -or
        $HubSmokeConfirmationPhrase -cne $script:HubAttestationPhrase) {
        throw "The exact interactive Hub/input attestation is required."
    }
    $controllerSid = Get-ComoteControllerSid
    $applications = @{}
    foreach ($role in @("client", "manager", "broker")) {
        $matches = @($Artifacts.Release.Document.cmt1Applications |
            Where-Object { [string]$_.role -ceq $role })
        if ($matches.Count -ne 1) {
            throw "The release has no exact $role application."
        }
        $applications[$role] = $matches[0]
    }
    $installRoot = [string]$InstallReceipt.paths.installRoot
    $clientPath = Join-Path `
        $installRoot `
        ([string]$applications.client.path).Replace('/', '\')
    $managerPath = Join-Path `
        $installRoot `
        ([string]$applications.manager.path).Replace('/', '\')
    $brokerPath = Join-Path `
        $installRoot `
        ([string]$applications.broker.path).Replace('/', '\')
    $clientPid = Get-ComoteSingletonProcessId `
        -Name "ComoteClient.exe" `
        -ExpectedPath $clientPath
    $managerPid = Get-ComoteSingletonProcessId `
        -Name "ComoteManager.exe" `
        -ExpectedPath $managerPath
    $services = @(Get-ComoteBrokerService)
    if ($services.Count -ne 1 -or
        [string]$services[0].State -cne "Running" -or
        [int]$services[0].ProcessId -le 0) {
        throw "The Broker service is not a running singleton."
    }
    Assert-ComoteBrokerServiceIdentity `
        -Service $services[0] `
        -BinaryPath $brokerPath `
        -BinarySha256 ([string]$applications.broker.sha256)
    $brokerPid = [int]$services[0].ProcessId
    $clientEvidence = Get-ComoteExactProcessEvidence `
        -ProcessId $clientPid `
        -ExpectedPath $clientPath `
        -ExpectedArguments @($clientPath, "--manager-hub", "--virtual-hid") `
        -Application $applications.client `
        -RuntimePolicy $Artifacts.Release.Document.runtimePolicy `
        -ExpectedOwnerSid $controllerSid `
        -ExpectedElevated $false `
        -RequireLimited `
        -RequireTrustedSigner
    $managerEvidence = Get-ComoteExactProcessEvidence `
        -ProcessId $managerPid `
        -ExpectedPath $managerPath `
        -ExpectedArguments @($managerPath, "--manager-hub") `
        -Application $applications.manager `
        -RuntimePolicy $Artifacts.Release.Document.runtimePolicy `
        -ExpectedOwnerSid $controllerSid `
        -ExpectedElevated $false `
        -RequireLimited `
        -RequireTrustedSigner
    $brokerEvidence = Get-ComoteExactProcessEvidence `
        -ProcessId $brokerPid `
        -ExpectedPath $brokerPath `
        -ExpectedArguments @($brokerPath) `
        -Application $applications.broker `
        -RuntimePolicy $Artifacts.Release.Document.runtimePolicy `
        -ExpectedOwnerSid "S-1-5-18" `
        -ExpectedElevated $true `
        -RequireTrustedSigner `
        -RequireProtectedRuntimeAcl
    $route = Get-ComoteVmwareNatRouteEvidence -Environment $Environment
    $samples = @()
    $firstTuple = $null
    $udpObserved = $false
    for ($index = 0; $index -lt 6; $index++) {
        if ((Get-ComoteSingletonProcessId `
                -Name "ComoteClient.exe" `
                -ExpectedPath $clientPath) -ne $clientPid -or
            (Get-ComoteSingletonProcessId `
                -Name "ComoteManager.exe" `
                -ExpectedPath $managerPath) -ne $managerPid) {
            throw "A Hub UI process identity changed during sampling."
        }
        $currentService = @(Get-ComoteBrokerService)
        if ($currentService.Count -ne 1 -or
            [int]$currentService[0].ProcessId -ne $brokerPid -or
            [string]$currentService[0].State -cne "Running") {
            throw "The Broker identity changed during Hub sampling."
        }
        $tuple = Get-ComoteReciprocalHubTuple `
            -ClientProcessId $clientPid `
            -ManagerProcessId $managerPid `
            -Port $HubPort `
            -RouteEvidence $route
        $tupleJson = $tuple | ConvertTo-Json -Depth 5 -Compress
        if ($null -eq $firstTuple) {
            $firstTuple = $tupleJson
        }
        elseif ($tupleJson -cne $firstTuple) {
            throw "The reciprocal Hub TCP tuple changed during sampling."
        }
        $udp = @(Get-NetUDPEndpoint -ErrorAction Stop | Where-Object {
            [int]$_.OwningProcess -in @($clientPid, $managerPid) -and
            ([int]$_.LocalPort -eq $HubPort -or
                [int]$_.RemotePort -eq $HubPort)
        })
        if ($udp.Count -gt 0) {
            $udpObserved = $true
        }
        $samples += [PSCustomObject][ordered]@{
            offsetSeconds = $index * 5
            sampledUtc = [DateTime]::UtcNow.ToString("o")
            tuple = $tuple
            udpObserved = ($udp.Count -gt 0)
        }
        if ($index -lt 5) {
            Start-Sleep -Seconds 5
        }
    }
    Stop-ComoteExactProcess `
        -ProcessId $clientPid `
        -ExpectedPath $clientPath
    Stop-ComoteExactProcess `
        -ProcessId $managerPid `
        -ExpectedPath $managerPath
    return [PSCustomObject][ordered]@{
        confirmedUtc = [DateTime]::UtcNow.ToString("o")
        confirmationPhrase = $script:HubAttestationPhrase
        client = $clientEvidence
        manager = $managerEvidence
        broker = $brokerEvidence
        route = $route
        samples = $samples
        udpObserved = $udpObserved
        uiProcessesStopped = $true
    }
}

function Get-ComoteVerifierRegistryState {
    $path =
        "HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management"
    $value = Get-ItemProperty -LiteralPath $path -ErrorAction Stop
    $driversProperty = $value.PSObject.Properties["VerifyDrivers"]
    $levelProperty = $value.PSObject.Properties["VerifyDriverLevel"]
    return [PSCustomObject]@{
        Drivers = if ($null -eq $driversProperty) {
            ""
        }
        else {
            [string]$driversProperty.Value
        }
        Level = if ($null -eq $levelProperty) {
            [uint32]0
        }
        else {
            [uint32]$levelProperty.Value
        }
    }
}

function Enable-ComoteOneBootVerifier {
    $initial = Get-ComoteVerifierRegistryState
    if (-not [string]::IsNullOrWhiteSpace($initial.Drivers) -or
        $initial.Level -ne 0) {
        throw "Driver Verifier is not initially clean."
    }
    $verifier = Join-Path $env:SystemRoot "System32\verifier.exe"
    $standard = Invoke-ComoteNativeProcess `
        -FilePath $verifier `
        -Arguments @(
            "/standard",
            "/driver",
            "ComoteVirtualHidPhase2.sys"
        )
    if ($standard.ExitCode -notin @(0, 2)) {
        throw "Driver Verifier rejected the exact Phase 2 target."
    }
    $oneBoot = Invoke-ComoteNativeProcess `
        -FilePath $verifier `
        -Arguments @("/bootmode", "oneboot")
    if ($oneBoot.ExitCode -notin @(0, 2)) {
        [void](Invoke-ComoteNativeProcess `
            -FilePath $verifier `
            -Arguments @("/reset"))
        throw "Driver Verifier rejected oneboot mode."
    }
    $configured = Get-ComoteVerifierRegistryState
    if ([string]$configured.Drivers -cne "ComoteVirtualHidPhase2.sys" -or
        $configured.Level -eq 0) {
        [void](Invoke-ComoteNativeProcess `
            -FilePath $verifier `
            -Arguments @("/reset"))
        throw "Driver Verifier was not limited to the exact Phase 2 driver."
    }
    return [PSCustomObject][ordered]@{
        standardExitCode = [int]$standard.ExitCode
        oneBootExitCode = [int]$oneBoot.ExitCode
        verifyDrivers = [string]$configured.Drivers
        verifyDriverLevel = [uint32]$configured.Level
    }
}

function Repair-ComoteVerifierConfigurationForRetry {
    $pending = Get-ComoteVerifierRegistryState
    if ([string]::IsNullOrWhiteSpace([string]$pending.Drivers) -and
        [uint32]$pending.Level -eq 0) {
        return
    }
    if ([string]$pending.Drivers -cne "ComoteVirtualHidPhase2.sys" -or
        [uint32]$pending.Level -eq 0) {
        throw "Unexpected Driver Verifier settings block safe retry."
    }
    $verifier = Join-Path $env:SystemRoot "System32\verifier.exe"
    $settings = Invoke-ComoteNativeProcess `
        -FilePath $verifier `
        -Arguments @("/querysettings")
    if ($settings.ExitCode -ne 0 -or
        $settings.Output.IndexOf(
            "ComoteVirtualHidPhase2.sys",
            [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Pending Verifier settings are not the exact Phase 2 target."
    }
    [void](Reset-ComoteVerifier)
}

function Assert-ComoteOneBootVerifierActive {
    $verifier = Join-Path $env:SystemRoot "System32\verifier.exe"
    $active = Invoke-ComoteNativeProcess `
        -FilePath $verifier `
        -Arguments @("/query")
    if ($active.ExitCode -ne 0 -or
        $active.Output.IndexOf(
            "ComoteVirtualHidPhase2.sys",
            [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Active Driver Verifier does not report the Phase 2 driver."
    }
    $settings = Invoke-ComoteNativeProcess `
        -FilePath $verifier `
        -Arguments @("/querysettings")
    if ($settings.ExitCode -ne 0 -or
        $settings.Output.IndexOf(
            "ComoteVirtualHidPhase2.sys",
            [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Oneboot did not clear the next-boot Phase 2 target."
    }
    $nextBoot = Get-ComoteVerifierRegistryState
    if (-not [string]::IsNullOrWhiteSpace($nextBoot.Drivers) -or
        $nextBoot.Level -ne 0) {
        throw "Oneboot Driver Verifier next-boot state is not clean."
    }
    return [PSCustomObject][ordered]@{
        activeCurrentTarget = "ComoteVirtualHidPhase2.sys"
        nextBootDrivers = ""
        nextBootLevel = 0
        activeQuerySha256 = [BitConverter]::ToString(
            [Security.Cryptography.SHA256]::Create().ComputeHash(
                [Text.Encoding]::UTF8.GetBytes([string]$active.Output)
            )
        ).Replace("-", "")
        settingsQuerySha256 = [BitConverter]::ToString(
            [Security.Cryptography.SHA256]::Create().ComputeHash(
                [Text.Encoding]::UTF8.GetBytes([string]$settings.Output)
            )
        ).Replace("-", "")
    }
}

function Reset-ComoteVerifier {
    $verifier = Join-Path $env:SystemRoot "System32\verifier.exe"
    $reset = Invoke-ComoteNativeProcess `
        -FilePath $verifier `
        -Arguments @("/reset")
    if ($reset.ExitCode -notin @(0, 2)) {
        throw "Driver Verifier reset failed."
    }
    $clean = Get-ComoteVerifierRegistryState
    if (-not [string]::IsNullOrWhiteSpace($clean.Drivers) -or
        $clean.Level -ne 0) {
        throw "Driver Verifier reset did not leave next-boot state clean."
    }
    return [int]$reset.ExitCode
}

function Get-ComoteNativeStatus {
    param([Parameter(Mandatory)]$Artifacts)

    return Invoke-ComoteNativeDriverInstaller `
        -Command status `
        -InstallerPath (Join-Path `
            $Artifacts.PackageRoot `
            "Driver\ComoteDriverInstaller.exe") `
        -ManifestPath (Join-Path `
            $Artifacts.PackageRoot `
            "Driver\package-manifest.txt")
}

function Invoke-ComoteSigningCleanup {
    $phase2Root = Join-Path `
        ([string]$State.arguments.isolatedWorkRoot) `
        "Driver\ComoteVirtualHidPhase2"
    $compatibilityReceipt = Join-Path `
        $phase2Root `
        "artifacts\phase2-runtime-state\enumeration-installation.json"
    $compatibilityRemoved = $false
    if ([IO.File]::Exists($compatibilityReceipt)) {
        [void](Assert-ComoteOrdinaryLocalPath `
            -LiteralPath $compatibilityReceipt `
            -Directory $false `
            -Description "Phase 2 compatibility receipt")
        $compatibility = Read-ComoteJson `
            -LiteralPath $compatibilityReceipt `
            -Description "Phase 2 compatibility receipt"
        Assert-ComoteExactProperties `
            -InputObject $compatibility `
            -Expected @(
                "schemaVersion", "status", "recoverySnapshotName",
                "signingSnapshotName", "manufacturer", "model",
                "osBuildNumber", "serviceName", "publishedInfName",
                "rootDeviceInstanceId", "keyboardInstanceIds",
                "mouseInstanceIds", "probeLaunched",
                "inputReportsSubmitted", "source"
            ) `
            -Description "Phase 2 compatibility receipt"
        if ([int]$compatibility.schemaVersion -ne 1 -or
            [string]$compatibility.status -cne "installed-enumerated" -or
            [string]$compatibility.recoverySnapshotName -cne $SnapshotName -or
            [string]$compatibility.signingSnapshotName -cne
                [string]$State.signingSnapshotName -or
            [string]$compatibility.serviceName -cne
                "ComoteVirtualHidPhase2" -or
            @($compatibility.keyboardInstanceIds).Count -ne 1 -or
            @($compatibility.mouseInstanceIds).Count -ne 2 -or
            [bool]$compatibility.probeLaunched -ne $false -or
            [int]$compatibility.inputReportsSubmitted -ne 0 -or
            [string]$compatibility.source -cne
                "native-release-installer-compatibility-evidence") {
            throw "The Phase 2 compatibility receipt is not exact."
        }
        [IO.File]::Delete($compatibilityReceipt)
        $compatibilityRemoved = $true
    }
    $signingReceipt = Join-Path `
        $phase2Root `
        "artifacts\phase2-runtime-state\test-signing-preparation.json"
    $restoreExecuted = $false
    $restoreExitCode = $null
    if (Test-Path -LiteralPath $signingReceipt -PathType Leaf) {
        $signingDocument = Read-ComoteJson `
            -LiteralPath $signingReceipt `
            -Description "Phase 2 signing preparation receipt"
        if ([string]$signingDocument.snapshotName -cne
            [string]$State.signingSnapshotName) {
            throw "The signing receipt differs from protected snapshot state."
        }
        & (Join-Path `
            $phase2Root `
            "Runtime\Restore-Phase2TestSigningState.ps1") `
            -AcknowledgeDisposableVm `
            -SnapshotName $SnapshotName `
            -RequiredBuildNumber "19045"
        $restoreExitCode = if ($null -eq $LASTEXITCODE) { 0 } else {
            [int]$LASTEXITCODE
        }
        if ($restoreExitCode -ne 0) {
            throw "Phase 2 signing-state cleanup returned a nonzero exit code."
        }
        $restoreExecuted = $true
    }
    & (Join-Path `
        $phase2Root `
        "Runtime\Test-Phase2CleanSigningState.ps1") `
        -AcknowledgeDisposableVm `
        -RequiredBuildNumber "19045"
    if ($LASTEXITCODE -notin @($null, 0)) {
        throw "Phase 2 clean signing-state audit returned a nonzero exit code."
    }
    return [PSCustomObject][ordered]@{
        compatibilityReceiptRemoved = $compatibilityRemoved
        restoreExecuted = $restoreExecuted
        restoreExitCode = $restoreExitCode
        cleanSigningAuditPassed = $true
        signingSnapshotName = [string]$State.signingSnapshotName
    }
}

function Assert-ComoteFullCleanState {
    param(
        [Parameter(Mandatory)]$Artifacts,
        [switch]$RequireRetainedBrokerLogRoot
    )

    $native = Get-ComoteNativeStatus -Artifacts $Artifacts
    $paths = Get-ComotePreviewPaths
    $groupState = Get-ComoteLocalGroup
    $thumbprint = [string]$Artifacts.Release.Document.driver.certificateThumbprint
    $rootCertificates = @(Get-ComoteCertificateFromStore `
        -StoreName Root `
        -Thumbprint $thumbprint)
    $publisherCertificates = @(Get-ComoteCertificateFromStore `
        -StoreName TrustedPublisher `
        -Thumbprint $thumbprint)
    foreach ($certificate in @($rootCertificates + $publisherCertificates)) {
        $certificate.Dispose()
    }
    $verifier = Get-ComoteVerifierRegistryState
    $brokerLogRetained = Test-Path `
        -LiteralPath $paths.BrokerLogRoot `
        -PathType Container
    if ($RequireRetainedBrokerLogRoot.IsPresent) {
        if (-not $brokerLogRetained) {
            throw "The receipt-owned cleanup did not retain Broker logs."
        }
        [void](Assert-ComoteOrdinaryLocalPath `
            -LiteralPath $paths.BrokerLogRoot `
            -Directory $true `
            -Description "Retained protected Broker log root")
        Assert-ComoteNoUntrustedWriteAcl -LiteralPath $paths.BrokerLogRoot
    }
    if ($native.ExitCode -ne 20 -or
        [string]$native.State -cne "not-installed" -or
        @(Get-ComoteBrokerService).Count -ne 0 -or
        $null -ne $groupState.Group -or
        $rootCertificates.Count -ne 0 -or
        $publisherCertificates.Count -ne 0 -or
        (Test-Path -LiteralPath $paths.ReceiptPath) -or
        (Test-Path -LiteralPath $paths.InstallRoot) -or
        -not [string]::IsNullOrWhiteSpace($verifier.Drivers) -or
        $verifier.Level -ne 0) {
        throw "The full preview machine state is not clean."
    }
    return [PSCustomObject][ordered]@{
        nativeExitCode = [int]$native.ExitCode
        nativeState = [string]$native.State
        nativeResultLine = [string]$native.ResultLine
        brokerServiceAbsent = $true
        controllerGroupAbsent = $true
        certificateAbsentFromRoot = $true
        certificateAbsentFromTrustedPublisher = $true
        receiptAbsent = $true
        installRootAbsent = $true
        verifierNextBootClean = $true
        brokerLogRootRetained = [bool]$brokerLogRetained
        nativeOsLogsRetained = $true
        signingCleanPassed = $true
        testSigningChangedByWorkflow = $false
        osLogsRemoved = $false
    }
}

function Get-ComoteTreeInventory {
    param([Parameter(Mandatory)][string]$Root)

    $rootIdentity = Assert-ComoteOrdinaryLocalPath `
        -LiteralPath $Root `
        -Directory $true `
        -Description "Exact role tree"
    $records = @()
    $ordinal = New-Object `
        'Collections.Generic.HashSet[string]' `
        ([StringComparer]::Ordinal)
    $ignoreCase = New-Object `
        'Collections.Generic.HashSet[string]' `
        ([StringComparer]::OrdinalIgnoreCase)
    foreach ($item in @(Get-ChildItem `
        -LiteralPath $rootIdentity.FullPath `
        -Force `
        -Recurse `
        -ErrorAction Stop)) {
        $relative = Get-ComoteRelativePath `
            -Root $rootIdentity.FullPath `
            -Child $item.FullName
        Assert-ComoteSafeRelativePath -RelativePath $relative
        if (-not $ordinal.Add($relative) -or
            -not $ignoreCase.Add($relative)) {
            throw "The role tree contains a case-colliding path."
        }
        $identity = Assert-ComoteOrdinaryLocalPath `
            -LiteralPath $item.FullName `
            -Directory:([bool]$item.PSIsContainer) `
            -Description "Exact role tree item"
        $records += [PSCustomObject][ordered]@{
            kind = if ($item.PSIsContainer) { "directory" } else { "file" }
            path = $relative
            length = if ($item.PSIsContainer) {
                [int64]0
            }
            else {
                [int64]$identity.Identity.Length
            }
            sha256 = if ($item.PSIsContainer) {
                $null
            }
            else {
                Get-ComoteSha256 -LiteralPath $item.FullName
            }
        }
    }
    return @($records | Sort-Object path, kind)
}

function Expand-ComoteRoleZipExact {
    param(
        [Parameter(Mandatory)][string]$ZipPath,
        [Parameter(Mandatory)][string]$SourceRoleRoot,
        [Parameter(Mandatory)][string]$DestinationRoot
    )

    $expected = @(Get-ComoteTreeInventory -Root $SourceRoleRoot)
    if (Test-Path -LiteralPath $DestinationRoot) {
        $existing = @(Get-ComoteTreeInventory -Root $DestinationRoot)
        if (($existing | ConvertTo-Json -Depth 8 -Compress) -cne
            ($expected | ConvertTo-Json -Depth 8 -Compress)) {
            throw "An existing role extraction is not exact."
        }
        return
    }
    $parent = [IO.Path]::GetDirectoryName($DestinationRoot)
    $leaf = [IO.Path]::GetFileName($DestinationRoot)
    [void](Assert-ComoteOrdinaryLocalPath `
        -LiteralPath $parent `
        -Directory $true `
        -Description "Role extraction parent")
    $partials = @(Get-ChildItem `
        -LiteralPath $parent `
        -Force `
        -Filter "$leaf.partial-*" `
        -ErrorAction Stop)
    if ($partials.Count -ne 0) {
        throw ("A retained role-extraction partial blocks retry: " +
            ($partials.FullName -join ", "))
    }
    $extractRoot = Join-Path `
        $parent `
        ("$leaf.partial-" + [Guid]::NewGuid().ToString("N"))
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Directory]::CreateDirectory($extractRoot) | Out-Null
    Set-ComoteProtectedDirectoryAcl -LiteralPath $extractRoot
    $published = $false
    try {
        $archive = [IO.Compression.ZipFile]::OpenRead($ZipPath)
        try {
            $ordinal = New-Object `
                'Collections.Generic.HashSet[string]' `
                ([StringComparer]::Ordinal)
            $ignoreCase = New-Object `
                'Collections.Generic.HashSet[string]' `
                ([StringComparer]::OrdinalIgnoreCase)
            foreach ($entry in $archive.Entries) {
                $relative =
                    [string]$entry.FullName.Replace('\', '/').TrimEnd('/')
                if ([string]::IsNullOrWhiteSpace($relative)) {
                    throw "A role ZIP contains an empty entry name."
                }
                Assert-ComoteSafeRelativePath -RelativePath $relative
                if (-not $ordinal.Add($relative) -or
                    -not $ignoreCase.Add($relative)) {
                    throw "A role ZIP contains duplicate/case-colliding paths."
                }
                $target = Join-Path $extractRoot $relative.Replace('/', '\')
                if ([string]$entry.FullName.EndsWith(
                        "/", [StringComparison]::Ordinal) -or
                    [string]::IsNullOrEmpty([string]$entry.Name)) {
                    [IO.Directory]::CreateDirectory($target) | Out-Null
                    continue
                }
                [IO.Directory]::CreateDirectory(
                    [IO.Path]::GetDirectoryName($target)
                ) | Out-Null
                $input = $entry.Open()
                try {
                    $output = New-Object IO.FileStream(
                        $target,
                        [IO.FileMode]::CreateNew,
                        [IO.FileAccess]::Write,
                        [IO.FileShare]::None
                    )
                    try {
                        $input.CopyTo($output)
                    }
                    finally {
                        $output.Dispose()
                    }
                }
                finally {
                    $input.Dispose()
                }
            }
        }
        finally {
            $archive.Dispose()
        }
        Set-ComoteProtectedDirectoryAcl -LiteralPath $extractRoot
        $actual = @(Get-ComoteTreeInventory -Root $extractRoot)
        if (($actual | ConvertTo-Json -Depth 8 -Compress) -cne
            ($expected | ConvertTo-Json -Depth 8 -Compress)) {
            throw "Extracted role ZIP differs from authenticated directory."
        }
        [IO.Directory]::Move($extractRoot, $DestinationRoot)
        $published = $true
        $publishedInventory = @(Get-ComoteTreeInventory -Root $DestinationRoot)
        if (($publishedInventory | ConvertTo-Json -Depth 8 -Compress) -cne
            ($expected | ConvertTo-Json -Depth 8 -Compress)) {
            throw "Published role extraction changed after atomic move."
        }
    }
    finally {
        if (-not $published -and
            (Test-Path -LiteralPath $extractRoot -PathType Container)) {
            Write-Warning "Incomplete role extraction retained: $extractRoot"
        }
    }
}

function Get-ComoteCandidateBinding {
    param(
        [Parameter(Mandatory)]$State,
        [Parameter(Mandatory)][string]$ExporterPath,
        [switch]$AllowUnboundState
    )

    $candidateRoot = if ($AllowUnboundState.IsPresent) {
        Join-Path `
            ([string](Get-ComotePromotionPaths).Root) `
            ("Candidate-{0}" -f $ReleaseId)
    }
    else {
        [string]$State.candidateRoot
    }
    $indexPath = Join-Path $candidateRoot "CANDIDATE-ROLE-INDEX.json"
    $indexHash = Get-ComoteSha256 -LiteralPath $indexPath
    & $ExporterPath `
        -VerifyCandidate `
        -CandidateDirectory $candidateRoot `
        -ExpectedCandidateIndexSha256 $indexHash `
        -ExpectedCandidateSourceManifestSha256 `
            ([string]$State.releaseManifestSha256) `
        -ExpectedCandidateProvisionalReportSha256 `
            ([string]$State.provisionalReportSha256)
    if ($LASTEXITCODE -notin @($null, 0)) {
        throw "Candidate role verification returned a nonzero exit code."
    }
    [void](Assert-ComoteAsciiHashSidecar `
        -LiteralPath "$indexPath.sha256" `
        -TargetPath $indexPath)
    $index = Read-ComoteJson `
        -LiteralPath $indexPath `
        -Description "Candidate role index"
    $roleHashes = [PSCustomObject][ordered]@{
        managerZipSha256 = [string]$index.manager.zipSha256
        managerManifestSha256 = [string]$index.manager.manifestSha256
        clientZipSha256 = [string]$index.clientVirtualHid.zipSha256
        clientManifestSha256 = [string]$index.clientVirtualHid.manifestSha256
    }
    return [PSCustomObject]@{
        Root = $candidateRoot
        IndexPath = $indexPath
        IndexSha256 = $indexHash
        Index = $index
        RoleHashes = $roleHashes
    }
}

function Initialize-ComoteCandidateRoleTests {
    param(
        [Parameter(Mandatory)]$Candidate,
        [Parameter(Mandatory)]$Paths
    )

    $managerSource = Join-Path `
        $Candidate.Root `
        ([string]$Candidate.Index.manager.directory)
    $clientSource = Join-Path `
        $Candidate.Root `
        ([string]$Candidate.Index.clientVirtualHid.directory)
    $managerRoot = Join-Path $Paths.RoleTests "Manager"
    $clientRoot = Join-Path $Paths.RoleTests "Client"
    Expand-ComoteRoleZipExact `
        -ZipPath (Join-Path `
            $Candidate.Root `
            ([string]$Candidate.Index.manager.zip)) `
        -SourceRoleRoot $managerSource `
        -DestinationRoot $managerRoot
    Expand-ComoteRoleZipExact `
        -ZipPath (Join-Path `
            $Candidate.Root `
            ([string]$Candidate.Index.clientVirtualHid.zip)) `
        -SourceRoleRoot $clientSource `
        -DestinationRoot $clientRoot
    return [PSCustomObject]@{
        ManagerRoot = $managerRoot
        ClientRoot = $clientRoot
    }
}

function Write-ComoteImmutablePromotionReport {
    param(
        [Parameter(Mandatory)][string]$LiteralPath,
        [Parameter(Mandatory)]$Report
    )

    $sidecar = "$LiteralPath.sha256"
    $reportExists = Test-Path -LiteralPath $LiteralPath -PathType Leaf
    $sidecarExists = Test-Path -LiteralPath $sidecar -PathType Leaf
    if (-not $reportExists -and $sidecarExists) {
        throw "An orphaned promotion-report checksum blocks retry."
    }
    if ($reportExists) {
        $existing = Read-ComoteJson `
            -LiteralPath $LiteralPath `
            -Description "Existing immutable promotion report"
        $propertyNames = @($Report.PSObject.Properties.Name)
        Assert-ComoteExactProperties `
            -InputObject $existing `
            -Expected $propertyNames `
            -Description "Existing immutable promotion report"
        if ([string]$existing.completedUtc -cnotmatch
            '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}Z$') {
            throw "An existing promotion report has an invalid completion time."
        }
        foreach ($name in @($propertyNames | Where-Object {
            $_ -cne "completedUtc"
        })) {
            if (($existing.$name | ConvertTo-Json -Depth 20 -Compress) -cne
                ($Report.$name | ConvertTo-Json -Depth 20 -Compress)) {
                throw "An existing promotion report differs at $name."
            }
        }
        $hash = Get-ComoteSha256 -LiteralPath $LiteralPath
        if ($sidecarExists) {
            if ((Assert-ComoteAsciiHashSidecar `
                    -LiteralPath $sidecar `
                    -TargetPath $LiteralPath) -cne $hash) {
                throw "An existing promotion-report checksum changed."
            }
        }
        else {
            $recoveredHash = Write-ComoteHashSidecar `
                -LiteralPath $LiteralPath
            if ($recoveredHash -cne $hash) {
                throw "Promotion-report checksum recovery changed the report."
            }
        }
        return [PSCustomObject]@{ Path = $LiteralPath; Sha256 = $hash }
    }
    Write-ComoteJsonAtomically `
        -LiteralPath $LiteralPath `
        -InputObject $Report `
        -Depth 20
    $hash = Write-ComoteHashSidecar -LiteralPath $LiteralPath
    return [PSCustomObject]@{ Path = $LiteralPath; Sha256 = $hash }
}

function Write-ComoteProvisionalReport {
    param(
        [Parameter(Mandatory)]$State,
        [Parameter(Mandatory)]$Paths
    )

    $path = Join-Path $Paths.Evidence "unified-provisional-report.json"
    $report = [PSCustomObject][ordered]@{
        schemaVersion = 1
        status = "unified-validation-passed-role-tests-pending"
        completedUtc = [DateTime]::UtcNow.ToString("o")
        releaseId = $ReleaseId
        releaseManifestSha256 = [string]$State.releaseManifestSha256
        sourceInventorySha256 =
            [string]$State.arguments.sourceInventorySha256
        toolInventorySha256 = [string]$State.toolInventorySha256
        snapshotName = $SnapshotName
        runtimePolicy = $State.runtimePolicy
        preInstallMediaGate = $State.preInstallMediaGate
        hubSmoke = $State.hubSmoke
        powerEvidence = $State.bootEvidence
        e2eRuns = $State.e2eRuns
        verifierEvidence = $State.verifierEvidence
        unifiedCleanup = $State.unifiedCleanup
        signingCleanPassed = $true
        testSigningChangedByWorkflow = $false
        osLogsRemoved = $false
    }
    if ($report.PSObject.Properties.Name -ccontains
        "candidateRoleIndexSha256") {
        throw "A provisional report must not contain a candidate hash."
    }
    return Write-ComoteImmutablePromotionReport `
        -LiteralPath $path `
        -Report $report
}

function Write-ComoteFinalReport {
    param(
        [Parameter(Mandatory)]$State,
        [Parameter(Mandatory)]$Paths
    )

    $path = Join-Path $Paths.Evidence "final-promotion-report.json"
    $report = [PSCustomObject][ordered]@{
        schemaVersion = 1
        status = "role-validation-passed"
        completedUtc = [DateTime]::UtcNow.ToString("o")
        releaseId = $ReleaseId
        releaseManifestSha256 = [string]$State.releaseManifestSha256
        sourceInventorySha256 =
            [string]$State.arguments.sourceInventorySha256
        toolInventorySha256 = [string]$State.toolInventorySha256
        snapshotName = $SnapshotName
        runtimePolicy = $State.runtimePolicy
        preInstallMediaGate = $State.preInstallMediaGate
        hubSmoke = $State.hubSmoke
        powerEvidence = $State.bootEvidence
        e2eRuns = $State.e2eRuns
        verifierEvidence = $State.verifierEvidence
        unifiedCleanup = $State.unifiedCleanup
        provisionalReportSha256 = [string]$State.provisionalReportSha256
        candidateRoleIndexSha256 = [string]$State.candidateIndexSha256
        candidateRoles = $State.candidateRoleHashes
        managerRoleTest = $State.managerRoleTest
        clientRoleTest = $State.clientRoleTest
        clientCleanup = $State.clientCleanup
        signingCleanPassed = $true
        testSigningChangedByWorkflow = $false
        osLogsRemoved = $false
    }
    return Write-ComoteImmutablePromotionReport `
        -LiteralPath $path `
        -Report $report
}

function Copy-ComoteTreeExact {
    param(
        [Parameter(Mandatory)][string]$SourceRoot,
        [Parameter(Mandatory)][string]$DestinationRoot
    )

    $inventory = @(Get-ComoteTreeInventory -Root $SourceRoot)
    [IO.Directory]::CreateDirectory($DestinationRoot) | Out-Null
    foreach ($record in @($inventory | Where-Object kind -ceq "directory")) {
        [IO.Directory]::CreateDirectory((Join-Path `
            $DestinationRoot `
            ([string]$record.path).Replace('/', '\'))) | Out-Null
    }
    foreach ($record in @($inventory | Where-Object kind -ceq "file")) {
        $destination = Join-Path `
            $DestinationRoot `
            ([string]$record.path).Replace('/', '\')
        [IO.Directory]::CreateDirectory(
            [IO.Path]::GetDirectoryName($destination)
        ) | Out-Null
        [IO.File]::Copy(
            (Join-Path `
                $SourceRoot `
                ([string]$record.path).Replace('/', '\')),
            $destination,
            $false
        )
    }
    $copied = @(Get-ComoteTreeInventory -Root $DestinationRoot)
    if (($copied | ConvertTo-Json -Depth 8 -Compress) -cne
        ($inventory | ConvertTo-Json -Depth 8 -Compress)) {
        throw "An exact tree publication copy failed verification."
    }
}

function Assert-ComoteFinalPublication {
    param(
        [Parameter(Mandatory)]$State,
        [Parameter(Mandatory)][string]$Root
    )

    $evidence = Join-Path $Root "Evidence"
    $roles = Join-Path $Root "Roles"
    $candidateDestination = Join-Path `
        $roles `
        ([IO.Path]::GetFileName([string]$State.candidateRoot))
    foreach ($directoryBinding in @(
        @($Root, "Final publication wrapper"),
        @($evidence, "Final publication Evidence"),
        @($roles, "Final publication Roles"),
        @($candidateDestination, "Final publication candidate")
    )) {
        [void](Assert-ComoteOrdinaryLocalPath `
            -LiteralPath ([string]$directoryBinding[0]) `
            -Directory $true `
            -Description ([string]$directoryBinding[1]))
    }
    $bindings = @(
        @(
            (Join-Path $evidence "unified-provisional-report.json"),
            [string]$State.provisionalReportSha256
        ),
        @(
            (Join-Path $evidence "final-promotion-report.json"),
            [string]$State.finalReportSha256
        )
    )
    foreach ($binding in $bindings) {
        if ((Get-ComoteSha256 -LiteralPath ([string]$binding[0])) -cne
                [string]$binding[1] -or
            (Assert-ComoteAsciiHashSidecar `
                -LiteralPath "$($binding[0]).sha256" `
                -TargetPath ([string]$binding[0])) -cne
                [string]$binding[1]) {
            throw "Final publication evidence differs from protected state."
        }
    }
    $expectedEvidenceNames = @(
        "unified-provisional-report.json",
        "unified-provisional-report.json.sha256",
        "final-promotion-report.json",
        "final-promotion-report.json.sha256"
    )
    $evidenceItems = @(Get-ChildItem `
        -LiteralPath $evidence `
        -Force `
        -ErrorAction Stop)
    if ($evidenceItems.Count -ne 4 -or
        @($evidenceItems | Where-Object {
            $_.PSIsContainer -or
            $expectedEvidenceNames -cnotcontains [string]$_.Name
        }).Count -ne 0) {
        throw "Final publication Evidence inventory is not exact."
    }
    foreach ($item in $evidenceItems) {
        [void](Assert-ComoteOrdinaryLocalPath `
            -LiteralPath $item.FullName `
            -Directory $false `
            -Description "Final publication evidence file")
    }
    $roleItems = @(Get-ChildItem `
        -LiteralPath $roles `
        -Force `
        -ErrorAction Stop)
    if ($roleItems.Count -ne 1 -or
        -not $roleItems[0].PSIsContainer -or
        [string]$roleItems[0].Name -cne
            [IO.Path]::GetFileName([string]$State.candidateRoot)) {
        throw "Final publication Roles inventory is not exact."
    }
    $sourceCandidate = @(Get-ComoteTreeInventory `
        -Root ([string]$State.candidateRoot))
    $publishedCandidate = @(Get-ComoteTreeInventory `
        -Root $candidateDestination)
    if (($sourceCandidate | ConvertTo-Json -Depth 8 -Compress) -cne
        ($publishedCandidate | ConvertTo-Json -Depth 8 -Compress)) {
        throw "Published candidate bytes differ from the protected candidate."
    }
    $top = @(Get-ChildItem -LiteralPath $Root -Force -ErrorAction Stop)
    if ($top.Count -ne 2 -or
        @($top | Where-Object {
            [string]$_.Name -notin @("Evidence", "Roles") -or
            -not $_.PSIsContainer
        }).Count -ne 0) {
        throw "Final publication wrapper inventory is not exact."
    }
}

function Publish-ComoteFinalOutput {
    param(
        [Parameter(Mandatory)]$State,
        [Parameter(Mandatory)][string]$OutputRoot
    )

    if (Test-Path -LiteralPath $OutputRoot -PathType Container) {
        Assert-ComoteFinalPublication -State $State -Root $OutputRoot
        return
    }
    if (Test-Path -LiteralPath $OutputRoot) {
        throw "Final publication output exists but is not a directory."
    }
    $parent = [IO.Path]::GetDirectoryName($OutputRoot)
    $leaf = [IO.Path]::GetFileName($OutputRoot)
    $partials = @(Get-ChildItem `
        -LiteralPath $parent `
        -Directory `
        -Force `
        -Filter "$leaf.partial-*" `
        -ErrorAction Stop)
    if ($partials.Count -ne 0) {
        throw "A retained final-publication partial blocks retry: $($partials.FullName -join ', ')"
    }
    $partial = Join-Path `
        $parent `
        ("$leaf.partial-" + [Guid]::NewGuid().ToString("N"))
    $published = $false
    try {
        [IO.Directory]::CreateDirectory((Join-Path $partial "Evidence")) |
            Out-Null
        [IO.Directory]::CreateDirectory((Join-Path $partial "Roles")) |
            Out-Null
        foreach ($binding in @(
            @([string]$State.provisionalReportPath,
                "unified-provisional-report.json"),
            @([string]$State.finalReportPath,
                "final-promotion-report.json")
        )) {
            $target = Join-Path (Join-Path $partial "Evidence") $binding[1]
            [IO.File]::Copy([string]$binding[0], $target, $false)
            [IO.File]::Copy("$($binding[0]).sha256", "$target.sha256", $false)
        }
        Copy-ComoteTreeExact `
            -SourceRoot ([string]$State.candidateRoot) `
            -DestinationRoot (Join-Path `
                (Join-Path $partial "Roles") `
                ([IO.Path]::GetFileName([string]$State.candidateRoot))
            )
        Assert-ComoteFinalPublication -State $State -Root $partial
        [IO.Directory]::Move($partial, $OutputRoot)
        $published = $true
        Assert-ComoteFinalPublication -State $State -Root $OutputRoot
    }
    finally {
        if (-not $published -and
            (Test-Path -LiteralPath $partial -PathType Container)) {
            Write-Warning "Incomplete final publication retained: $partial"
        }
    }
}

function Test-ComotePathOverlap {
    param(
        [Parameter(Mandatory)][string]$First,
        [Parameter(Mandatory)][string]$Second
    )

    $firstFull = [IO.Path]::GetFullPath($First).TrimEnd('\')
    $secondFull = [IO.Path]::GetFullPath($Second).TrimEnd('\')
    return $firstFull.Equals(
            $secondFull, [StringComparison]::OrdinalIgnoreCase) -or
        $firstFull.StartsWith(
            $secondFull + "\", [StringComparison]::OrdinalIgnoreCase) -or
        $secondFull.StartsWith(
            $firstFull + "\", [StringComparison]::OrdinalIgnoreCase)
}

function Assert-ComotePromotionPathSeparation {
    param(
        [Parameter(Mandatory)]$Paths,
        [switch]$AllowExistingWorkAndOutput
    )

    $source = (Assert-ComoteOrdinaryLocalPath `
        -LiteralPath $SourceRoot `
        -Directory $true `
        -Description "Promotion source root").FullPath
    $signTool = (Assert-ComoteOrdinaryLocalPath `
        -LiteralPath $SignToolPath `
        -Directory $false `
        -Description "Promotion SignTool").FullPath
    $isolated = [IO.Path]::GetFullPath($IsolatedWorkRoot)
    $external = [IO.Path]::GetFullPath($OutputDirectory)
    foreach ($candidatePath in @($isolated, $external)) {
        $parent = [IO.Path]::GetDirectoryName($candidatePath)
        [void](Assert-ComoteOrdinaryLocalPath `
            -LiteralPath $parent `
            -Directory $true `
            -Description "Promotion output parent")
        if ([string]::IsNullOrWhiteSpace(
                [IO.Path]::GetFileName($candidatePath))) {
            throw "A promotion output must be a named child path."
        }
    }
    foreach ($pair in @(
        @($source, $isolated),
        @($source, $external),
        @($source, $Paths.Root),
        @($isolated, $external),
        @($isolated, $Paths.Root),
        @($external, $Paths.Root)
    )) {
        if (Test-ComotePathOverlap -First $pair[0] -Second $pair[1]) {
            throw "Promotion source, work, protected, and output paths overlap."
        }
    }
    foreach ($mutable in @($isolated, $external, $Paths.Root)) {
        if (Test-ComotePathOverlap -First $signTool -Second $mutable) {
            throw "SignTool must be outside every mutable promotion tree."
        }
    }
    if (-not $AllowExistingWorkAndOutput.IsPresent -and
        ((Test-Path -LiteralPath $isolated) -or
            (Test-Path -LiteralPath $external))) {
        throw "IsolatedWorkRoot and external OutputDirectory must start absent."
    }
}

function Get-ComoteApplicationByRole {
    param(
        [Parameter(Mandatory)]$Document,
        [Parameter(Mandatory)][string]$Role
    )

    $matches = @($Document.cmt1Applications |
        Where-Object { [string]$_.role -ceq $Role })
    if ($matches.Count -ne 1) {
        throw "The manifest has no singleton $Role application."
    }
    return $matches[0]
}

if (-not $AcknowledgeDisposableVm.IsPresent) {
    throw "Promotion requires the disposable-VM acknowledgement."
}
$promotionPaths = Get-ComotePromotionPaths
$promotionMutex = New-Object Threading.Mutex(
    $false,
    "Global\ComoteVirtualHidPreviewPromotionSchema2"
)
$promotionLockAcquired = $false
try {
    try {
        $promotionLockAcquired = $promotionMutex.WaitOne(0)
    }
    catch [Threading.AbandonedMutexException] {
        $promotionLockAcquired = $true
    }
    if (-not $promotionLockAcquired) {
        throw "Another schema-2 preview promotion operation is active."
    }

    $stateExists = Test-Path -LiteralPath $promotionPaths.State -PathType Leaf
    Assert-ComotePromotionPathSeparation `
        -Paths $promotionPaths `
        -AllowExistingWorkAndOutput:$stateExists
    if (-not $stateExists) {
        if (Test-Path -LiteralPath $promotionPaths.Root) {
            throw "Promotion root exists without its exact schema-2 state."
        }
        [void](Initialize-ComotePromotionState -Paths $promotionPaths)
        Write-Host "Protected schema-2 promotion state initialized."
        Write-Host "Rerun the same command to begin pre-install gates."
        return
    }

    [void](Assert-ComoteOrdinaryLocalPath `
        -LiteralPath $promotionPaths.Root `
        -Directory $true `
        -Description "Protected promotion root")
    Assert-ComoteNoUntrustedWriteAcl -LiteralPath $promotionPaths.Root
    Assert-ComoteNoUntrustedWriteAcl -LiteralPath $promotionPaths.State
    $state = Read-ComoteJson `
        -LiteralPath $promotionPaths.State `
        -Description "Protected schema-2 promotion state"
    Assert-ComotePromotionStateSchema -State $state
    Assert-ComoteToolTrees -State $state -CurrentRoot $PSScriptRoot
    . (Join-Path `
        ([string]$state.protectedToolRoot) `
        "VirtualHidPreview.Common.ps1")
    $script:State = $state
    $artifacts = Assert-ComoteBoundArtifactState -State $state
    if ($null -ne $state.provisionalReportPath) {
        if ((Get-ComoteSha256 `
                -LiteralPath ([string]$state.provisionalReportPath)) -cne
                [string]$state.provisionalReportSha256 -or
            (Assert-ComoteAsciiHashSidecar `
                -LiteralPath "$($state.provisionalReportPath).sha256" `
                -TargetPath ([string]$state.provisionalReportPath)) -cne
                [string]$state.provisionalReportSha256) {
            throw "The provisional report changed after state binding."
        }
    }
    if ($null -ne $state.finalReportPath) {
        if ((Get-ComoteSha256 `
                -LiteralPath ([string]$state.finalReportPath)) -cne
                [string]$state.finalReportSha256 -or
            (Assert-ComoteAsciiHashSidecar `
                -LiteralPath "$($state.finalReportPath).sha256" `
                -TargetPath ([string]$state.finalReportPath)) -cne
                [string]$state.finalReportSha256) {
            throw "The final report changed after state binding."
        }
    }
    $exporterPath = Join-Path `
        ([string]$state.protectedToolRoot) `
        "Export-VirtualHidPreviewRolePackages.ps1"
    $candidate = $null
    if ($null -ne $state.candidateRoot) {
        $candidate = Get-ComoteCandidateBinding `
            -State $state `
            -ExporterPath $exporterPath
        if ([string]$candidate.IndexSha256 -cne
                [string]$state.candidateIndexSha256 -or
            [string]$candidate.IndexPath -cne
                [string]$state.candidateIndexPath -or
            ($candidate.RoleHashes | ConvertTo-Json -Compress) -cne
                ($state.candidateRoleHashes | ConvertTo-Json -Compress)) {
            throw "The bound candidate role output changed."
        }
    }
    if ([string]$state.phase -ceq "complete") {
        $expectedPromotedPath = Join-Path `
            ([string]$state.candidateRoot) `
            "PROMOTED-ROLE-INDEX.json"
        if (-not [IO.Path]::GetFullPath(
                [string]$state.promotedIndexPath
            ).Equals(
                [IO.Path]::GetFullPath($expectedPromotedPath),
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "The completed promoted-index path is not canonical."
        }
        [void](Assert-ComoteOrdinaryLocalPath `
            -LiteralPath $expectedPromotedPath `
            -Directory $false `
            -Description "Completed promoted role index")
        $completedPromotedHash = Assert-ComoteAsciiHashSidecar `
            -LiteralPath "$expectedPromotedPath.sha256" `
            -TargetPath $expectedPromotedPath
        if ($completedPromotedHash -cne
            [string]$state.promotedIndexSha256) {
            throw "The completed promoted role index changed."
        }
        & $exporterPath `
            -PromoteCandidate `
            -CandidateDirectory ([string]$state.candidateRoot) `
            -ExpectedCandidateIndexSha256 `
                ([string]$state.candidateIndexSha256) `
            -FinalReportPath ([string]$state.finalReportPath) `
            -ExpectedFinalReportSha256 ([string]$state.finalReportSha256)
        if ($LASTEXITCODE -notin @($null, 0) -or
            (Get-ComoteSha256 -LiteralPath $expectedPromotedPath) -cne
                [string]$state.promotedIndexSha256) {
            throw "Completed promoted-index semantic verification failed."
        }
        Publish-ComoteFinalOutput `
            -State $state `
            -OutputRoot ([string]$state.arguments.outputDirectory)
        Write-Host "Promotion is complete and all evidence is intact." `
            -ForegroundColor Green
        Write-Host "Final report: $($state.finalReportPath)"
        Write-Host "SHA-256: $($state.finalReportSha256)"
        return
    }

    $environment = Assert-ComoteDisposableVmEnvironment `
        -AcknowledgeDisposableVm
    switch ([string]$state.phase) {
        "pre-install-gates" {
            if ($null -eq $artifacts) {
                $protectedOutputExists = Test-Path `
                    -LiteralPath $promotionPaths.Output `
                    -PathType Container
                if (-not $protectedOutputExists) {
                    if (Test-Path `
                        -LiteralPath ([string]$state.arguments.isolatedWorkRoot)) {
                        throw ("An incomplete isolated build without published " +
                            "output is retained for forensic review.")
                    }
                    & (Join-Path `
                        ([string]$state.protectedToolRoot) `
                        "New-VirtualHidPreviewReleaseInVm.ps1") `
                        -AcknowledgeDisposableVm `
                        -SnapshotName $SnapshotName `
                        -ReleaseId $ReleaseId `
                        -SourceRoot ([string]$state.arguments.sourceRoot) `
                        -ExpectedSourceInventorySha256 `
                            ([string]$state.arguments.sourceInventorySha256) `
                        -IsolatedWorkRoot `
                            ([string]$state.arguments.isolatedWorkRoot) `
                        -SignToolPath ([string]$state.arguments.signToolPath) `
                        -OutputDirectory $promotionPaths.Output
                    if ($LASTEXITCODE -notin @($null, 0)) {
                        throw "Fresh release construction returned nonzero."
                    }
                    Set-ComoteProtectedDirectoryAcl `
                        -LiteralPath $promotionPaths.Output
                }
                $artifacts = Get-ComoteAuthenticatedUnifiedOutput `
                    -OutputRoot $promotionPaths.Output
                $state.packageRoot = $artifacts.PackageRoot
                $state.releaseManifestSha256 = $artifacts.ManifestSha256
                $state.regressionReportPath = $artifacts.RegressionPath
                $state.regressionReportSha256 = $artifacts.RegressionSha256
                $state.sbomPath = $artifacts.SbomPath
                $state.sbomSha256 = $artifacts.SbomSha256
                $state.mediaEvidencePath = $artifacts.MediaPath
                $state.mediaEvidenceSha256 = $artifacts.MediaSha256
                $state.runtimePolicy = $artifacts.Release.Document.runtimePolicy
                $state.signingSnapshotName = $artifacts.SigningSnapshotName
            }
            $state.preInstallMediaGate = Invoke-ComoteFinalPreInstallMediaGate `
                -Artifacts $artifacts `
                -Paths $promotionPaths
            Move-ComotePromotionPhase `
                -State $state `
                -ExpectedCurrent "pre-install-gates" `
                -Next "installing" `
                -StatePath $promotionPaths.State
            Write-Host "Pre-install release, SBOM, media, and signer gates passed."
            return
        }
        "installing" {
            $previewPaths = Get-ComotePreviewPaths
            $receipt = $null
            if (Test-Path -LiteralPath $previewPaths.ReceiptPath -PathType Leaf) {
                $receipt = Read-ComoteProtectedReceipt `
                    -ExpectedReleaseManifestSha256 `
                        ([string]$state.releaseManifestSha256) `
                    -ExpectedPackageRole "validation-unified"
                if ([string]$receipt.status -cne "installed") {
                    & (Join-Path `
                        $artifacts.PackageRoot `
                        "Uninstall-ComoteVirtualHidPreview.ps1") `
                        -AcknowledgeDisposableVm `
                        -ExpectedReleaseManifestSha256 `
                            ([string]$state.releaseManifestSha256)
                    if ($LASTEXITCODE -notin @($null, 0)) {
                        throw "Unified receipt-owned recovery returned nonzero."
                    }
                    $receipt = $null
                    [void](Assert-ComoteFullCleanState -Artifacts $artifacts)
                }
            }
            elseif ((Test-Path -LiteralPath $previewPaths.StateRoot) -or
                (Test-Path -LiteralPath $previewPaths.InstallRoot) -or
                @(Get-ComoteBrokerService).Count -ne 0) {
                throw ("Unreceipted preview state blocks safe installation; " +
                    "restore the bound clean VM snapshot.")
            }
            if ($null -eq $receipt) {
                try {
                    [void](Assert-ComoteFullCleanState -Artifacts $artifacts)
                }
                catch {
                    throw ("Fresh installation is not provably clean and has " +
                        "no authenticated removal receipt; restore snapshot " +
                        "'$SnapshotName'. " + $_.Exception.Message)
                }
                & (Join-Path `
                    $artifacts.PackageRoot `
                    "Install-ComoteVirtualHidPreview.ps1") `
                    -AcknowledgeDisposableVm `
                    -ExpectedReleaseManifestSha256 `
                        ([string]$state.releaseManifestSha256) `
                    -ControllerUser $ControllerUser
                    if ($LASTEXITCODE -notin @($null, 0)) {
                        throw "Unified preview installation returned nonzero."
                    }
                $receipt = Read-ComoteProtectedReceipt `
                    -ExpectedReleaseManifestSha256 `
                        ([string]$state.releaseManifestSha256) `
                    -ExpectedPackageRole "validation-unified"
            }
            if ([string]$receipt.status -cne "installed") {
                throw "Unified install receipt is not installed."
            }
            $state.installEvidence = [PSCustomObject][ordered]@{
                receiptPath = $previewPaths.ReceiptPath
                receiptSha256 = Get-ComoteSha256 `
                    -LiteralPath $previewPaths.ReceiptPath
                packageRole = "validation-unified"
                installedUtc = [DateTime]::UtcNow.ToString("o")
            }
            Move-ComotePromotionPhase `
                -State $state `
                -ExpectedCurrent "installing" `
                -Next "await-controller-logon" `
                -StatePath $promotionPaths.State
            Write-Host "Sign out and sign in manually as the controller, then rerun."
            return
        }
        "await-controller-logon" {
            [void](Assert-ComoteControllerToken)
            Move-ComotePromotionPhase `
                -State $state `
                -ExpectedCurrent "await-controller-logon" `
                -Next "await-hub-smoke" `
                -StatePath $promotionPaths.State
            Write-Host "Launch exact Manager and Client protected UI shortcuts."
            Write-Host "Authenticate in the UIs; never place the access key on argv."
            return
        }
        "await-hub-smoke" {
            [void](Assert-ComoteControllerToken)
            $receipt = Read-ComoteProtectedReceipt `
                -ExpectedReleaseManifestSha256 `
                    ([string]$state.releaseManifestSha256) `
                -ExpectedPackageRole "validation-unified"
            $state.hubSmoke = Invoke-ComoteHubSmoke `
                -Artifacts $artifacts `
                -InstallReceipt $receipt `
                -Environment $environment
            Move-ComotePromotionPhase `
                -State $state `
                -ExpectedCurrent "await-hub-smoke" `
                -Next "normal-e2e" `
                -StatePath $promotionPaths.State
            Write-Host "Hub and harmless authenticated input smoke passed."
            return
        }
        "normal-e2e" {
            [void](Assert-ComoteControllerToken)
            $normal = Invoke-ComotePromotionE2E `
                -Label "normal" `
                -Artifacts $artifacts
            Add-ComoteE2ERun -State $state -Evidence $normal
            $state.bootEvidence.normalBootBefore = Get-ComoteBootMarker
            Move-ComotePromotionPhase `
                -State $state `
                -ExpectedCurrent "normal-e2e" `
                -Next "await-normal-reboot" `
                -StatePath $promotionPaths.State
            Write-Host "Perform a normal Windows restart manually, then rerun."
            return
        }
        "await-normal-reboot" {
            [void](Assert-ComoteControllerToken)
            $afterBoot = Get-ComoteBootMarker
            if ($afterBoot -ceq [string]$state.bootEvidence.normalBootBefore) {
                throw "A changed boot marker is required after the normal restart."
            }
            $state.bootEvidence.normalBootAfter = $afterBoot
            $native = Get-ComoteNativeStatus -Artifacts $artifacts
            if ($native.ExitCode -ne 0 -or
                [string]$native.State -cne "installed") {
                throw "The driver is not installed after the normal restart."
            }
            $normalReboot = Invoke-ComotePromotionE2E `
                -Label "after-normal-reboot" `
                -Artifacts $artifacts
            Add-ComoteE2ERun -State $state -Evidence $normalReboot
            $state.bootEvidence.s1CheckpointUtc =
                [DateTime]::UtcNow.ToString("o")
            $state.bootEvidence.s1BootMarker = $afterBoot
            Move-ComotePromotionPhase `
                -State $state `
                -ExpectedCurrent "await-normal-reboot" `
                -Next "await-s1-resume" `
                -StatePath $promotionPaths.State
            Write-Host "Enter and resume from S1 sleep manually, then rerun."
            return
        }
        "await-s1-resume" {
            [void](Assert-ComoteControllerToken)
            $afterS1Boot = Get-ComoteBootMarker
            if ($afterS1Boot -cne [string]$state.bootEvidence.s1BootMarker) {
                throw "S1 validation requires the same Windows boot marker."
            }
            $state.bootEvidence.s1Evidence = Get-ComoteS1ResumeEvidence `
                -CheckpointUtc ([DateTime]::Parse(
                    [string]$state.bootEvidence.s1CheckpointUtc,
                    [Globalization.CultureInfo]::InvariantCulture,
                    [Globalization.DateTimeStyles]::RoundtripKind
                ))
            $s1 = Invoke-ComotePromotionE2E `
                -Label "after-s1-resume" `
                -Artifacts $artifacts
            Add-ComoteE2ERun -State $state -Evidence $s1
            $state.bootEvidence.coldCheckpointUtc =
                [DateTime]::UtcNow.ToString("o")
            $state.bootEvidence.coldBootBefore = $afterS1Boot
            Move-ComotePromotionPhase `
                -State $state `
                -ExpectedCurrent "await-s1-resume" `
                -Next "await-cold-start" `
                -StatePath $promotionPaths.State
            Write-Host "Shut down Windows cleanly and cold-start the VM manually."
            Write-Host "Rerun this command only after the cold start."
            return
        }
        "await-cold-start" {
            [void](Assert-ComoteControllerToken)
            $coldAfter = Get-ComoteBootMarker
            $state.bootEvidence.coldBootAfter = $coldAfter
            $state.bootEvidence.coldEvidence = Get-ComoteColdStartEvidence `
                -CheckpointUtc ([DateTime]::Parse(
                    [string]$state.bootEvidence.coldCheckpointUtc,
                    [Globalization.CultureInfo]::InvariantCulture,
                    [Globalization.DateTimeStyles]::RoundtripKind
                )) `
                -BeforeBoot ([string]$state.bootEvidence.coldBootBefore) `
                -AfterBoot $coldAfter
            $cold = Invoke-ComotePromotionE2E `
                -Label "after-cold-start" `
                -Artifacts $artifacts
            Add-ComoteE2ERun -State $state -Evidence $cold
            Move-ComotePromotionPhase `
                -State $state `
                -ExpectedCurrent "await-cold-start" `
                -Next "configure-verifier" `
                -StatePath $promotionPaths.State
            Write-Host "Cold-start evidence and E2E passed."
            return
        }
        "configure-verifier" {
            [void](Assert-ComoteControllerToken)
            Repair-ComoteVerifierConfigurationForRetry
            $state.verifierConfiguration = Enable-ComoteOneBootVerifier
            $state.bootEvidence.verifierBootBefore = Get-ComoteBootMarker
            Move-ComotePromotionPhase `
                -State $state `
                -ExpectedCurrent "configure-verifier" `
                -Next "await-verifier-reboot" `
                -StatePath $promotionPaths.State
            Write-Host "Driver Verifier is configured for one boot."
            Write-Host "Restart Windows manually, then rerun."
            return
        }
        "await-verifier-reboot" {
            [void](Assert-ComoteControllerToken)
            $verifierAfter = Get-ComoteBootMarker
            if ($verifierAfter -ceq
                [string]$state.bootEvidence.verifierBootBefore) {
                throw "A changed boot marker is required for Verifier evidence."
            }
            $state.bootEvidence.verifierBootAfter = $verifierAfter
            $state.verifierEvidence = Assert-ComoteOneBootVerifierActive
            $underVerifier = Invoke-ComotePromotionE2E `
                -Label "under-driver-verifier" `
                -Artifacts $artifacts
            Add-ComoteE2ERun -State $state -Evidence $underVerifier
            Move-ComotePromotionPhase `
                -State $state `
                -ExpectedCurrent "await-verifier-reboot" `
                -Next "cleanup-unified" `
                -StatePath $promotionPaths.State
            Write-Host "One-boot Driver Verifier validation passed."
            return
        }
        "cleanup-unified" {
            [void](Assert-ComoteControllerToken)
            $previewPaths = Get-ComotePreviewPaths
            if (Test-Path -LiteralPath $previewPaths.ReceiptPath -PathType Leaf) {
                [void](Read-ComoteProtectedReceipt `
                    -ExpectedReleaseManifestSha256 `
                        ([string]$state.releaseManifestSha256) `
                    -ExpectedPackageRole "validation-unified")
                & (Join-Path `
                    $artifacts.PackageRoot `
                    "Uninstall-ComoteVirtualHidPreview.ps1") `
                    -AcknowledgeDisposableVm `
                    -ExpectedReleaseManifestSha256 `
                        ([string]$state.releaseManifestSha256)
                if ($LASTEXITCODE -notin @($null, 0)) {
                    throw "Unified receipt-owned cleanup returned nonzero."
                }
            }
            $verifierResetExitCode = Reset-ComoteVerifier
            $signingCleanup = Invoke-ComoteSigningCleanup
            $clean = Assert-ComoteFullCleanState `
                -Artifacts $artifacts `
                -RequireRetainedBrokerLogRoot
            $state.unifiedCleanup = [PSCustomObject][ordered]@{
                completedUtc = [DateTime]::UtcNow.ToString("o")
                verifierResetExitCode = [int]$verifierResetExitCode
                signingCleanup = $signingCleanup
                machineState = $clean
            }
            $state.bootEvidence.postCleanupCheckpointUtc =
                [DateTime]::UtcNow.ToString("o")
            $state.bootEvidence.postCleanupBootBefore = Get-ComoteBootMarker
            Move-ComotePromotionPhase `
                -State $state `
                -ExpectedCurrent "cleanup-unified" `
                -Next "await-post-verifier-clean-reboot" `
                -StatePath $promotionPaths.State
            Write-Host "Unified receipt-owned cleanup is proven clean."
            Write-Host "Restart Windows manually once more, then rerun."
            return
        }
        "await-post-verifier-clean-reboot" {
            [void](Assert-ComoteControllerToken)
            $postCleanupBoot = Get-ComoteBootMarker
            if ($postCleanupBoot -ceq
                [string]$state.bootEvidence.postCleanupBootBefore) {
                throw "A changed boot marker is required after cleanup."
            }
            $state.bootEvidence.postCleanupBootAfter = $postCleanupBoot
            [void](Assert-ComoteFullCleanState `
                -Artifacts $artifacts `
                -RequireRetainedBrokerLogRoot)
            $provisional = Write-ComoteProvisionalReport `
                -State $state `
                -Paths $promotionPaths
            $state.provisionalReportPath = $provisional.Path
            $state.provisionalReportSha256 = $provisional.Sha256
            Move-ComotePromotionPhase `
                -State $state `
                -ExpectedCurrent "await-post-verifier-clean-reboot" `
                -Next "export-role-candidate" `
                -StatePath $promotionPaths.State
            Write-Host "Unified provisional evidence is immutable and bound."
            return
        }
        "export-role-candidate" {
            [void](Assert-ComoteFullCleanState `
                -Artifacts $artifacts `
                -RequireRetainedBrokerLogRoot)
            $candidateRoot = Join-Path `
                $promotionPaths.Root `
                ("Candidate-{0}" -f $ReleaseId)
            if (-not (Test-Path -LiteralPath $candidateRoot -PathType Container)) {
                & $exporterPath `
                    -CreateCandidate `
                    -AcknowledgeDisposableVm `
                    -UnifiedPackageRoot $artifacts.PackageRoot `
                    -ExpectedUnifiedManifestSha256 `
                        ([string]$state.releaseManifestSha256) `
                    -UnifiedProvisionalReportPath `
                        ([string]$state.provisionalReportPath) `
                    -ExpectedUnifiedProvisionalReportSha256 `
                        ([string]$state.provisionalReportSha256) `
                    -OutputDirectory $candidateRoot
                if ($LASTEXITCODE -notin @($null, 0)) {
                    throw "Candidate role export returned nonzero."
                }
                Set-ComoteProtectedDirectoryAcl -LiteralPath $candidateRoot
            }
            $candidate = Get-ComoteCandidateBinding `
                -State $state `
                -ExporterPath $exporterPath `
                -AllowUnboundState
            $roleRoots = Initialize-ComoteCandidateRoleTests `
                -Candidate $candidate `
                -Paths $promotionPaths
            $state.candidateRoot = $candidate.Root
            $state.candidateIndexPath = $candidate.IndexPath
            $state.candidateIndexSha256 = $candidate.IndexSha256
            $state.candidateRoleHashes = $candidate.RoleHashes
            $state.managerRoleRoot = $roleRoots.ManagerRoot
            $state.clientRoleRoot = $roleRoots.ClientRoot
            $state.clientManifestSha256 =
                [string]$candidate.Index.clientVirtualHid.manifestSha256
            Move-ComotePromotionPhase `
                -State $state `
                -ExpectedCurrent "export-role-candidate" `
                -Next "await-manager-role-start" `
                -StatePath $promotionPaths.State
            Write-Host "Candidate role bytes are authenticated and extracted."
            Write-Host "Launch START COMOTE MANAGER.cmd from the protected Manager role."
            Write-Host "Rerun while that exact Manager process is open."
            return
        }
        "await-manager-role-start" {
            [void](Assert-ComoteControllerToken)
            & (Join-Path `
                ([string]$state.managerRoleRoot) `
                "Verify-ComoteManagerRole.ps1")
            if ($LASTEXITCODE -notin @($null, 0)) {
                throw "Portable Manager role verification returned nonzero."
            }
            $managerManifestPath = Join-Path `
                ([string]$state.managerRoleRoot) `
                "manager-role-manifest.json"
            if ((Get-ComoteSha256 -LiteralPath $managerManifestPath) -cne
                [string]$state.candidateRoleHashes.managerManifestSha256) {
                throw "The extracted Manager manifest changed."
            }
            $managerManifest = Read-ComoteJson `
                -LiteralPath $managerManifestPath `
                -Description "Extracted Manager role manifest"
            $managerPath = Join-Path `
                ([string]$state.managerRoleRoot) `
                ([string]$managerManifest.application.path).Replace('/', '\')
            $managerPid = Get-ComoteSingletonProcessId `
                -Name "ComoteManager.exe" `
                -ExpectedPath $managerPath
            $managerProcess = Get-ComoteExactProcessEvidence `
                -ProcessId $managerPid `
                -ExpectedPath $managerPath `
                -ExpectedArguments @($managerPath, "--manager-hub") `
                -Application $managerManifest.application `
                -RuntimePolicy $managerManifest.runtimePolicy `
                -ExpectedOwnerSid (Get-ComoteControllerSid) `
                -ExpectedElevated $false `
                -RequireLimited
            Stop-ComoteExactProcess `
                -ProcessId $managerPid `
                -ExpectedPath $managerPath
            $state.managerRoleTest = [PSCustomObject][ordered]@{
                completedUtc = [DateTime]::UtcNow.ToString("o")
                roleManifestPath = $managerManifestPath
                roleManifestSha256 =
                    [string]$state.candidateRoleHashes.managerManifestSha256
                verificationPassed = $true
                process = $managerProcess
                processStopped = $true
            }
            Move-ComotePromotionPhase `
                -State $state `
                -ExpectedCurrent "await-manager-role-start" `
                -Next "install-client-role" `
                -StatePath $promotionPaths.State
            Write-Host "Portable Manager role process evidence passed."
            return
        }
        "install-client-role" {
            [void](Assert-ComoteControllerToken)
            $previewPaths = Get-ComotePreviewPaths
            $receipt = $null
            if (Test-Path -LiteralPath $previewPaths.ReceiptPath -PathType Leaf) {
                $receipt = Read-ComoteProtectedReceipt `
                    -ExpectedReleaseManifestSha256 `
                        ([string]$state.clientManifestSha256) `
                    -ExpectedPackageRole "client-virtual-hid"
                if ([string]$receipt.status -cne "installed") {
                    & (Join-Path `
                        ([string]$state.clientRoleRoot) `
                        "Uninstall-ComoteVirtualHidPreview.ps1") `
                        -AcknowledgeTestSignedPreview `
                        -ExpectedReleaseManifestSha256 `
                            ([string]$state.clientManifestSha256)
                    if ($LASTEXITCODE -notin @($null, 0)) {
                        throw "Client receipt-owned recovery returned nonzero."
                    }
                    $receipt = $null
                    [void](Assert-ComoteFullCleanState `
                        -Artifacts $artifacts `
                        -RequireRetainedBrokerLogRoot)
                    Write-Host "Recovered the exact partial Client receipt."
                    Write-Host "Launch INSTALL CLIENT (Administrator).cmd manually."
                    Write-Host "Complete its typed acceptance prompt, then rerun."
                    return
                }
            }
            elseif ((Test-Path -LiteralPath $previewPaths.StateRoot) -or
                (Test-Path -LiteralPath $previewPaths.InstallRoot) -or
                @(Get-ComoteBrokerService).Count -ne 0) {
                throw ("Unreceipted preview state blocks safe Client retry; " +
                    "restore the bound clean VM snapshot.")
            }
            if ($null -eq $receipt) {
                try {
                    [void](Assert-ComoteFullCleanState `
                        -Artifacts $artifacts `
                        -RequireRetainedBrokerLogRoot)
                }
                catch {
                    throw ("Client installation is not provably clean and has " +
                        "no authenticated receipt; restore snapshot " +
                        "'$SnapshotName'. " + $_.Exception.Message)
                }
                Write-Host "Launch INSTALL CLIENT (Administrator).cmd manually."
                Write-Host "Complete its exact typed acceptance prompt, then rerun."
                return
            }
            if ([string]$receipt.status -cne "installed") {
                throw "Client role receipt is not installed."
            }
            $state.clientInstallEvidence = [PSCustomObject][ordered]@{
                receiptPath = $previewPaths.ReceiptPath
                receiptSha256 = Get-ComoteSha256 `
                    -LiteralPath $previewPaths.ReceiptPath
                packageRole = "client-virtual-hid"
                installedUtc = [string]$receipt.updatedUtc
            }
            Move-ComotePromotionPhase `
                -State $state `
                -ExpectedCurrent "install-client-role" `
                -Next "await-client-role-logon" `
                -StatePath $promotionPaths.State
            Write-Host "Client role installed from exact extracted candidate bytes."
            Write-Host "Sign out and sign in manually as the controller."
            Write-Host "Launch START HERE - Client Virtual HID.cmd, then rerun."
            return
        }
        "await-client-role-logon" {
            [void](Assert-ComoteControllerToken)
            $receipt = Read-ComoteProtectedReceipt `
                -ExpectedReleaseManifestSha256 `
                    ([string]$state.clientManifestSha256) `
                -ExpectedPackageRole "client-virtual-hid"
            if ([string]$receipt.status -cne "installed") {
                throw "The Client role receipt is not installed."
            }
            $clientRelease = Get-ComoteReleaseManifest `
                -PackageRoot ([string]$state.clientRoleRoot) `
                -ExpectedManifestSha256 `
                    ([string]$state.clientManifestSha256)
            Assert-ComoteReleaseInventory -Release $clientRelease
            [void](Assert-ComoteDriverPackageBinding -Release $clientRelease)
            Assert-ComoteReleaseSigners -Release $clientRelease
            $clientApplication = Get-ComoteApplicationByRole `
                -Document $clientRelease.Document `
                -Role "client"
            $brokerApplication = Get-ComoteApplicationByRole `
                -Document $clientRelease.Document `
                -Role "broker"
            $clientPath = Join-Path `
                ([string]$receipt.paths.installRoot) `
                ([string]$clientApplication.path).Replace('/', '\')
            $clientPid = Get-ComoteSingletonProcessId `
                -Name "ComoteClient.exe" `
                -ExpectedPath $clientPath
            $clientProcess = Get-ComoteExactProcessEvidence `
                -ProcessId $clientPid `
                -ExpectedPath $clientPath `
                -ExpectedArguments @(
                    $clientPath,
                    "--manager-hub",
                    "--virtual-hid"
                ) `
                -Application $clientApplication `
                -RuntimePolicy $clientRelease.Document.runtimePolicy `
                -ExpectedOwnerSid (Get-ComoteControllerSid) `
                -ExpectedElevated $false `
                -RequireLimited `
                -RequireTrustedSigner
            $services = @(Get-ComoteBrokerService)
            if ($services.Count -ne 1 -or
                [string]$services[0].State -cne "Running") {
                throw "Client role Broker service is not a running singleton."
            }
            Assert-ComoteBrokerServiceIdentity `
                -Service $services[0] `
                -BinaryPath (Join-Path `
                    ([string]$receipt.paths.installRoot) `
                    ([string]$brokerApplication.path).Replace('/', '\')) `
                -BinarySha256 ([string]$brokerApplication.sha256)
            $brokerPath = Join-Path `
                ([string]$receipt.paths.installRoot) `
                ([string]$brokerApplication.path).Replace('/', '\')
            $brokerProcess = Get-ComoteExactProcessEvidence `
                -ProcessId ([int]$services[0].ProcessId) `
                -ExpectedPath $brokerPath `
                -ExpectedArguments @($brokerPath) `
                -Application $brokerApplication `
                -RuntimePolicy $clientRelease.Document.runtimePolicy `
                -ExpectedOwnerSid "S-1-5-18" `
                -ExpectedElevated $true `
                -RequireTrustedSigner `
                -RequireProtectedRuntimeAcl
            $native = Get-ComoteNativeStatus -Artifacts $artifacts
            if ($native.ExitCode -ne 0 -or
                [string]$native.State -cne "installed") {
                throw "Client role native driver is not installed."
            }
            $state.clientRoleTest = [PSCustomObject][ordered]@{
                launchedUtc = [DateTime]::UtcNow.ToString("o")
                manifestSha256 = [string]$state.clientManifestSha256
                process = $clientProcess
                brokerProcess = $brokerProcess
                brokerServiceName = [string]$services[0].Name
                nativeState = [string]$native.State
                e2e = $null
                revalidatedProcess = $null
                processStopped = $false
            }
            Move-ComotePromotionPhase `
                -State $state `
                -ExpectedCurrent "await-client-role-logon" `
                -Next "client-role-e2e" `
                -StatePath $promotionPaths.State
            Write-Host "Client role process and service identities passed."
            return
        }
        "client-role-e2e" {
            [void](Assert-ComoteControllerToken)
            $clientPath = [string]$state.clientRoleTest.process.path
            $clientPid = Get-ComoteSingletonProcessId `
                -Name "ComoteClient.exe" `
                -ExpectedPath $clientPath
            if ($clientPid -ne [int]$state.clientRoleTest.process.processId) {
                throw "The verified Client PID changed before role E2E."
            }
            $clientApplication = [PSCustomObject]@{
                sha256 = [string]$state.clientRoleTest.process.sha256
                originalFilename =
                    [string]$state.clientRoleTest.process.originalFilename
                signerThumbprint =
                    [string]$state.clientRoleTest.process.signerThumbprint
            }
            $revalidatedClient = Get-ComoteExactProcessEvidence `
                -ProcessId $clientPid `
                -ExpectedPath $clientPath `
                -ExpectedArguments @(
                    $clientPath,
                    "--manager-hub",
                    "--virtual-hid"
                ) `
                -Application $clientApplication `
                -RuntimePolicy $state.runtimePolicy `
                -ExpectedOwnerSid (Get-ComoteControllerSid) `
                -ExpectedElevated $false `
                -RequireLimited `
                -RequireTrustedSigner
            $clientE2E = Invoke-ComotePromotionE2E `
                -Label "client-role" `
                -Artifacts $artifacts
            Add-ComoteE2ERun -State $state -Evidence $clientE2E
            $state.clientRoleTest.e2e = $clientE2E
            $state.clientRoleTest.revalidatedProcess = $revalidatedClient
            Move-ComotePromotionPhase `
                -State $state `
                -ExpectedCurrent "client-role-e2e" `
                -Next "cleanup-client-role" `
                -StatePath $promotionPaths.State
            Write-Host "Client role E2E passed."
            return
        }
        "cleanup-client-role" {
            [void](Assert-ComoteControllerToken)
            $clientPath = [string]$state.clientRoleTest.process.path
            $clientProcesses = @(Get-CimInstance `
                -ClassName Win32_Process `
                -Filter "Name='ComoteClient.exe'" `
                -ErrorAction Stop)
            if ($clientProcesses.Count -eq 1 -and
                [int]$clientProcesses[0].ProcessId -eq
                    [int]$state.clientRoleTest.process.processId -and
                [IO.Path]::GetFullPath(
                    [string]$clientProcesses[0].ExecutablePath
                ).Equals(
                    [IO.Path]::GetFullPath($clientPath),
                    [StringComparison]::OrdinalIgnoreCase)) {
                Stop-ComoteExactProcess `
                    -ProcessId ([int]$clientProcesses[0].ProcessId) `
                    -ExpectedPath $clientPath
            }
            elseif ($clientProcesses.Count -ne 0) {
                throw "An unexpected Client process blocks role cleanup."
            }
            $state.clientRoleTest.processStopped = $true
            $previewPaths = Get-ComotePreviewPaths
            if (Test-Path -LiteralPath $previewPaths.ReceiptPath -PathType Leaf) {
                [void](Read-ComoteProtectedReceipt `
                    -ExpectedReleaseManifestSha256 `
                        ([string]$state.clientManifestSha256) `
                    -ExpectedPackageRole "client-virtual-hid")
                & (Join-Path `
                    ([string]$state.clientRoleRoot) `
                    "Uninstall-ComoteVirtualHidPreview.ps1") `
                    -AcknowledgeTestSignedPreview `
                    -ExpectedReleaseManifestSha256 `
                        ([string]$state.clientManifestSha256)
                if ($LASTEXITCODE -notin @($null, 0)) {
                    throw "Client receipt-owned cleanup returned nonzero."
                }
            }
            $clientSigningCleanup = Invoke-ComoteSigningCleanup
            $clientClean = Assert-ComoteFullCleanState `
                -Artifacts $artifacts `
                -RequireRetainedBrokerLogRoot
            $state.clientCleanup = [PSCustomObject][ordered]@{
                completedUtc = [DateTime]::UtcNow.ToString("o")
                signingCleanup = $clientSigningCleanup
                machineState = $clientClean
            }
            Move-ComotePromotionPhase `
                -State $state `
                -ExpectedCurrent "cleanup-client-role" `
                -Next "final-report" `
                -StatePath $promotionPaths.State
            Write-Host "Client receipt-owned cleanup is proven clean."
            return
        }
        "final-report" {
            [void](Assert-ComoteFullCleanState `
                -Artifacts $artifacts `
                -RequireRetainedBrokerLogRoot)
            $final = Write-ComoteFinalReport `
                -State $state `
                -Paths $promotionPaths
            $state.finalReportPath = $final.Path
            $state.finalReportSha256 = $final.Sha256
            Move-ComotePromotionPhase `
                -State $state `
                -ExpectedCurrent "final-report" `
                -Next "promote-index" `
                -StatePath $promotionPaths.State
            Write-Host "Final promotion report is immutable and bound."
            return
        }
        "promote-index" {
            $candidate = Get-ComoteCandidateBinding `
                -State $state `
                -ExporterPath $exporterPath
            & $exporterPath `
                -PromoteCandidate `
                -CandidateDirectory $candidate.Root `
                -ExpectedCandidateIndexSha256 $candidate.IndexSha256 `
                -FinalReportPath ([string]$state.finalReportPath) `
                -ExpectedFinalReportSha256 `
                    ([string]$state.finalReportSha256)
            if ($LASTEXITCODE -notin @($null, 0)) {
                throw "Candidate index promotion returned nonzero."
            }
            $promotedPath = Join-Path `
                $candidate.Root `
                "PROMOTED-ROLE-INDEX.json"
            $promotedHash = Assert-ComoteAsciiHashSidecar `
                -LiteralPath "$promotedPath.sha256" `
                -TargetPath $promotedPath
            $state.promotedIndexPath = $promotedPath
            $state.promotedIndexSha256 = $promotedHash
            Publish-ComoteFinalOutput `
                -State $state `
                -OutputRoot ([string]$state.arguments.outputDirectory)
            Move-ComotePromotionPhase `
                -State $state `
                -ExpectedCurrent "promote-index" `
                -Next "complete" `
                -StatePath $promotionPaths.State
            Write-Host "Promotion is complete." -ForegroundColor Green
            Write-Host "Final report: $($state.finalReportPath)"
            Write-Host "SHA-256: $($state.finalReportSha256)"
            return
        }
        "complete" {
            throw "The complete phase must use the authenticated short circuit."
        }
        default {
            throw "The protected promotion phase is unsupported."
        }
    }
}
finally {
    if ($promotionLockAcquired) {
        try {
            $promotionMutex.ReleaseMutex()
        }
        catch {
        }
    }
    $promotionMutex.Dispose()
}
