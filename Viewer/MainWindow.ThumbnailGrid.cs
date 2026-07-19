using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Viewer
{
    public partial class MainWindow
    {
        private readonly Dictionary<string, Image> _liveThumbnailImages =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Border> _thumbnailTiles =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _thumbnailSourcesSeen =
            new(StringComparer.OrdinalIgnoreCase);

        private Border CreateThumbnailTile(HostInfo host, int index)
        {
            var image = new Image
            {
                Stretch = Stretch.UniformToFill,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            _liveThumbnailImages[host.Id] = image;
            if (host.ThumbnailBytes is { Length: > 0 })
                image.Source = DecodeThumbnail(host.ThumbnailBytes);

            var noSignal = new TextBlock
            {
                Text = host.IsOnline ? "WAITING FOR SCREEN" : "OFFLINE",
                Foreground = new SolidColorBrush(
                    Color.FromRgb(100, 100, 100)),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (image.Source != null) noSignal.Visibility = Visibility.Collapsed;

            var name = new TextBlock
            {
                Text = $"{index}. {host.Name}",
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 122,
            };
            var status = new Ellipse
            {
                Width = 8,
                Height = 8,
                Margin = new Thickness(6, 0, 0, 0),
                Fill = host.IsOnline
                    ? new SolidColorBrush(Color.FromRgb(0, 255, 65))
                    : new SolidColorBrush(Color.FromRgb(90, 90, 90)),
            };
            var overlay = new Border
            {
                Background = new SolidColorBrush(
                    Color.FromArgb(205, 0, 0, 0)),
                Padding = new Thickness(5, 3, 5, 3),
                VerticalAlignment = VerticalAlignment.Top,
                Child = new DockPanel
                {
                    Children = { name, status },
                },
            };
            DockPanel.SetDock(status, Dock.Right);

            var layers = new Grid();
            layers.Children.Add(image);
            layers.Children.Add(noSignal);
            layers.Children.Add(overlay);
            image.Tag = noSignal;

            var tile = new Border
            {
                Width = 150,
                Height = 92,
                Margin = new Thickness(2),
                Background = Brushes.Black,
                BorderBrush = host.IsOnline
                    ? new SolidColorBrush(Color.FromRgb(45, 45, 45))
                    : new SolidColorBrush(Color.FromRgb(25, 25, 25)),
                BorderThickness = new Thickness(1),
                ClipToBounds = true,
                Cursor = Cursors.Hand,
                Tag = host.Id,
                Child = layers,
            };
            _thumbnailTiles[host.Id] = tile;

            tile.MouseEnter += (_, _) => UpdateThumbnailSelectionVisuals();
            tile.MouseLeave += (_, _) => UpdateThumbnailSelectionVisuals();
            tile.PreviewMouseRightButtonDown += (_, _) =>
                SelectHostFromCard(host.Id);
            tile.MouseLeftButtonDown += (_, args) =>
            {
                SelectHostFromCard(host.Id);
                if (args.ClickCount == 2 && host.IsOnline)
                    ConnectToHost(host.Id);
            };

            return tile;
        }

        private void SetLiveThumbnail(string hostId, byte[] jpeg)
        {
            if (!_persistentHosts.TryGetValue(hostId, out var host)) return;
            host.ThumbnailBytes = jpeg;
            host.LastSeen = DateTime.UtcNow;
            if (!_liveThumbnailImages.TryGetValue(hostId, out var image))
                return;

            var bitmap = DecodeThumbnail(jpeg);
            if (bitmap == null) return;
            image.Source = bitmap;
            if (image.Tag is UIElement placeholder)
                placeholder.Visibility = Visibility.Collapsed;
            if (_thumbnailSourcesSeen.Add(hostId))
                Console.WriteLine($"[Hub] Thumbnail active: {hostId} ({jpeg.Length} bytes)");
        }

        private static BitmapImage? DecodeThumbnail(byte[] jpeg)
        {
            try
            {
                using var stream = new MemoryStream(jpeg, writable: false);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 360;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                return null;
            }
        }
    }
}
