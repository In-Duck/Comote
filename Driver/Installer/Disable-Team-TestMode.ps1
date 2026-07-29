#Requires -RunAsAdministrator
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$base = $PSScriptRoot
$installer = Join-Path $base "ComoteVirtualHidInstaller.exe"
if (Test-Path -LiteralPath $installer) {
    $process = Start-Process -FilePath $installer -ArgumentList "uninstall" `
        -WorkingDirectory $base -Wait -PassThru
    if ($process.ExitCode -notin @(0, 2)) {
        throw "드라이버 제거기가 종료 코드 $($process.ExitCode)로 실패했습니다."
    }
}

$certificatePath = Join-Path $base "ComoteTeamTest.cer"
if (Test-Path -LiteralPath $certificatePath) {
    $certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
        $certificatePath
    )
    foreach ($store in @("Root", "TrustedPublisher")) {
        Get-ChildItem "Cert:\LocalMachine\$store" |
            Where-Object Thumbprint -eq $certificate.Thumbprint |
            Remove-Item -Force
    }
}

& bcdedit.exe /set testsigning off
if ($LASTEXITCODE -ne 0) {
    throw "Windows 테스트 모드를 끄지 못했습니다. 관리자 권한과 Secure Boot 상태를 확인하세요."
}

Write-Host "Comote Virtual HID를 제거하고 테스트 모드를 껐습니다." -ForegroundColor Green
Write-Host "원래 Windows 보안 상태로 돌아가려면 지금 재부팅하세요." -ForegroundColor Yellow
exit 3010
