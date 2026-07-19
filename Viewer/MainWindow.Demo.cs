using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Viewer
{
    public partial class MainWindow
    {
        private readonly Dictionary<string, string> _demoMemos = new();
        private readonly Dictionary<string, HashSet<string>> _demoGroups =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _demoInputModes = new();

        private void LoadDemoFleet()
        {
            _persistentHosts.Clear();
            foreach (var host in DemoFleetData.CreateHosts())
            {
                _persistentHosts[host.Id] = host;
                _demoMemos[host.Id] = host.Id.EndsWith("005")
                    ? "업데이트 확인 대상"
                    : "";
                _demoInputModes[host.Id] = 1;
            }

            _demoGroups["메인 뷰"] = _persistentHosts.Keys.Take(20).ToHashSet();
            _demoGroups["사무실"] = _persistentHosts.Keys.Skip(20).Take(8).ToHashSet();
            _demoGroups["테스트"] = _persistentHosts.Keys.Skip(28).ToHashSet();

            UpdateLobbyUI(_persistentHosts.Values.ToList());
            _statusBarText.Text =
                "DEMO MODE · 우클릭 메뉴와 다중 선택을 테스트할 수 있습니다.";
            Title = "Comote Viewer 1.4 Demo";
        }

        private void ConfigureFleetContextMenu()
        {
            _hostDataGrid.SelectionMode = DataGridSelectionMode.Extended;
            _hostDataGrid.SelectionUnit = DataGridSelectionUnit.FullRow;
            _hostDataGrid.PreviewKeyDown += (_, args) =>
            {
                if (args.Key == Key.A &&
                    Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                {
                    _hostDataGrid.SelectAll();
                    args.Handled = true;
                }
            };

            var menu = new ContextMenu();
            menu.Opened += (_, _) => PopulateFleetContextMenu(menu);
            _hostDataGrid.ContextMenu = menu;
            _gridTab.ContextMenu = menu;
        }

        private void PopulateFleetContextMenu(ContextMenu menu)
        {
            menu.Items.Clear();
            var selected = GetSelectedHosts();
            var count = selected.Count;
            var prefix = $"({count}개) ";

            AddMenuItem(
                menu,
                count == 1
                    ? $"{selected[0].Name} - 컨트롤 창 열기"
                    : $"{prefix}컨트롤 창 열기",
                count > 0,
                (_, _) => OpenControlWindows(selected));

            AddMenuItem(
                menu,
                "전체 선택",
                _hostDataGrid.Items.Count > 0,
                (_, _) => _hostDataGrid.SelectAll(),
                "Ctrl+A");

            menu.Items.Add(new Separator());

            AddMenuItem(
                menu,
                $"{prefix}새 그룹으로",
                count > 0,
                (_, _) => CreateDemoGroup(selected));

            var existingGroups = new MenuItem
            {
                Header = $"{prefix}기존 그룹으로 이동",
                IsEnabled = count > 0 && _demoGroups.Count > 0,
            };
            foreach (var groupName in _demoGroups.Keys.OrderBy(name => name))
            {
                var targetGroup = groupName;
                AddMenuItem(
                    existingGroups,
                    targetGroup,
                    true,
                    (_, _) => MoveToDemoGroup(selected, targetGroup));
            }
            menu.Items.Add(existingGroups);

            AddMenuItem(
                menu,
                "이름/메모 변경",
                count == 1,
                (_, _) => RenameAndMemo(selected[0]),
                "F2");

            AddMenuItem(
                menu,
                "순서 변경: 앞으로",
                count > 0,
                (_, _) => MoveSelectedHosts(selected, -1));
            AddMenuItem(
                menu,
                "순서 변경: 뒤로",
                count > 0,
                (_, _) => MoveSelectedHosts(selected, 1));
            AddMenuItem(
                menu,
                "순서 변경: 원하는 위치로...",
                count > 0,
                (_, _) => MoveSelectedHostsToPosition(selected));

            menu.Items.Add(new Separator());

            AddMenuItem(
                menu,
                $"{prefix}삭제",
                count > 0,
                (_, _) => DeleteSelectedHosts(selected));
            AddMenuItem(
                menu,
                $"{prefix}로그아웃",
                count > 0,
                (_, _) => SimulateFleetCommand(
                    "클라이언트 계정 등록 해제",
                    selected,
                    true));

            var inputMode = new MenuItem
            {
                Header = $"{prefix}입력제어 전환",
                IsEnabled = count > 0,
            };
            for (var mode = 1; mode <= 3; mode++)
            {
                var selectedMode = mode;
                AddMenuItem(
                    inputMode,
                    $"모드{mode}",
                    true,
                    (_, _) => SetDemoInputMode(selected, selectedMode));
            }
            menu.Items.Add(inputMode);

            var driverDelete = new MenuItem
            {
                Header = $"{prefix}드라이버 삭제",
                IsEnabled = count > 0,
            };
            foreach (var driver in new[] { "구버전", "모드2", "모드3" })
            {
                var selectedDriver = driver;
                AddMenuItem(
                    driverDelete,
                    selectedDriver,
                    true,
                    (_, _) => SimulateFleetCommand(
                        $"드라이버 삭제: {selectedDriver}",
                        selected,
                        true));
            }
            menu.Items.Add(driverDelete);

            var driverInstall = new MenuItem
            {
                Header = $"{prefix}드라이버 설치",
                IsEnabled = count > 0,
            };
            foreach (var driver in new[] { "모드2", "모드3" })
            {
                var selectedDriver = driver;
                AddMenuItem(
                    driverInstall,
                    selectedDriver,
                    true,
                    (_, _) => SimulateFleetCommand(
                        $"드라이버 설치: {selectedDriver}",
                        selected,
                        true));
            }
            menu.Items.Add(driverInstall);

            menu.Items.Add(new Separator());

            AddMenuItem(
                menu,
                $"{prefix}원격 프로그램 재실행 (업데이트)",
                count > 0,
                (_, _) => SimulateFleetCommand(
                    "Host 재실행 및 업데이트",
                    selected,
                    true));
            AddMenuItem(
                menu,
                $"{prefix}원격 윈도우 재부팅",
                count > 0,
                (_, _) => SimulateFleetCommand(
                    "Windows 재부팅",
                    selected,
                    true));
            AddMenuItem(
                menu,
                $"{prefix}원격 윈도우 시스템종료",
                count > 0,
                (_, _) => SimulateFleetCommand(
                    "Windows 시스템 종료",
                    selected,
                    true));
            AddMenuItem(
                menu,
                $"{prefix}외부원격 실행",
                count > 0,
                (_, _) => SimulateFleetCommand(
                    "외부 원격 Adapter 실행",
                    selected,
                    false));

            menu.Items.Add(new Separator());
            AddMenuItem(
                menu,
                "파일 전송",
                count > 0,
                _isDemoMode
                    ? (_, _) => SimulateFleetCommand(
                        "파일 전송",
                        selected,
                        false)
                    : OnMultiFileTransferClick);
            AddMenuItem(
                menu,
                "Wake Up (WoL)",
                count > 0,
                (_, _) => SimulateFleetCommand(
                    "Wake-on-LAN",
                    selected,
                    false));
        }

        private static void AddMenuItem(
            ItemsControl parent,
            string header,
            bool enabled,
            RoutedEventHandler click,
            string? gesture = null)
        {
            var item = new MenuItem
            {
                Header = header,
                IsEnabled = enabled,
                InputGestureText = gesture ?? "",
            };
            item.Click += click;
            parent.Items.Add(item);
        }

        private List<HostInfo> GetSelectedHosts()
        {
            var selectedIds = _hostDataGrid.SelectedItems
                .OfType<HostDisplayItem>()
                .Select(item => item.HostId)
                .ToHashSet();

            return _currentHosts
                .Where(host => selectedIds.Contains(host.Id))
                .ToList();
        }

        private void SelectHostFromCard(string hostId)
        {
            var item = _hostDataGrid.Items
                .OfType<HostDisplayItem>()
                .FirstOrDefault(candidate => candidate.HostId == hostId);
            if (item == null) return;

            var currentIndex = _currentHosts.FindIndex(host =>
                host.Id.Equals(hostId, StringComparison.OrdinalIgnoreCase));
            var anchorIndex = _currentHosts.FindIndex(host =>
                host.Id.Equals(_selectionAnchorHostId,
                    StringComparison.OrdinalIgnoreCase));
            var control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

            if (shift && anchorIndex >= 0 && currentIndex >= 0)
            {
                if (!control) _hostDataGrid.UnselectAll();
                var first = Math.Min(anchorIndex, currentIndex);
                var last = Math.Max(anchorIndex, currentIndex);
                foreach (var host in _currentHosts.Skip(first).Take(last - first + 1))
                    SelectHostInGrid(host.Id, true);
            }
            else if (control)
            {
                SelectHostInGrid(hostId, !_hostDataGrid.SelectedItems.Contains(item));
                _selectionAnchorHostId = hostId;
            }
            else
            {
                _hostDataGrid.UnselectAll();
                SelectHostInGrid(hostId, true);
                _selectionAnchorHostId = hostId;
            }

            _hostDataGrid.ScrollIntoView(item);
            UpdateThumbnailSelectionVisuals();
        }
        private void OpenControlWindows(IReadOnlyList<HostInfo> hosts)
        {
            if (hosts.Count == 0) return;

            if (!_isDemoMode)
            {
                ConnectToHost(hosts[0].Id);
                return;
            }

            foreach (var host in hosts.Take(6))
            {
                var panel = new StackPanel { Margin = new Thickness(24) };
                panel.Children.Add(
                    new TextBlock
                    {
                        Text = host.Name,
                        FontSize = 24,
                        FontWeight = FontWeights.Bold,
                        Foreground = Brushes.White,
                    });
                panel.Children.Add(
                    new TextBlock
                    {
                        Text =
                            $"\nDEMO CONTROL WINDOW\n\n" +
                            $"상태: {(host.IsOnline ? "ONLINE" : "OFFLINE")}\n" +
                            $"IP: {host.Ip}\n" +
                            $"해상도: {host.Resolution}\n" +
                            $"CPU: {host.Cpu}%\n\n" +
                            "실제 서버 연결 시 이 영역에 원격 화면이 표시됩니다.",
                        FontFamily = new FontFamily("Consolas"),
                        Foreground = new SolidColorBrush(
                            Color.FromRgb(0, 255, 65)),
                    });

                new Window
                {
                    Title = $"{host.Name} - Comote Demo Control",
                    Width = 760,
                    Height = 480,
                    Background = new SolidColorBrush(Color.FromRgb(8, 8, 8)),
                    Content = panel,
                    Owner = this,
                }.Show();
            }
        }

        private void CreateDemoGroup(IReadOnlyList<HostInfo> hosts)
        {
            var name = PromptText(
                "새 그룹",
                "새 그룹 이름을 입력하세요.",
                $"새 그룹 {_demoGroups.Count + 1}");
            if (string.IsNullOrWhiteSpace(name)) return;

            _demoGroups[name.Trim()] =
                hosts.Select(host => host.Id).ToHashSet();
            SetDemoStatus(
                $"그룹 '{name.Trim()}' 생성 · {hosts.Count}대 추가");
        }

        private void MoveToDemoGroup(
            IReadOnlyList<HostInfo> hosts,
            string groupName)
        {
            foreach (var group in _demoGroups.Values)
            {
                foreach (var host in hosts)
                {
                    group.Remove(host.Id);
                }
            }

            foreach (var host in hosts)
            {
                _demoGroups[groupName].Add(host.Id);
            }

            SetDemoStatus(
                $"{hosts.Count}대를 그룹 '{groupName}'으로 이동");
        }

        private void RenameAndMemo(HostInfo host)
        {
            var newName = PromptText(
                "이름 변경",
                "표시 이름",
                host.Name);
            if (string.IsNullOrWhiteSpace(newName)) return;

            var memo = PromptText(
                "메모 변경",
                "메모",
                _demoMemos.GetValueOrDefault(host.Id, ""));
            if (memo == null) return;

            host.Name = newName.Trim();
            _demoMemos[host.Id] = memo.Trim();
            UpdateLobbyUI(_persistentHosts.Values.ToList());
            SetDemoStatus(
                $"{host.Name} 저장 · 메모: " +
                $"{(memo.Length == 0 ? "없음" : memo)}");
        }

        private void MoveSelectedHosts(
            IReadOnlyList<HostInfo> hosts,
            int direction)
        {
            EnsureHostOrder();
            var selectedIds = hosts.Select(host => host.Id).ToHashSet();

            if (direction < 0)
            {
                for (var index = 1; index < _settings.HostOrder.Count; index++)
                {
                    if (selectedIds.Contains(_settings.HostOrder[index]) &&
                        !selectedIds.Contains(_settings.HostOrder[index - 1]))
                    {
                        (_settings.HostOrder[index - 1],
                         _settings.HostOrder[index]) =
                            (_settings.HostOrder[index],
                             _settings.HostOrder[index - 1]);
                    }
                }
            }
            else
            {
                for (var index = _settings.HostOrder.Count - 2;
                     index >= 0;
                     index--)
                {
                    if (selectedIds.Contains(_settings.HostOrder[index]) &&
                        !selectedIds.Contains(_settings.HostOrder[index + 1]))
                    {
                        (_settings.HostOrder[index + 1],
                         _settings.HostOrder[index]) =
                            (_settings.HostOrder[index],
                             _settings.HostOrder[index + 1]);
                    }
                }
            }

            _settings.Save();
            UpdateLobbyUI(_persistentHosts.Values.ToList());
        }

        private void MoveSelectedHostsToPosition(
            IReadOnlyList<HostInfo> hosts)
        {
            var input = PromptText(
                "원하는 위치로 이동",
                $"1부터 {_currentHosts.Count} 사이의 위치",
                "1");
            if (!int.TryParse(input, out var position)) return;

            EnsureHostOrder();
            position = Math.Clamp(position, 1, _settings.HostOrder.Count);
            var selectedIds = hosts.Select(host => host.Id).ToHashSet();
            var moving = _settings.HostOrder
                .Where(selectedIds.Contains)
                .ToList();
            _settings.HostOrder.RemoveAll(selectedIds.Contains);
            _settings.HostOrder.InsertRange(
                Math.Min(position - 1, _settings.HostOrder.Count),
                moving);

            _settings.Save();
            UpdateLobbyUI(_persistentHosts.Values.ToList());
        }

        private void EnsureHostOrder()
        {
            if (_settings.HostOrder.Count != _currentHosts.Count ||
                _currentHosts.Any(
                    host => !_settings.HostOrder.Contains(host.Id)))
            {
                _settings.HostOrder =
                    _currentHosts.Select(host => host.Id).ToList();
            }
        }

        private void DeleteSelectedHosts(IReadOnlyList<HostInfo> hosts)
        {
            if (hosts.Count == 0) return;
            if (!_isDemoMode)
            {
                MessageBox.Show(
                    "실제 장치의 일괄 삭제는 서버 작업 API 연결 후 활성화됩니다.",
                    "Comote");
                return;
            }

            if (MessageBox.Show(
                    $"{hosts.Count}대를 데모 목록에서 삭제할까요?",
                    "데모 삭제",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            foreach (var host in hosts)
            {
                _persistentHosts.Remove(host.Id);
                _settings.HostOrder.Remove(host.Id);
                foreach (var group in _demoGroups.Values)
                {
                    group.Remove(host.Id);
                }
            }

            UpdateLobbyUI(_persistentHosts.Values.ToList());
            SetDemoStatus($"{hosts.Count}대를 데모 목록에서 삭제");
        }

        private void SetDemoInputMode(
            IReadOnlyList<HostInfo> hosts,
            int mode)
        {
            foreach (var host in hosts)
            {
                _demoInputModes[host.Id] = mode;
            }

            SetDemoStatus(
                $"{hosts.Count}대 입력제어를 모드{mode}로 전환 " +
                "(데모 시뮬레이션)");
        }

        private void SimulateFleetCommand(
            string command,
            IReadOnlyList<HostInfo> hosts,
            bool requiresConfirmation)
        {
            if (hosts.Count == 0) return;

            if (!_isDemoMode)
            {
                MessageBox.Show(
                    $"'{command}'은 원격 작업 API 연결 후 활성화됩니다.",
                    "Comote");
                return;
            }

            if (requiresConfirmation &&
                MessageBox.Show(
                    $"{hosts.Count}대에 '{command}' 작업을 예약할까요?\n\n" +
                    "데모 모드에서는 실제 컴퓨터 상태를 변경하지 않습니다.",
                    "위험 작업 확인",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            SetDemoStatus(
                $"작업 접수 → 실행 → 성공 · {command} · {hosts.Count}대 " +
                "(데모 시뮬레이션)");
        }

        private void SetDemoStatus(string message)
        {
            _statusBarText.Text = message;
        }

        private static string? PromptText(
            string title,
            string label,
            string initialValue)
        {
            var textBox = new TextBox
            {
                Text = initialValue,
                Margin = new Thickness(0, 8, 0, 14),
                MinWidth = 300,
            };
            var ok = new Button
            {
                Content = "확인",
                Width = 90,
                IsDefault = true,
                Margin = new Thickness(4),
            };
            var cancel = new Button
            {
                Content = "취소",
                Width = 90,
                IsCancel = true,
                Margin = new Thickness(4),
            };
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);

            var panel = new StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(new TextBlock { Text = label });
            panel.Children.Add(textBox);
            panel.Children.Add(buttons);

            var dialog = new Window
            {
                Title = title,
                Width = 380,
                Height = 170,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Content = panel,
            };
            ok.Click += (_, _) => dialog.DialogResult = true;
            textBox.SelectAll();
            textBox.Focus();

            return dialog.ShowDialog() == true
                ? textBox.Text
                : null;
        }
    }
}
