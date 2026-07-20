using System.Diagnostics;
using System.Windows.Forms;

namespace Host
{
    internal static class InputBackendFactory
    {
        public const string VirtualHidInstallerFileName =
            "FakerInput_Setup_0.1.1_x64.msi";
        public const string VirtualHidInstallerUrl =
            "https://github.com/Ryochan7/FakerInput/releases/download/" +
            "v0.1.1/FakerInput_Setup_0.1.1_x64.msi";

        public static IInputBackend Create(
            InputBackendMode mode,
            ScreenCapture capture)
        {
            IInputBackend backend = mode switch
            {
                InputBackendMode.VirtualHid => new FakerInputBackend(
                    capture.Left,
                    capture.Top,
                    capture.Width,
                    capture.Height),
                _ => new SendInputBackend(capture.Width, capture.Height),
            };
            backend.UpdateScreenBounds(
                capture.Left,
                capture.Top,
                capture.Width,
                capture.Height);
            return backend;
        }

        public static bool EnsureVirtualHidReady(IWin32Window? owner)
        {
            if (FakerInputBackend.IsDriverAvailable()) return true;

            var answer = MessageBox.Show(
                owner,
                "가상 HID 입력에는 FakerInput 드라이버가 필요합니다.\n\n" +
                "지금 관리자 권한으로 설치하시겠습니까? 설치가 끝나면 " +
                "이 창으로 돌아와 다시 시작을 누르세요.",
                "Comote 가상 HID 설치",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);
            if (answer != DialogResult.Yes) return false;

            try
            {
                var localInstaller = Path.Combine(
                    AppContext.BaseDirectory,
                    VirtualHidInstallerFileName);
                if (File.Exists(localInstaller))
                {
                    using var process = Process.Start(new ProcessStartInfo
                    {
                        FileName = "msiexec.exe",
                        Arguments = $"/i \"{localInstaller}\"",
                        UseShellExecute = true,
                        Verb = "runas",
                    });
                    process?.WaitForExit();
                }
                else
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = VirtualHidInstallerUrl,
                        UseShellExecute = true,
                    });
                    MessageBox.Show(
                        owner,
                        "설치 파일 다운로드를 열었습니다. 설치를 완료한 뒤 " +
                        "Comote Client를 다시 실행해 주세요.",
                        "Comote 가상 HID 설치");
                    return false;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    owner,
                    "가상 HID 설치를 시작하지 못했습니다.\n" + ex.Message,
                    "Comote 가상 HID 설치",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return false;
            }

            if (FakerInputBackend.IsDriverAvailable()) return true;
            MessageBox.Show(
                owner,
                "설치는 완료됐지만 가상 HID 장치가 아직 준비되지 않았습니다.\n\n" +
                "Comote Client를 종료한 뒤 Client 컴퓨터를 한 번 재부팅하고 " +
                "다시 실행해 주세요. 드라이버를 반복해서 설치할 필요는 없습니다.",
                "Comote 가상 HID 설치",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return false;
        }
    }
}