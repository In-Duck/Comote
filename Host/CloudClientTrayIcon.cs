using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using Comote.Shared;

namespace Host
{
    internal sealed class CloudClientTrayIcon : IDisposable
    {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new(false);
        private ApplicationContext? _context;
        private NotifyIcon? _icon;
        private System.Windows.Forms.Timer? _updateTimer;
        private ToolStripMenuItem? _updateItem;
        private ClientUpdateInfo? _availableUpdate;
        private int _checkingUpdate;

        private CloudClientTrayIcon(
            string hostName,
            InputBackendMode inputMode,
            bool systemAgent)
        {
            _thread = new Thread(() => Run(hostName, inputMode, systemAgent))
            {
                IsBackground = true,
                Name = "Comote Client Tray",
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            _ready.Wait(TimeSpan.FromSeconds(5));
        }

        public static CloudClientTrayIcon Start(
            string hostName,
            InputBackendMode inputMode,
            bool systemAgent = false) =>
            new(hostName, inputMode, systemAgent);

        private void Run(string hostName, InputBackendMode inputMode, bool systemAgent)
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem($"연결됨 · {hostName}") { Enabled = false });
            menu.Items.Add(new ToolStripMenuItem(
                inputMode == InputBackendMode.VirtualHid
                    ? "입력 · 가상 HID"
                    : "입력 · SendInput") { Enabled = false });
            menu.Items.Add(new ToolStripSeparator());

            _updateItem = new ToolStripMenuItem(
                $"업데이트 확인 · {ClientAutoUpdater.CurrentVersion}");
            _updateItem.Click += async (_, _) => await OnUpdateClickedAsync();
            menu.Items.Add(_updateItem);
            menu.Items.Add("가상 HID 상태 확인", null, (_, _) => ShowVirtualHidStatus());

            if (!systemAgent)
            {
                menu.Items.Add("고급 설정", null, (_, _) => RestartWithSetup());
                menu.Items.Add("보안 화면 서비스 설치/복구", null, (_, _) => InstallSecureDesktopService());
                menu.Items.Add("로그아웃", null, (_, _) => LogOut());
                menu.Items.Add("종료", null, (_, _) => Environment.Exit(0));
            }
            else
            {
                menu.Items.Add("서비스 다시 시작", null, (_, _) => SecureDesktopService.Restart());
            }

            _context = new ApplicationContext();
            _icon = new NotifyIcon
            {
                Text = "Comote Client · 연결됨",
                Icon = SystemIcons.Application,
                ContextMenuStrip = menu,
                Visible = true,
            };

            _updateTimer = new System.Windows.Forms.Timer { Interval = 30 * 60 * 1000 };
            _updateTimer.Tick += async (_, _) => await CheckForUpdateAsync(notify: true);
            _updateTimer.Start();
            _ready.Set();
            _context.MainForm = null;
            _ = CheckForUpdateAsync(notify: true);
            Application.Run(_context);
        }

        private async Task CheckForUpdateAsync(bool notify)
        {
            if (Interlocked.Exchange(ref _checkingUpdate, 1) != 0) return;
            try
            {
                if (_updateItem != null)
                {
                    _updateItem.Enabled = false;
                    _updateItem.Text = "업데이트 확인 중…";
                }

                _availableUpdate = await ClientAutoUpdater.CheckForUpdateAsync(
                    ProductUpdateSettings.ClientManifestUrl);
                if (_updateItem == null) return;

                if (_availableUpdate == null)
                {
                    _updateItem.Text = $"최신 버전 · {ClientAutoUpdater.CurrentVersion}";
                    _updateItem.Enabled = true;
                    return;
                }

                _updateItem.Text = $"업데이트 가능 · {_availableUpdate.Version}";
                _updateItem.Enabled = true;
                if (_icon != null)
                {
                    _icon.Text = $"Comote · {_availableUpdate.Version} 업데이트 가능";
                    if (notify)
                    {
                        _icon.BalloonTipTitle = "Comote 업데이트";
                        _icon.BalloonTipText =
                            $"새 버전 {_availableUpdate.Version}을 설치할 수 있습니다.";
                        _icon.ShowBalloonTip(5000);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Updater] Tray check failed: {ex.Message}");
                if (_updateItem != null)
                {
                    _updateItem.Text = "업데이트 확인 실패 · 다시 시도";
                    _updateItem.Enabled = true;
                }
            }
            finally
            {
                Interlocked.Exchange(ref _checkingUpdate, 0);
            }
        }

        private async Task OnUpdateClickedAsync()
        {
            if (_availableUpdate == null)
            {
                await CheckForUpdateAsync(notify: false);
                if (_availableUpdate == null) return;
            }

            var notes = string.IsNullOrWhiteSpace(_availableUpdate.ReleaseNotes)
                ? ""
                : $"\n\n변경 사항\n{_availableUpdate.ReleaseNotes}";
            var answer = MessageBox.Show(
                $"Comote {_availableUpdate.Version} 업데이트를 설치할까요?" +
                "\n\n파일을 검증한 뒤 Client 서비스가 자동으로 다시 시작됩니다." + notes,
                "Comote 업데이트",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);
            if (answer != DialogResult.Yes) return;

            if (_updateItem != null)
            {
                _updateItem.Enabled = false;
                _updateItem.Text = "업데이트 다운로드 중…";
            }

            var staged = await ClientAutoUpdater.StageUpdateAsync(
                _availableUpdate,
                Environment.GetCommandLineArgs().Skip(1).ToArray());
            if (!staged)
            {
                MessageBox.Show(
                    "업데이트를 설치하지 못했습니다. 네트워크 연결을 확인한 뒤 다시 시도해주세요.",
                    "Comote 업데이트",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                if (_updateItem != null)
                {
                    _updateItem.Enabled = true;
                    _updateItem.Text = "업데이트 설치 실패 · 다시 시도";
                }
                return;
            }

            if (_updateItem != null) _updateItem.Text = "재시작 중…";
            await Task.Delay(750);
            Environment.Exit(0);
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
                using var installer = Process.Start(new ProcessStartInfo(executable, "--install")
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
                _updateTimer?.Stop();
                _updateTimer?.Dispose();
                _icon.Visible = false;
                _icon.Dispose();
                _context?.ExitThread();
            }
            catch { }
            _ready.Dispose();
        }
    }
}