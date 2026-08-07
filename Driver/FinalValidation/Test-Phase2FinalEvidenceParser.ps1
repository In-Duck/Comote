#Requires -Version 5.1

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot "Phase2FinalValidation.Common.ps1")

$keyboardId = "HID\HID_DEVICE_SYSTEM_VHF&COL01\TEST"
$relativeMouseId = "HID\HID_DEVICE_SYSTEM_VHF&COL02\TEST"
$absoluteMouseId = "HID\HID_DEVICE_SYSTEM_VHF&COL03\TEST"
$startedUtc = [DateTime]::UtcNow.AddSeconds(-1)

function New-SyntheticKeyboardEvent {
    param(
        [Parameter(Mandatory)][string]$Stage,
        [Parameter(Mandatory)][int]$VirtualKey,
        [Parameter(Mandatory)][int]$MakeCode,
        [Parameter(Mandatory)][bool]$KeyBreak
    )
    return [PSCustomObject]@{
        kind = "Keyboard"
        stage = $Stage
        normalizedInstanceId = $keyboardId
        virtualKey = $VirtualKey
        makeCode = $MakeCode
        keyBreak = $KeyBreak
        mouseAbsolute = $false
        deltaX = 0
        deltaY = 0
        mouseButtonFlags = 0
        mouseButtonData = 0
    }
}

function New-SyntheticMouseEvent {
    param(
        [Parameter(Mandatory)][string]$Stage,
        [Parameter(Mandatory)][string]$InstanceId,
        [Parameter(Mandatory)][bool]$Absolute,
        [int]$DeltaX = 0,
        [int]$DeltaY = 0,
        [int]$ButtonFlags = 0,
        [int]$ButtonData = 0
    )
    return [PSCustomObject]@{
        kind = "Mouse"
        stage = $Stage
        normalizedInstanceId = $InstanceId
        virtualKey = 0
        makeCode = 0
        keyBreak = $false
        mouseAbsolute = $Absolute
        deltaX = $DeltaX
        deltaY = $DeltaY
        mouseButtonFlags = $ButtonFlags
        mouseButtonData = $ButtonData
    }
}

$tests = @(
    1..12 |
        ForEach-Object {
            [PSCustomObject]@{
                name = "test-$_"
                passed = $true
            }
        }
)
$events = @(
    New-SyntheticKeyboardEvent `
        -Stage "broker-keyboard-f24" -VirtualKey 0x87 `
        -MakeCode 0 -KeyBreak $false
    New-SyntheticKeyboardEvent `
        -Stage "broker-keyboard-f24" -VirtualKey 0x87 `
        -MakeCode 0 -KeyBreak $true
)
foreach ($virtualKey in @(0x7C, 0x7D, 0x7E, 0x7F, 0x80, 0x81)) {
    $events += New-SyntheticKeyboardEvent `
        -Stage "broker-keyboard-6kro" -VirtualKey $virtualKey `
        -MakeCode 0 -KeyBreak $false
    $events += New-SyntheticKeyboardEvent `
        -Stage "broker-keyboard-6kro" -VirtualKey $virtualKey `
        -MakeCode 0 -KeyBreak $true
}
$events += @(
    New-SyntheticMouseEvent `
        -Stage "broker-relative-mouse" `
        -InstanceId $relativeMouseId -Absolute $false `
        -DeltaX 11 -DeltaY 7
    New-SyntheticMouseEvent `
        -Stage "broker-absolute-mouse" `
        -InstanceId $absoluteMouseId -Absolute $true
    New-SyntheticMouseEvent `
        -Stage "broker-mouse-five-buttons" `
        -InstanceId $relativeMouseId -Absolute $false `
        -ButtonFlags 0x0155
    New-SyntheticMouseEvent `
        -Stage "broker-mouse-five-buttons" `
        -InstanceId $relativeMouseId -Absolute $false `
        -ButtonFlags 0x02AA
    New-SyntheticMouseEvent `
        -Stage "broker-mouse-wheels" `
        -InstanceId $relativeMouseId -Absolute $false `
        -ButtonFlags 0x0400 -ButtonData 120
    New-SyntheticMouseEvent `
        -Stage "broker-mouse-wheels" `
        -InstanceId $relativeMouseId -Absolute $false `
        -ButtonFlags 0x0800 -ButtonData -120
    New-SyntheticKeyboardEvent `
        -Stage "host-backend-keyboard-f12" -VirtualKey 0x7B `
        -MakeCode 0 -KeyBreak $false
    New-SyntheticKeyboardEvent `
        -Stage "host-backend-keyboard-f12" -VirtualKey 0x7B `
        -MakeCode 0 -KeyBreak $true
    New-SyntheticKeyboardEvent `
        -Stage "host-backend-left-control" -VirtualKey 0x11 `
        -MakeCode 0x1D -KeyBreak $false
    New-SyntheticKeyboardEvent `
        -Stage "host-backend-left-control" -VirtualKey 0x11 `
        -MakeCode 0x1D -KeyBreak $true
    New-SyntheticMouseEvent `
        -Stage "host-backend-absolute-mouse" `
        -InstanceId $absoluteMouseId -Absolute $true
    New-SyntheticMouseEvent `
        -Stage "host-backend-x2-button" `
        -InstanceId $absoluteMouseId -Absolute $true `
        -ButtonFlags 0x0100
    New-SyntheticMouseEvent `
        -Stage "host-backend-x2-button" `
        -InstanceId $absoluteMouseId -Absolute $true `
        -ButtonFlags 0x0200
    New-SyntheticMouseEvent `
        -Stage "host-backend-vertical-wheel" `
        -InstanceId $absoluteMouseId -Absolute $true `
        -ButtonFlags 0x0400 -ButtonData 127
    New-SyntheticMouseEvent `
        -Stage "host-backend-vertical-wheel" `
        -InstanceId $absoluteMouseId -Absolute $true `
        -ButtonFlags 0x0400 -ButtonData 113
    New-SyntheticMouseEvent `
        -Stage "host-backend-horizontal-wheel" `
        -InstanceId $absoluteMouseId -Absolute $true `
        -ButtonFlags 0x0800 -ButtonData -127
    New-SyntheticMouseEvent `
        -Stage "host-backend-horizontal-wheel" `
        -InstanceId $absoluteMouseId -Absolute $true `
        -ButtonFlags 0x0800 -ButtonData -113
)

$report = [PSCustomObject]@{
    schemaVersion = 1
    runId = "evidence-parser-self-test"
    status = "passed"
    recoverySnapshotName = "comote-phase2-self-test"
    startedUtc = $startedUtc.ToString("o")
    completedUtc = [DateTime]::UtcNow.ToString("o")
    tests = $tests
    rawInputEvents = $events
    cleanup = [PSCustomObject]@{
        releaseAllSucceeded = $true
        cursorRestoreSucceeded = $true
        rawInputRegistrationRemoved = $true
    }
    environment = [PSCustomObject]@{
        systemManufacturer = "VMware, Inc."
        systemProductName = "VMware20,1"
        productName = "Windows 10 Home"
        osBuild = 19045
        is64BitOperatingSystem = $true
        userInteractive = $true
        expectedKeyboardInstanceIds = @($keyboardId)
        expectedMouseInstanceIds = @(
            $relativeMouseId,
            $absoluteMouseId
        )
    }
}

$inventory = Assert-ComoteFinalValidationEvidence `
    -Report $report `
    -RunId "evidence-parser-self-test" `
    -RecoverySnapshotName "comote-phase2-self-test" `
    -ExpectedKeyboardInstanceIds @($keyboardId) `
    -ExpectedMouseInstanceIds @(
        $relativeMouseId,
        $absoluteMouseId
    )
if ($inventory.TestCount -ne 12 -or
    $inventory.RawInputEventCount -ne 31) {
    throw "The valid synthetic evidence was not accepted exactly."
}

$invalidBoolean = $report | ConvertTo-Json -Depth 12 | ConvertFrom-Json
$invalidBoolean.cleanup.releaseAllSucceeded = "true"
try {
    Assert-ComoteFinalValidationEvidence `
        -Report $invalidBoolean `
        -RunId "evidence-parser-self-test" `
        -RecoverySnapshotName "comote-phase2-self-test" `
        -ExpectedKeyboardInstanceIds @($keyboardId) `
        -ExpectedMouseInstanceIds @(
            $relativeMouseId,
            $absoluteMouseId
        ) |
        Out-Null
    throw "String-valued cleanup evidence was incorrectly accepted."
}
catch {
    if ($_.Exception.Message -eq
        "String-valued cleanup evidence was incorrectly accepted.") {
        throw
    }
}

$invalidNumeric = $report | ConvertTo-Json -Depth 12 | ConvertFrom-Json
$invalidNumeric.rawInputEvents[14].mouseButtonFlags = "0"
try {
    Assert-ComoteFinalValidationEvidence `
        -Report $invalidNumeric `
        -RunId "evidence-parser-self-test" `
        -RecoverySnapshotName "comote-phase2-self-test" `
        -ExpectedKeyboardInstanceIds @($keyboardId) `
        -ExpectedMouseInstanceIds @(
            $relativeMouseId,
            $absoluteMouseId
        ) |
        Out-Null
    throw "String-valued Raw Input evidence was incorrectly accepted."
}
catch {
    if ($_.Exception.Message -eq
        "String-valued Raw Input evidence was incorrectly accepted.") {
        throw
    }
}

$invalidDevice = $report | ConvertTo-Json -Depth 12 | ConvertFrom-Json
$invalidDevice.rawInputEvents[14].normalizedInstanceId =
    "HID\UNEXPECTED\DEVICE"
try {
    Assert-ComoteFinalValidationEvidence `
        -Report $invalidDevice `
        -RunId "evidence-parser-self-test" `
        -RecoverySnapshotName "comote-phase2-self-test" `
        -ExpectedKeyboardInstanceIds @($keyboardId) `
        -ExpectedMouseInstanceIds @(
            $relativeMouseId,
            $absoluteMouseId
        ) |
        Out-Null
    throw "Unexpected-device Raw Input evidence was incorrectly accepted."
}
catch {
    if ($_.Exception.Message -eq
        "Unexpected-device Raw Input evidence was incorrectly accepted.") {
        throw
    }
}

$quoteCases = @(
    [PSCustomObject]@{ Input = ""; Expected = '""' },
    [PSCustomObject]@{ Input = "plain"; Expected = "plain" },
    [PSCustomObject]@{ Input = "a b"; Expected = '"a b"' },
    [PSCustomObject]@{
        Input = "C:\path with space\"
        Expected = '"C:\path with space\\"'
    },
    [PSCustomObject]@{ Input = 'a"b'; Expected = '"a\"b"' }
)
foreach ($quoteCase in $quoteCases) {
    $actual = ConvertTo-ComoteFinalValidationCommandLineArgument `
        -Argument $quoteCase.Input
    if ($actual -cne $quoteCase.Expected) {
        throw ("Command-line quoting failed. Expected '{0}', got '{1}'." -f
            $quoteCase.Expected,
            $actual)
    }
}

Write-Host "Phase 2 final E2E evidence parser self-test passed." `
    -ForegroundColor Green
Write-Host "No process was launched and no input or system mutation occurred."