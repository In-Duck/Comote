#Requires -Version 5.1

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSCommandPath
$paths = [ordered]@{
    Source = Join-Path $root "ComoteDriverInstaller.cpp"
    Project = Join-Path $root "ComoteDriverInstaller.vcxproj"
    Readme = Join-Path $root "README.md"
    Helper = Join-Path $root "Build-ComoteReleasePinnedInstaller.ps1"
    PinHeader = Join-Path $root "ComoteReleaseManifestPin.h"
}
foreach ($entry in $paths.GetEnumerator()) {
    if (-not (Test-Path -LiteralPath $entry.Value -PathType Leaf)) {
        throw "Required installer file is missing: $($entry.Value)"
    }
}

$source = Get-Content -LiteralPath $paths.Source -Raw
$project = Get-Content -LiteralPath $paths.Project -Raw
$readme = Get-Content -LiteralPath $paths.Readme -Raw
$helper = Get-Content -LiteralPath $paths.Helper -Raw
$pinHeader = Get-Content -LiteralPath $paths.PinHeader -Raw

function Assert-Contains {
    param([string]$Text, [string]$Token, [string]$Description)
    if (-not $Text.Contains($Token)) {
        throw "Missing $Description ($Token)."
    }
}

function Assert-NotContains {
    param([string]$Text, [string]$Token, [string]$Description)
    if ($Text.IndexOf($Token, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "Forbidden $Description is present ($Token)."
    }
}

function Get-ExactRange {
    param([string]$Text, [string]$Start, [string]$End, [string]$Description)
    $startIndex = $Text.IndexOf($Start, [StringComparison]::Ordinal)
    $endIndex = $Text.IndexOf($End, $startIndex + $Start.Length, [StringComparison]::Ordinal)
    if ($startIndex -lt 0 -or $endIndex -le $startIndex) {
        throw "Unable to isolate $Description."
    }
    return $Text.Substring($startIndex, $endIndex - $startIndex)
}

foreach ($identity in @(
    'L"ROOT\\COMOTEVIRTUALHID_PHASE2"',
    'L"ROOT\\COMOTEVIRTUALHID_PHASE2\\COMOTE_PHASE2"',
    'L"ComoteVirtualHidPhase2"',
    'L"ComoteVirtualHidPhase2.inf"',
    'L"ComoteVirtualHidPhase2.cat"',
    'L"ComoteVirtualHidPhase2.sys"',
    '0xba2bc8d8', '0x8d1b', '0x48e4'
)) {
    Assert-Contains $source $identity "exact Phase 2 identity"
}

foreach ($token in @(
    "VerifyCompileTimeManifestPin",
    "comote::release_manifest_pin::kPinned",
    "InspectLocalRegularFile",
    "GetFinalPathNameByHandleW",
    "GetFileInformationByHandle",
    "nNumberOfLinks != 1",
    "GetVolumePathNameW",
    "DRIVE_FIXED",
    "VerifyExactPackageFileSet",
    "InfSize", "CatSize", "SysSize",
    "SetupOpenInfFileW",
    "SetupDiGetActualModelsSectionW",
    "SetupDiGetActualSectionToInstallW",
    "SetupEnumInfSectionsW",
    "SetupGetFieldCount",
    "VerifyExactCatalogMemberAndTrust",
    "CryptCATAdminCalcHashFromFileHandle2",
    "CryptCATGetMemberInfo",
    "WTD_CHOICE_CATALOG",
    "SetNamedSecurityInfoW",
    "GetNamedSecurityInfoW",
    "SE_DACL_PROTECTED",
    "StageValidatedPackage",
    "SetupCopyOEMInfW",
    "SetupDiBuildDriverInfoList",
    "SetupDiSetSelectedDriverW",
    "DiInstallDevice",
    "VerifyPreBindInventory",
    "SPDRP_ENUMERATOR_NAME",
    "GUID_DEVCLASS_SYSTEM",
    "QueryServiceRecordFromHandle",
    "REG_MULTI_SZ",
    "DEVPROP_TYPE_STRING",
    "CurrentBootId",
    '"Operation="', '"StagePath="', '"PublishedInf="',
    '"BootId="', '"NeedsReboot="',
    "COMOTE-PHASE2-INSTALLER-STATE-V2",
    "COMOTE_INSTALLER_VM_TEST",
    "COMOTE_INSTALLER_RESULT"
)) {
    Assert-Contains $source $token "hardened native contract"
}

foreach ($forbidden in @(
    "UpdateDriverForPlugAndPlayDevices",
    "INSTALLFLAG_FORCE",
    "SetupUninstallOEMInf",
    "DICD_GENERATE_ID",
    "CreateProcess",
    "ShellExecute",
    "system(",
    "pnputil",
    "devgen",
    "TESTSIGNING",
    "bcdedit",
    "Cert:\\",
    "Remove-Item"
)) {
    Assert-NotContains $source $forbidden "broad or external mutation path"
}

$infVerifier = Get-ExactRange $source `
    "void VerifyInfIdentity(" `
    "void VerifyAuthenticodeFile(" `
    "semantic INF verifier"
foreach ($token in @(
    "SetupOpenInfFileW",
    "GetActualModelsSection(",
    "GetActualInstallSection(",
    "RequireExactInfSections(",
    "GetInfSectionLines",
    "RequireUniqueInfKey("
)) {
    Assert-Contains $infVerifier $token "semantic INF validation"
}
foreach ($token in @(
    "ReadStrictAsciiFile",
    "CountSubstring",
    ".find(",
    "std::tolower"
)) {
    Assert-NotContains $infVerifier $token "raw INF substring parsing"
}

$validatePackage = Get-ExactRange $source `
    "[[nodiscard]] PackagePaths ValidatePackage(" `
    "void ClearExactStagingDirectory(" `
    "package validator"
foreach ($token in @(
    "InspectLocalDirectory(directory)",
    "VerifyExactPackageFileSet(directory)",
    "manifest.infSize", "manifest.catSize", "manifest.sysSize",
    "VerifyInfIdentity", "VerifyInfCatalogTrust",
    "VerifyExactCatalogMemberAndTrust"
)) {
    Assert-Contains $validatePackage $token "package validation step"
}
Assert-NotContains $validatePackage `
    "VerifyAuthenticodeFile(package.sys)" `
    "embedded SYS signature requirement"

$deleteService = Get-ExactRange $source `
    "void DeleteVerifiedOrphanedService(" `
    "[[nodiscard]] fs::path VerifyOrphanedServiceBinary(" `
    "orphan service deletion"
foreach ($token in @(
    "SERVICE_QUERY_STATUS",
    "SERVICE_QUERY_CONFIG",
    "SERVICE_CHANGE_CONFIG",
    "SERVICE_STOP",
    "DELETE",
    "ChangeServiceConfigW(",
    "ControlService(",
    "QueryServiceRecordFromHandle(",
    "DeleteService(service.get())"
)) {
    Assert-Contains $deleteService $token "same-handle service verification"
}

$install = Get-ExactRange $source `
    "[[nodiscard]] ExitCode Install(" `
    "[[nodiscard]] ExitCode Remove(" `
    "install transaction"
$receiptIndex = $install.IndexOf("WriteStateAtomically(statePath, state);")
$stageIndex = $install.IndexOf("StageValidatedPackage(sourcePackage, manifest)")
$createIndex = $install.IndexOf("CreateExactRootDevice();")
$publishedIndex = $install.IndexOf("StageExactDriverInf(staged, manifest)")
$bindIndex = $install.IndexOf("BindExactDriver(published, manifest);")
if ($receiptIndex -lt 0 -or $stageIndex -le $receiptIndex -or
    $createIndex -le $stageIndex -or $publishedIndex -le $createIndex -or
    $bindIndex -le $publishedIndex) {
    throw "Receipt, protected staging, exact root, exact INF stage, and exact bind order is invalid."
}

$arguments = Get-ExactRange $source `
    "[[nodiscard]] Arguments ParseArguments(" `
    "[[nodiscard]] std::string EscapeResultMessage(" `
    "argument parser"
Assert-Contains $arguments "#if COMOTE_INSTALLER_VM_TEST" "VM-only state override"
Assert-Contains $arguments "--state is disabled in production installers" "production state rejection"
$candidateInventory = Get-ExactRange $source `
    "[[nodiscard]] bool IsPhase2InfCandidate(" `
    "[[nodiscard]] std::vector<fs::path> FindPhase2PublishedInfs(" `
    "semantic published-INF inventory"
foreach ($token in @(
    "SetupOpenInfFileW",
    'L"Manufacturer"',
    "TryGetActualModelsSection(",
    "TryGetActualInstallSection(",
    'L".Services"',
    "kHardwareId",
    "kServiceName"
)) {
    Assert-Contains $candidateInventory $token "semantic published-INF inventory"
}
foreach ($token in @(
    "ReadStrictAsciiFile",
    ".find(",
    "std::tolower",
    'QueryInfVersionValue(path, L"Provider")'
)) {
    Assert-NotContains $candidateInventory $token "raw or provider-gated OEM INF inventory"
}

$protectedDirectory = Get-ExactRange $source `
    "void EnsureProtectedDirectory(" `
    "void VerifyExactPackageFileSet(" `
    "protected directory creation"
foreach ($token in @(
    "SECURITY_ATTRIBUTES",
    "CreateDirectoryW(path.c_str(), &attributes)",
    "ERROR_ALREADY_EXISTS",
    "VerifyProtectedPathDacl(path, true)"
)) {
    Assert-Contains $protectedDirectory $token "atomic protected directory contract"
}
Assert-NotContains $protectedDirectory `
    "ApplyAndVerifyProtectedPathDacl" `
    "pre-existing directory adoption"

$stagingCopy = Get-ExactRange $source `
    "void CopyFileToProtectedStage(" `
    "[[nodiscard]] PackagePaths StageValidatedPackage(" `
    "atomic staging copy"
foreach ($token in @(
    'L".incoming"',
    "CopyFileW(source.c_str(), incoming.c_str(), TRUE)",
    "ApplyAndVerifyProtectedPathDacl(incoming, false)",
    "MoveFileExW(",
    "MOVEFILE_WRITE_THROUGH",
    "VerifyProtectedPathDacl(destination, false)"
)) {
    Assert-Contains $stagingCopy $token "atomic protected staging contract"
}
$copyIndex = $stagingCopy.IndexOf("CopyFileW(source.c_str(), incoming.c_str(), TRUE)")
$secureIndex = $stagingCopy.IndexOf("ApplyAndVerifyProtectedPathDacl(incoming, false)")
$commitIndex = $stagingCopy.IndexOf("MoveFileExW(")
if ($copyIndex -lt 0 -or $secureIndex -le $copyIndex -or
    $commitIndex -le $secureIndex) {
    throw "Staging copy, security, and atomic commit order is invalid."
}

$cleanup = Get-ExactRange $source `
    "void CleanupExactInstallation(" `
    "[[nodiscard]] InstallerState NewInstallingState(" `
    "exact cleanup transaction"
$serviceDeleteIndex = $cleanup.IndexOf("DeleteVerifiedOrphanedService(expectedSys)")
$packageDeleteIndex = $cleanup.IndexOf("UninstallExactDriverPackage(")
if ($serviceDeleteIndex -lt 0 -or
    $packageDeleteIndex -le $serviceDeleteIndex) {
    throw "Verified service must be deleted before its Driver Store package."
}
foreach ($token in @(
    "children.all.size() != 3",
    "instanceIdMatches",
    "hardwareIdMatches",
    "VerifyInstallerMutexSecurity",
    'L"O:BAD:P(A;;GA;;;SY)(A;;GA;;;BA)"',
    'L"ComoteDriverInstaller" / L"Phase2"'
)) {
    Assert-Contains $source $token "exact topology or protected coordination contract"
}

function Test-SourcePolicy {
    param([string]$Candidate)
    return $Candidate.Contains("VerifyCompileTimeManifestPin") -and
        $Candidate.Contains("WTD_CHOICE_CATALOG") -and
        $Candidate.Contains("VerifyExactPackageFileSet") -and
        $Candidate.Contains("SetupCopyOEMInfW") -and
        $Candidate.Contains("DiInstallDevice") -and
        -not $Candidate.Contains("UpdateDriverForPlugAndPlayDevices")
}
if (-not (Test-SourcePolicy $source)) {
    throw "Baseline source policy unexpectedly failed."
}
$withoutPin = $source.Replace("VerifyCompileTimeManifestPin", "RemovedManifestPin")
if (Test-SourcePolicy $withoutPin) {
    throw "Negative mutation: removing the manifest pin was not detected."
}
$globalBind = $source.Replace("DiInstallDevice", "UpdateDriverForPlugAndPlayDevices")
if (Test-SourcePolicy $globalBind) {
    throw "Negative mutation: broad hardware-ID binding was not detected."
}

$manifestKeys = @(
    "HardwareId", "RootInstanceId", "ServiceName", "Provider",
    "PackageFiles", "InfSize", "InfSha256", "CatSize", "CatSha256",
    "SysSize", "SysSha256"
)
function Test-ManifestShape {
    param([string[]]$Lines)
    if ($Lines.Count -ne 12 -or
        $Lines[0] -cne "COMOTE-PHASE2-PACKAGE-MANIFEST-V1") {
        return $false
    }
    for ($index = 0; $index -lt $manifestKeys.Count; $index++) {
        $prefix = $manifestKeys[$index] + "="
        if (-not $Lines[$index + 1].StartsWith(
                $prefix,
                [StringComparison]::Ordinal)) {
            return $false
        }
    }
    if ($Lines[5] -cne
        "PackageFiles=ComoteVirtualHidPhase2.inf,ComoteVirtualHidPhase2.cat,ComoteVirtualHidPhase2.sys") {
        return $false
    }
    foreach ($lineIndex in @(6, 8, 10)) {
        if ($Lines[$lineIndex] -notmatch '^[A-Za-z]+Size=[1-9][0-9]*$') {
            return $false
        }
    }
    foreach ($lineIndex in @(7, 9, 11)) {
        if ($Lines[$lineIndex] -cnotmatch '^[A-Za-z]+Sha256=[0-9A-F]{64}$') {
            return $false
        }
    }
    return $true
}
$hash = "A" * 64
$validManifest = @(
    "COMOTE-PHASE2-PACKAGE-MANIFEST-V1",
    "HardwareId=ROOT\COMOTEVIRTUALHID_PHASE2",
    "RootInstanceId=ROOT\COMOTEVIRTUALHID_PHASE2\COMOTE_PHASE2",
    "ServiceName=ComoteVirtualHidPhase2",
    "Provider=Comote",
    "PackageFiles=ComoteVirtualHidPhase2.inf,ComoteVirtualHidPhase2.cat,ComoteVirtualHidPhase2.sys",
    "InfSize=1", "InfSha256=$hash",
    "CatSize=2", "CatSha256=$hash",
    "SysSize=3", "SysSha256=$hash"
)
if (-not (Test-ManifestShape $validManifest)) {
    throw "Baseline manifest-shape negative-test harness failed."
}
function Assert-ManifestRejected {
    param([string[]]$Lines)
    if (Test-ManifestShape $Lines) {
        throw "Negative mutation: malformed manifest was accepted."
    }
}
Assert-ManifestRejected ([string[]]$validManifest[0..10])
Assert-ManifestRejected ([string[]]@(
    $validManifest[0..4] + "Unknown=x" + $validManifest[5..11]
))
Assert-ManifestRejected ([string[]]@(
    $validManifest | ForEach-Object {
        if ($_ -eq "InfSize=1") { "InfSize=0" } else { $_ }
    }
))
Assert-ManifestRejected ([string[]]@(
    $validManifest | ForEach-Object {
        if ($_ -like "PackageFiles=*") {
            "PackageFiles=ComoteVirtualHidPhase2.inf"
        } else { $_ }
    }
))
Assert-ManifestRejected ([string[]]@(
    $validManifest | ForEach-Object {
        if ($_ -like "InfSha256=*") {
            "InfSha256=$($hash.ToLowerInvariant())"
        } else { $_ }
    }
))
function Test-PackageNameSet {
    param([string[]]$Names)
    $expected = @(
        "ComoteVirtualHidPhase2.inf",
        "ComoteVirtualHidPhase2.cat",
        "ComoteVirtualHidPhase2.sys"
    )
    if ($Names.Count -ne $expected.Count) { return $false }
    for ($index = 0; $index -lt $expected.Count; $index++) {
        if ($Names[$index] -cne $expected[$index]) { return $false }
    }
    return $true
}
$canonicalNames = @(
    "ComoteVirtualHidPhase2.inf",
    "ComoteVirtualHidPhase2.cat",
    "ComoteVirtualHidPhase2.sys"
)
if (-not (Test-PackageNameSet $canonicalNames) -or
    (Test-PackageNameSet @($canonicalNames + "extra.dll")) -or
    (Test-PackageNameSet @("comotevirtualhidphase2.inf", $canonicalNames[1], $canonicalNames[2])) -or
    (Test-PackageNameSet @($canonicalNames[0], $canonicalNames[1]))) {
    throw "Exact package-set negative tests failed."
}

foreach ($token in @(
    '[string]$PackageDirectory',
    '"PackageFiles"', '"InfSize"', '"CatSize"', '"SysSize"',
    "FileAttributes]::ReparsePoint",
    "ComoteReleaseFileIdentity",
    "GetFinalPathNameByHandleW",
    "NumberOfLinks",
    "FileAttributes",
    "DriveType]::Fixed",
    "FILE_SHARE_READ",
    "captureBytes",
    "validatedManifest.Identity.Sha256",
    "validatedFile.Identity.Sha256",
    "ValidateOnly: no header was written"
)) {
    Assert-Contains $helper $token "release pin helper contract"
}
$tokens = $null
$parseErrors = $null
[void][Management.Automation.Language.Parser]::ParseFile(
    $paths.Helper,
    [ref]$tokens,
    [ref]$parseErrors)
if ($parseErrors.Count -ne 0) {
    throw "Release pin helper has PowerShell parse errors."
}
Assert-Contains $pinHeader "inline constexpr bool kPinned = false;" "fail-closed placeholder"
Assert-Contains $pinHeader "UNPINNED-INSTALL-MUST-REJECT" "unpinned marker"

[xml]$projectXml = $project
$namespace = New-Object Xml.XmlNamespaceManager($projectXml.NameTable)
$namespace.AddNamespace("msb", "http://schemas.microsoft.com/developer/msbuild/2003")
$configurations = @($projectXml.SelectNodes("//msb:ProjectConfiguration", $namespace))
if ($configurations.Count -ne 2 -or
    @($configurations | Where-Object {
        [string]$_.Include -notmatch "^(Debug|Release)\|x64$"
    }).Count -ne 0) {
    throw "Installer project must contain x64 Debug and Release only."
}
$preprocessorNodes = @(
    $projectXml.SelectNodes(
        "//msb:ItemDefinitionGroup/msb:ClCompile/msb:PreprocessorDefinitions",
        $namespace
    )
)
if ($preprocessorNodes.Count -ne 2 -or
    @($preprocessorNodes | Where-Object {
        ([string]$_.InnerText).Split(';') -cnotcontains
            "COMOTE_INSTALLER_VM_TEST=0"
    }).Count -ne 0) {
    throw "Every installer project configuration must force VM-test mode off."
}
foreach ($token in @(
    "<PlatformToolset>v143</PlatformToolset>",
    "<WarningLevel>Level4</WarningLevel>",
    "<TreatWarningAsError>true</TreatWarningAsError>",
    "<SDLCheck>true</SDLCheck>",
    "<LanguageStandard>stdcpp17</LanguageStandard>",
    "<ExceptionHandling>Sync</ExceptionHandling>",
    "<RuntimeLibrary>MultiThreaded</RuntimeLibrary>",
    "<ControlFlowGuard>Guard</ControlFlowGuard>",
    "<SpectreMitigation>Spectre</SpectreMitigation>",
    "<UACExecutionLevel>AsInvoker</UACExecutionLevel>",
    '<ClInclude Include="ComoteReleaseManifestPin.h" />',
    'COMOTE_INSTALLER_VM_TEST=0'
)) {
    Assert-Contains $project $token "hardened project setting"
}

foreach ($token in @(
    "not caller-trusted",
    "Build-ComoteReleasePinnedInstaller.ps1",
    "exactly these 12 non-empty lines",
    "protected local staging directory",
    "embedded SYS signature is not required",
    "COMOTE_INSTALLER_VM_TEST=1",
    "SetupCopyOEMInfW",
    "DiInstallDevice",
    "one healthy Keyboard-class descendant",
    "two healthy Mouse-class descendants",
    "Do not link, execute, install"
)) {
    Assert-Contains $readme $token "installer handoff documentation"
}

if (([regex]::Matches($source, "\{")).Count -ne
    ([regex]::Matches($source, "\}")).Count) {
    throw "Native source brace count is not balanced."
}

Write-Host "Comote Phase 2 hardened installer boundary verified." -ForegroundColor Green
Write-Host "Manifest/package/source negative mutations were rejected."
Write-Host "No binary was linked or executed; no driver or system state was changed."