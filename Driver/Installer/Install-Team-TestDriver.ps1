#Requires -RunAsAdministrator
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$base = $PSScriptRoot
$required = @(
    "ComoteTeamTest.cer",
    "ComoteVirtualHid.inf",
    "ComoteVirtualHid.sys",
    "ComoteVirtualHid.cat",
    "ComoteVirtualHidInstaller.exe"
)
foreach ($name in $required) {
    $path = Join-Path $base $name
    if (-not (Test-Path -LiteralPath $path)) {
        throw "필수 파일이 없습니다: $name"
    }
}

try {
    if (Confirm-SecureBootUEFI) {
        throw (
            "Secure Boot가 켜져 있어 테스트 서명 드라이버를 사용할 수 없습니다. " +
            "UEFI/BIOS에서 Secure Boot를 직접 끈 뒤 이 파일을 다시 실행하세요. " +
            "이 스크립트는 Secure Boot 설정을 자동 변경하지 않습니다."
        )
    }
} catch [System.UnauthorizedAccessException] {
    throw "Secure Boot 상태를 확인할 권한이 없습니다. 관리자 권한으로 다시 실행하세요."
} catch {
    if ($_.Exception.Message -notmatch "not supported|지원되지") {
        throw
    }
    # Legacy BIOS systems do not expose Secure Boot state.
}

$certificatePath = Join-Path $base "ComoteTeamTest.cer"
$certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
    $certificatePath
)
foreach ($store in @("Root", "TrustedPublisher")) {
    $existing = Get-ChildItem "Cert:\LocalMachine\$store" |
        Where-Object Thumbprint -eq $certificate.Thumbprint
    if (-not $existing) {
        Import-Certificate -FilePath $certificatePath `
            -CertStoreLocation "Cert:\LocalMachine\$store" | Out-Null
    }
}

if (-not ("Comote.CodeIntegrity" -as [type])) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;
namespace Comote {
    public static class CodeIntegrity {
        [StructLayout(LayoutKind.Sequential)]
        private struct Information {
            public uint Length;
            public uint Options;
        }
        [DllImport("ntdll.dll")]
        private static extern int NtQuerySystemInformation(
            int informationClass,
            ref Information information,
            int informationLength,
            IntPtr returnLength);
        public static bool IsTestSigningEnabled() {
            var information = new Information { Length = 8 };
            int status = NtQuerySystemInformation(
                103, ref information, Marshal.SizeOf(information), IntPtr.Zero);
            if (status != 0) throw new InvalidOperationException(
                "Windows code integrity state query failed: 0x" + status.ToString("X8"));
            return (information.Options & 0x2) != 0;
        }
    }
}
"@
}

if (-not [Comote.CodeIntegrity]::IsTestSigningEnabled()) {
    & bcdedit.exe /set testsigning on
    if ($LASTEXITCODE -ne 0) {
        throw (
            "Windows 테스트 모드를 켜지 못했습니다. Secure Boot가 꺼져 있는지 확인하세요. " +
            "조직에서 관리하는 PC라면 관리자 정책 때문에 차단될 수 있습니다."
        )
    }

    Write-Host ""
    Write-Host "테스트 모드를 켰습니다. Windows를 재부팅한 뒤 이 파일을 한 번 더 실행하세요." -ForegroundColor Yellow
    exit 3010
}

$installer = Join-Path $base "ComoteVirtualHidInstaller.exe"
$process = Start-Process -FilePath $installer -ArgumentList "install" `
    -WorkingDirectory $base -Wait -PassThru
if ($process.ExitCode -notin @(0, 2)) {
    throw "드라이버 설치기가 종료 코드 $($process.ExitCode)로 실패했습니다."
}
if ($process.ExitCode -eq 2) {
    Write-Host "설치를 마치려면 Windows를 재부팅하세요." -ForegroundColor Yellow
    exit 3010
}

Write-Host "Comote Virtual HID 설치가 완료되었습니다." -ForegroundColor Green
Write-Host "Comote Client에서 입력 모드 2를 선택할 수 있습니다."
