using System.Drawing;
using System.Windows.Forms;

namespace Host
{
    internal sealed class ClientTrayIcon : IDisposable
    {
        private readonly CancellationTokenSource _shutdown;
        private readonly Action<bool>? _restartForSetup;
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new(false);
        private SynchronizationContext? _uiContext;
        private NotifyIcon? _notifyIcon;
        private ApplicationContext? _applicationContext;

        private ClientTrayIcon(
            string manager,
            int port,
            string clientName,
            CancellationTokenSource shutdown,
            Action<bool>? restartForSetup)
        {
            _shutdown = shutdown;
            _restartForSetup = restartForSetup;
            _thread = new Thread(
                () => RunTray(manager, port, clientName))
            {
                IsBackground = true,
                Name = "Comote Client Tray",
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            _ready.Wait(TimeSpan.FromSeconds(5));
        }

        public static ClientTrayIcon Start(
            string manager,
            int port,
            string clientName,
            CancellationTokenSource shutdown,
            Action<bool>? restartForSetup = null) =>
            new(manager, port, clientName, shutdown, restartForSetup);

        public void SetConnected(bool connected, string detail)
        {
            _uiContext?.Post(_ =>
            {
                if (_notifyIcon == null) return;
                _notifyIcon.Text = TrimText(
                    connected
                        ? $"Comote Client - 연결됨 ({detail})"
                        : $"Comote Client - 재연결 중 ({detail})");
            }, null);
        }

        private void RunTray(
            string manager,
            int port,
            string clientName)
        {
            SynchronizationContext.SetSynchronizationContext(
                new WindowsFormsSynchronizationContext());
            _uiContext = SynchronizationContext.Current;
            _applicationContext = new ApplicationContext();

            var menu = new ContextMenuStrip();
            var status = new ToolStripMenuItem(
                $"{clientName} → {manager}:{port}")
            {
                Enabled = false,
            };
            var exit = new ToolStripMenuItem("Comote Client 종료");
            exit.Click += (_, _) =>
            {
                _shutdown.Cancel();
                _applicationContext?.ExitThread();
            };
            menu.Items.Add(status);
            if (_restartForSetup != null)
            {
                var settings = new ToolStripMenuItem("연결 설정 변경");
                settings.Click += (_, _) => _restartForSetup(false);
                var resetIdentity = new ToolStripMenuItem("이 PC 새 ID로 등록");
                resetIdentity.Click += (_, _) => _restartForSetup(true);
                menu.Items.Add(settings);
                menu.Items.Add(resetIdentity);
                menu.Items.Add(new ToolStripSeparator());
            }

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exit);

            Icon? icon = null;
            try
            {
                icon = Icon.ExtractAssociatedIcon(
                    Application.ExecutablePath);
            }
            catch
            {
            }

            _notifyIcon = new NotifyIcon
            {
                Icon = icon ?? SystemIcons.Application,
                Text = TrimText("Comote Client - Manager 연결 중"),
                ContextMenuStrip = menu,
                Visible = true,
            };
            _ready.Set();

            try
            {
                Application.Run(_applicationContext);
            }
            finally
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                menu.Dispose();
                icon?.Dispose();
            }
        }

        private static string TrimText(string value) =>
            value.Length <= 63 ? value : value[..63];

        public void Dispose()
        {
            _uiContext?.Post(_ =>
                _applicationContext?.ExitThread(), null);
            if (_thread.IsAlive)
                _thread.Join(TimeSpan.FromSeconds(3));
            _ready.Dispose();
        }
    }
}
