using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Newtonsoft.Json;

namespace Viewer
{
    internal sealed class DirectConnectWindow : Window
    {
        private readonly TextBox _hostBox;
        private readonly TextBox _portBox;
        private readonly PasswordBox _passwordBox;
        private readonly CheckBox _rememberBox;

        public string HostAddress => _hostBox.Text.Trim();
        public int Port => int.Parse(_portBox.Text);
        public string ConnectionPassword => _passwordBox.Password;

        public DirectConnectWindow()
        {
            Title = "Comote Manager - 직접 연결";
            Width = 460;
            Height = 510;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(Color.FromRgb(10, 10, 10));
            Foreground = new SolidColorBrush(Color.FromRgb(34, 197, 94));

            var root = new StackPanel { Margin = new Thickness(32) };
            Content = root;
            root.Children.Add(new TextBlock
            {
                Text = "COMOTE DIRECT MANAGER",
                FontSize = 22,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 8),
            });
            root.Children.Add(new TextBlock
            {
                Text = "중앙 서버 없이 Client PC에 직접 연결합니다.",
                Foreground = Brushes.LightGray,
                Margin = new Thickness(0, 0, 0, 24),
            });

            root.Children.Add(Label("Client 공인 IP 또는 DDNS"));
            _hostBox = Input("127.0.0.1");
            root.Children.Add(_hostBox);
            root.Children.Add(Label("포트 (Client 공유기에서 TCP 포워딩)"));
            _portBox = Input("45820");
            root.Children.Add(_portBox);
            root.Children.Add(Label("접속 암호 (12자 이상 권장)"));
            _passwordBox = new PasswordBox
            {
                Height = 34,
                Margin = new Thickness(0, 4, 0, 12),
                Background = new SolidColorBrush(Color.FromRgb(28, 28, 28)),
                Foreground = Brushes.White,
                BorderBrush = Brushes.DimGray,
                Padding = new Thickness(8, 5, 8, 5),
            };
            root.Children.Add(_passwordBox);

            _rememberBox = new CheckBox
            {
                Content = "이 PC에 접속 정보 저장",
                Foreground = Brushes.LightGray,
                Margin = new Thickness(0, 0, 0, 14),
            };
            root.Children.Add(_rememberBox);

            root.Children.Add(new TextBlock
            {
                Text =
                    "필수: Client 공유기에서 위 TCP 포트를 Client PC의 " +
                    "내부 IP로 포트포워딩하고 Windows 방화벽에서도 허용하세요.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248)),
                Margin = new Thickness(0, 0, 0, 18),
            });

            var connect = new Button
            {
                Content = "직접 연결",
                Height = 42,
                Background = new SolidColorBrush(Color.FromRgb(0, 122, 204)),
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
            };
            connect.Click += (_, _) => Connect();
            root.Children.Add(connect);
            LoadSaved();
        }

        private static TextBlock Label(string text) => new()
        {
            Text = text,
            Foreground = Brushes.LightGray,
            Margin = new Thickness(0, 2, 0, 0),
        };

        private static TextBox Input(string text) => new()
        {
            Text = text,
            Height = 34,
            Margin = new Thickness(0, 4, 0, 12),
            Background = new SolidColorBrush(Color.FromRgb(28, 28, 28)),
            Foreground = Brushes.White,
            BorderBrush = Brushes.DimGray,
            Padding = new Thickness(8, 5, 8, 5),
        };

        private void Connect()
        {
            if (string.IsNullOrWhiteSpace(HostAddress))
            {
                MessageBox.Show("Client IP 또는 DDNS를 입력하세요.");
                return;
            }
            if (!int.TryParse(_portBox.Text, out var port) ||
                port is < 1024 or > 65535)
            {
                MessageBox.Show("포트는 1024~65535 범위여야 합니다.");
                return;
            }
            if (ConnectionPassword.Length < 8)
            {
                MessageBox.Show("접속 암호는 최소 8자 이상이어야 합니다.");
                return;
            }

            if (_rememberBox.IsChecked == true) Save();
            else DirectConnectionStore.Clear();
            DialogResult = true;
        }

        private void LoadSaved()
        {
            if (!DirectConnectionStore.TryLoad(
                    out var host,
                    out var port,
                    out var password))
                return;

            _hostBox.Text = host;
            _portBox.Text = port.ToString();
            _passwordBox.Password = password;
            _rememberBox.IsChecked = true;
        }

        private void Save() => DirectConnectionStore.Save(
            HostAddress,
            Port,
            ConnectionPassword);
    }

    internal static class DirectConnectionStore
    {
        private sealed class StoredConnection
        {
            public string Host { get; set; } = "";
            public int Port { get; set; }
            public string Password { get; set; } = "";
        }

        private static readonly string DirectoryPath = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "Comote");
        private static readonly string FilePath =
            Path.Combine(DirectoryPath, "direct-connection.dat");
        private static readonly byte[] Entropy =
            Encoding.UTF8.GetBytes("Comote.DirectConnection.v1");

        public static void Save(string host, int port, string password)
        {
            Directory.CreateDirectory(DirectoryPath);
            var json = JsonConvert.SerializeObject(
                new StoredConnection
                {
                    Host = host,
                    Port = port,
                    Password = password,
                });
            var protectedBytes = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(json),
                Entropy,
                DataProtectionScope.CurrentUser);
            File.WriteAllBytes(FilePath, protectedBytes);
        }

        public static bool TryLoad(
            out string host,
            out int port,
            out string password)
        {
            host = "";
            port = 45820;
            password = "";
            try
            {
                if (!File.Exists(FilePath)) return false;
                var json = Encoding.UTF8.GetString(
                    ProtectedData.Unprotect(
                        File.ReadAllBytes(FilePath),
                        Entropy,
                        DataProtectionScope.CurrentUser));
                var stored =
                    JsonConvert.DeserializeObject<StoredConnection>(json);
                if (stored == null ||
                    string.IsNullOrWhiteSpace(stored.Host) ||
                    stored.Port is < 1024 or > 65535)
                    return false;
                host = stored.Host;
                port = stored.Port;
                password = stored.Password;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static void Clear()
        {
            try
            {
                if (File.Exists(FilePath)) File.Delete(FilePath);
            }
            catch
            {
            }
        }
    }
}

