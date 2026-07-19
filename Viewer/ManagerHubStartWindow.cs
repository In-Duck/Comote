using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Viewer
{
    internal sealed class ManagerHubStartWindow : Window
    {
        private readonly TextBox _portBox;
        private readonly PasswordBox _passwordBox;

        public int Port => int.Parse(_portBox.Text);
        public string AccessPassword => _passwordBox.Password;

        public ManagerHubStartWindow()
        {
            Title = "Comote Manager Hub";
            Width = 460;
            Height = 410;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(Color.FromRgb(10, 10, 10));
            Foreground = new SolidColorBrush(Color.FromRgb(0, 255, 65));

            var root = new StackPanel { Margin = new Thickness(32) };
            Content = root;
            root.Children.Add(new TextBlock
            {
                Text = "COMOTE MANAGER HUB",
                FontSize = 24,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 8),
            });
            root.Children.Add(new TextBlock
            {
                Text =
                    "이 Manager가 한 포트에서 여러 Client 접속을 받습니다.",
                Foreground = Brushes.LightGray,
                Margin = new Thickness(0, 0, 0, 22),
                TextWrapping = TextWrapping.Wrap,
            });

            root.Children.Add(Label("수신 포트"));
            _portBox = Input("45820");
            root.Children.Add(_portBox);
            root.Children.Add(Label("Client 등록 암호 (12자 이상 권장)"));
            _passwordBox = new PasswordBox
            {
                Height = 34,
                Margin = new Thickness(0, 4, 0, 14),
                Background = new SolidColorBrush(Color.FromRgb(28, 28, 28)),
                Foreground = Brushes.White,
                BorderBrush = Brushes.DimGray,
                Padding = new Thickness(8, 5, 8, 5),
            };
            root.Children.Add(_passwordBox);
            root.Children.Add(new TextBlock
            {
                Text =
                    "Manager 공유기에서 위 TCP 포트 하나만 이 PC로 " +
                    "포트포워딩합니다. Client 측 포트포워딩은 필요 없습니다.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(255, 176, 0)),
                Margin = new Thickness(0, 0, 0, 18),
            });

            var start = new Button
            {
                Content = "Manager Hub 시작",
                Height = 42,
                Background = new SolidColorBrush(Color.FromRgb(0, 122, 204)),
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
            };
            start.Click += (_, _) => StartHub();
            root.Children.Add(start);
        }

        private static TextBlock Label(string text) => new()
        {
            Text = text,
            Foreground = Brushes.LightGray,
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

        private void StartHub()
        {
            if (!int.TryParse(_portBox.Text, out var port) ||
                port is < 1024 or > 65535)
            {
                MessageBox.Show("포트는 1024~65535 범위여야 합니다.");
                return;
            }
            if (AccessPassword.Length < 8)
            {
                MessageBox.Show("Client 등록 암호는 최소 8자 이상이어야 합니다.");
                return;
            }
            DialogResult = true;
        }
    }
}

