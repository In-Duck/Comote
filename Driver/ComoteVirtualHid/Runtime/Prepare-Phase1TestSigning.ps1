#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [switch]$AcknowledgeDisposableVm,

    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$SnapshotName,

    [ValidatePattern("^19045$")]
    [string]$RequiredBuildNumber = "19045"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot "Phase1Runtime.Common.ps1")

$environment = Assert-ComotePhase1RuntimeEnvironment `
    -AcknowledgeDisposableVm:$AcknowledgeDisposableVm `
    -RequiredBuildNumber $RequiredBuildNumber
$prerequisites = Assert-ComotePhase1SigningPrerequisites
Assert-ComotePhase1NoInstalledDevice

if (Get-ComotePhase1TestSigningState) {
    throw "TESTSIGNING is already enabled; restore the clean snapshot."
}

$preflightPath = Join-Path $PSScriptRoot "Invoke-Phase1RuntimePreflight.ps1"
& $preflightPath `
    -AcknowledgeDisposableVm:$AcknowledgeDisposableVm `
    -SnapshotName $SnapshotName `
    -RequiredBuildNumber $RequiredBuildNumber

$projectRoot = Split-Path -Parent $PSScriptRoot
$artifactsRoot = Join-Path $projectRoot "artifacts"
$unsignedPackagePath = Join-Path $artifactsRoot "phase1-unsigned"
$signedPackagePath = Join-Path $artifactsRoot "phase1-test-signed"
$stateDirectory = Join-Path $artifactsRoot "phase1-runtime-state"
$receiptPath = Join-Path $stateDirectory "test-signing-preparation.json"

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
    throw "A Phase 1 signing receipt already exists; restore the clean snapshot."
}

$programFilesX86 = [Environment]::GetFolderPath(
    [Environment+SpecialFolder]::ProgramFilesX86
)
$kitRoot = Join-Path $programFilesX86 "Windows Kits\10"
$signTool = Find-ComotePhase1Tool `
    -Root (Join-Path $kitRoot "bin") `
    -Name "signtool.exe"
$inf2Cat = Find-ComotePhase1Tool `
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

$certificateSubjectPrefix = "CN=Comote Phase 1 VM Test Signing"
$existingCertificates = @(
    Get-ChildItem Cert:\LocalMachine\My -ErrorAction SilentlyContinue |
        Where-Object {
            [string]$_.Subject -like "$certificateSubjectPrefix*"
        }
)
if ($existingCertificates.Count -gt 0) {
    throw "A Comote Phase 1 test certificate already exists; restore the clean snapshot."
}

$certificate = $null
$signedPackageCreated = $false
try {
    New-Item -ItemType Directory -Path $signedPackagePath | Out-Null
    $signedPackageCreated = $true
    New-Item -ItemType Directory -Path $stateDirectory -Force | Out-Null

    foreach ($fileName in @(
        "ComoteVirtualHid.inf",
        "ComoteVirtualHid.sys"
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
        -FriendlyName "Comote Phase 1 VM Test Signing" `
        -CertStoreLocation "Cert:\LocalMachine\My" `
        -HashAlgorithm SHA256 `
        -KeyAlgorithm RSA `
        -KeyLength 3072 `
        -KeyExportPolicy NonExportable `
        -NotBefore (Get-Date).AddMinutes(-5) `
        -NotAfter (Get-Date).AddDays(14)
    if ($null -eq $certificate -or -not $certificate.HasPrivateKey) {
        throw "The Phase 1 test certificate was not created with a private key."
    }
    if (-not (Test-ComotePhase1CodeSigningEku `
            -Certificate $certificate)) {
        throw "The Phase 1 test certificate is missing the code-signing EKU."
    }

    $certificatePath = Join-Path $signedPackagePath "ComotePhase1Test.cer"
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
    $sysPath = Join-Path $signedPackagePath "ComoteVirtualHid.sys"
    $infPath = Join-Path $signedPackagePath "ComoteVirtualHid.inf"
    & $signTool sign /v /fd SHA256 /sha1 $thumbprint /sm $sysPath
    if ($LASTEXITCODE -ne 0) {
        throw "SignTool failed to embed the test signature in the driver."
    }

    & $inf2Cat `
        "/driver:$signedPackagePath" `
        "/os:10_X64" `
        /uselocaltime
    if ($LASTEXITCODE -ne 0) {
        throw "Inf2Cat failed to regenerate the catalog after driver signing."
    }

    $catalog = Get-ChildItem `
        -LiteralPath $signedPackagePath `
        -Filter "ComoteVirtualHid.cat" `
        -File |
        Select-Object -First 1
    if ($null -eq $catalog) {
        throw "Inf2Cat did not create ComoteVirtualHid.cat."
    }
    $catalogPath = $catalog.FullName
    & $signTool sign /v /fd SHA256 /sha1 $thumbprint /sm $catalogPath
    if ($LASTEXITCODE -ne 0) {
        throw "SignTool failed to sign the driver catalog."
    }

    & $signTool verify /v /pa $sysPath
    if ($LASTEXITCODE -ne 0) {
        throw "Authenticode verification failed for ComoteVirtualHid.sys."
    }
    & $signTool verify /v /pa $catalogPath
    if ($LASTEXITCODE -ne 0) {
        throw "Authenticode verification failed for ComoteVirtualHid.cat."
    }
    & $signTool verify /v /pa /c $catalogPath $infPath
    if ($LASTEXITCODE -ne 0) {
        throw "Catalog membership verification failed for ComoteVirtualHid.inf."
    }

    foreach ($signedFile in @($sysPath, $catalogPath)) {
        $signature = Get-AuthenticodeSignature -LiteralPath $signedFile
        if ($signature.Status -ne
            [System.Management.Automation.SignatureStatus]::Valid -or
            $signature.SignerCertificate.Thumbprint -ne $thumbprint) {
            throw "Authenticode verification failed: $signedFile"
        }
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
        testSigningInitiallyEnabled = $false
        testSigningChangedByComote = $false
        certificateSubject = $certificate.Subject
        certificateThumbprint = $thumbprint
        certificateNotAfterUtc = $certificate.NotAfter.ToUniversalTime().ToString("o")
        signedPackagePath = $signedPackagePath
        signTool = $signTool
        inf2Cat = $inf2Cat
        files = $hashes
    }
    $receipt | ConvertTo-Json -Depth 6 |
        Set-Content -LiteralPath $receiptPath -Encoding UTF8
}
catch {
    $preparationError = $_
    $cleanupErrors = @()
    if ($null -ne $certificate) {
        try {
            Remove-ComotePhase1TestCertificate `
                -Thumbprint $certificate.Thumbprint.ToUpperInvariant() `
                -Subject ([string]$certificate.Subject)
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
    if ($null -ne $certificate) {
        $remainingCertificateCopies = @(
            Get-ComotePhase1CertificateCopies `
                -Thumbprint $certificate.Thumbprint.ToUpperInvariant()
        )
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
Write-Host "Phase 1 test-signed package is ready." -ForegroundColor Green
Write-Host "No device was created and no driver was installed."
Write-Host "TESTSIGNING is still off."
Write-Host "Receipt: $receiptPath"
