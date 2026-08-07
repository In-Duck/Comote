#Requires -Version 5.1

Set-StrictMode -Version Latest

function Assert-ComoteFinalValidationSnapshotName {
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$Name
    )

    if ($Name.Length -gt 128 -or
        $Name -notmatch "^[A-Za-z0-9][A-Za-z0-9._-]{2,127}$") {
        throw "Use the exact VMware snapshot name (3-128 safe characters)."
    }
}

function Assert-ComoteFinalValidationVm {
    param(
        [Parameter(Mandatory)]
        [switch]$AcknowledgeDisposableVm
    )

    if (-not $AcknowledgeDisposableVm.IsPresent) {
        throw "The disposable VMware acknowledgement is required."
    }
    if (-not [Environment]::UserInteractive) {
        throw "Final E2E validation requires an interactive user session."
    }

    $principal = New-Object Security.Principal.WindowsPrincipal(
        [Security.Principal.WindowsIdentity]::GetCurrent()
    )
    if (-not $principal.IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Run elevated PowerShell inside the disposable VMware guest."
    }

    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        $localSystemSid = New-Object Security.Principal.SecurityIdentifier(
            [Security.Principal.WellKnownSidType]::LocalSystemSid,
            $null
        )
        if ($identity.User -eq $localSystemSid) {
            throw "Do not run Raw Input validation as LocalSystem."
        }
    }
    finally {
        $identity.Dispose()
    }

    $computer = Get-CimInstance `
        -ClassName Win32_ComputerSystem `
        -ErrorAction Stop
    $operatingSystem = Get-CimInstance `
        -ClassName Win32_OperatingSystem `
        -ErrorAction Stop
    if ([string]$computer.Manufacturer -ne "VMware, Inc." -or
        [string]$computer.Model -notmatch "^VMware") {
        throw ("Refusing to continue: exact VMware identity required; " +
            "found '{0}' / '{1}'." -f
            $computer.Manufacturer,
            $computer.Model)
    }
    if ([int]$operatingSystem.ProductType -ne 1 -or
        [string]$operatingSystem.Caption -notmatch "Windows 10" -or
        [string]$operatingSystem.OSArchitecture -notmatch "64" -or
        [string]$operatingSystem.BuildNumber -ne "19045") {
        throw ("Final E2E validation requires Windows 10 x64 client " +
            "build 19045; found '{0}' / '{1}' / build '{2}'." -f
            $operatingSystem.Caption,
            $operatingSystem.OSArchitecture,
            $operatingSystem.BuildNumber)
    }

    $currentVersion = Get-ItemProperty `
        -LiteralPath "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion" `
        -ErrorAction Stop
    return [PSCustomObject]@{
        Computer = $computer
        OperatingSystem = $operatingSystem
        Ubr = [int]$currentVersion.UBR
    }
}

function Assert-ComoteFinalValidationControllerAccess {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        $account = New-Object Security.Principal.NTAccount(
            $env:COMPUTERNAME,
            "Comote Input Controllers"
        )
        try {
            $groupSid = [Security.Principal.SecurityIdentifier](
                $account.Translate(
                    [Security.Principal.SecurityIdentifier]
                )
            )
        }
        catch [Security.Principal.IdentityNotMappedException] {
            throw ("The local 'Comote Input Controllers' group is missing. " +
                "Install the final Broker package first.")
        }

        $principal = New-Object Security.Principal.WindowsPrincipal($identity)
        if (-not $principal.IsInRole($groupSid)) {
            throw ("The current logon token is not in " +
                "'Comote Input Controllers'. Add the intended user, " +
                "sign out, and sign in again before E2E validation.")
        }
    }
    finally {
        $identity.Dispose()
    }
}

function Assert-ComoteFinalValidationBroker {
    $services = @(
        Get-CimInstance `
            -ClassName Win32_Service `
            -Filter "Name='ComoteInputBroker'" `
            -ErrorAction Stop
    )
    if ($services.Count -ne 1) {
        throw "Exactly one ComoteInputBroker service is required."
    }

    $service = $services[0]
    if ([string]$service.State -ne "Running" -or
        [string]$service.StartMode -ne "Auto" -or
        [uint32]$service.ProcessId -eq 0 -or
        [string]$service.StartName -notin @(
            "LocalSystem",
            "NT AUTHORITY\LocalSystem"
        )) {
        throw ("ComoteInputBroker must be a running LocalSystem service " +
            "configured for automatic start.")
    }

    $commandLine = [string]$service.PathName
    $executableText = $null
    if ($commandLine -match '^"([^"]+)"(?:\s+.*)?$') {
        $executableText = $Matches[1]
    }
    elseif ($commandLine -match '^(\S+)(?:\s+.*)?$') {
        $executableText = $Matches[1]
    }
    else {
        throw "The ComoteInputBroker service command line is ambiguous."
    }
    $executablePath = [IO.Path]::GetFullPath(
        [Environment]::ExpandEnvironmentVariables($executableText)
    )
    if ([IO.Path]::GetFileName($executablePath) -cne
            "Comote.InputBroker.exe" -or
        -not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
        throw "The ComoteInputBroker service executable is invalid."
    }

    $processes = @(
        Get-CimInstance `
            -ClassName Win32_Process `
            -Filter ("ProcessId={0}" -f [uint32]$service.ProcessId) `
            -ErrorAction Stop
    )
    if ($processes.Count -ne 1 -or
        [string]::IsNullOrWhiteSpace(
            [string]$processes[0].ExecutablePath) -or
        -not [IO.Path]::GetFullPath(
            [string]$processes[0].ExecutablePath
        ).Equals(
            $executablePath,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "The running Broker image does not match its service path."
    }

    return [PSCustomObject]@{
        Service = $service
        ExecutablePath = $executablePath
        Sha256 = (
            Get-FileHash `
                -Algorithm SHA256 `
                -LiteralPath $executablePath
        ).Hash
    }
}

function ConvertTo-ComoteFinalValidationCommandLineArgument {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Argument
    )

    if ($Argument.Length -gt 0 -and $Argument -notmatch '[\s"]') {
        return $Argument
    }

    $builder = New-Object Text.StringBuilder
    [void]$builder.Append('"')
    $backslashCount = 0
    foreach ($character in $Argument.ToCharArray()) {
        if ($character -eq '\') {
            $backslashCount++
            continue
        }
        if ($character -eq '"') {
            if ($backslashCount -gt 0) {
                [void]$builder.Append(
                    (('' + ('\' * ($backslashCount * 2))) -join '')
                )
            }
            [void]$builder.Append('\"')
            $backslashCount = 0
            continue
        }
        if ($backslashCount -gt 0) {
            [void]$builder.Append(
                (('' + ('\' * $backslashCount)) -join '')
            )
            $backslashCount = 0
        }
        [void]$builder.Append($character)
    }
    if ($backslashCount -gt 0) {
        [void]$builder.Append(
            (('' + ('\' * ($backslashCount * 2))) -join '')
        )
    }
    [void]$builder.Append('"')
    return $builder.ToString()
}

function Invoke-ComoteFinalValidationProcess {
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$FilePath,

        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [string[]]$ArgumentList,

        [Parameter(Mandatory)]
        [ValidateRange(1, 600)]
        [int]$TimeoutSeconds
    )

    $fullPath = [IO.Path]::GetFullPath($FilePath)
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "The final-validation process executable is missing."
    }
    $startInfo = New-Object Diagnostics.ProcessStartInfo
    $startInfo.FileName = $fullPath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $false
    $startInfo.Arguments = (
        $ArgumentList |
            ForEach-Object {
                ConvertTo-ComoteFinalValidationCommandLineArgument `
                    -Argument $_
            }
    ) -join ' '

    $process = New-Object Diagnostics.Process
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw "The final-validation process could not be started."
        }
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            try {
                $process.Kill()
                [void]$process.WaitForExit(10000)
            }
            catch {
            }
            $timeoutError = New-Object `
                -TypeName System.TimeoutException `
                -ArgumentList @(
                    "The final-validation process exceeded its safety timeout."
                )
            throw $timeoutError
        }
        return [int]$process.ExitCode
    }
    finally {
        $process.Dispose()
    }
}

function ConvertTo-ComoteFinalValidationInstanceId {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Value
    )

    return $Value.Trim().TrimEnd('\').ToUpperInvariant()
}

function Assert-ComoteFinalValidationEvidence {
    param(
        [Parameter(Mandatory)]
        $Report,

        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$RunId,

        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$RecoverySnapshotName,

        [Parameter(Mandatory)]
        [string[]]$ExpectedKeyboardInstanceIds,

        [Parameter(Mandatory)]
        [string[]]$ExpectedMouseInstanceIds
    )

    $tests = @($Report.tests)
    $events = @($Report.rawInputEvents)
    if ([int]$Report.schemaVersion -ne 1 -or
        [string]$Report.runId -cne $RunId -or
        [string]$Report.status -cne "passed" -or
        [string]$Report.recoverySnapshotName -cne $RecoverySnapshotName -or
        $tests.Count -ne 12 -or
        $events.Count -lt 25) {
        throw "The E2E evidence header or inventory is invalid."
    }

    $testNames = @(
        $tests |
            ForEach-Object {
                if ($_.passed -isnot [bool] -or
                    -not $_.passed -or
                    [string]::IsNullOrWhiteSpace([string]$_.name)) {
                    throw "An E2E test result is missing or not passed."
                }
                [string]$_.name
            }
    )
    if (@($testNames | Select-Object -Unique).Count -ne 12) {
        throw "The E2E evidence contains duplicate test results."
    }

    foreach ($cleanupProperty in @(
        "releaseAllSucceeded",
        "cursorRestoreSucceeded",
        "rawInputRegistrationRemoved"
    )) {
        $value = $Report.cleanup.$cleanupProperty
        if ($value -isnot [bool] -or -not $value) {
            throw "The E2E cleanup evidence is not strictly successful."
        }
    }

    $expectedKeyboardIds = @(
        $ExpectedKeyboardInstanceIds |
            ForEach-Object {
                ConvertTo-ComoteFinalValidationInstanceId -Value $_
            } |
            Sort-Object -Unique
    )
    $expectedMouseIds = @(
        $ExpectedMouseInstanceIds |
            ForEach-Object {
                ConvertTo-ComoteFinalValidationInstanceId -Value $_
            } |
            Sort-Object -Unique
    )
    $reportedKeyboardIds = @(
        $Report.environment.expectedKeyboardInstanceIds |
            ForEach-Object {
                ConvertTo-ComoteFinalValidationInstanceId `
                    -Value ([string]$_)
            } |
            Sort-Object -Unique
    )
    $reportedMouseIds = @(
        $Report.environment.expectedMouseInstanceIds |
            ForEach-Object {
                ConvertTo-ComoteFinalValidationInstanceId `
                    -Value ([string]$_)
            } |
            Sort-Object -Unique
    )
    if ($expectedKeyboardIds.Count -ne 1 -or
        $expectedMouseIds.Count -ne 2 -or
        @(Compare-Object `
            -ReferenceObject $expectedKeyboardIds `
            -DifferenceObject $reportedKeyboardIds).Count -ne 0 -or
        @(Compare-Object `
            -ReferenceObject $expectedMouseIds `
            -DifferenceObject $reportedMouseIds).Count -ne 0 -or
        [string]$Report.environment.systemManufacturer -ne "VMware, Inc." -or
        [string]$Report.environment.systemProductName -notmatch "^VMware" -or
        [string]$Report.environment.productName -notmatch "Windows 10" -or
        [int]$Report.environment.osBuild -ne 19045 -or
        $Report.environment.is64BitOperatingSystem -isnot [bool] -or
        -not $Report.environment.is64BitOperatingSystem -or
        $Report.environment.userInteractive -isnot [bool] -or
        -not $Report.environment.userInteractive) {
        throw "The E2E observer environment evidence is invalid."
    }

    foreach ($event in $events) {
        if ($event.keyBreak -isnot [bool] -or
            $event.mouseAbsolute -isnot [bool] -or
            $event.virtualKey -isnot [ValueType] -or
            $event.makeCode -isnot [ValueType] -or
            $event.deltaX -isnot [ValueType] -or
            $event.deltaY -isnot [ValueType] -or
            $event.mouseButtonFlags -isnot [ValueType] -or
            $event.mouseButtonData -isnot [ValueType] -or
            $event.virtualKey -is [bool] -or
            $event.makeCode -is [bool] -or
            $event.deltaX -is [bool] -or
            $event.deltaY -is [bool] -or
            $event.mouseButtonFlags -is [bool] -or
            $event.mouseButtonData -is [bool]) {
            throw "Raw Input evidence contains a non-strict field type."
        }
    }

    $isExpectedKeyboard = {
        param($Event)
        $instanceId = ConvertTo-ComoteFinalValidationInstanceId `
            -Value ([string]$Event.normalizedInstanceId)
        return [string]$Event.kind -ceq "Keyboard" -and
            $expectedKeyboardIds -contains $instanceId
    }
    $isExpectedMouse = {
        param($Event)
        $instanceId = ConvertTo-ComoteFinalValidationInstanceId `
            -Value ([string]$Event.normalizedInstanceId)
        return [string]$Event.kind -ceq "Mouse" -and
            $expectedMouseIds -contains $instanceId
    }
    $hasKeyboardPair = {
        param(
            [string]$Stage,
            [int]$VirtualKey,
            [int]$MakeCode,
            [bool]$UseMakeCode
        )
        $matches = @(
            $events |
                Where-Object {
                    (& $isExpectedKeyboard $_) -and
                    [string]$_.stage -ceq $Stage -and
                    (($UseMakeCode -and [int]$_.makeCode -eq $MakeCode) -or
                     (-not $UseMakeCode -and
                        [int]$_.virtualKey -eq $VirtualKey))
                }
        )
        $down = @(
            $matches |
                Where-Object {
                    $_.keyBreak -is [bool] -and -not $_.keyBreak
                }
        ).Count
        $up = @(
            $matches |
                Where-Object {
                    $_.keyBreak -is [bool] -and $_.keyBreak
                }
        ).Count
        return $down -ge 1 -and $up -ge 1
    }

    if (-not (& $hasKeyboardPair "broker-keyboard-f24" 0x87 0 $false) -or
        -not (& $hasKeyboardPair "host-backend-keyboard-f12" 0x7B 0 $false) -or
        -not (& $hasKeyboardPair "host-backend-left-control" 0 0x1D $true)) {
        throw "Expected keyboard make/break evidence is missing."
    }
    foreach ($virtualKey in @(0x7C, 0x7D, 0x7E, 0x7F, 0x80, 0x81)) {
        if (-not (& $hasKeyboardPair `
                "broker-keyboard-6kro" `
                $virtualKey `
                0 `
                $false)) {
            throw "The six-key rollover evidence is incomplete."
        }
    }

    $relative = @(
        $events |
            Where-Object {
                (& $isExpectedMouse $_) -and
                [string]$_.stage -ceq "broker-relative-mouse" -and
                $_.mouseAbsolute -is [bool] -and
                -not $_.mouseAbsolute -and
                [int]$_.deltaX -eq 11 -and
                [int]$_.deltaY -eq 7
            }
    )
    $directAbsolute = @(
        $events |
            Where-Object {
                (& $isExpectedMouse $_) -and
                [string]$_.stage -ceq "broker-absolute-mouse" -and
                $_.mouseAbsolute -is [bool] -and
                $_.mouseAbsolute
            }
    )
    $hostAbsolute = @(
        $events |
            Where-Object {
                (& $isExpectedMouse $_) -and
                [string]$_.stage -ceq "host-backend-absolute-mouse" -and
                $_.mouseAbsolute -is [bool] -and
                $_.mouseAbsolute
            }
    )
    if ($relative.Count -lt 1 -or
        $directAbsolute.Count -lt 1 -or
        $hostAbsolute.Count -lt 1) {
        throw "Expected relative or absolute mouse evidence is missing."
    }

    $getMouseFlags = {
        param([string]$Stage)
        $combined = 0
        foreach ($event in @(
            $events |
                Where-Object {
                    (& $isExpectedMouse $_) -and
                    [string]$_.stage -ceq $Stage
                })) {
            $combined = $combined -bor [int]$event.mouseButtonFlags
        }
        return $combined
    }
    $directButtons = & $getMouseFlags "broker-mouse-five-buttons"
    if (($directButtons -band 0x0155) -ne 0x0155 -or
        ($directButtons -band 0x02AA) -ne 0x02AA) {
        throw "The five-button mouse evidence is incomplete."
    }
    $hostX2 = & $getMouseFlags "host-backend-x2-button"
    if (($hostX2 -band 0x0100) -ne 0x0100 -or
        ($hostX2 -band 0x0200) -ne 0x0200) {
        throw "The Host-path X2 mouse-button evidence is incomplete."
    }

    $getWheelTotal = {
        param(
            [string]$Stage,
            [int]$Flag
        )
        return [int](
            @(
                $events |
                    Where-Object {
                        (& $isExpectedMouse $_) -and
                        [string]$_.stage -ceq $Stage -and
                        ([int]$_.mouseButtonFlags -band $Flag) -ne 0
                    } |
                    ForEach-Object { [int]$_.mouseButtonData }
            ) |
            Measure-Object -Sum
        ).Sum
    }
    if ((& $getWheelTotal "broker-mouse-wheels" 0x0400) -ne 120 -or
        (& $getWheelTotal "broker-mouse-wheels" 0x0800) -ne -120 -or
        (& $getWheelTotal "host-backend-vertical-wheel" 0x0400) -ne 240 -or
        (& $getWheelTotal "host-backend-horizontal-wheel" 0x0800) -ne -240) {
        throw "Vertical or horizontal wheel evidence is incomplete."
    }

    $startedUtc = [DateTime]::Parse(
        [string]$Report.startedUtc,
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::RoundtripKind
    ).ToUniversalTime()
    $completedUtc = [DateTime]::Parse(
        [string]$Report.completedUtc,
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::RoundtripKind
    ).ToUniversalTime()
    if ($completedUtc -lt $startedUtc -or
        $completedUtc -gt [DateTime]::UtcNow.AddMinutes(1)) {
        throw "The E2E evidence timestamps are invalid."
    }

    return [PSCustomObject]@{
        TestCount = $tests.Count
        RawInputEventCount = $events.Count
    }
}
function Write-ComoteFinalValidationJson {
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$LiteralPath,

        [Parameter(Mandatory)]
        [AllowNull()]
        $InputObject
    )

    $fullPath = [IO.Path]::GetFullPath($LiteralPath)
    $directory = [IO.Path]::GetDirectoryName($fullPath)
    if ([string]::IsNullOrWhiteSpace($directory)) {
        throw "JSON output requires a parent directory."
    }
    [IO.Directory]::CreateDirectory($directory) | Out-Null

    $temporaryPath = Join-Path $directory (
        ".{0}.{1}.tmp" -f
        [IO.Path]::GetFileName($fullPath),
        [Guid]::NewGuid().ToString("N")
    )
    try {
        $json = ($InputObject | ConvertTo-Json -Depth 12) +
            [Environment]::NewLine
        [IO.File]::WriteAllText(
            $temporaryPath,
            $json,
            (New-Object Text.UTF8Encoding($false))
        )
        if (Test-Path -LiteralPath $fullPath -PathType Leaf) {
            [IO.File]::Replace(
                $temporaryPath,
                $fullPath,
                $null,
                $true
            )
        }
        else {
            [IO.File]::Move($temporaryPath, $fullPath)
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }
}

function Get-ComoteFinalValidationPaths {
    $driverRoot = Split-Path -Parent $PSScriptRoot
    $repositoryRoot = Split-Path -Parent $driverRoot
    return [PSCustomObject]@{
        DriverRoot = $driverRoot
        RepositoryRoot = $repositoryRoot
        Phase2Root = Join-Path $driverRoot "ComoteVirtualHidPhase2"
        E2EProject = Join-Path `
            $PSScriptRoot `
            "VirtualHidE2E\Comote.VirtualHidE2E.csproj"
    }
}