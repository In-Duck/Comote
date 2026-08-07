#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet("Install", "Uninstall")]
    [string]$Action
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$acceptancePhrase = "I ACCEPT COMOTE TEST-SIGNED VIRTUAL HID PREVIEW"

function Assert-ComoteHelperOrdinaryPath {
    param(
        [Parameter(Mandatory)]
        [string]$LiteralPath,

        [Parameter(Mandatory)]
        [bool]$Directory,

        [Parameter(Mandatory)]
        [string]$Description
    )

    if ($LiteralPath.StartsWith("\\", [StringComparison]::Ordinal) -or
        $LiteralPath.StartsWith("\\?\", [StringComparison]::Ordinal) -or
        $LiteralPath.StartsWith("\\.\", [StringComparison]::Ordinal)) {
        throw "$Description must use a normal fixed local path."
    }
    $fullPath = [IO.Path]::GetFullPath($LiteralPath)
    if (($Directory -and
            -not (Test-Path -LiteralPath $fullPath -PathType Container)) -or
        (-not $Directory -and
            -not (Test-Path -LiteralPath $fullPath -PathType Leaf))) {
        throw "$Description was not found."
    }
    $root = [IO.Path]::GetPathRoot($fullPath)
    $drive = New-Object IO.DriveInfo($root)
    if ($root -cnotmatch '^[A-Za-z]:\\$' -or
        -not $drive.IsReady -or
        $drive.DriveType -ne [IO.DriveType]::Fixed -or
        [string]$drive.DriveFormat -cne "NTFS") {
        throw "$Description must be on a ready fixed NTFS volume."
    }
    $cursor = if ($Directory) {
        $fullPath
    }
    else {
        [IO.Path]::GetDirectoryName($fullPath)
    }
    while (-not [string]::IsNullOrWhiteSpace($cursor)) {
        $item = Get-Item -LiteralPath $cursor -Force -ErrorAction Stop
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$Description has a reparse-point ancestor."
        }
        if ($cursor.TrimEnd('\') -ieq $root.TrimEnd('\')) {
            break
        }
        $cursor = [IO.Path]::GetDirectoryName($cursor.TrimEnd('\'))
    }
    return $fullPath
}

function ConvertTo-ComoteHelperArgument {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Value
    )

    if ($Value.IndexOfAny([char[]]@("`0", "`r", "`n")) -ge 0) {
        throw "A UAC helper argument contains a forbidden control character."
    }
    $builder = New-Object Text.StringBuilder
    [void]$builder.Append('"')
    $backslashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') {
            $backslashes++
            continue
        }
        if ($character -eq '"') {
            [void]$builder.Append(('\' * (($backslashes * 2) + 1)))
            [void]$builder.Append('"')
            $backslashes = 0
            continue
        }
        if ($backslashes -gt 0) {
            [void]$builder.Append(('\' * $backslashes))
            $backslashes = 0
        }
        [void]$builder.Append($character)
    }
    if ($backslashes -gt 0) {
        [void]$builder.Append(('\' * ($backslashes * 2)))
    }
    [void]$builder.Append('"')
    return $builder.ToString()
}

function Get-ComoteHelperLocalInteractiveUser {
    $computer = Get-CimInstance `
        -ClassName Win32_ComputerSystem `
        -ErrorAction Stop
    $account = [string]$computer.UserName
    $prefix = "$env:COMPUTERNAME\"
    if ([string]::IsNullOrWhiteSpace($account) -or
        -not $account.StartsWith(
            $prefix,
            [StringComparison]::OrdinalIgnoreCase) -or
        $account.Substring($prefix.Length) -notmatch '^[^\\/:*?"<>|]{1,64}$' -or
        -not $account.Equals(
            [Security.Principal.WindowsIdentity]::GetCurrent().Name,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Install must be launched by the exact current LOCAL user."
    }
    $sid = (New-Object Security.Principal.NTAccount($account)).Translate(
        [Security.Principal.SecurityIdentifier]
    )
    $userName = $account.Substring($prefix.Length)
    $adsiUser = [ADSI]("WinNT://{0}/{1},user" -f
        $env:COMPUTERNAME,
        $userName)
    $adsiSid = (
        New-Object Security.Principal.SecurityIdentifier(
            [byte[]]$adsiUser.psbase.Properties["objectSid"].Value,
            0
        )
    ).Value
    if ([string]$sid.Value -cne $adsiSid) {
        throw "The current LOCAL interactive user identity is ambiguous."
    }
    return $account
}

$root = Assert-ComoteHelperOrdinaryPath `
    -LiteralPath $PSScriptRoot `
    -Directory $true `
    -Description "Client role root"
$manifestPath = Assert-ComoteHelperOrdinaryPath `
    -LiteralPath (Join-Path $root "release-manifest.json") `
    -Directory $false `
    -Description "Sibling Client release manifest"
$manifestHash = (
    Get-FileHash `
        -Algorithm SHA256 `
        -LiteralPath $manifestPath `
        -ErrorAction Stop
).Hash.ToUpperInvariant()
$scriptName = if ($Action -ceq "Install") {
    "Install-ComoteVirtualHidPreview.ps1"
}
else {
    "Uninstall-ComoteVirtualHidPreview.ps1"
}
$operationScript = Assert-ComoteHelperOrdinaryPath `
    -LiteralPath (Join-Path $root $scriptName) `
    -Directory $false `
    -Description "Client $Action script"
$powerShellPath = Assert-ComoteHelperOrdinaryPath `
    -LiteralPath (Join-Path $PSHOME "powershell.exe") `
    -Directory $false `
    -Description "Windows PowerShell"

$arguments = @(
    "-NoLogo",
    "-NoProfile",
    "-ExecutionPolicy",
    "Bypass",
    "-File",
    (ConvertTo-ComoteHelperArgument -Value $operationScript),
    "-ExpectedReleaseManifestSha256",
    $manifestHash,
    "-AcknowledgeTestSignedPreview"
)
if ($Action -ceq "Install") {
    Write-Host "Type this exact non-secret acceptance phrase:"
    Write-Host $acceptancePhrase -ForegroundColor Yellow
    $typedPhrase = Read-Host "Acceptance"
    if ([string]$typedPhrase -cne $acceptancePhrase) {
        throw "The exact test-signed preview acceptance phrase was not entered."
    }
    $controllerUser = Get-ComoteHelperLocalInteractiveUser
    $arguments += @(
        "-PreviewAcceptancePhrase",
        (ConvertTo-ComoteHelperArgument -Value $typedPhrase),
        "-ControllerUser",
        (ConvertTo-ComoteHelperArgument -Value $controllerUser)
    )
}

$process = Start-Process `
    -FilePath $powerShellPath `
    -Verb RunAs `
    -ArgumentList ($arguments -join " ") `
    -Wait `
    -PassThru
if ($process.ExitCode -ne 0) {
    throw "Elevated Client $Action returned exit code $($process.ExitCode)."
}
