using System.Diagnostics;
using System.Security.Cryptography;
using System.Windows.Forms;

namespace Host
{
    internal static class InputBackendFactory
    {
        public const string VirtualHidInstallerFileName = "FakerInput_Setup_0.1.1_x64.msi";
        public const string VirtualHidInstallerUrl = "https://github.com/Ryochan7/FakerInput/releases/download/v0.1.1/FakerInput_Setup_0.1.1_x64.msi";
        private const string InstallerSha256 = "4C0AEFB7340051A91D606776243298B5CD1143EF5508BBAE6800C474F9ED0840";

        public static IInputBackend Create(InputBackendMode mode, ScreenCapture capture)
        {
            IInputBackend backend = mode switch
            {
                InputBackendMode.VirtualHid => new ResilientInputBackend(capture.Left, capture.Top, capture.Width, capture.Height),
                _ => new SendInputBackend(capture.Width, capture.Height),
            };
            backend.UpdateScreenBounds(capture.Left, capture.Top, capture.Width, capture.Height);
            return backend;
        }

        public static bool EnsureVirtualHidReady(IWin32Window? owner)
        {
            if (FakerInputBackend.IsDriverAvailable()) return true;

            var answer = MessageBox.Show(
                owner,
                "가상 HID 입력에는 공식 FakerInput v0.1.1 드라이버가 필요합니다.\n\n" +
                "검증된 설치 파일을 관리자 권한으로 설치할까요?",
                "Comote · 가상 HID 설치",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);
            if (answer != DialogResult.Yes) return false;

            var installer = Path.Combine(AppContext.BaseDirectory, VirtualHidInstallerFileName);
            if (!File.Exists(installer))
            {
                MessageBox.Show(owner, "설치 파일이 Client 폴더에 없습니다. 정식 Client 패키지를 다시 받아 주세요.\n\n" + VirtualHidInstallerUrl,
                    "Comote · 설치 파일 없음", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            try
            {
                using (var stream = File.OpenRead(installer))
                using (var sha256 = SHA256.Create())
                {
                    var actual = sha256.ComputeHash(stream);
                    var expected = Convert.FromHexString(InstallerSha256);
                    if (!CryptographicOperations.FixedTimeEquals(actual, expected))
                        throw new InvalidDataException("설치 파일의 SHA-256 값이 공식 배포본과 다릅니다.");
                }

                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "msiexec.exe",
                    Arguments = $"/i \"{installer}\" /passive /norestart",
                    UseShellExecute = true,
                    Verb = "runas",
                });
                if (process == null) throw new InvalidOperationException("Windows Installer를 시작하지 못했습니다.");
                if (!process.WaitForExit((int)TimeSpan.FromMinutes(10).TotalMilliseconds))
                    throw new TimeoutException("설치가 10분 안에 끝나지 않았습니다. 설치 창을 확인해 주세요.");
                if (process.ExitCode is not (0 or 1641 or 3010))
                    throw new InvalidOperationException($"Windows Installer 종료 코드: {process.ExitCode}");
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner, "FakerInput 설치를 완료하지 못했습니다.\n" + ex.Message,
                    "Comote · 가상 HID 설치", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            if (FakerInputBackend.IsDriverAvailable()) return true;
            MessageBox.Show(owner, "설치는 완료됐지만 장치가 아직 준비되지 않았습니다.\n\nClient를 완전히 종료하고 PC를 한 번 재부팅한 뒤 다시 실행해 주세요.",
                "Comote · 재부팅 필요", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }
}