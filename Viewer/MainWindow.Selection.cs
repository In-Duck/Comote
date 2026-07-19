using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Viewer
{
    public partial class MainWindow
    {
        private string? _selectionAnchorHostId;

        private void SelectAllHosts()
        {
            _hostDataGrid.UnselectAll();
            foreach (var item in _hostDataGrid.Items.OfType<HostDisplayItem>())
                _hostDataGrid.SelectedItems.Add(item);
            _selectionAnchorHostId = _currentHosts.FirstOrDefault()?.Id;
            UpdateThumbnailSelectionVisuals();
        }

        private void SelectHostInGrid(string hostId, bool select)
        {
            var item = _hostDataGrid.Items.OfType<HostDisplayItem>()
                .FirstOrDefault(candidate => candidate.HostId.Equals(
                    hostId, StringComparison.OrdinalIgnoreCase));
            if (item == null) return;

            if (select && !_hostDataGrid.SelectedItems.Contains(item))
                _hostDataGrid.SelectedItems.Add(item);
            else if (!select && _hostDataGrid.SelectedItems.Contains(item))
                _hostDataGrid.SelectedItems.Remove(item);
        }

        private void UpdateThumbnailSelectionVisuals()
        {
            var selectedIds = _hostDataGrid.SelectedItems
                .OfType<HostDisplayItem>()
                .Select(item => item.HostId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var (hostId, tile) in _thumbnailTiles)
            {
                var online = _currentHosts.FirstOrDefault(host =>
                    host.Id.Equals(hostId, StringComparison.OrdinalIgnoreCase))
                    ?.IsOnline == true;
                tile.BorderBrush = selectedIds.Contains(hostId)
                    ? new SolidColorBrush(Color.FromRgb(0, 153, 255))
                    : new SolidColorBrush(online
                        ? Color.FromRgb(45, 45, 45)
                        : Color.FromRgb(25, 25, 25));
                tile.BorderThickness = selectedIds.Contains(hostId)
                    ? new Thickness(3)
                    : new Thickness(1);
            }
        }

        private void OnThumbnailSurfaceMouseDown(
            object sender, MouseButtonEventArgs args)
        {
            if (FindTile(args.OriginalSource as DependencyObject) != null)
                return;

            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                _hostDataGrid.UnselectAll();
            _selectionMarqueeStart = args.GetPosition(_thumbnailPanel);
            _isDrawingSelectionMarquee = true;
            _selectionMarquee!.Visibility = Visibility.Visible;
            _thumbnailPanel.CaptureMouse();
            UpdateSelectionMarquee(_selectionMarqueeStart);
            args.Handled = true;
        }

        private void OnThumbnailSurfaceMouseMove(
            object sender, MouseEventArgs args)
        {
            if (!_isDrawingSelectionMarquee) return;
            UpdateSelectionMarquee(args.GetPosition(_thumbnailPanel));
        }

        private void OnThumbnailSurfaceMouseUp(
            object sender, MouseButtonEventArgs args)
        {
            if (!_isDrawingSelectionMarquee) return;

            var end = args.GetPosition(_thumbnailPanel);
            var region = CreateSelectionRegion(_selectionMarqueeStart, end);
            foreach (var (hostId, tile) in _thumbnailTiles)
            {
                var position = tile.TransformToAncestor(_thumbnailPanel)
                    .Transform(new Point(0, 0));
                var bounds = new Rect(position,
                    new Size(tile.ActualWidth, tile.ActualHeight));
                if (region.IntersectsWith(bounds))
                    SelectHostInGrid(hostId, true);
            }

            _isDrawingSelectionMarquee = false;
            _selectionMarquee!.Visibility = Visibility.Collapsed;
            _thumbnailPanel.ReleaseMouseCapture();
            UpdateThumbnailSelectionVisuals();
            args.Handled = true;
        }

        private void UpdateSelectionMarquee(Point end)
        {
            var region = CreateSelectionRegion(_selectionMarqueeStart, end);
            _selectionMarquee!.Width = region.Width;
            _selectionMarquee.Height = region.Height;
            _selectionMarquee.HorizontalAlignment = HorizontalAlignment.Left;
            _selectionMarquee.VerticalAlignment = VerticalAlignment.Top;
            _selectionMarquee.Margin = new Thickness(region.X, region.Y, 0, 0);
        }

        private static Rect CreateSelectionRegion(Point first, Point second) =>
            new(Math.Min(first.X, second.X), Math.Min(first.Y, second.Y),
                Math.Abs(first.X - second.X), Math.Abs(first.Y - second.Y));

        private static Border? FindTile(DependencyObject? element)
        {
            while (element != null)
            {
                if (element is Border { Tag: string }) return (Border)element;
                element = VisualTreeHelper.GetParent(element);
            }
            return null;
        }
    }
}
