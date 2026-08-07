#Requires -Version 5.1

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$scripts = @(
    Get-ChildItem `
        -LiteralPath $PSScriptRoot `
        -Filter "*.ps1" `
        -File `
        -Recurse `
        -ErrorAction Stop
)
foreach ($script in $scripts) {
    $tokens = $null
    $errors = $null
    [void][Management.Automation.Language.Parser]::ParseFile(
        $script.FullName,
        [ref]$tokens,
        [ref]$errors
    )
    if (@($errors).Count -ne 0) {
        throw ("PowerShell parse error in {0}: {1}" -f
            $script.FullName,
            (($errors | ForEach-Object Message) -join "; "))
    }
}

$invokePath = Join-Path `
    $PSScriptRoot `
    "Invoke-Phase2FinalE2E.ps1"
$invokeText = Get-Content -LiteralPath $invokePath -Raw
foreach ($required in @(
    "Assert-ComoteFinalValidationVm",
    "Test-Phase2InstalledState.ps1",
    "AcknowledgeInputGeneration",
    "RUN COMOTE VIRTUAL HID E2E IN VM",
    "--cleanup-only",
    "COMOTE-WIN10-VM-E2E",
    "raw-input-report.json",
    "Assert-ComoteFinalValidationEvidence",
    "Invoke-ComoteFinalValidationProcess",
    "-TimeoutSeconds 120",
    "-TimeoutSeconds 30",
    "brokerExecutableSha256",
    "ExpectedBrokerSha256"
)) {
    if ($invokeText.IndexOf(
            $required,
            [StringComparison]::Ordinal) -lt 0) {
        throw "The final E2E invoke gate is missing: $required"
    }
}

$commonText = Get-Content `
    -LiteralPath (Join-Path `
        $PSScriptRoot `
        "Phase2FinalValidation.Common.ps1") `
    -Raw
foreach ($required in @(
    'Manufacturer -ne "VMware, Inc."',
    'Model -notmatch "^VMware"',
    'BuildNumber -ne "19045"',
    "Comote Input Controllers",
    "ComoteInputBroker",
    'StartMode -ne "Auto"',
    "Win32_Process",
    "WaitForExit"
)) {
    if ($commonText.IndexOf(
            $required,
            [StringComparison]::Ordinal) -lt 0) {
        throw "The final E2E VM boundary is missing: $required"
    }
}

$allText = (
    $scripts |
        Where-Object {
            $_.Name -ne "Test-Phase2FinalE2EBoundary.ps1"
        } |
        ForEach-Object {
            Get-Content -LiteralPath $_.FullName -Raw
        }
) -join "`n"
foreach ($forbidden in @(
    "bcdedit.exe /set",
    "verifier.exe /",
    "pnputil.exe /add-driver",
    "devcon.exe",
    "Restart-Computer",
    "Stop-Computer",
    "SendInput(",
    "keybd_event(",
    "mouse_event("
)) {
    if ($allText.IndexOf(
            $forbidden,
            [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "A forbidden final E2E operation was found: $forbidden"
    }
}

$projectPath = Join-Path `
    $PSScriptRoot `
    "VirtualHidE2E\Comote.VirtualHidE2E.csproj"
$projectText = Get-Content -LiteralPath $projectPath -Raw
foreach ($required in @(
    "InputCore\Comote.InputCore.csproj",
    "HostInputProtocol.cs",
    "VirtualHidInputBackend.cs"
)) {
    if ($projectText.IndexOf(
            $required,
            [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "The final E2E project is missing a required path: $required"
    }
}

$sourceText = (
    Get-ChildItem `
        -LiteralPath (Join-Path $PSScriptRoot "VirtualHidE2E") `
        -Filter "*.cs" `
        -File |
    ForEach-Object {
        Get-Content -LiteralPath $_.FullName -Raw
    }
) -join "`n"
foreach ($required in @(
    "VMware, Inc.",
    "build != 19045",
    "InputBrokerClient",
    "RawInput",
    "ReleaseAll",
    "SetCursorPos",
    "MouseRelative",
    "MouseAbsolute",
    "VirtualHidInputBackend",
    "broker-keyboard-6kro",
    "host-backend-left-control",
    "broker-mouse-five-buttons",
    "host-backend-x2-button",
    "host-backend-vertical-wheel",
    "host-backend-horizontal-wheel",
    "MouseButtonFlags",
    "MouseButtonData",
    "RiMouseWheel",
    "RiMouseHorizontalWheel"
)) {
    if ($sourceText.IndexOf(
            $required,
            [StringComparison]::Ordinal) -lt 0) {
        throw "The final E2E observer is missing: $required"
    }
}
if ($sourceText.IndexOf(
        "DesignerSerializationVisibility.Hidden",
        [StringComparison]::Ordinal) -lt 0) {
    throw "The WinForms validation-only Stage property is not hidden."
}

foreach ($forbidden in @(
    "CreateFile(",
    "DeviceIoControl(",
    "SendInput(",
    "keybd_event(",
    "mouse_event("
)) {
    if ($sourceText.IndexOf(
            $forbidden,
            [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw "The observer bypasses the Broker boundary: $forbidden"
    }
}

Write-Host "Phase 2 final E2E source boundary verified." `
    -ForegroundColor Green
Write-Host "No project was built and no input or system mutation occurred."
