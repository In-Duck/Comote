using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace Host
{
    internal sealed class CloudClientTrayIcon : IDisposable
    {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new(false);
        private ApplicationContext? _context;
        private NotifyIcon? _icon;

        private CloudClientTrayIcon(string hostName, InputBackendMode inputMode)
        {
            _thread = new Thread(() => Run(hostName, inputMode))
            {
                IsBackground = true,
                Name = "Comote Client Tray",
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            _ready.Wait(TimeSpan.FromSeconds(5));
        }

        public static CloudClientTrayIcon Start(string hostName, InputBackendMode inputMode) =>
            new(hostName, inputMode);

        private void Run(string hostName, InputBackendMode inputMode)
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem($"연결됨 · {hostName}") { Enabled = false });
            menu.Items.Add(new ToolStripMenuItem(inputMode == InputBackendMode.VirtualHid ? "입력 · 가상 HID" : "입력 · SendInput") { Enabled = false });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("가상 HID 상태 확인", null, (_, _) => ShowVirtualHidStatus());
            menu.Items.Add("고급 설정", null, (_, _) => RestartWithSetup());
            menu.Items.Add("보안 화면 서비스 설치/복구", null, (_, _) => InstallSecureDesktopService());
            menu.Items.Add("로그아웃", null, (_, _) => LogOut());
            menu.Items.Add("종료", null, (_, _) => Environment.Exit(0));

            _context = new ApplicationContext();
            _icon = new NotifyIcon
            {
                Text = "Comote Client · 연결됨",
                Icon = SystemIcons.Application,
                ContextMenuStrip = menu,
                Visible = true,
            };
            _ready.Set();
            Application.Run(_context);
        }

        private static void ShowVirtualHidStatus()
        {
            var ready = FakerInputBackend.TryGetDriverStatus(out var status);
            MessageBox.Show(
                status,
                "Comote · FakerInput 상태",
                MessageBoxButtons.OK,
                ready ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        private static void InstallSecureDesktopService()
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable)) return;
            try
            {
                using var installer = Process.Start(new ProcessStartInfo(
                    executable, "--install")
                {
                    UseShellExecute = true,
                    Verb = "runas",
                });
                installer?.WaitForExit();
                if (installer?.ExitCode == 0) Environment.Exit(0);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                MessageBox.Show(
                    "관리자 승인이 취소되어 서비스를 설치하지 않았습니다.",
                    "Comote 보안 화면 서비스");
            }
        }

        private static void RestartWithSetup()
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable)) return;
            Process.Start(new ProcessStartInfo(executable, "--setup") { UseShellExecute = true });
            Environment.Exit(0);
        }

        private static void LogOut()
        {
            UserCredentialStore.Delete();
            ServiceCredentialStore.Delete();
            var executable = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(executable))
                Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
            Environment.Exit(0);
        }

        public void Dispose()
        {
            if (_icon == null) return;
            try
            {
                _icon.Visible = false;
                _icon.Dispose();
                _context?.ExitThread();
            }
            catch { }
            _ready.Dispose();
        }
    }
}
