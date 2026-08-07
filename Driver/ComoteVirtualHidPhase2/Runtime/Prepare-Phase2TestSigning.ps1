#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [switch]$AcknowledgeDisposableVm,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$SnapshotName,

    [switch]$ValidateOnly,

    [ValidatePattern("^19045$")]
    [string]$RequiredBuildNumber = "19045"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot "Phase2Runtime.Common.ps1")
. (Join-Path $PSScriptRoot "Phase2Enumeration.Common.ps1")

$runtimeLock = Enter-ComotePhase2RuntimeLock
try {

$environment = Assert-ComotePhase2RuntimeEnvironment `
    -AcknowledgeDisposableVm:$AcknowledgeDisposableVm `
    -RequiredBuildNumber $RequiredBuildNumber
$prerequisites = Assert-ComotePhase2SigningPrerequisites
Assert-ComotePhase2NoInstalledDevice
if (@(Get-ComotePhase2DriverPackages).Count -ne 0) {
    throw "A Phase 2 Driver Store package already exists."
}

if (-not (Get-ComotePhase2TestSigningState)) {
    throw "TESTSIGNING must already be enabled inside the Phase 2 VM."
}
$codeIntegrity = Get-ComotePhase2ActiveCodeIntegrityState
if (-not $codeIntegrity.TestSigningActive) {
    throw "The active Windows boot session is not running in test-signing mode."
}
if ($codeIntegrity.HvciKernelModeActive) {
    throw "HVCI kernel-mode enforcement must be inactive in the Phase 2 VM."
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$artifactsRoot = Join-Path $projectRoot "artifacts"
$unsignedPackagePath = Join-Path $artifactsRoot "phase2-unsigned"
$signedPackagePath = Join-Path $artifactsRoot "phase2-test-signed"
$stateDirectory = Join-Path $artifactsRoot "phase2-runtime-state"
$receiptPath = Join-Path $stateDirectory "test-signing-preparation.json"

$manifestPath = Join-Path $unsignedPackagePath "SHA256.json"
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "The Phase 2 unsigned package manifest is missing."
}
$manifestDocument = Get-Content -LiteralPath $manifestPath -Raw |
    ConvertFrom-Json
if ($manifestDocument -is [Array]) {
    $manifestEntries = $manifestDocument
} else {
    $manifestEntries = @($manifestDocument)
}
$expectedUnsignedNames = @(
    "comotevirtualhidphase2.cat",
    "ComoteVirtualHidPhase2.inf",
    "ComoteVirtualHidPhase2.sys",
    "ComoteVirtualHidProbe.exe"
)
if ($manifestEntries.Count -ne $expectedUnsignedNames.Count) {
    throw "The Phase 2 unsigned manifest has an unexpected file count."
}
foreach ($expectedName in $expectedUnsignedNames) {
    $entry = @(
        $manifestEntries |
            Where-Object {
                [string]$_.file -eq $expectedName
            }
    )
    if ($entry.Count -ne 1) {
        throw "The Phase 2 unsigned manifest is missing or duplicates: $expectedName"
    }
    $filePath = Join-Path $unsignedPackagePath $expectedName
    if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) {
        throw "Unsigned package file is missing: $filePath"
    }
    $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $filePath).Hash
    if ($actualHash -ne [string]$entry[0].sha256) {
        throw "Unsigned package hash mismatch: $expectedName"
    }
}
$actualUnsignedNames = @(
    Get-ChildItem -LiteralPath $unsignedPackagePath -File |
        Where-Object Name -ne "SHA256.json" |
        Select-Object -ExpandProperty Name
)
if ($actualUnsignedNames.Count -ne $expectedUnsignedNames.Count) {
    throw "The Phase 2 unsigned package contains an unexpected file."
}
foreach ($unsignedBinaryName in @(
    "comotevirtualhidphase2.cat",
    "ComoteVirtualHidPhase2.sys",
    "ComoteVirtualHidProbe.exe"
)) {
    $signature = Get-AuthenticodeSignature `
        -LiteralPath (Join-Path $unsignedPackagePath $unsignedBinaryName)
    if ($signature.Status -ne
        [System.Management.Automation.SignatureStatus]::NotSigned) {
        throw "The input package is not unsigned: $unsignedBinaryName"
    }
}

$normalizedArtifactsRoot = [IO.Path]::GetFullPath($artifactsRoot).TrimEnd(
    [IO.Path]::DirectorySeparatorChar
)
$normalizedSignedPackagePath = [IO.Path]::GetFullPath($signedPackagePath)
if (-not $normalizedSignedPackagePath.StartsWith(
        "$normalizedArtifactsRoot$([IO.Path]::DirectorySeparatorChar)",
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Signed package path escaped the project artifacts directory."
}
if (Test-Path -LiteralPath $signedPackagePath) {
    throw "Signed package directory already exists; restore the clean snapshot."
}
if (Test-Path -LiteralPath $receiptPath) {
    throw "A Phase 2 signing receipt already exists; restore the clean snapshot."
}

$programFilesX86 = [Environment]::GetFolderPath(
    [Environment+SpecialFolder]::ProgramFilesX86
)
$kitRoot = Join-Path $programFilesX86 "Windows Kits\10"
$signTool = Find-ComotePhase2Tool `
    -Root (Join-Path $kitRoot "bin") `
    -Name "signtool.exe"
$inf2Cat = Find-ComotePhase2Tool `
    -Root (Join-Path $kitRoot "bin") `
    -Name "inf2cat.exe" `
    -PreferredArchitectures @("x86", "x64", "amd64")
if (-not $signTool) {
    throw "SignTool.exe was not found in the installed Windows Kit."
}
if (-not $inf2Cat) {
    throw "Inf2Cat.exe was not found in the installed WDK."
}

foreach ($requiredCommand in @(
    "New-SelfSignedCertificate",
    "Export-Certificate",
    "Import-Certificate"
)) {
    if (-not (Get-Command $requiredCommand -ErrorAction SilentlyContinue)) {
        throw "Required PKI command was not found: $requiredCommand"
    }
}

$certificateSubjectPrefix = "CN=Comote Phase 2 VM Test Signing"
$existingCertificates = @(Get-ComotePhase2NamedCertificates)
if ($existingCertificates.Count -gt 0) {
    throw "A Comote Phase 2 test certificate already exists; restore the clean snapshot."
}

if ($ValidateOnly.IsPresent) {
    Write-Host ""
    Write-Host "Phase 2 test-signing validation passed." -ForegroundColor Green
    Write-Host "Unsigned package hashes and signing prerequisites: verified"
    Write-Host "No certificate was created and no file was changed."
    Write-Host "No device was created and no driver was installed."
    return
}

$certificate = $null
$certificateSubject = $null
$signedPackageCreated = $false
try {
    New-Item -ItemType Directory -Path $signedPackagePath | Out-Null
    $signedPackageCreated = $true
    New-Item -ItemType Directory -Path $stateDirectory -Force | Out-Null

    foreach ($fileName in @(
        "ComoteVirtualHidPhase2.inf",
        "ComoteVirtualHidPhase2.sys",
        "ComoteVirtualHidProbe.exe"
    )) {
        Copy-Item `
            -LiteralPath (Join-Path $unsignedPackagePath $fileName) `
            -Destination (Join-Path $signedPackagePath $fileName)
    }

    $certificateSubject = "{0} {1}" -f
        $certificateSubjectPrefix,
        ([Guid]::NewGuid().ToString("N"))
    $certificate = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $certificateSubject `
        -FriendlyName "Comote Phase 2 VM Test Signing" `
        -CertStoreLocation "Cert:\LocalMachine\My" `
        -HashAlgorithm SHA256 `
        -KeyAlgorithm RSA `
        -KeyLength 3072 `
        -KeyExportPolicy NonExportable `
        -NotBefore (Get-Date).AddMinutes(-5) `
        -NotAfter (Get-Date).AddDays(365)
    if ($null -eq $certificate -or -not $certificate.HasPrivateKey) {
        throw "The Phase 2 test certificate was not created with a private key."
    }
    if (-not (Test-ComotePhase2CodeSigningEku `
            -Certificate $certificate)) {
        throw "The Phase 2 test certificate is missing the code-signing EKU."
    }

    $certificatePath = Join-Path $signedPackagePath "ComotePhase2Test.cer"
    Export-Certificate `
        -Cert $certificate `
        -FilePath $certificatePath `
        -Type CERT | Out-Null
    foreach ($store in @(
        "Cert:\LocalMachine\Root",
        "Cert:\LocalMachine\TrustedPublisher"
    )) {
        Import-Certificate `
            -FilePath $certificatePath `
            -CertStoreLocation $store | Out-Null
    }

    $thumbprint = $certificate.Thumbprint.ToUpperInvariant()
    $sysPath = Join-Path $signedPackagePath "ComoteVirtualHidPhase2.sys"
    $infPath = Join-Path $signedPackagePath "ComoteVirtualHidPhase2.inf"
    $signSysResult = Invoke-ComotePhase2NativeCommand `
        -FilePath $signTool `
        -Arguments @(
            "sign", "/v", "/fd", "SHA256",
            "/sha1", $thumbprint, "/sm", $sysPath
        )
    if ($signSysResult.ExitCode -ne 0) {
        throw "SignTool failed to embed the test signature in the driver."
    }

    $catalogGenerationResult = Invoke-ComotePhase2NativeCommand `
        -FilePath $inf2Cat `
        -Arguments @(
            "/driver:$signedPackagePath",
            "/os:10_X64",
            "/uselocaltime"
        )
    if ($catalogGenerationResult.ExitCode -ne 0) {
        throw "Inf2Cat failed to regenerate the catalog after driver signing."
    }

    $catalog = Get-ChildItem `
        -LiteralPath $signedPackagePath `
        -Filter "ComoteVirtualHidPhase2.cat" `
        -File |
        Select-Object -First 1
    if ($null -eq $catalog) {
        throw "Inf2Cat did not create ComoteVirtualHidPhase2.cat."
    }
    $catalogPath = $catalog.FullName
    $signCatalogResult = Invoke-ComotePhase2NativeCommand `
        -FilePath $signTool `
        -Arguments @(
            "sign", "/v", "/fd", "SHA256",
            "/sha1", $thumbprint, "/sm", $catalogPath
        )
    if ($signCatalogResult.ExitCode -ne 0) {
        throw "SignTool failed to sign the driver catalog."
    }

    $verifySysResult = Invoke-ComotePhase2NativeCommand `
        -FilePath $signTool `
        -Arguments @("verify", "/v", "/pa", $sysPath)
    if ($verifySysResult.ExitCode -ne 0) {
        throw "Authenticode verification failed for ComoteVirtualHidPhase2.sys."
    }
    $verifyCatalogResult = Invoke-ComotePhase2NativeCommand `
        -FilePath $signTool `
        -Arguments @("verify", "/v", "/pa", $catalogPath)
    if ($verifyCatalogResult.ExitCode -ne 0) {
        throw "Authenticode verification failed for ComoteVirtualHidPhase2.cat."
    }
    $verifyMembershipResult = Invoke-ComotePhase2NativeCommand `
        -FilePath $signTool `
        -Arguments @(
            "verify", "/v", "/pa", "/c",
            $catalogPath, $infPath
        )
    if ($verifyMembershipResult.ExitCode -ne 0) {
        throw "Catalog membership verification failed for ComoteVirtualHidPhase2.inf."
    }

    foreach ($signedFile in @($sysPath, $catalogPath)) {
        $signature = Get-AuthenticodeSignature -LiteralPath $signedFile
        if ($signature.Status -ne
            [System.Management.Automation.SignatureStatus]::Valid -or
            $signature.SignerCertificate.Thumbprint -ne $thumbprint) {
            throw "Authenticode verification failed: $signedFile"
        }
    }
    $probePath = Join-Path $signedPackagePath "ComoteVirtualHidProbe.exe"
    $probeSignature = Get-AuthenticodeSignature -LiteralPath $probePath
    if ($probeSignature.Status -ne
        [System.Management.Automation.SignatureStatus]::NotSigned) {
        throw "The Phase 2 probe must remain unsigned."
    }

    $hashes = @(
        Get-ChildItem -LiteralPath $signedPackagePath -File |
            Sort-Object Name |
            ForEach-Object {
                [ordered]@{
                    file = $_.Name
                    sha256 = (Get-FileHash `
                        -Algorithm SHA256 `
                        -LiteralPath $_.FullName).Hash
                }
            }
    )
    $currentVersion = Get-ItemProperty `
        -LiteralPath "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion"
    $receipt = [ordered]@{
        schemaVersion = 1
        status = "signed-package-ready"
        createdUtc = [DateTime]::UtcNow.ToString("o")
        snapshotName = $SnapshotName
        manufacturer = [string]$environment.Computer.Manufacturer
        model = [string]$environment.Computer.Model
        osBuildNumber = [string]$environment.OperatingSystem.BuildNumber
        osUbr = [int]$currentVersion.UBR
        secureBootEnabled = $prerequisites.SecureBootEnabled
        bitLockerProtection = $prerequisites.BitLockerProtection
        testSigningInitiallyEnabled = $true
        testSigningChangedByComote = $false
        activeCodeIntegrityOptions = [uint32]$codeIntegrity.Options
        certificateSubject = $certificate.Subject
        certificateThumbprint = $thumbprint
        certificateNotAfterUtc = $certificate.NotAfter.ToUniversalTime().ToString("o")
        signedPackagePath = $signedPackagePath
        signTool = $signTool
        inf2Cat = $inf2Cat
        files = $hashes
    }
    Write-ComotePhase2JsonAtomically `
        -LiteralPath $receiptPath `
        -InputObject $receipt
}
catch {
    $preparationError = $_
    $cleanupErrors = @()
    $cleanupCertificates = @()
    if ($null -ne $certificate) {
        $cleanupCertificates = @($certificate)
    }
    elseif (-not [string]::IsNullOrWhiteSpace($certificateSubject)) {
        try {
            $cleanupCertificates = @(
                Get-ComotePhase2NamedCertificates |
                    Where-Object {
                        [string]$_.Subject -eq $certificateSubject
                    } |
                    Group-Object Thumbprint |
                    ForEach-Object { $_.Group[0] }
            )
        }
        catch {
            $cleanupErrors += $_.Exception.Message
        }
    }
    foreach ($cleanupCertificate in $cleanupCertificates) {
        try {
            Remove-ComotePhase2TestCertificate `
                -Thumbprint $cleanupCertificate.Thumbprint.ToUpperInvariant() `
                -Subject ([string]$cleanupCertificate.Subject)
        }
        catch {
            $cleanupErrors += $_.Exception.Message
        }
    }
    try {
        if (Test-Path -LiteralPath $receiptPath) {
            Remove-Item -LiteralPath $receiptPath -Force
        }
        if ($signedPackageCreated -and
            (Test-Path -LiteralPath $signedPackagePath)) {
            Remove-Item -LiteralPath $signedPackagePath -Recurse -Force
        }
    }
    catch {
        $cleanupErrors += $_.Exception.Message
    }

    $remainingCertificateCopies = @()
    if (-not [string]::IsNullOrWhiteSpace($certificateSubject)) {
        try {
            $remainingCertificateCopies = @(
                Get-ComotePhase2NamedCertificates |
                    Where-Object {
                        [string]$_.Subject -eq $certificateSubject
                    }
            )
        }
        catch {
            $cleanupErrors += $_.Exception.Message
        }
    }
    if ($remainingCertificateCopies.Count -ne 0 -or
        (Test-Path -LiteralPath $receiptPath) -or
        (Test-Path -LiteralPath $signedPackagePath)) {
        $cleanupErrors += "Post-failure cleanup audit did not pass."
    }
    if ($cleanupErrors.Count -ne 0) {
        throw ("Preparation failed: {0} Cleanup also failed: {1}" -f
            $preparationError.Exception.Message,
            ($cleanupErrors -join " | "))
    }
    throw $preparationError
}

Write-Host ""
Write-Host "Phase 2 test-signed package is ready." -ForegroundColor Green
Write-Host "No device was created and no driver was installed."
Write-Host "TESTSIGNING remains active and was not changed."
Write-Host "Receipt: $receiptPath"
}
finally {
    Exit-ComotePhase2RuntimeLock -Mutex $runtimeLock
}
