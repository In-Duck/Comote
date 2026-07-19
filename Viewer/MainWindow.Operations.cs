using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Viewer
{
    public partial class MainWindow
    {
        private Border? _operationsPanel;
        private bool _operationsVisible = true;
        private readonly ObservableCollection<RemoteRunTask> _runTasks = new();
        private readonly ObservableCollection<RemoteStopTask> _stopTasks = new();
        private ComboBox? _operationTabs;
        private Grid? _operationBody;
        private TextBox? _runNameBox;
        private TextBox? _runPathBox;
        private ComboBox? _runFolderBox;
        private TextBox? _stopNameBox;
        private TextBox? _stopProcessBox;
        private ListBox? _runTaskList;
        private ListBox? _stopTaskList;

        private void AttachOperationsPanel(Grid workspace)
        {
            workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
            Grid.SetColumn(_mainContent, 0);
            workspace.Children.Add(_mainContent);

            _operationsPanel = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(18, 18, 18)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                BorderThickness = new Thickness(1, 0, 0, 0),
                Padding = new Thickness(10),
            };
            Grid.SetColumn(_operationsPanel, 1);
            workspace.Children.Add(_operationsPanel);

            var root = new DockPanel();
            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(header, Dock.Top);
            var title = new TextBlock
            {
                Text = "작업 패널",
                Foreground = new SolidColorBrush(Color.FromRgb(255, 176, 0)),
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var toggle = new Button { Content = "닫기", Padding = new Thickness(9, 3, 9, 3) };
            toggle.Click += (_, _) => ToggleOperationsPanel();
            DockPanel.SetDock(toggle, Dock.Right);
            header.Children.Add(toggle);
            header.Children.Add(title);
            root.Children.Add(header);

            _operationTabs = new ComboBox
            {
                ItemsSource = new[] { "파일 전송", "파일 실행", "파일 종료" },
                SelectedIndex = 0,
                Margin = new Thickness(0, 0, 0, 8),
            };
            _operationTabs.SelectionChanged += (_, _) => RenderOperationTab();
            DockPanel.SetDock(_operationTabs, Dock.Top);
            root.Children.Add(_operationTabs);
            _operationBody = new Grid();
            root.Children.Add(_operationBody);
            _operationsPanel.Child = root;
            LoadTaskLibrary();
            RenderOperationTab();
        }

        private void ToggleOperationsPanel()
        {
            if (_operationsPanel == null) return;
            _operationsVisible = !_operationsVisible;
            _operationsPanel.Visibility = _operationsVisible ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RenderOperationTab()
        {
            if (_operationBody == null || _operationTabs == null) return;
            _operationBody.Children.Clear();
            switch (_operationTabs.SelectedIndex)
            {
                case 0: RenderTransferTab(); break;
                case 1: RenderRunTab(); break;
                case 2: RenderStopTab(); break;
            }
        }

        private void RenderTransferTab()
        {
            var panel = CreatePanel();
            panel.Children.Add(CreateDescription("선택한 PC에 파일을 전송합니다. 파일 추가를 누르면 Windows 파일 선택 창이 열립니다."));
            var button = new Button { Content = "파일 추가 및 전송", Height = 34, Margin = new Thickness(0, 8, 0, 0) };
            button.Click += OnMultiFileTransferClick;
            panel.Children.Add(button);
            var selectedCount = _hostDataGrid == null ? 0 : GetSelectedHosts().Count;
            panel.Children.Add(CreateDescription("선택된 PC 수: " + selectedCount));
            _operationBody!.Children.Add(panel);
        }

        private void RenderRunTab()
        {
            var panel = CreatePanel();
            _runNameBox = CreateTextBox("작업 이름");
            _runFolderBox = new ComboBox { ItemsSource = new[] { "바탕화면", "직접 입력" }, SelectedIndex = 0, Margin = new Thickness(0, 0, 0, 6) };
            _runPathBox = CreateTextBox("파일명 또는 전체 경로 (예: Tool.exe)");
            panel.Children.Add(_runNameBox); panel.Children.Add(_runFolderBox); panel.Children.Add(_runPathBox);
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            var execute = new Button { Content = "선택 PC에서 실행", Margin = new Thickness(0, 0, 6, 0) };
            execute.Click += async (_, _) => await SendRunTaskAsync(new RemoteRunTask(_runNameBox.Text, _runFolderBox.SelectedIndex == 1 ? "custom" : "desktop", _runPathBox.Text));
            var add = new Button { Content = "목록 추가" };
            add.Click += (_, _) => AddRunTask();
            actions.Children.Add(execute); actions.Children.Add(add); panel.Children.Add(actions);
            panel.Children.Add(CreateDescription("저장된 실행 작업"));
            panel.Children.Add(CreateRunList());
            var delete = new Button { Content = "선택 작업 삭제", Margin = new Thickness(0, 6, 0, 0) };
            delete.Click += (_, _) => DeleteSelectedRunTask();
            panel.Children.Add(delete);
            _operationBody!.Children.Add(panel);
        }

        private void RenderStopTab()
        {
            var panel = CreatePanel();
            _stopNameBox = CreateTextBox("작업 이름");
            _stopProcessBox = CreateTextBox("프로세스명 (예: Tool.exe)");
            panel.Children.Add(_stopNameBox); panel.Children.Add(_stopProcessBox);
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            var stop = new Button { Content = "선택 PC에서 종료", Margin = new Thickness(0, 0, 6, 0) };
            stop.Click += async (_, _) => await SendStopTaskAsync(new RemoteStopTask(_stopNameBox.Text, _stopProcessBox.Text));
            var add = new Button { Content = "목록 추가" };
            add.Click += (_, _) => AddStopTask();
            actions.Children.Add(stop); actions.Children.Add(add); panel.Children.Add(actions);
            panel.Children.Add(CreateDescription("저장된 종료 작업"));
            panel.Children.Add(CreateStopList());
            var delete = new Button { Content = "선택 작업 삭제", Margin = new Thickness(0, 6, 0, 0) };
            delete.Click += (_, _) => DeleteSelectedStopTask();
            panel.Children.Add(delete);
            _operationBody!.Children.Add(panel);
        }

        private StackPanel CreatePanel() => new() { Margin = new Thickness(0) };
        private TextBox CreateTextBox(string hint) => new() { ToolTip = hint, Text = "", Margin = new Thickness(0, 0, 0, 6), MinHeight = 28 };
        private TextBlock CreateDescription(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightGray, Margin = new Thickness(0, 0, 0, 6) };

        private ListBox CreateRunList()
        {
            var list = new ListBox { ItemsSource = _runTasks, DisplayMemberPath = "Display", MaxHeight = 250 };
            _runTaskList = list;
            list.MouseDoubleClick += async (_, _) => { if (list.SelectedItem is RemoteRunTask task) await SendRunTaskAsync(task); };
            return list;
        }
        private ListBox CreateStopList()
        {
            var list = new ListBox { ItemsSource = _stopTasks, DisplayMemberPath = "Display", MaxHeight = 250 };
            _stopTaskList = list;
            list.MouseDoubleClick += async (_, _) => { if (list.SelectedItem is RemoteStopTask task) await SendStopTaskAsync(task); };
            return list;
        }

        private void AddRunTask()
        {
            var task = new RemoteRunTask(_runNameBox?.Text ?? "", _runFolderBox?.SelectedIndex == 1 ? "custom" : "desktop", _runPathBox?.Text ?? "");
            if (string.IsNullOrWhiteSpace(task.Path)) { MessageBox.Show("실행할 파일을 입력해주세요."); return; }
            _runTasks.Add(task); SaveTaskLibrary(); RenderOperationTab();
        }
        private void AddStopTask()
        {
            var task = new RemoteStopTask(_stopNameBox?.Text ?? "", _stopProcessBox?.Text ?? "");
            if (string.IsNullOrWhiteSpace(task.ProcessName)) { MessageBox.Show("종료할 프로세스명을 입력해주세요."); return; }
            _stopTasks.Add(task); SaveTaskLibrary(); RenderOperationTab();
        }

        private void DeleteSelectedRunTask()
        {
            if (_runTaskList?.SelectedItem is not RemoteRunTask task) return;
            _runTasks.Remove(task); SaveTaskLibrary(); RenderOperationTab();
        }

        private void DeleteSelectedStopTask()
        {
            if (_stopTaskList?.SelectedItem is not RemoteStopTask task) return;
            _stopTasks.Remove(task); SaveTaskLibrary(); RenderOperationTab();
        }
        private async Task SendRunTaskAsync(RemoteRunTask task) => await SendFleetCommandAsync("run", task.Name, task.Folder, task.Path);
        private async Task SendStopTaskAsync(RemoteStopTask task) => await SendFleetCommandAsync("terminate", task.Name, "", task.ProcessName);

        private async Task SendFleetCommandAsync(string action, string name, string folder, string value)
        {
            var targets = GetSelectedHosts().Where(host => host.IsOnline).ToList();
            if (targets.Count == 0) { MessageBox.Show("온라인 상태의 대상 PC를 선택해주세요."); return; }
            if (!_isHubMode || _hubServer == null) { MessageBox.Show("현재 명령 일괄 실행은 매니저 허브 방식에서 사용할 수 있습니다."); return; }
            var result = await Task.WhenAll(targets.Select(async host =>
            {
                try { await _hubServer.SendCommandAsync(host.Id, action, name, folder, value); return host.Name; }
                catch { return host.Name + " (연결 실패)"; }
            }));
            _statusBarText.Text = $"작업 전송: {action} / {string.Join(", ", result)}";
        }

        private static string TaskLibraryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Comote", "manager-tasks.json");
        private void LoadTaskLibrary()
        {
            try
            {
                if (!File.Exists(TaskLibraryPath)) return;
                var data = JsonSerializer.Deserialize<TaskLibrary>(File.ReadAllText(TaskLibraryPath));
                if (data == null) return;
                foreach (var item in data.RunTasks) _runTasks.Add(item);
                foreach (var item in data.StopTasks) _stopTasks.Add(item);
            }
            catch { }
        }
        private void SaveTaskLibrary()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(TaskLibraryPath)!);
            File.WriteAllText(TaskLibraryPath, JsonSerializer.Serialize(new TaskLibrary { RunTasks = _runTasks.ToList(), StopTasks = _stopTasks.ToList() }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    public sealed record RemoteRunTask(string Name, string Folder, string Path) { public string Display => $"{Name}  |  {Path}"; }
    public sealed record RemoteStopTask(string Name, string ProcessName) { public string Display => $"{Name}  |  {ProcessName}"; }
    public sealed class TaskLibrary { public List<RemoteRunTask> RunTasks { get; set; } = []; public List<RemoteStopTask> StopTasks { get; set; } = []; }
}
