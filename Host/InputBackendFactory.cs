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
            IInputBackend backend;
            if (mode == InputBackendMode.VirtualHid)
            {
                try
                {
                    backend = new ResilientInputBackend(
                        capture.Left, capture.Top, capture.Width, capture.Height);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"[Input] FakerInput could not be opened; using SendInput: {ex.Message}");
                    backend = new SendInputBackend(capture.Width, capture.Height);
                }
            }
            else
            {
                backend = new SendInputBackend(capture.Width, capture.Height);
            }
            backend.UpdateScreenBounds(capture.Left, capture.Top, capture.Width, capture.Height);
            return backend;
        }

        public static InputBackendMode ResolveConfiguredMode(
            InputBackendMode requestedMode,
            IWin32Window? owner,
            bool allowInstall)
        {
            if (requestedMode != InputBackendMode.VirtualHid ||
                FakerInputBackend.IsDriverAvailable())
                return requestedMode;

            if (allowInstall && EnsureVirtualHidReady(owner))
                return InputBackendMode.VirtualHid;

            Console.WriteLine(
                "[Input] FakerInput is unavailable. The configured mode was changed to SendInput.");
            if (Environment.UserInteractive)
            {
                MessageBox.Show(
                    owner,
                    "FakerInput 장치를 사용할 수 없어 입력 모드를 Windows SendInput으로 변경했습니다.\n\n" +
                    "가상 HID를 사용하려면 고급 설정에서 FakerInput을 설치한 뒤 다시 선택해 주세요.",
                    "Comote · 입력 모드 변경",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            return InputBackendMode.SendInput;
        }

        public static bool EnsureVirtualHidReady(IWin32Window? owner)
        {
            if (FakerInputBackend.TryGetDriverStatus(out var driverStatus)) return true;

            var answer = MessageBox.Show(
                owner,
                "현재 상태:\n" + driverStatus + "\n\n" +
                "공식 FakerInput v0.1.1 설치 파일로 복구 설치할까요?",
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

            if (FakerInputBackend.TryGetDriverStatus(out driverStatus)) return true;
            MessageBox.Show(
                owner,
                "설치는 완료됐지만 가상 HID를 사용할 수 없습니다.\n\n" + driverStatus +
                "\n\n장치 관리자 > 시스템 장치에서 FakerInput Device에 노란 경고 표시가 있는지 확인해 주세요.",
                "Comote · FakerInput 상태",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return false;
        }
    }
}