using System;
using System.IO;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Interop;
using SIPSorcery.Net;

namespace Viewer
{
    public class MainWindow : Window
    {
        // === 공용 필드 ===
        private SignalingClient? _signaling;
        private VideoReceiver? _receiver;
        private string? _connectedHostId;
        private KeyboardHook? _keyboardHook;
        private Timer? _statsDisplayTimer;
        private bool _isReconnecting = false;
        private string? _enteredPassword;

        public SignalingClient? Signaling => _signaling;
        public string? ConnectedHostId => _connectedHostId;
        public VideoReceiver? Receiver => _receiver;

        // === 로비 뷰 (리스트 + 그리드) ===
        private Grid _lobbyGrid = null!;
        private DataGrid _hostDataGrid = null!;
        private WrapPanel _thumbnailPanel = null!;
        private Grid _listTab = null!;
        private ScrollViewer _gridTab = null!;
        private TextBlock _statusBarText = null!;
        private TextBlock _hostCountText = null!;
        private List<HostInfo> _currentHosts = new();
        private Button _listTabBtn = null!;
        private Button _gridTabBtn = null!;

        // === 리모트 뷰 (영상 + 입력) ===
        private Grid _remoteGrid = null!;
        private Image _videoDisplay = null!;
        private TextBlock _statusText = null!;
        private TextBlock _statsOverlay = null!;
        private TextBlock _fileProgressOverlay = null!;

        // === 비밀번호 UI ===
        private StackPanel? _passwordPanel;
        private TextBox? _passwordBox;
        private TextBlock? _passwordStatus;

        // === 풀스크린 ===
        private bool _isFullscreen = false;
        private WindowState _prevWindowState;
        private WindowStyle _prevWindowStyle;
        private ResizeMode _prevResizeMode;

        // === Modifier 키 상태 ===
        private bool _ctrlPressed = false;
        private bool _shiftPressed = false;

        // === 설정 ===
        private AppSettings _settings = null!;

        // === 프로토콜 상수 ===
        private const byte MSG_KEY_DOWN   = 0x10;
        private const byte MSG_KEY_UP     = 0x11;
        private const byte MSG_MOUSE_MOVE = 0x01;  // Host InputSimulator와 일치
        private const byte MSG_MOUSE_DOWN = 0x02;
        private const byte MSG_MOUSE_UP   = 0x03;
        private const byte MSG_MOUSE_WHEEL= 0x04;

        // === 인증 토큰 & 사용자 ID ===
        private string _accessToken;
        private string _userId;
        
        // === 영구 호스트 저장소 ===
        private HostRepository? _hostRepo;
        private Dictionary<string, HostInfo> _persistentHosts = new();

        public MainWindow(string accessToken, string userId)
        {
            _accessToken = accessToken;
            _userId = userId;
            Console.WriteLine("[DEBUG] MainWindow constructor started");

            Title = "Comote Viewer";
            Background = new SolidColorBrush(Color.FromRgb(25, 25, 28));

            // 설정 로드
            _settings = AppSettings.Load();
            Width = _settings.RememberWindowSize ? _settings.WindowWidth : 1280;
            Height = _settings.RememberWindowSize ? _settings.WindowHeight : 720;
            Topmost = _settings.AlwaysOnTop;

            var rootGrid = new Grid();

            // ============================================
            // 1. 로비 뷰 (기본 화면)
            // ============================================
            _lobbyGrid = BuildLobbyView();
            rootGrid.Children.Add(_lobbyGrid);

            // ============================================
            // 2. 리모트 뷰 (연결 후 화면)
            // ============================================
            _remoteGrid = BuildRemoteView();
            _remoteGrid.Visibility = Visibility.Collapsed;
            rootGrid.Children.Add(_remoteGrid);

            Content = rootGrid;
            Console.WriteLine("[DEBUG] MainWindow constructor done");

            // 키보드 훅
            _keyboardHook = new KeyboardHook();
            _keyboardHook.OnKeyEvent += OnKeyHookEvent;
            _keyboardHook.IsCapturing = false; // 로비에서는 비활성화
            _keyboardHook.Start(); // 훅 설치 (필수)

            // 마우스 이벤트
            RegisterMouseEvents();
 
            // [무인 업데이트] 시작 시 최신 버전 체크
            _ = AutoUpdater.CheckAndApplyUpdate();
            SourceInitialized += MainWindow_SourceInitialized;
            Loaded += MainWindow_Loaded;
            
            // 포커스 관리
            Activated += (s, e) => UpdateInputCaptureState();
            Deactivated += (s, e) => UpdateInputCaptureState();
            Closed += (s, e) => _keyboardHook.Dispose();
        }

        // ==========================================================
        // 로비 뷰 빌드
        // ==========================================================
        private Grid BuildLobbyView()
        {
            var grid = new Grid();
            // --- 상단 메뉴바 --- (배경색 조정)
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });

            // 전체 배경
            grid.Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)); // #1E1E1E 느낌의 짙은 회색

            // --- 상단 메뉴바 ---
            var menuBar = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(40, 40, 40)), // Lighter header
                BorderThickness = new Thickness(0, 0, 0, 1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                Padding = new Thickness(16, 0, 16, 0)
            };
            var menuPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            menuPanel.Children.Add(new TextBlock
            {
                Text = "🖥️ Comote",
                Foreground = new SolidColorBrush(Color.FromRgb(100, 160, 255)),
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            });

            // ⚙ 설정 버튼 (우측 정렬)
            var settingsBtn = new Button
            {
                Content = "⚙ 설정",
                Background = new SolidColorBrush(Color.FromRgb(55, 55, 62)),
                Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 210)),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(12, 4, 12, 4),
                FontSize = 12,
                Cursor = System.Windows.Input.Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0)
            };
            settingsBtn.Click += (s, e) => OpenSettings();

            // DockPanel으로 좌/우 배치
            var menuDock = new DockPanel { LastChildFill = false };
            DockPanel.SetDock(menuPanel, Dock.Left);
            DockPanel.SetDock(settingsBtn, Dock.Right);
            menuDock.Children.Add(menuPanel);
            menuDock.Children.Add(settingsBtn);
            menuBar.Child = menuDock;

            Grid.SetRow(menuBar, 0);
            grid.Children.Add(menuBar);

            // --- 탭 바 ---
            var tabBar = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(30, 30, 35)),
                Padding = new Thickness(8, 0, 8, 0)
            };
            var tabPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _listTabBtn = CreateTabButton("📋 리스트", false);
            _listTabBtn.Click += (s, e) => SwitchLobbyTab(true);
            tabPanel.Children.Add(_listTabBtn);
            _gridTabBtn = CreateTabButton("🖥️ 모니터", true);
            _gridTabBtn.Click += (s, e) => SwitchLobbyTab(false);
            tabPanel.Children.Add(_gridTabBtn);
            tabBar.Child = tabPanel;
            Grid.SetRow(tabBar, 1);
            grid.Children.Add(tabBar);

            // --- 리스트 탭 (DataGrid) ---
            _listTab = new Grid();
            _hostDataGrid = new DataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly = true,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                Background = new SolidColorBrush(Color.FromRgb(25, 25, 28)),
                Foreground = new SolidColorBrush(Colors.White),
                BorderThickness = new Thickness(0),
                RowBackground = new SolidColorBrush(Color.FromRgb(30, 30, 35)),
                AlternatingRowBackground = new SolidColorBrush(Color.FromRgb(35, 35, 40)),
                HorizontalGridLinesBrush = new SolidColorBrush(Color.FromRgb(50, 50, 55)),
                SelectionMode = DataGridSelectionMode.Single,
                FontSize = 13
            };
            // 컬럼 정의
            _hostDataGrid.Columns.Add(new DataGridTextColumn { Header = "상태", Binding = new Binding("StatusText"), Width = 60 });
            _hostDataGrid.Columns.Add(new DataGridTextColumn { Header = "이름", Binding = new Binding("Name"), Width = 120 });
            _hostDataGrid.Columns.Add(new DataGridTextColumn { Header = "아이피", Binding = new Binding("Ip"), Width = 140 });
            _hostDataGrid.Columns.Add(new DataGridTextColumn { Header = "CPU", Binding = new Binding("CpuText"), Width = 60 });
            _hostDataGrid.Columns.Add(new DataGridTextColumn { Header = "RAM", Binding = new Binding("Ram"), Width = 120 });
            _hostDataGrid.Columns.Add(new DataGridTextColumn { Header = "HDD", Binding = new Binding("Hdd"), Width = 120 });
            _hostDataGrid.Columns.Add(new DataGridTextColumn { Header = "해상도", Binding = new Binding("Resolution"), Width = 100 });
            _hostDataGrid.Columns.Add(new DataGridTextColumn { Header = "가동시간", Binding = new Binding("Uptime"), Width = 100 });
            _hostDataGrid.MouseDoubleClick += HostDataGrid_DoubleClick;
            
            // 컨텍스트 메뉴
            var contextMenu = new ContextMenu();
            var sendFileMenuItem = new MenuItem { Header = "멀티 파일 전송" };
            sendFileMenuItem.Click += OnMultiFileTransferClick;
            contextMenu.Items.Add(sendFileMenuItem);
            _hostDataGrid.ContextMenu = contextMenu;

            _listTab.Children.Add(_hostDataGrid);
            Grid.SetRow(_listTab, 2);
            grid.Children.Add(_listTab);

            // --- 그리드 탭 (썸네일) ---
            // --- 그리드 탭 (썸네일) ---
            _gridTab = new ScrollViewer { 
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(10)
            };
            _thumbnailPanel = new WrapPanel { Margin = new Thickness(0) };
            _gridTab.Content = _thumbnailPanel;
            _gridTab.Visibility = Visibility.Visible; // 기본값 표시
            _listTab.Visibility = Visibility.Collapsed; // 리스트 숨김
            // 썸네일 패널에도 컨텍스트 메뉴 (빈 공간 클릭 시 혹은 아이템 클릭 시 처리 필요)
            // 개별 아이템에 메뉴를 달아야 함 -> CreateHostCard 수정 필요
            // 여기서는 전체 리스트 대상 메뉴만 우선 추가
            _gridTab.ContextMenu = contextMenu; 
            Grid.SetRow(_gridTab, 2);
            grid.Children.Add(_gridTab);

            // --- 하단 상태바 ---
            var statusBar = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(30, 30, 35)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(55, 55, 60)),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(12, 0, 12, 0)
            };
            var statusPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _statusBarText = new TextBlock
            {
                Text = "대기 중",
                Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 160)),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
            statusPanel.Children.Add(_statusBarText);
            statusPanel.Children.Add(new TextBlock
            {
                Text = " | ",
                Foreground = new SolidColorBrush(Color.FromRgb(60, 60, 65)),
                Margin = new Thickness(8, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            _hostCountText = new TextBlock
            {
                Text = "호스트: 0",
                Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 160)),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
            statusPanel.Children.Add(_hostCountText);
            statusBar.Child = statusPanel;
            Grid.SetRow(statusBar, 3);
            grid.Children.Add(statusBar);

            return grid;
        }

        // ==========================================================
        // 리모트 뷰 빌드
        // ==========================================================
        private Grid BuildRemoteView()
        {
            var grid = new Grid();

            // 영상 배경
            grid.Background = new SolidColorBrush(Colors.Black);

            // 비디오 디스플레이
            _videoDisplay = new Image
            {
                Stretch = System.Windows.Media.Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            grid.Children.Add(_videoDisplay);

            // 상태 텍스트 (연결 중 메시지)
            _statusText = new TextBlock
            {
                Text = "연결 중...",
                Foreground = new SolidColorBrush(Colors.White),
                FontSize = 16,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            grid.Children.Add(_statusText);

            // FPS/RTT 오버레이
            _statsOverlay = new TextBlock
            {
                Text = "",
                FontSize = 12,
                Foreground = new SolidColorBrush(Colors.Lime),
                Background = new SolidColorBrush(Color.FromArgb(128, 0, 0, 0)),
                Padding = new Thickness(6, 3, 6, 3),
                Margin = new Thickness(8, 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Visibility = Visibility.Collapsed
            };
            grid.Children.Add(_statsOverlay);

            // 상단 우측 버튼 컨테이너
            var topBar = new StackPanel 
            { 
                Orientation = Orientation.Horizontal, 
                HorizontalAlignment = HorizontalAlignment.Right, 
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 8, 8, 0)
            };

            // 화면 전환 버튼
            var monitorBtn = new Button
            {
                Content = "📺 화면 전환",
                FontSize = 12,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 8, 0),
                Background = new SolidColorBrush(Color.FromArgb(180, 60, 60, 60)),
                Foreground = new SolidColorBrush(Colors.White),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Opacity = 0.7
            };
            monitorBtn.MouseEnter += (s, e) => monitorBtn.Opacity = 1.0;
            monitorBtn.MouseLeave += (s, e) => monitorBtn.Opacity = 0.7;
            monitorBtn.Click += (s, e) => _receiver?.SendMonitorSwitch();
            topBar.Children.Add(monitorBtn);

            // 뒤로가기 버튼
            var backBtn = new Button
            {
                Content = "✕ 로비",
                FontSize = 12,
                Padding = new Thickness(10, 4, 10, 4),
                Background = new SolidColorBrush(Color.FromArgb(180, 60, 60, 60)),
                Foreground = new SolidColorBrush(Colors.White),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Opacity = 0.7
            };
            backBtn.MouseEnter += (s, e) => backBtn.Opacity = 1.0;
            backBtn.MouseLeave += (s, e) => backBtn.Opacity = 0.7;
            backBtn.Click += (s, e) => DisconnectAndReturnToLobby();
            topBar.Children.Add(backBtn);

            grid.Children.Add(topBar);

            // 비밀번호 패널
            BuildPasswordPanel(grid);

            // 파일 전송 프로그레스 오버레이
            _fileProgressOverlay = new TextBlock
            {
                Text = "",
                FontSize = 14,
                Foreground = new SolidColorBrush(Colors.White),
                Background = new SolidColorBrush(Color.FromArgb(200, 30, 30, 35)),
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 0, 0, 12),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Visibility = Visibility.Collapsed
            };
            grid.Children.Add(_fileProgressOverlay);

            // 드래그 & 드롭 파일 전송
            grid.AllowDrop = true;
            grid.DragOver += (s, e) =>
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                    e.Effects = DragDropEffects.Copy;
                else
                    e.Effects = DragDropEffects.None;
                e.Handled = true;
            };
            grid.Drop += async (s, e) =>
            {
                if (_receiver == null || _connectedHostId == null) return;
                if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
                string[]? files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files == null || files.Length == 0) return;

                foreach (var filePath in files)
                {
                    if (!File.Exists(filePath)) continue;
                    string name = Path.GetFileName(filePath);
                    _fileProgressOverlay.Text = $"📁 전송 중: {name} (0%)";
                    _fileProgressOverlay.Visibility = Visibility.Visible;

                    _receiver.OnFileProgress += (pct) =>
                    {
                        Dispatcher.BeginInvoke(() =>
                            _fileProgressOverlay.Text = $"📁 전송 중: {name} ({pct}%)");
                    };
                    _receiver.OnFileComplete += (msg) =>
                    {
                        Dispatcher.BeginInvoke(() =>
                        {
                            _fileProgressOverlay.Text = $"✅ {name} {msg}";
                            _ = Task.Run(async () =>
                            {
                                await Task.Delay(3000);
                                Dispatcher.BeginInvoke(() =>
                                    _fileProgressOverlay.Visibility = Visibility.Collapsed);
                            });
                        });
                    };

                    await _receiver.SendFileAsync(filePath);
                }
            };

            return grid;
        }

        private void BuildPasswordPanel(Grid parent)
        {
            _passwordPanel = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
                Width = 320
            };
            var cardBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(230, 30, 30, 30)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(80, 120, 200)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(24, 20, 24, 20),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Black, BlurRadius = 20, Opacity = 0.6, ShadowDepth = 0
                }
            };
            var innerPanel = new StackPanel();
            innerPanel.Children.Add(new TextBlock
            {
                Text = "🔒 패스워드",
                Foreground = new SolidColorBrush(Colors.White),
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 16)
            });
            _passwordBox = new TextBox
            {
                FontSize = 16,
                Padding = new Thickness(10, 8, 10, 8),
                Background = new SolidColorBrush(Color.FromRgb(50, 50, 55)),
                Foreground = new SolidColorBrush(Colors.White),
                BorderBrush = new SolidColorBrush(Color.FromRgb(90, 90, 100)),
                BorderThickness = new Thickness(1)
            };
            _passwordBox.KeyDown += (s, e) => { if (e.Key == Key.Enter) ConnectWithPassword(); };
            innerPanel.Children.Add(_passwordBox);
            _passwordStatus = new TextBlock
            {
                Text = "",
                Foreground = new SolidColorBrush(Color.FromRgb(255, 100, 100)),
                FontSize = 12,
                Margin = new Thickness(0, 6, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                Visibility = Visibility.Collapsed
            };
            innerPanel.Children.Add(_passwordStatus);
            var connectBtn = new Button
            {
                Content = "연결",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Padding = new Thickness(0, 10, 0, 10),
                Margin = new Thickness(0, 14, 0, 0),
                Background = new SolidColorBrush(Color.FromRgb(60, 110, 200)),
                Foreground = new SolidColorBrush(Colors.White),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
            connectBtn.Click += (s, e) => ConnectWithPassword();
            innerPanel.Children.Add(connectBtn);
            cardBorder.Child = innerPanel;
            _passwordPanel.Children.Add(cardBorder);
            parent.Children.Add(_passwordPanel);
        }

        // ==========================================================
        // 탭 전환
        // ==========================================================
        private Button CreateTabButton(string text, bool isActive)
        {
            return new Button
            {
                Content = text,
                FontSize = 13,
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 0, 4, 0),
                Background = new SolidColorBrush(isActive ? Color.FromRgb(60, 110, 200) : Color.FromRgb(45, 45, 50)),
                Foreground = new SolidColorBrush(Colors.White),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand
            };
        }

        private void SwitchLobbyTab(bool showList)
        {
            _listTab.Visibility = showList ? Visibility.Visible : Visibility.Collapsed;
            _gridTab.Visibility = showList ? Visibility.Collapsed : Visibility.Visible;
            _listTabBtn.Background = new SolidColorBrush(showList ? Color.FromRgb(60, 110, 200) : Color.FromRgb(45, 45, 50));
            _gridTabBtn.Background = new SolidColorBrush(showList ? Color.FromRgb(45, 45, 50) : Color.FromRgb(60, 110, 200));
        }

        // ==========================================================
        // 호스트 목록 업데이트 (로비)
        // ==========================================================
        private void UpdateLobbyUI(List<HostInfo> hosts)
        {
            _currentHosts = hosts;
            int onlineCount = 0;

            // DataGrid 바인딩용 래퍼 리스트
            var displayList = new List<HostDisplayItem>();
            _thumbnailPanel.Children.Clear();

            int index = 1;
            foreach (var host in hosts)
            {
                if (host.IsOnline) onlineCount++;

                // 리스트 뷰 데이터
                displayList.Add(new HostDisplayItem
                {
                    StatusText = host.IsOnline ? "ON" : "OFF",
                    Name = host.Name,
                    Ip = host.Ip,
                    CpuText = $"{host.Cpu}%",
                    Ram = host.Ram,
                    Hdd = host.Hdd,
                    Resolution = host.Resolution,
                    Uptime = host.Uptime,
                    HostId = host.Id,
                    Index = index
                });

                // 썸네일 카드
                var card = CreateHostCard(host, index);
                _thumbnailPanel.Children.Add(card);
                index++;
            }

            _hostDataGrid.ItemsSource = displayList;
            _hostCountText.Text = $"호스트: {onlineCount}/{hosts.Count}";
            _statusBarText.Text = onlineCount > 0 ? "접속 가능" : "대기 중";
        }

        private Border CreateHostCard(HostInfo host, int index)
        {
            // === 카드 스타일 (Mockup: #252526 Background, Rounded, Shadow) ===
            var card = new Border
            {
                Width = 240,  // 넓게
                Height = 210, // 컴팩트하게 -> 버튼 공간 확보 위해 늘림
                Margin = new Thickness(10),
                Background = new SolidColorBrush(Color.FromRgb(37, 37, 38)), // #252526
                BorderBrush = new SolidColorBrush(Color.FromRgb(60, 60, 60)), // Subtle border
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Cursor = Cursors.Hand,
                Tag = host.Id,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Black, BlurRadius = 15, Opacity = 0.3, ShadowDepth = 4, Direction = 270
                }
            };

            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) }); // Header
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Content (Spacer)
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) }); // CPU
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) }); // Details
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) }); // Footer

            // === 1. 헤더: 이름 & 상태 ===
            // Mockup: "DESKTOP-ABC" (White, Bold) 아래에 "Online" (Green dot + Text)
            var headerStack = new StackPanel { Orientation = Orientation.Vertical };
            
            // 호스트 이름
            headerStack.Children.Add(new TextBlock
            {
                Text = host.Name.ToUpper(), // 대문자로 스타일링
                Foreground = new SolidColorBrush(Colors.White),
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            // 상태 표시줄 (점 + 텍스트)
            var statusPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            statusPanel.Children.Add(new Border
            {
                Width = 8, Height = 8, CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(host.IsOnline ? Color.FromRgb(50, 205, 50) : Color.FromRgb(100, 100, 100)), // LimeGreen
                Margin = new Thickness(0, 1, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            statusPanel.Children.Add(new TextBlock
            {
                Text = host.IsOnline ? "Online" : "Offline",
                Foreground = new SolidColorBrush(host.IsOnline ? Color.FromRgb(150, 200, 150) : Color.FromRgb(120, 120, 120)),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            });
            headerStack.Children.Add(statusPanel);
            
            Grid.SetRow(headerStack, 0);
            grid.Children.Add(headerStack);

            // === 2. 본문: CPU Bar ===
            // Mockup: 중간에 위치
            var cpuPanel = new StackPanel { Margin = new Thickness(0, 16, 0, 8) };
            
            // "CPU: 45%" 텍스트가 바 위에 있거나 옆에 있음. Mockup은 바 위에 텍스트 포지셔닝되거나 별도.
            // 여기선 바 위에 텍스트를 배치하고 아래에 바.
            var cpuHeader = new DockPanel { LastChildFill = false };
            cpuHeader.Children.Add(new TextBlock { Text = "CPU", Foreground = new SolidColorBrush(Color.FromRgb(120, 120, 120)), FontSize = 10 });
            var cpuText = new TextBlock { Text = $"{host.Cpu}%", Foreground = new SolidColorBrush(Colors.White), FontSize = 10, FontWeight = FontWeights.SemiBold };
            cpuHeader.Children.Add(cpuText);
            DockPanel.SetDock(cpuText, Dock.Right);
            
            cpuPanel.Children.Add(cpuHeader);

            // Progress Bar Track
            var track = new Border 
            { 
                Height = 4, Background = new SolidColorBrush(Color.FromRgb(50, 50, 50)), CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 4, 0, 0) 
            };
            
            // Progress Fill
            // Width 계산: (Percent / 100) * (CardWidth - Padding)
            // 하지만 Grid 안이라 Width를 알기 어려움. Grid 사용.
            var fillGrid = new Grid();
            fillGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(host.Cpu, GridUnitType.Star) });
            fillGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100 - host.Cpu, GridUnitType.Star) });
            
            var fill = new Border 
            { 
                Background = new SolidColorBrush(Color.FromRgb(50, 205, 50)), // LimeGreen #32CD32
                CornerRadius = new CornerRadius(2)
            };
            if (host.Cpu > 80) fill.Background = new SolidColorBrush(Color.FromRgb(220, 60, 60)); // Red warning
            
            Grid.SetColumn(fill, 0);
            fillGrid.Children.Add(fill);
            track.Child = fillGrid;
            
            cpuPanel.Children.Add(track);

            Grid.SetRow(cpuPanel, 2);
            grid.Children.Add(cpuPanel);

            // === 3. 상세 정보 (RAM, IP) ===
            var detailsPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            detailsPanel.Children.Add(new TextBlock 
            { 
                Text = $"RAM: {host.Ram}", 
                Foreground = new SolidColorBrush(Color.FromRgb(130, 130, 130)), 
                FontSize = 11, Margin = new Thickness(0, 0, 0, 2)
            });
             detailsPanel.Children.Add(new TextBlock 
            { 
                Text = $"{host.Ip}", 
                Foreground = new SolidColorBrush(Color.FromRgb(80, 80, 80)), 
                FontSize = 10 
            });
            Grid.SetRow(detailsPanel, 3);
            grid.Children.Add(detailsPanel);

            // === 4. 푸터: Connect 버튼 ===
            // Mockup: 파란색(#007ACC), 꽉 참.
            var connectBtn = new Button
            {
                Content = "Connect",
                Height = 30,
                Background = new SolidColorBrush(host.IsOnline ? Color.FromRgb(0, 122, 204) : Color.FromRgb(60, 60, 60)), // VS Blue #007ACC
                Foreground = new SolidColorBrush(Colors.White),
                FontWeight = FontWeights.Normal,
                FontSize = 12,
                BorderThickness = new Thickness(0),
                Cursor = host.IsOnline ? Cursors.Hand : Cursors.No,
                IsEnabled = host.IsOnline
            };
            // Style
            var btnStyle = new Style(typeof(Button));
            var template = new ControlTemplate(typeof(Button));
            var factory = new FrameworkElementFactory(typeof(Border));
            factory.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            factory.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            var contentPresenter = new FrameworkElementFactory(typeof(ContentPresenter));
            contentPresenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            contentPresenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            factory.AppendChild(contentPresenter);
            template.VisualTree = factory;
            btnStyle.Setters.Add(new Setter(Button.TemplateProperty, template));
            connectBtn.Style = btnStyle;

            if (host.IsOnline)
            {
                connectBtn.MouseEnter += (s, e) => connectBtn.Background = new SolidColorBrush(Color.FromRgb(28, 151, 234)); // Lighter Blue
                connectBtn.MouseLeave += (s, e) => connectBtn.Background = new SolidColorBrush(Color.FromRgb(0, 122, 204));
                connectBtn.Click += (s, e) => ConnectToHost(host.Id);
                card.MouseLeftButtonDown += (s, e) => { if (e.ClickCount == 2) ConnectToHost(host.Id); };
            }

            Grid.SetRow(connectBtn, 4);
            grid.Children.Add(connectBtn);

            card.Child = grid;
            return card;
        }

        // CreateStatRow는 이제 사용하지 않음 (CreateHostCard 내에 인라인으로 구현하여 정밀 제어)
        private Grid CreateStatRow(string label, string valueText, int percentage)
        {
            return new Grid(); // Dummy
        }

        // ==========================================================
        // 입력 캡처 상태 업데이트
        // ==========================================================
        private void UpdateInputCaptureState()
        {
            if (_keyboardHook == null) return;
            // 리모트 뷰가 보이고 + 창이 활성화 상태 + 패스워드 패널이 안 보일 때만 캡처
            bool isPasswordInput = _passwordPanel != null && _passwordPanel.Visibility == Visibility.Visible;
            bool shouldCapture = (_remoteGrid.Visibility == Visibility.Visible) && IsActive && !isPasswordInput;
            _keyboardHook.IsCapturing = shouldCapture;
        }

        // ==========================================================
        // 호스트 연결 / 해제
        // ==========================================================
        private async void ConnectToHost(string hostId)
        {
            _connectedHostId = hostId;

            // 리모트 뷰로 전환
            _lobbyGrid.Visibility = Visibility.Collapsed;
            _remoteGrid.Visibility = Visibility.Visible;

            _statusText.Text = "연결 중...";
            _statusText.Visibility = Visibility.Visible;
            _statsOverlay.Visibility = Visibility.Collapsed;

            // 키보드 훅 활성화 (상태 업데이트)
            UpdateInputCaptureState();

            // 바로 연결 시도 (비밀번호 없이)
            try
            {
                InitializeReceiver();
                await _receiver!.StartAsync(null);
                Console.WriteLine($"[UI] Connecting to {hostId} (no password)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UI] ConnectToHost error: {ex.Message}");
                _statusText.Text = $"연결 오류: {ex.Message}";
            }
        }

        private void DisconnectAndReturnToLobby()
        {
            // 풀스크린 해제
            if (_isFullscreen) ToggleFullscreen();

            // 키보드 훅 비활성화 (나중에 상태 업데이트로 처리)
            // VideoReceiver 정리
            if (_receiver != null)
            {
                _receiver.Dispose();
                _receiver = null;
            }

            _connectedHostId = null;
            _enteredPassword = null;
            _isReconnecting = false;
            _statsDisplayTimer?.Dispose();
            _statsDisplayTimer = null;

            // 로비로 전환
            _remoteGrid.Visibility = Visibility.Collapsed;
            _lobbyGrid.Visibility = Visibility.Visible;
            _passwordPanel!.Visibility = Visibility.Collapsed;

            UpdateInputCaptureState();

            Console.WriteLine("[UI] Returned to lobby");
        }

        private void HostDataGrid_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (_hostDataGrid.SelectedItem is HostDisplayItem item && item.StatusText == "ON")
            {
                ConnectToHost(item.HostId);
            }
        }

        private void OnMultiFileTransferClick(object sender, RoutedEventArgs e)
        {
            var selectedHosts = new List<HostInfo>();
            foreach (var item in _hostDataGrid.SelectedItems)
            {
                if (item is HostDisplayItem d) 
                {
                    var info = _currentHosts.Find(h => h.Id == d.HostId);
                    if (info != null) selectedHosts.Add(info);
                }
            }

            if (selectedHosts.Count == 0)
            {
                MessageBox.Show("전송할 PC를 선택해주세요.");
                return;
            }
            
            // 연결된 호스트 제외 (충돌 방지)
            if (_connectedHostId != null)
            {
                selectedHosts.RemoveAll(h => h.Id == _connectedHostId);
                if (selectedHosts.Count == 0)
                {
                    MessageBox.Show("현재 원격 제어 중인 PC로는 멀티 파일 전송을 할 수 없습니다.\n제어 창의 드래그&드롭을 이용하세요.");
                    return;
                }
            }

            var win = new MultiFileTransferWindow(selectedHosts);
            win.Owner = this;
            win.ShowDialog();
        }

        /// <summary>
        /// DisconnectAndReturnToLobby에서 _enteredPassword 초기화
        /// </summary>

        // ==========================================================
        // 마우스 이벤트 등록 (비디오 표시 영역 기준)
        // ==========================================================
        private void RegisterMouseEvents()
        {
            _videoDisplay.MouseMove += (s, e) =>
            {
                if (_receiver == null || _connectedHostId == null) return;
                var pos = e.GetPosition(_videoDisplay);
                double nx = pos.X / _videoDisplay.ActualWidth;
                double ny = pos.Y / _videoDisplay.ActualHeight;
                if (nx < 0 || nx > 1 || ny < 0 || ny > 1) return;

                var data = new byte[9];
                data[0] = MSG_MOUSE_MOVE;
                BitConverter.GetBytes((float)nx).CopyTo(data, 1);
                BitConverter.GetBytes((float)ny).CopyTo(data, 5);
                _receiver.SendInput(data);
            };

            _videoDisplay.MouseDown += (s, e) =>
            {
                if (_receiver == null || _connectedHostId == null) return;
                _videoDisplay.CaptureMouse();
                var pos = e.GetPosition(_videoDisplay);
                float nx = (float)(pos.X / _videoDisplay.ActualWidth);
                float ny = (float)(pos.Y / _videoDisplay.ActualHeight);
                byte btn = e.ChangedButton == MouseButton.Left ? (byte)0 :
                           e.ChangedButton == MouseButton.Right ? (byte)1 : (byte)2;
                // 프로토콜: {type, button, float_x, float_y} = 10바이트
                var data = new byte[10];
                data[0] = MSG_MOUSE_DOWN;
                data[1] = btn;
                BitConverter.GetBytes(nx).CopyTo(data, 2);
                BitConverter.GetBytes(ny).CopyTo(data, 6);
                _receiver.SendInput(data);
            };

            _videoDisplay.MouseUp += (s, e) =>
            {
                if (_receiver == null || _connectedHostId == null) return;
                _videoDisplay.ReleaseMouseCapture();
                var pos = e.GetPosition(_videoDisplay);
                float nx = (float)(pos.X / _videoDisplay.ActualWidth);
                float ny = (float)(pos.Y / _videoDisplay.ActualHeight);
                byte btn = e.ChangedButton == MouseButton.Left ? (byte)0 :
                           e.ChangedButton == MouseButton.Right ? (byte)1 : (byte)2;
                var data = new byte[10];
                data[0] = MSG_MOUSE_UP;
                data[1] = btn;
                BitConverter.GetBytes(nx).CopyTo(data, 2);
                BitConverter.GetBytes(ny).CopyTo(data, 6);
                _receiver.SendInput(data);
            };

            _videoDisplay.MouseWheel += (s, e) =>
            {
                if (_receiver == null || _connectedHostId == null) return;
                // 프로토콜: {type, int_delta} = 5바이트
                var data = new byte[5];
                data[0] = MSG_MOUSE_WHEEL;
                // 휘 민감도 적용
                int delta = (int)(e.Delta * _settings.GetWheelMultiplier());
                BitConverter.GetBytes(delta).CopyTo(data, 1);
                _receiver.SendInput(data);
            };
        }

        // ==========================================================
        // 환경 설정
        // ==========================================================
        private void OpenSettings()
        {
            var win = new SettingsWindow(_settings);
            win.Owner = this;
            win.ShowDialog();
            if (win.SettingsChanged)
            {
                Topmost = _settings.AlwaysOnTop;
                Console.WriteLine("[Settings] Applied");
            }
        }

        // 클립보드 감지 관련
        private const int WM_CLIPBOARDUPDATE = 0x031D;
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool AddClipboardFormatListener(IntPtr hwnd);
        private IntPtr _windowHandle;

        private void MainWindow_SourceInitialized(object? sender, EventArgs e)
        {
            _windowHandle = new WindowInteropHelper(this).Handle;
            HwndSource source = HwndSource.FromHwnd(_windowHandle);
            source.AddHook(WndProc);
            AddClipboardFormatListener(_windowHandle);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_CLIPBOARDUPDATE)
            {
                OnClipboardUpdated();
            }
            return IntPtr.Zero;
        }

        private string? _lastSentClipboardText = null;
        private void OnClipboardUpdated()
        {
            if (_receiver == null || !_settings.AutoClipboard) return;

            try
            {
                if (Clipboard.ContainsText())
                {
                    string text = Clipboard.GetText();
                    // 무한 루프 방지 (내가 방금 받은 텍스트라면 무시)
                    if (text != _lastSentClipboardText)
                    {
                        _lastSentClipboardText = text;
                        _receiver.SendClipboard(text);
                        Console.WriteLine($"[Clipboard] Auto-Sent to Host ({text.Length} chars)");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Clipboard] Monitoring error: {ex.Message}");
            }
        }
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (_settings.RememberWindowSize)
            {
                _settings.WindowWidth = Width;
                _settings.WindowHeight = Height;
                _settings.Save();
            }
            base.OnClosing(e);
        }

        // ==========================================================
        // 키보드 훅 이벤트
        // ==========================================================
        private void OnKeyHookEvent(ushort vk, bool isDown)
        {
            // Modifier 키 상태 추적 (로컬 단축키 판단용, Host로도 전달)
            if (vk == 0xA2 || vk == 0xA3) _ctrlPressed = isDown;
            if (vk == 0xA0 || vk == 0xA1) _shiftPressed = isDown;

            if (_receiver == null || _connectedHostId == null) return;

            // Ctrl+Shift+F: 풀스크린 토글
            if (vk == 0x46 && isDown && _ctrlPressed && _shiftPressed)
            {
                Dispatcher.Invoke(ToggleFullscreen);
                return;
            }

            // Ctrl+Shift+Q: 로비로 복귀 (연결 종료)
            if (vk == 0x51 && isDown && _ctrlPressed && _shiftPressed)
            {
                Dispatcher.Invoke(DisconnectAndReturnToLobby);
                return;
            }

            // ESC: 키 전달 (기존 로비 복귀 로직 삭제됨)

            // Ctrl+C: 키 전달 (클립보드 동기화는 자동 감지 로직에서 처리)
            if (vk == 0x43 && isDown && _ctrlPressed && !_shiftPressed)
            {
                var keyData = new byte[3];
                keyData[0] = MSG_KEY_DOWN;
                BitConverter.GetBytes(vk).CopyTo(keyData, 1);
                _receiver?.SendInput(keyData);
                return;
            }

            var data = new byte[3];
            data[0] = isDown ? MSG_KEY_DOWN : MSG_KEY_UP;
            BitConverter.GetBytes(vk).CopyTo(data, 1);
            _receiver?.SendInput(data);
        }

        // ==========================================================
        // 비밀번호 관련
        // ==========================================================
        private void ShowPasswordPanel(string? errorMsg = null)
        {
            _passwordPanel!.Visibility = Visibility.Visible;
            _passwordBox!.Text = "";
            _passwordBox.Focus();

            if (errorMsg != null)
            {
                _passwordStatus!.Text = errorMsg;
                _passwordStatus.Visibility = Visibility.Visible;
            }
            else
            {
                _passwordStatus!.Visibility = Visibility.Collapsed;
            }

            if (_keyboardHook != null) _keyboardHook.IsCapturing = false;
        }

        private async void ConnectWithPassword()
        {
            // 빈 문자열은 null로 치환 (비밀번호 없음과 동일하게 취급)
            string? pwd = _passwordBox?.Text;
            _enteredPassword = string.IsNullOrWhiteSpace(pwd) ? null : pwd;

            _passwordPanel!.Visibility = Visibility.Collapsed;
            _statusText.Text = "연결 중...";
            _statusText.Visibility = Visibility.Visible;

            UpdateInputCaptureState();

            try
            {
                // 기존 receiver가 있으면 Reset 후 재시도
                if (_receiver != null)
                {
                    _receiver.Reset();
                    await _receiver.StartAsync(_enteredPassword);
                    Console.WriteLine($"[UI] Reconnecting with password (len={_enteredPassword?.Length ?? 0})");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UI] ConnectWithPassword error: {ex.Message}");
                _statusText.Text = $"연결 오류: {ex.Message}";
            }
        }
        // ==========================================================
        // Supabase 호스트 폴링 (Presence 채널 대체)
        // last_seen이 60초 이내면 Online, 아니면 Offline
        // ==========================================================
        private async Task PollHostsFromSupabaseAsync()
        {
            if (_hostRepo == null) return;
            try
            {
                var hosts = await _hostRepo.GetHostsAsync();
                var now = DateTime.UtcNow;

                Dispatcher.Invoke(() =>
                {
                    // 기존 호스트 모두 Offline으로 초기화
                    foreach (var kvp in _persistentHosts)
                    {
                        kvp.Value.IsOnline = false;
                    }

                    foreach (var h in hosts)
                    {
                        bool isOnline = (now - h.LastSeen).TotalSeconds < 60;

                        if (_persistentHosts.ContainsKey(h.HostId))
                        {
                            var existing = _persistentHosts[h.HostId];
                            existing.IsOnline = isOnline;
                            existing.Name = h.HostName ?? existing.Name;
                            existing.Ip = h.Ip ?? existing.Ip;
                            existing.Resolution = h.Resolution ?? existing.Resolution;
                            existing.Cpu = h.Cpu;
                            existing.Ram = h.Ram ?? existing.Ram;
                            existing.Hdd = h.Hdd ?? existing.Hdd;
                            existing.Uptime = h.Uptime ?? existing.Uptime;
                            existing.LastSeen = h.LastSeen;
                        }
                        else
                        {
                            _persistentHosts[h.HostId] = new HostInfo
                            {
                                Id = h.HostId,
                                Name = h.HostName ?? h.HostId,
                                IsOnline = isOnline,
                                Ip = h.Ip ?? "unknown",
                                Resolution = h.Resolution ?? "N/A",
                                Cpu = h.Cpu,
                                Ram = h.Ram ?? "N/A",
                                Hdd = h.Hdd ?? "N/A",
                                Uptime = h.Uptime ?? "N/A",
                                LastSeen = h.LastSeen
                            };
                        }
                    }

                    UpdateLobbyUI(_persistentHosts.Values.ToList());
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Poll] Error polling hosts: {ex.Message}");
            }
        }

        // ==========================================================
        // 풀스크린
        // ==========================================================
        private void ToggleFullscreen()
        {
            if (_isFullscreen)
            {
                WindowStyle = _prevWindowStyle;
                ResizeMode = _prevResizeMode;
                WindowState = _prevWindowState;
                _isFullscreen = false;
                Console.WriteLine("[UI] Windowed mode");
            }
            else
            {
                _prevWindowState = WindowState;
                _prevWindowStyle = WindowStyle;
                _prevResizeMode = ResizeMode;

                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.NoResize;
                WindowState = WindowState.Maximized;
                _isFullscreen = true;
                Console.WriteLine("[UI] Fullscreen mode");
            }
        }

        // ==========================================================
        // 초기화 (Loaded)
        // ==========================================================
        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Console.WriteLine("[DEBUG] MainWindow_Loaded fired");

            try
            {
                // Signaling Client 초기화
                _signaling = new SignalingClient(
                    _settings.PusherAppId, 
                    _settings.PusherAppKey, 
                    _settings.PusherCluster,
                    _settings.WebAuthUrl,
                    _accessToken);

                // Repository 초기화
                if (!string.IsNullOrEmpty(_settings.SupabaseUrl) && !string.IsNullOrEmpty(_settings.SupabaseAnonKey))
                {
                    _hostRepo = new HostRepository(_settings.SupabaseUrl, _settings.SupabaseAnonKey);
                    await _hostRepo.InitializeAsync(_accessToken, _userId);
                }

                // Signal Received (WebRTC)
                _signaling.OnSignalReceived += async (from, signal) =>
                {
                    Console.WriteLine($"[Signaling] Signal from {from}: {signal}");
                    if (_receiver != null)
                    {
                        await _receiver.HandleSignalAsync(from, signal);
                    }
                };

                Console.WriteLine("[DEBUG] Connecting to signaling server...");
                await _signaling.ConnectAsync();
                Console.WriteLine("[DEBUG] Signaling connected");

                // Supabase에서 호스트 목록 로드 + 폴링 시작
                // (Presence 채널 대체 → 10초마다 Supabase 폴링)
                if (_hostRepo != null)
                {
                    // 최초 로드
                    await PollHostsFromSupabaseAsync();

                    // 10초마다 폴링 타이머
                    var pollTimer = new Timer(async _ =>
                    {
                        try { await PollHostsFromSupabaseAsync(); }
                        catch (Exception ex) { Console.WriteLine($"[Poll] Error: {ex.Message}"); }
                    }, null, 10000, 10000);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] MainWindow_Loaded: {ex.Message}");
                _statusBarText.Text = $"오류: {ex.Message}";
            }
        }

        // ==========================================================
        // VideoReceiver 초기화
        // ==========================================================
        private void InitializeReceiver()
        {
            _receiver = new VideoReceiver();

            // 시그널 전송
            _receiver.OnSignalReady += async (signal) =>
            {
                if (_signaling != null && _connectedHostId != null)
                {
                    await _signaling.SendSignalAsync(_connectedHostId, signal);
                }
            };

            // 비디오 프레임 수신
            _receiver.OnFrameReady += () =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (_receiver?.VideoBitmap != null)
                    {
                        _videoDisplay.Source = _receiver.VideoBitmap;
                        _statusText.Visibility = Visibility.Collapsed;
                        _statsOverlay.Visibility = Visibility.Visible;
                    }
                });
            };

            // 연결 상태 변경
            _receiver.OnConnectionStateChanged += (state) =>
            {
                if (state == RTCPeerConnectionState.failed ||
                    state == RTCPeerConnectionState.disconnected)
                {
                    _ = ReconnectAsync();
                }
            };

            // 비밀번호 거절
            _receiver.OnRejected += (reason) =>
            {
                Dispatcher.Invoke(() =>
                {
                    ShowPasswordPanel("비밀번호가 틀립니다. 다시 입력해 주세요.");
                });
            };

            // 클립보드 수신
            _receiver.OnClipboardReceived += (text) =>
            {
                Dispatcher.Invoke(() =>
                {
                    try { Clipboard.SetText(text); }
                    catch { }
                });
            };

            // Stats 표시 타이머 (1초 간격)
            _statsDisplayTimer?.Dispose();
            _statsDisplayTimer = new Timer(_ =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (_receiver != null && _statsOverlay.Visibility == Visibility.Visible)
                    {
                        string rtt = _receiver.RttMs >= 0 ? $"{_receiver.RttMs}ms" : "N/A";
                        _statsOverlay.Text = $"FPS: {_receiver.CurrentFps} | RTT: {rtt}";
                    }
                });
            }, null, 1000, 1000);
        }

        // ==========================================================
        // 자동 재연결
        // ==========================================================
        private async Task ReconnectAsync()
        {
            if (_isReconnecting || _connectedHostId == null) return;
            _isReconnecting = true;

            Console.WriteLine("[UI] Connection lost, reconnecting in 3s...");
            Dispatcher.Invoke(() =>
            {
                _statusText.Text = "연결 끊김. 3초 후 재연결...";
                _statusText.Visibility = Visibility.Visible;
                _statsOverlay.Visibility = Visibility.Collapsed;
            });

            await Task.Delay(3000);

            if (_receiver != null && _connectedHostId != null)
            {
                try
                {
                    _receiver.Reset();
                    await _receiver.StartAsync(_enteredPassword);
                    Console.WriteLine("[UI] Reconnect offer sent");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[UI] Reconnect failed: {ex.Message}");
                }
            }

            _isReconnecting = false;
        }
    }

    /// <summary>
    /// DataGrid 표시용 래퍼 클래스
    /// </summary>
    public class HostDisplayItem
    {
        public string StatusText { get; set; } = "";
        public string Name { get; set; } = "";
        public string Ip { get; set; } = "";
        public string CpuText { get; set; } = "";
        public string Ram { get; set; } = "";
        public string Hdd { get; set; } = "";
        public string Resolution { get; set; } = "";
        public string Uptime { get; set; } = "";
        public string HostId { get; set; } = "";
        public int Index { get; set; }
    }
}