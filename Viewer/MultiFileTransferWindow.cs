using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace Viewer
{
    /// <summary>
    /// 여러 호스트에게 파일을 일괄 전송하는 윈도우.
    /// </summary>
    public class MultiFileTransferWindow : Window
    {
        private List<HostInfo> _targetHosts;
        private List<string> _selectedFiles = new();
        private List<FileTransferTask> _transferTasks = new();
        
        // UI
        private ListBox _fileListBox;
        private ComboBox _targetFolderCombo;
        private DataGrid _progressGrid;
        private Button _startBtn;
        private Button _stopBtn;
        private TextBlock _statusText;

        public MultiFileTransferWindow(List<HostInfo> targets)
        {
            _targetHosts = targets;
            Title = "파일 전송";
            Width = 800;
            Height = 600;
            Background = new SolidColorBrush(Color.FromRgb(240, 240, 240)); // 밝은 테마 (참고 이미지)
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var mainGrid = new Grid { Margin = new Thickness(10) };
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(80) }); // 헤더
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(200) }); // 파일 선택 & 대상 목록
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(50) });  // 대상 폴더
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 진행 리스트
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });  // 하단 버튼

            // === 1. 헤더 ===
            var headerPanel = new StackPanel 
            { 
                Orientation = Orientation.Horizontal, 
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            // (이미지 대신 텍스트로 대체)
            headerPanel.Children.Add(CreateHeaderIcon("Manager PC", Colors.Orange));
            headerPanel.Children.Add(new TextBlock { Text = " > ", FontSize = 24, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20,0,20,0) });
            headerPanel.Children.Add(CreateHeaderIcon("Client PC", Colors.DodgerBlue));
            Grid.SetRow(headerPanel, 0);
            mainGrid.Children.Add(headerPanel);

            // === 2. 파일 선택 & 대상 목록 (Top Split) ===
            var topSplit = new Grid();
            topSplit.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topSplit.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });

            // 좌측: 파일 리스트
            var fileGroup = new DockPanel { Margin = new Thickness(0,0,10,0) };
            var fileLabelPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,0,0,5) };
            DockPanel.SetDock(fileLabelPanel, Dock.Top);
            fileLabelPanel.Children.Add(new TextBlock { Text = "💠 전송할 파일들", FontWeight = FontWeights.Bold });
            fileLabelPanel.Children.Add(new TextBlock { Text = " ※ 파일 및 폴더는 ctrl+c로 추가가능 (구현예정)", FontSize = 11, Foreground = Brushes.Gray, Margin = new Thickness(10,0,0,0) });
            
            var fileBtn = new Button { Content = "파일선택", Padding = new Thickness(10,2,10,2) };
            DockPanel.SetDock(fileBtn, Dock.Right);
            fileBtn.Click += OnSelectFiles;
            fileLabelPanel.Children.Add(new Border { Width = 100 }); // Spacer (hack)
            fileLabelPanel.Children.Add(fileBtn); // Button is usually simpler to place directly, mostly reusing DockPanel logic or Grid

            fileGroup.Children.Add(fileLabelPanel); // Re-add correctly
            // Button placement fix
            var fileHeaderGrid = new Grid { Margin = new Thickness(0,0,0,5) };
            DockPanel.SetDock(fileHeaderGrid, Dock.Top);
            fileHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            fileHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            fileHeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            
            var t1 = new TextBlock { Text = "💠 전송할 파일들", FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
            var t2 = new TextBlock { Text = " ※ 드래그 & 드롭 가능", FontSize = 11, Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10,0,0,0) };
            var b1 = new Button { Content = "파일선택", Padding = new Thickness(8,2,8,2) };
            b1.Click += OnSelectFiles;

            fileHeaderGrid.Children.Add(t1); Grid.SetColumn(t1, 0);
            fileHeaderGrid.Children.Add(t2); Grid.SetColumn(t2, 1);
            fileHeaderGrid.Children.Add(b1); Grid.SetColumn(b1, 2);
            fileGroup.Children.Add(fileHeaderGrid);

            _fileListBox = new ListBox { AllowDrop = true };
            _fileListBox.Drop += OnFileDrop;
            fileGroup.Children.Add(_fileListBox);

            Grid.SetColumn(fileGroup, 0);
            topSplit.Children.Add(fileGroup);

            // 우측: 전송 대상
            var targetGroup = new DockPanel();
            var tHeader = new TextBlock { Text = "전송대상 PC", Margin = new Thickness(0,0,0,5) };
            DockPanel.SetDock(tHeader, Dock.Top);
            targetGroup.Children.Add(tHeader);
            
            var targetList = new ListBox();
            foreach (var h in _targetHosts) targetList.Items.Add($"{h.Name} ({h.Id})");
            targetGroup.Children.Add(targetList);

            var tFooter = new TextBlock { Text = $"{_targetHosts.Count} slot 선택", HorizontalAlignment = HorizontalAlignment.Right, Foreground = Brushes.Blue, Margin = new Thickness(0,5,0,0) };
            DockPanel.SetDock(tFooter, Dock.Bottom);
            targetGroup.Children.Add(tFooter);

            Grid.SetColumn(targetGroup, 1);
            topSplit.Children.Add(targetGroup);

            Grid.SetRow(topSplit, 1);
            mainGrid.Children.Add(topSplit);

            // === 3. 대상 폴더 ===
            var folderPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            folderPanel.Children.Add(new TextBlock { Text = "💠 전송대상 폴더 : ", FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
            _targetFolderCombo = new ComboBox { Width = 300, IsEditable = true, Margin = new Thickness(5,0,0,0) };
            _targetFolderCombo.Items.Add("바탕화면");
            _targetFolderCombo.Items.Add("내 문서");
            _targetFolderCombo.SelectedIndex = 0;
            folderPanel.Children.Add(_targetFolderCombo);
            
            var folderGrid = new Grid(); // Wrapper to create background or spacing if needed
            folderGrid.Children.Add(folderPanel);
            
            Grid.SetRow(folderGrid, 2);
            mainGrid.Children.Add(folderGrid);

            // === 4. 진행 리스트 ===
            _progressGrid = new DataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly = true,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal
            };
            _progressGrid.Columns.Add(new DataGridTextColumn { Header = "파일", Binding = new System.Windows.Data.Binding("FileName"), Width = 200 });
            _progressGrid.Columns.Add(new DataGridTextColumn { Header = "대상", Binding = new System.Windows.Data.Binding("HostName"), Width = 100 });
            _progressGrid.Columns.Add(new DataGridTextColumn { Header = "속도", Binding = new System.Windows.Data.Binding("SpeedText"), Width = 80 });
            _progressGrid.Columns.Add(new DataGridTextColumn { Header = "결과", Binding = new System.Windows.Data.Binding("Status"), Width = 80 });
            _progressGrid.Columns.Add(new DataGridTemplateColumn 
            { 
                Header = "진행률", 
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                CellTemplate = CreateProgressTemplate()
            });

            Grid.SetRow(_progressGrid, 3);
            mainGrid.Children.Add(_progressGrid);

            // === 5. 하단 버튼 ===
            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            _startBtn = new Button { Content = "전송시작", Width = 80, Margin = new Thickness(5) };
            _startBtn.Click += OnStartTransfer;
            _stopBtn = new Button { Content = "전송중단", Width = 80, Margin = new Thickness(5), IsEnabled = false };
            _stopBtn.Click += (s,e) => { /* Cancel Logic */ }; // TODO
            var closeBtn = new Button { Content = "닫기", Width = 80, Margin = new Thickness(5) };
            closeBtn.Click += (s,e) => Close();

            btnPanel.Children.Add(_startBtn);
            btnPanel.Children.Add(_stopBtn);
            btnPanel.Children.Add(closeBtn);

            Grid.SetRow(btnPanel, 4);
            mainGrid.Children.Add(btnPanel);

            Content = mainGrid;
        }

        private UIElement CreateHeaderIcon(string text, Color color)
        {
            var grid = new Grid();
            var shape = new System.Windows.Shapes.Rectangle 
            { 
                Width = 60, Height = 60, 
                Fill = new SolidColorBrush(color),
                RadiusX = 10, RadiusY = 10
            };
            // 45 degree rotation to look like diamond
            shape.LayoutTransform = new RotateTransform(45);
            
            grid.Children.Add(shape);
            grid.Children.Add(new TextBlock 
            { 
                Text = text, 
                FontWeight = FontWeights.Bold, 
                VerticalAlignment = VerticalAlignment.Bottom, 
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0,65,0,0)
            });
            return grid;
        }

        private DataTemplate CreateProgressTemplate()
        {
            // XamlReader or constructing via code factory is complex.
            // Simplified: use a factory
            var factory = new FrameworkElementFactory(typeof(ProgressBar));
            factory.SetBinding(ProgressBar.ValueProperty, new System.Windows.Data.Binding("Progress"));
            factory.SetValue(ProgressBar.MinimumProperty, 0.0);
            factory.SetValue(ProgressBar.MaximumProperty, 100.0);
            factory.SetValue(ProgressBar.HeightProperty, 15.0);
            return new DataTemplate { VisualTree = factory };
        }

        private void OnSelectFiles(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Multiselect = true };
            if (dlg.ShowDialog() == true)
            {
                foreach (var f in dlg.FileNames)
                {
                    if (!_selectedFiles.Contains(f))
                    {
                        _selectedFiles.Add(f);
                        _fileListBox.Items.Add(f);
                    }
                }
            }
        }

        private void OnFileDrop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                foreach (var f in files)
                {
                    if (File.Exists(f) && !_selectedFiles.Contains(f))
                    {
                        _selectedFiles.Add(f);
                        _fileListBox.Items.Add(f);
                    }
                }
            }
        }

        private async void OnStartTransfer(object sender, RoutedEventArgs e)
        {
            if (_selectedFiles.Count == 0 || _targetHosts.Count == 0) return;

            _startBtn.IsEnabled = false;
            _stopBtn.IsEnabled = true;

            // Tasks 초기화
            _transferTasks.Clear();
            foreach (var host in _targetHosts)
            {
                foreach (var file in _selectedFiles)
                {
                    _transferTasks.Add(new FileTransferTask 
                    { 
                        HostId = host.Id,
                        HostName = host.Name,
                        FilePath = file,
                        FileName = Path.GetFileName(file),
                        Status = "대기"
                    });
                }
            }
            _progressGrid.ItemsSource = _transferTasks;
            _progressGrid.Items.Refresh(); // Force update

            // 실행
            // TODO: Parallel or Sequential? Image suggests concurrent.
            // We need a helper to connect and send.
            
            var signaling = (Application.Current.MainWindow as MainWindow)?.Signaling;
            if (signaling == null) return;

            // Process per host to reuse connection? Or per file?
            // Better to open connection to Host, send all files, close.
            
            var tasksByHost = _transferTasks.GroupBy(t => t.HostId);
            
            var processingTasks = new List<Task>();

            foreach (var group in tasksByHost)
            {
                var hostId = group.Key;
                var filesToSend = group.ToList();
                
                processingTasks.Add(Task.Run(async () => 
                {
                    await ProcessHostTransfer(hostId, filesToSend, signaling);
                }));
            }

            await Task.WhenAll(processingTasks);
            
            _stopBtn.IsEnabled = false;
            _startBtn.IsEnabled = true;
            MessageBox.Show("전송이 완료되었습니다.");
        }

        private async Task ProcessHostTransfer(string hostId, List<FileTransferTask> tasks, SignalingClient signaling)
        {
            if (signaling == null)
            {
                 foreach(var t in tasks) t.Status = "오류: Signaling 없음";
                 return;
            }

            // [재사용 로직] 현재 연결된 Host인지 확인
            var mw = Application.Current.Dispatcher.Invoke(() => Application.Current.MainWindow as MainWindow);
            bool isConnectedHost = (mw?.ConnectedHostId == hostId && mw?.Receiver != null);

            if (isConnectedHost)
            {
                var rx = mw!.Receiver!;
                foreach (var t in tasks)
                {
                    t.Status = "전송 중 (기존 연결)...";
                    Action<int> progressHandler = (p) => t.Progress = p;
                    rx.OnFileProgress += progressHandler;
                    
                    try
                    {
                        await rx.SendFileAsync(t.FilePath);
                        t.Status = "완료";
                        t.Progress = 100;
                    }
                    catch (Exception ex)
                    {
                        t.Status = $"오류: {ex.Message}";
                    }
                    finally
                    {
                        rx.OnFileProgress -= progressHandler;
                    }
                }
            }
            else
            {
                using var client = new FileTransferClient(signaling, hostId);
                
                foreach(var t in tasks) t.Status = "연결 중...";
                // Dispatcher.Invoke 호출 시 UI 업데이트 (필요시)
                
                bool connected = await client.ConnectAsync();
                if (!connected)
                {
                    foreach(var t in tasks) t.Status = "연결 실패";
                    return;
                }

                foreach (var task in tasks)
                {
                    task.Status = "전송 중...";
                    client.OnProgress = (pct) => 
                    {
                        task.Progress = pct;
                    };

                    try
                    {
                        await client.SendFileAsync(task.FilePath);
                        task.Status = "완료";
                        task.Progress = 100;
                    }
                    catch (Exception ex)
                    {
                        task.Status = "실패";
                        Console.WriteLine($"[MultiFile] Error: {ex.Message}");
                    }
                }
            }
        }

        // 데이터 모델
        public class FileTransferTask : System.ComponentModel.INotifyPropertyChanged
        {
            public string HostId { get; set; } = "";
            public string HostName { get; set; } = "";
            public string FilePath { get; set; } = "";
            public string FileName { get; set; } = "";
            
            private string _status = "";
            public string Status { get { return _status; } set { _status = value; OnPropertyChanged("Status"); } }
            
            private int _progress = 0;
            public int Progress { get { return _progress; } set { _progress = value; OnPropertyChanged("Progress"); } }
            
            public string SpeedText { get; set; } = "-";

            public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
            protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
        }
    }
}
