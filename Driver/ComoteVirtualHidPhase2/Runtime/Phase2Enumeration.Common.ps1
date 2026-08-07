#Requires -Version 5.1

Set-StrictMode -Version Latest

function Get-ComotePhase2DriverPackages {
    return @(
        Get-WindowsDriver -Online -ErrorAction Stop |
            Where-Object {
                [string]$_.ProviderName -eq "Comote" -and
                [string]$_.ClassName -eq "System" -and
                [IO.Path]::GetFileName(
                    [string]$_.OriginalFileName
                ) -eq "ComoteVirtualHidPhase2.inf"
            }
    )
}

function Assert-ComotePhase2DriverPackageIdentity {
    param(
        [Parameter(Mandatory)]
        [PSObject]$Package,

        [string]$ExpectedPublishedInfName
    )

    $publishedInfName = ([string]$Package.Driver).ToLowerInvariant()
    if ($publishedInfName -notmatch "^oem\d+\.inf$" -or
        [string]$Package.ProviderName -ne "Comote" -or
        [string]$Package.ClassName -ne "System" -or
        [IO.Path]::GetFileName(
            [string]$Package.OriginalFileName
        ) -ne "ComoteVirtualHidPhase2.inf" -or
        [version]$Package.Version -ne [version]"0.2.0.0") {
        throw "A Driver Store package did not match the exact Phase 2 identity."
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedPublishedInfName) -and
        $publishedInfName -ne $ExpectedPublishedInfName.ToLowerInvariant()) {
        throw "The Driver Store package did not match the recorded published INF."
    }

    return $publishedInfName
}
function Get-ComotePhase2RootDevices {
    return @(
        Get-CimInstance `
            -ClassName Win32_PnPEntity `
            -ErrorAction Stop |
            Where-Object {
                @($_.HardwareID) -contains
                    "ROOT\COMOTEVIRTUALHID_PHASE2"
            }
    )
}

function Test-ComotePhase2DeviceDescendant {
    param(
        [Parameter(Mandatory)]
        [string]$InstanceId,

        [Parameter(Mandatory)]
        [string]$AncestorInstanceId
    )

    $current = $InstanceId
    for ($depth = 0; $depth -lt 10; $depth++) {
        $parentProperties = @(
            Get-PnpDeviceProperty `
                -InstanceId $current `
                -KeyName "DEVPKEY_Device_Parent" `
                -ErrorAction Stop
        )
        if ($parentProperties.Count -ne 1) {
            return $false
        }
        $dataProperty =
            $parentProperties[0].PSObject.Properties["Data"]
        if ($null -eq $dataProperty) {
            return $false
        }
        $parent = [string]$dataProperty.Value
        if ([string]::IsNullOrWhiteSpace($parent)) {
            return $false
        }
        if ($parent.Equals(
                $AncestorInstanceId,
                [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
        if ($parent.Equals(
                $current,
                [StringComparison]::OrdinalIgnoreCase)) {
            return $false
        }
        $current = $parent
    }

    return $false
}

function Get-ComotePhase2InputChildren {
    param(
        [Parameter(Mandatory)]
        [string]$RootInstanceId
    )

    $keyboards = @(
        Get-PnpDevice `
            -PresentOnly `
            -Class Keyboard `
            -ErrorAction Stop |
            Where-Object {
                Test-ComotePhase2DeviceDescendant `
                    -InstanceId ([string]$_.InstanceId) `
                    -AncestorInstanceId $RootInstanceId
            }
    )
    $mice = @(
        Get-PnpDevice `
            -PresentOnly `
            -Class Mouse `
            -ErrorAction Stop |
            Where-Object {
                Test-ComotePhase2DeviceDescendant `
                    -InstanceId ([string]$_.InstanceId) `
                    -AncestorInstanceId $RootInstanceId
            }
    )

    return [PSCustomObject]@{
        Keyboards = $keyboards
        Mice = $mice
    }
}

function Assert-ComotePhase2InstalledState {
    param(
        [Parameter(Mandatory)]
        [PSObject]$InstallReceipt
    )

    if ([int]$InstallReceipt.schemaVersion -ne 1 -or
        [string]$InstallReceipt.status -ne "installed-enumerated" -or
        [string]$InstallReceipt.serviceName -ne
            "ComoteVirtualHidPhase2") {
        throw "The Phase 2 installation receipt is not active."
    }

    $publishedInfName =
        ([string]$InstallReceipt.publishedInfName).ToLowerInvariant()
    $rootInstanceId = [string]$InstallReceipt.rootDeviceInstanceId
    if ($publishedInfName -notmatch "^oem\d+\.inf$" -or
        $rootInstanceId -notlike "ROOT\*") {
        throw "The Phase 2 installation receipt contains an invalid target."
    }

    $packages = @(Get-ComotePhase2DriverPackages)
    if ($packages.Count -ne 1) {
        throw "The installed Phase 2 package is not unique."
    }
    [void](Assert-ComotePhase2DriverPackageIdentity `
        -Package $packages[0] `
        -ExpectedPublishedInfName $publishedInfName)
    $rootDevices = @(Get-ComotePhase2RootDevices)
    if ($rootDevices.Count -ne 1 -or
        -not ([string]$rootDevices[0].PNPDeviceID).Equals(
            $rootInstanceId,
            [StringComparison]::OrdinalIgnoreCase) -or
        [int]$rootDevices[0].ConfigManagerErrorCode -ne 0 -or
        [string]$rootDevices[0].Service -ne
            "ComoteVirtualHidPhase2") {
        throw "The installed Phase 2 root device is not healthy."
    }

    $driverService = Get-CimInstance `
        -ClassName Win32_SystemDriver `
        -Filter "Name='ComoteVirtualHidPhase2'" `
        -ErrorAction Stop
    if ($null -eq $driverService -or
        [string]$driverService.State -ne "Running" -or
        [string]$driverService.PathName -notmatch
            '(?i)\\DriverStore\\FileRepository\\comotevirtualhidphase2\.inf_amd64_[0-9a-f]+\\ComoteVirtualHidPhase2\.sys$') {
        throw "The ComoteVirtualHidPhase2 service is not running."
    }

    $children = Get-ComotePhase2InputChildren `
        -RootInstanceId $rootInstanceId
    if ($children.Keyboards.Count -ne 1 -or
        $children.Mice.Count -ne 2) {
        throw ("The exact Phase 2 VHF keyboard, relative mouse, and " +
            "absolute mouse set did not enumerate.")
    }
    $recordedKeyboardIds = @($InstallReceipt.keyboardInstanceIds)
    $recordedMouseIds = @($InstallReceipt.mouseInstanceIds)
    $actualMouseIds = @(
        $children.Mice |
            ForEach-Object {
                ([string]$_.InstanceId).ToUpperInvariant()
            } |
            Sort-Object
    )
    $expectedMouseIds = @(
        $recordedMouseIds |
            ForEach-Object {
                ([string]$_).ToUpperInvariant()
            } |
            Sort-Object
    )
    if ($recordedKeyboardIds.Count -ne 1 -or
        $recordedMouseIds.Count -ne 2 -or
        -not ([string]$children.Keyboards[0].InstanceId).Equals(
            [string]$recordedKeyboardIds[0],
            [StringComparison]::OrdinalIgnoreCase) -or
        @(Compare-Object `
            -ReferenceObject $actualMouseIds `
            -DifferenceObject $expectedMouseIds).Count -ne 0) {
        throw "The Phase 2 VHF children do not match the installation receipt."
    }
    foreach ($device in @($children.Keyboards + $children.Mice)) {
        if ([string]$device.Status -ne "OK") {
            throw "A Phase 2 VHF child is not healthy."
        }
    }

    return [PSCustomObject]@{
        PublishedInfName = $publishedInfName
        RootInstanceId = $rootInstanceId
        DriverService = $driverService
        Keyboards = $children.Keyboards
        Mice = $children.Mice
    }
}

function Remove-ComotePhase2OrphanedService {
    $serviceResult = Invoke-ComotePhase2NativeCommand `
        -FilePath "sc.exe" `
        -Arguments @("query", "ComoteVirtualHidPhase2")
    $serviceOutput = $serviceResult.Output
    $serviceExitCode = $serviceResult.ExitCode
    if ($serviceExitCode -eq 1060) {
        return
    }
    if ($serviceExitCode -ne 0) {
        throw ("Unable to query the Phase 2 service " +
            "(exit code {0}): {1}" -f
            $serviceExitCode,
            $serviceOutput)
    }

    $driverService = Get-CimInstance `
        -ClassName Win32_SystemDriver `
        -Filter "Name='ComoteVirtualHidPhase2'" `
        -ErrorAction Stop
    if ($null -ne $driverService -and
        [string]$driverService.State -ne "Stopped") {
        throw "Refusing to delete a running Phase 2 driver service."
    }
    $serviceKey =
        "HKLM:\SYSTEM\CurrentControlSet\Services\ComoteVirtualHidPhase2"
    if (-not (Test-Path -LiteralPath $serviceKey)) {
        throw "The Phase 2 service exists without its registry key."
    }
    $configuration = Get-ItemProperty -LiteralPath $serviceKey
    if ([int]$configuration.Start -ne 4 -or
        [string]$configuration.ImagePath -notmatch
            '(?i)^\\SystemRoot\\System32\\DriverStore\\FileRepository\\comotevirtualhidphase2\.inf_amd64_[0-9a-f]+\\ComoteVirtualHidPhase2\.sys$') {
        throw "The orphaned service configuration is not safe to delete."
    }

    $deleteResult = Invoke-ComotePhase2NativeCommand `
        -FilePath "sc.exe" `
        -Arguments @("delete", "ComoteVirtualHidPhase2")
    $deleteOutput = $deleteResult.Output
    if ($deleteResult.ExitCode -ne 0) {
        throw "Unable to delete the stopped Phase 2 service: $deleteOutput"
    }
    for ($attempt = 0; $attempt -lt 15; $attempt++) {
        $queryResult = Invoke-ComotePhase2NativeCommand `
            -FilePath "sc.exe" `
            -Arguments @("query", "ComoteVirtualHidPhase2")
        if ($queryResult.ExitCode -eq 1060) {
            return
        }
        Start-Sleep -Seconds 1
    }

    throw "The stopped Phase 2 service remained after deletion."
}
function Assert-ComotePhase2EnumerationTransaction {
    param(
        [Parameter(Mandatory)]
        [PSObject]$Transaction,

        [Parameter(Mandatory)]
        [ValidateSet("install", "remove")]
        [string]$ExpectedOperation,

        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$RecoverySnapshotName
    )

    foreach ($requiredProperty in @(
        "schemaVersion",
        "operationId",
        "operation",
        "status",
        "recoverySnapshotName",
        "hardwareId",
        "serviceName"
    )) {
        if ($null -eq $Transaction.PSObject.Properties[$requiredProperty]) {
            throw "The enumeration transaction is missing: $requiredProperty"
        }
    }
    if ([int]$Transaction.schemaVersion -ne 1 -or
        [string]$Transaction.operationId -notmatch
            "^[0-9a-f]{32}$" -or
        [string]$Transaction.operation -ne $ExpectedOperation -or
        [string]$Transaction.recoverySnapshotName -ne
            $RecoverySnapshotName -or
        [string]$Transaction.hardwareId -ne
            "ROOT\COMOTEVIRTUALHID_PHASE2" -or
        [string]$Transaction.serviceName -ne
            "ComoteVirtualHidPhase2") {
        throw "The enumeration transaction identity is invalid."
    }
}

function Invoke-ComotePhase2ExactEnumerationCleanup {
    param(
        [Parameter(Mandatory)]
        [PSObject]$Transaction,

        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$TransactionPath,

        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$DevGenPath
    )

    $operation = [string]$Transaction.operation
    if ($operation -notin @("install", "remove")) {
        throw "The exact cleanup transaction operation is invalid."
    }
    Assert-ComotePhase2EnumerationTransaction `
        -Transaction $Transaction `
        -ExpectedOperation $operation `
        -RecoverySnapshotName ([string]$Transaction.recoverySnapshotName)

    $rootDevices = @(Get-ComotePhase2RootDevices)
    if ($rootDevices.Count -gt 1) {
        throw "More than one Phase 2 root device exists; exact cleanup is unsafe."
    }
    if ($rootDevices.Count -eq 1) {
        $actualRootInstanceId = [string]$rootDevices[0].PNPDeviceID
        $recordedRootProperty =
            $Transaction.PSObject.Properties["rootDeviceInstanceId"]
        $recordedRootInstanceId = if ($null -eq $recordedRootProperty) {
            ""
        }
        else {
            [string]$recordedRootProperty.Value
        }
        if (-not [string]::IsNullOrWhiteSpace($recordedRootInstanceId) -and
            -not $actualRootInstanceId.Equals(
                $recordedRootInstanceId,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "The Phase 2 root device does not match the transaction."
        }
        Set-ComotePhase2NoteProperty `
            -InputObject $Transaction `
            -Name "rootDeviceInstanceId" `
            -Value $actualRootInstanceId
    }

    $packages = @(Get-ComotePhase2DriverPackages)
    if ($packages.Count -gt 1) {
        throw "More than one Phase 2 package exists; exact cleanup is unsafe."
    }
    if ($packages.Count -eq 1) {
        $recordedInfProperty =
            $Transaction.PSObject.Properties["publishedInfName"]
        $recordedInfName = if ($null -eq $recordedInfProperty) {
            ""
        }
        else {
            [string]$recordedInfProperty.Value
        }
        $actualInfName = Assert-ComotePhase2DriverPackageIdentity `
            -Package $packages[0] `
            -ExpectedPublishedInfName $recordedInfName
        Set-ComotePhase2NoteProperty `
            -InputObject $Transaction `
            -Name "publishedInfName" `
            -Value $actualInfName
    }

    Set-ComotePhase2NoteProperty `
        -InputObject $Transaction `
        -Name "status" `
        -Value "cleanup-targets-validated"
    Set-ComotePhase2NoteProperty `
        -InputObject $Transaction `
        -Name "lastUpdatedUtc" `
        -Value ([DateTime]::UtcNow.ToString("o"))
    Write-ComotePhase2JsonAtomically `
        -LiteralPath $TransactionPath `
        -InputObject $Transaction

    $rootDevices = @(Get-ComotePhase2RootDevices)
    if ($rootDevices.Count -eq 1) {
        $removeResult = Invoke-ComotePhase2NativeCommand `
            -FilePath $DevGenPath `
            -Arguments @(
                "/remove",
                ([string]$rootDevices[0].PNPDeviceID),
                "/subtree"
            )
        if ($removeResult.ExitCode -ne 0) {
            throw "DevGen exact-target cleanup failed: $($removeResult.Output)"
        }
        for ($attempt = 0; $attempt -lt 20; $attempt++) {
            if (@(Get-ComotePhase2RootDevices).Count -eq 0) {
                break
            }
            Start-Sleep -Seconds 1
        }
        if (@(Get-ComotePhase2RootDevices).Count -ne 0) {
            throw "The exact Phase 2 root remained after cleanup."
        }
    }

    $packages = @(Get-ComotePhase2DriverPackages)
    if ($packages.Count -eq 1) {
        $publishedInfName = Assert-ComotePhase2DriverPackageIdentity `
            -Package $packages[0] `
            -ExpectedPublishedInfName ([string]$Transaction.publishedInfName)
        $deleteResult = Invoke-ComotePhase2NativeCommand `
            -FilePath "pnputil.exe" `
            -Arguments @(
                "/delete-driver",
                $publishedInfName,
                "/uninstall"
            )
        if ($deleteResult.ExitCode -ne 0) {
            throw "PnPUtil exact-target cleanup failed: $($deleteResult.Output)"
        }
        for ($attempt = 0; $attempt -lt 20; $attempt++) {
            if (@(Get-ComotePhase2DriverPackages).Count -eq 0) {
                break
            }
            Start-Sleep -Seconds 1
        }
        if (@(Get-ComotePhase2DriverPackages).Count -ne 0) {
            throw "The exact Phase 2 package remained after cleanup."
        }
    }

    if (@(Get-ComotePhase2RootDevices).Count -ne 0 -or
        @(Get-ComotePhase2DriverPackages).Count -ne 0) {
        throw "Phase 2 exact-target cleanup inventory did not become empty."
    }
    Remove-ComotePhase2OrphanedService

    $serviceAudit = Invoke-ComotePhase2NativeCommand `
        -FilePath "sc.exe" `
        -Arguments @("query", "ComoteVirtualHidPhase2")
    if ($serviceAudit.ExitCode -ne 1060) {
        throw ("The Phase 2 service remained after exact cleanup " +
            "(exit code {0}): {1}" -f
            $serviceAudit.ExitCode,
            $serviceAudit.Output)
    }

    $presentPhase2Devices = @(
        Get-PnpDevice -PresentOnly -ErrorAction Stop |
            Where-Object {
                [string]$_.InstanceId -like
                    "ROOT\COMOTEVIRTUALHID_PHASE2*" -or
                [string]$_.InstanceId -like
                    "ROOT\DEVGEN\COMOTE_PHASE2*"
            }
    )
    if ($presentPhase2Devices.Count -ne 0) {
        throw "A Phase 2 PnP device remained after exact cleanup."
    }

    $inputProperty = $Transaction.PSObject.Properties["inputInstanceIds"]
    if ($null -ne $inputProperty) {
        $remainingIds = @(
            Get-PnpDevice -PresentOnly -ErrorAction Stop |
                ForEach-Object { [string]$_.InstanceId }
        )
        foreach ($inputId in @($inputProperty.Value)) {
            if ($remainingIds -contains [string]$inputId) {
                throw "A recorded Phase 2 VHF child remained after cleanup."
            }
        }
    }
}
