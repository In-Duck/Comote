using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Comote.Input;

namespace Viewer
{
    internal sealed class ManagerHubStartWindow : Window
    {
        private static readonly TimeSpan ClipboardLifetime =
            TimeSpan.FromSeconds(30);

        private readonly TextBox _portBox;
        private readonly TextBox _deviceNameBox;
        private readonly ListBox _deviceList;
        private readonly PasswordBox _maskedKeyBox;
        private readonly TextBox _revealedKeyBox;
        private readonly ObservableCollection<DeviceListItem> _devices = [];
        private readonly DispatcherTimer _clipboardTimer;
        private string? _clipboardSecret;

        public int Port { get; private set; }
        public IReadOnlyList<ManagerHubDeviceCredential> Devices { get; private set; } =
            Array.Empty<ManagerHubDeviceCredential>();

        public ManagerHubStartWindow()
        {
            var savedPort = 45820;
            if (ManagerHubSettingsStore.TryLoad(out var savedSettings))
            {
                savedPort = savedSettings.Port;
                foreach (var credential in savedSettings.Devices)
                {
                    _devices.Add(new DeviceListItem(credential));
                }
            }
            if (_devices.Count == 0)
            {
                _devices.Add(new DeviceListItem(
                    new ManagerHubDeviceCredential(
                        "Client 1",
                        ComoteAccessKey.Generate())));
            }

            Title = "Comote Manager Hub";
            Width = 700;
            Height = 650;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(Color.FromRgb(10, 10, 10));
            Foreground = new SolidColorBrush(Color.FromRgb(34, 197, 94));

            _clipboardTimer = new DispatcherTimer
            {
                Interval = ClipboardLifetime,
            };
            _clipboardTimer.Tick += (_, _) => ClearCopiedKey();
            Closed += (_, _) => ClearCopiedKey();

            var root = new StackPanel { Margin = new Thickness(28) };
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
                    "각 Client는 고유한 키 하나만 입력합니다. 키에서 기기 ID가 " +
                    "자동으로 만들어지며 다른 Client의 연결과 완전히 분리됩니다.",
                Foreground = Brushes.LightGray,
                Margin = new Thickness(0, 0, 0, 16),
                TextWrapping = TextWrapping.Wrap,
            });

            root.Children.Add(Label("수신 포트"));
            _portBox = Input(savedPort.ToString());
            _portBox.Width = 180;
            _portBox.HorizontalAlignment = HorizontalAlignment.Left;
            root.Children.Add(_portBox);

            root.Children.Add(Label("등록된 Client 키"));
            _deviceList = new ListBox
            {
                ItemsSource = _devices,
                Height = 150,
                Margin = new Thickness(0, 4, 0, 10),
                Background = new SolidColorBrush(Color.FromRgb(28, 28, 28)),
                Foreground = Brushes.White,
                BorderBrush = Brushes.DimGray,
            };
            _deviceList.SelectionChanged += (_, _) => ShowSelectedKey();
            root.Children.Add(_deviceList);

            var addPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 10),
            };
            _deviceNameBox = Input(NextDeviceName());
            _deviceNameBox.Width = 330;
            _deviceNameBox.Margin = new Thickness(0, 0, 8, 0);
            addPanel.Children.Add(_deviceNameBox);
            var addButton = CreateButton("새 Client 키 추가");
            addButton.Click += (_, _) => AddDevice();
            addPanel.Children.Add(addButton);
            root.Children.Add(addPanel);

            root.Children.Add(Label("선택한 Client 접속 키"));
            var keyGrid = new Grid { Margin = new Thickness(0, 4, 0, 8) };
            _maskedKeyBox = new PasswordBox
            {
                Height = 34,
                Background = new SolidColorBrush(Color.FromRgb(28, 28, 28)),
                Foreground = Brushes.White,
                BorderBrush = Brushes.DimGray,
                Padding = new Thickness(8, 5, 8, 5),
                IsHitTestVisible = false,
                Focusable = false,
            };
            _revealedKeyBox = Input("");
            _revealedKeyBox.Margin = new Thickness(0);
            _revealedKeyBox.IsReadOnly = true;
            _revealedKeyBox.Visibility = Visibility.Collapsed;
            keyGrid.Children.Add(_maskedKeyBox);
            keyGrid.Children.Add(_revealedKeyBox);
            root.Children.Add(keyGrid);

            var keyButtons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 14),
            };
            var copyButton = CreateButton("키 복사 (30초 후 삭제)");
            copyButton.Margin = new Thickness(0, 0, 8, 0);
            copyButton.Click += (_, _) => CopySelectedKey();
            keyButtons.Children.Add(copyButton);
            var revealKeyBox = new CheckBox
            {
                Content = "키 표시",
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.LightGray,
                Margin = new Thickness(4, 0, 12, 0),
            };
            revealKeyBox.Checked += (_, _) => SetKeyReveal(true);
            revealKeyBox.Unchecked += (_, _) => SetKeyReveal(false);
            keyButtons.Children.Add(revealKeyBox);
            var revokeButton = CreateButton("선택 키 폐기");
            revokeButton.Click += (_, _) => RevokeSelectedDevice();
            keyButtons.Children.Add(revokeButton);
            root.Children.Add(keyButtons);

            root.Children.Add(new TextBlock
            {
                Text =
                    "키는 Windows 현재 사용자 DPAPI로 암호화해 저장합니다. " +
                    "폐기한 키의 Client는 다음 연결부터 인증되지 않습니다.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248)),
                Margin = new Thickness(0, 0, 0, 16),
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
            _deviceList.SelectedIndex = 0;
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

        private static Button CreateButton(string text) => new()
        {
            Content = text,
            Padding = new Thickness(12, 6, 12, 6),
        };

        private DeviceListItem? SelectedDevice =>
            _deviceList.SelectedItem as DeviceListItem;

        private void ShowSelectedKey()
        {
            var key = SelectedDevice?.Credential.AccessKey ?? "";
            _maskedKeyBox.Password = key;
            _revealedKeyBox.Text = key;
        }

        private void SetKeyReveal(bool reveal)
        {
            _maskedKeyBox.Visibility = reveal
                ? Visibility.Collapsed
                : Visibility.Visible;
            _revealedKeyBox.Visibility = reveal
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void AddDevice()
        {
            var name = _deviceNameBox.Text.Trim();
            if (name.Length is <= 0 or > 128)
            {
                MessageBox.Show("Client 이름은 1~128자로 입력하세요.");
                return;
            }

            var item = new DeviceListItem(
                new ManagerHubDeviceCredential(
                    name,
                    ComoteAccessKey.Generate()));
            _devices.Add(item);
            _deviceList.SelectedItem = item;
            _deviceNameBox.Text = NextDeviceName();
        }

        private string NextDeviceName()
        {
            var suffix = 1;
            while (_devices.Any(item => string.Equals(
                       item.Credential.DisplayName,
                       $"Client {suffix}",
                       StringComparison.OrdinalIgnoreCase)))
            {
                suffix++;
            }
            return $"Client {suffix}";
        }

        private void CopySelectedKey()
        {
            var selected = SelectedDevice;
            if (selected is null)
            {
                MessageBox.Show("복사할 Client 키를 선택하세요.");
                return;
            }

            try
            {
                ClearCopiedKey();
                Clipboard.SetText(selected.Credential.AccessKey);
                _clipboardSecret = selected.Credential.AccessKey;
                _clipboardTimer.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"접속 키 복사 실패: {ex.Message}");
            }
        }

        private void ClearCopiedKey()
        {
            _clipboardTimer.Stop();
            var secret = _clipboardSecret;
            _clipboardSecret = null;
            if (string.IsNullOrEmpty(secret))
            {
                return;
            }

            try
            {
                if (Clipboard.ContainsText() &&
                    string.Equals(
                        Clipboard.GetText(),
                        secret,
                        StringComparison.Ordinal))
                {
                    Clipboard.Clear();
                }
            }
            catch
            {
            }
        }

        private void RevokeSelectedDevice()
        {
            var selected = SelectedDevice;
            if (selected is null)
            {
                return;
            }

            if (MessageBox.Show(
                    $"'{selected.Credential.DisplayName}' 키를 폐기할까요?",
                    "Comote Manager Hub",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            ClearCopiedKey();
            var index = _deviceList.SelectedIndex;
            _devices.Remove(selected);
            if (_devices.Count > 0)
            {
                _deviceList.SelectedIndex = Math.Min(index, _devices.Count - 1);
            }
            else
            {
                ShowSelectedKey();
            }
        }

        private void StartHub()
        {
            if (!int.TryParse(_portBox.Text, out var port) ||
                port is < 1024 or > 65535)
            {
                MessageBox.Show("포트는 1024~65535 범위여야 합니다.");
                return;
            }
            if (_devices.Count == 0)
            {
                MessageBox.Show("Client 키를 하나 이상 추가하세요.");
                return;
            }

            var credentials = _devices
                .Select(item => item.Credential)
                .ToArray();
            try
            {
                ManagerHubSettingsStore.Save(port, credentials);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Client 키를 Windows 사용자 보호 저장소에 저장하지 " +
                    $"못했습니다.\n\n{ex.Message}",
                    "Comote Manager Hub");
                return;
            }

            Port = port;
            Devices = credentials;
            ClearCopiedKey();
            DialogResult = true;
        }

        private sealed class DeviceListItem
        {
            public ManagerHubDeviceCredential Credential { get; }

            public DeviceListItem(ManagerHubDeviceCredential credential)
            {
                Credential = credential;
            }

            public override string ToString() =>
                $"{Credential.DisplayName}  ·  {Credential.RoutingId}";
        }
    }
}
