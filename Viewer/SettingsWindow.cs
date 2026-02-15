using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Viewer
{
    /// <summary>
    /// 환경 설정 윈도우 (WPF, 코드 기반 UI).
    /// 참고 이미지를 기반으로 탭 구조 + 원격제어 옵션 구현.
    /// </summary>
    public class SettingsWindow : Window
    {
        private readonly AppSettings _settings;
        private ComboBox _frameRateCombo = null!;
        private ComboBox _qualityCombo = null!;
        private CheckBox _clipboardCheck = null!;
        private CheckBox _alwaysOnTopCheck = null!;
        private CheckBox _rememberSizeCheck = null!;
        private ComboBox _wheelCombo = null!;
        private CheckBox _fullscreenCheck = null!;
        private CheckBox _preventSleepCheck = null!;

        /// <summary>설정이 변경되었는지 여부</summary>
        public bool SettingsChanged { get; private set; }

        public SettingsWindow(AppSettings settings)
        {
            _settings = settings;

            Title = "⚙ 환경 설정";
            Width = 520;
            Height = 480;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 35));

            var mainGrid = new Grid();
            mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // === 좌측 탭 메뉴 ===
            var tabPanel = new StackPanel
            {
                Background = new SolidColorBrush(Color.FromRgb(40, 40, 48))
            };

            var generalTab = CreateTabItem("일반", true);
            var shortcutTab = CreateTabItem("단축키", false);
            var infoTab = CreateTabItem("정보", false);
            tabPanel.Children.Add(generalTab);
            tabPanel.Children.Add(shortcutTab);
            tabPanel.Children.Add(infoTab);

            Grid.SetColumn(tabPanel, 0);
            mainGrid.Children.Add(tabPanel);

            // === 우측 콘텐츠 패널 ===
            var contentPanel = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(16, 12, 16, 60)
            };
            var contentStack = new StackPanel();

            // 제목
            contentStack.Children.Add(new TextBlock
            {
                Text = "General",
                FontSize = 22,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Colors.White),
                Margin = new Thickness(0, 0, 0, 4)
            });
            // 구분선
            contentStack.Children.Add(new Border
            {
                Height = 2,
                Background = new SolidColorBrush(Color.FromRgb(60, 110, 200)),
                Margin = new Thickness(0, 0, 0, 16)
            });

            // 섹션: 원격제어 옵션
            contentStack.Children.Add(CreateSectionHeader("원격제어 옵션"));

            // 출력프레임수
            var fpsRow = CreateOptionRow("출력프레임수 :");
            _frameRateCombo = CreateComboBox(new[] { "최상", "상", "중", "하" }, _settings.FrameRate);
            fpsRow.Children.Add(_frameRateCombo);
            contentStack.Children.Add(fpsRow);

            // 출력품질
            var qualRow = CreateOptionRow("출력품질 :");
            _qualityCombo = CreateComboBox(new[] { "최상", "상", "중", "하" }, _settings.Quality);
            qualRow.Children.Add(_qualityCombo);
            // 품질 힌트
            qualRow.Children.Add(new TextBlock
            {
                Text = "※ 품질이 낮을수록 부드러운 제어 가능",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(200, 160, 60)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            });
            contentStack.Children.Add(qualRow);

            // 클립보드 자동 동기화
            _clipboardCheck = CreateCheckBox("1:1 제어창 오픈시 Text 클립보드 자동 복사", _settings.AutoClipboard);
            contentStack.Children.Add(_clipboardCheck);

            // 원격제어 창 최상단 유지
            _alwaysOnTopCheck = CreateCheckBox("원격제어 창 최상단 유지", _settings.AlwaysOnTop);
            contentStack.Children.Add(_alwaysOnTopCheck);

            // 창 크기 기억
            _rememberSizeCheck = CreateCheckBox("원격제어 창 크기 및 위치 기억", _settings.RememberWindowSize);
            contentStack.Children.Add(_rememberSizeCheck);

            // 휠 민감도
            var wheelRow = CreateOptionRow("1:1 제어창 휠 민감도 :");
            _wheelCombo = CreateComboBox(new[] { "빠름", "보통", "느림" }, _settings.WheelSensitivity);
            wheelRow.Children.Add(_wheelCombo);
            contentStack.Children.Add(wheelRow);

            // 구분선
            contentStack.Children.Add(new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Color.FromRgb(60, 60, 70)),
                Margin = new Thickness(0, 12, 0, 12)
            });

            // 섹션: 시스템 옵션
            contentStack.Children.Add(CreateSectionHeader("시스템 옵션"));

            // 풀스크린 단축키
            _fullscreenCheck = CreateCheckBox("풀스크린 단축키 사용 (Ctrl+Shift+F)", _settings.EnableFullscreenShortcut);
            contentStack.Children.Add(_fullscreenCheck);

            // 절전모드 방지
            _preventSleepCheck = CreateCheckBox("자동 절전모드 진입 않기", _settings.PreventSleep);
            contentStack.Children.Add(_preventSleepCheck);

            contentPanel.Content = contentStack;
            Grid.SetColumn(contentPanel, 1);
            mainGrid.Children.Add(contentPanel);

            // === 하단 버튼 바 ===
            var btnBar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 16, 12)
            };

            var applyBtn = new Button
            {
                Content = "적용",
                Width = 80,
                Height = 32,
                Margin = new Thickness(8, 0, 0, 0),
                Background = new SolidColorBrush(Color.FromRgb(60, 110, 200)),
                Foreground = new SolidColorBrush(Colors.White),
                BorderThickness = new Thickness(0),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            applyBtn.Click += OnApplyClick;

            var cancelBtn = new Button
            {
                Content = "취소",
                Width = 80,
                Height = 32,
                Margin = new Thickness(8, 0, 0, 0),
                Background = new SolidColorBrush(Color.FromRgb(55, 55, 60)),
                Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 210)),
                BorderThickness = new Thickness(0),
                FontSize = 13,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            cancelBtn.Click += (s, e) => Close();

            btnBar.Children.Add(applyBtn);
            btnBar.Children.Add(cancelBtn);
            Grid.SetColumn(btnBar, 1);
            mainGrid.Children.Add(btnBar);

            Content = mainGrid;
        }

        private void OnApplyClick(object sender, RoutedEventArgs e)
        {
            _settings.FrameRate = _frameRateCombo.SelectedItem?.ToString() ?? "상";
            _settings.Quality = _qualityCombo.SelectedItem?.ToString() ?? "상";
            _settings.AutoClipboard = _clipboardCheck.IsChecked == true;
            _settings.AlwaysOnTop = _alwaysOnTopCheck.IsChecked == true;
            _settings.RememberWindowSize = _rememberSizeCheck.IsChecked == true;
            _settings.WheelSensitivity = _wheelCombo.SelectedItem?.ToString() ?? "보통";
            _settings.EnableFullscreenShortcut = _fullscreenCheck.IsChecked == true;
            _settings.PreventSleep = _preventSleepCheck.IsChecked == true;

            _settings.Save();
            SettingsChanged = true;
            Close();
        }

        // === UI 헬퍼 메서드 ===

        private Border CreateTabItem(string text, bool isActive)
        {
            var border = new Border
            {
                Height = 40,
                Background = new SolidColorBrush(isActive
                    ? Color.FromRgb(60, 110, 200)
                    : Colors.Transparent),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            border.Child = new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(Colors.White),
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(16, 0, 0, 0)
            };
            return border;
        }

        private TextBlock CreateSectionHeader(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(150, 170, 220)),
                Margin = new Thickness(0, 0, 0, 10)
            };
        }

        private StackPanel CreateOptionRow(string label)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 8)
            };
            row.Children.Add(new TextBlock
            {
                Text = label,
                Width = 150,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 210)),
                VerticalAlignment = VerticalAlignment.Center
            });
            return row;
        }

        private ComboBox CreateComboBox(string[] items, string selected)
        {
            var combo = new ComboBox
            {
                Width = 90,
                Height = 26,
                FontSize = 12,
                Background = new SolidColorBrush(Color.FromRgb(50, 50, 55)),
                Foreground = new SolidColorBrush(Colors.Black),
                BorderBrush = new SolidColorBrush(Color.FromRgb(80, 80, 90)),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            foreach (var item in items) combo.Items.Add(item);
            combo.SelectedItem = selected;
            if (combo.SelectedIndex < 0) combo.SelectedIndex = 0;
            return combo;
        }

        private CheckBox CreateCheckBox(string text, bool isChecked)
        {
            return new CheckBox
            {
                Content = text,
                IsChecked = isChecked,
                Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 210)),
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 8),
                VerticalContentAlignment = VerticalAlignment.Center
            };
        }
    }
}
