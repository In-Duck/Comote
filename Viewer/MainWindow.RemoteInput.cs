using System.Windows;
using System.Windows.Input;

namespace Viewer
{
    public partial class MainWindow
    {
        private bool TryGetRemoteCoordinates(
            MouseEventArgs args,
            bool clampOutside,
            out float normalizedX,
            out float normalizedY)
        {
            normalizedX = 0;
            normalizedY = 0;

            var source = _videoDisplay.Source;
            var controlWidth = _videoDisplay.ActualWidth;
            var controlHeight = _videoDisplay.ActualHeight;
            if (source == null ||
                controlWidth <= 0 ||
                controlHeight <= 0 ||
                source.Width <= 0 ||
                source.Height <= 0)
                return false;

            var scale = Math.Min(
                controlWidth / source.Width,
                controlHeight / source.Height);
            var renderedWidth = source.Width * scale;
            var renderedHeight = source.Height * scale;
            var offsetX = (controlWidth - renderedWidth) / 2.0;
            var offsetY = (controlHeight - renderedHeight) / 2.0;
            var position = args.GetPosition(_videoDisplay);
            var x = (position.X - offsetX) / renderedWidth;
            var y = (position.Y - offsetY) / renderedHeight;

            if (!clampOutside &&
                (x < 0 || x > 1 || y < 0 || y > 1))
                return false;

            normalizedX = (float)Math.Clamp(x, 0, 1);
            normalizedY = (float)Math.Clamp(y, 0, 1);
            return true;
        }

        private static bool TryGetMouseButton(
            MouseButton changedButton,
            out byte button)
        {
            button = changedButton switch
            {
                MouseButton.Left => 0,
                MouseButton.Right => 1,
                MouseButton.Middle => 2,
                _ => byte.MaxValue,
            };
            return button != byte.MaxValue;
        }

        private void ReleaseRemoteInputs()
        {
            // Disable the process-wide hook before releasing the remote state.
            if (_keyboardHook != null)
                _keyboardHook.IsCapturing = false;
            _ctrlPressed = false;
            _shiftPressed = false;

            try
            {
                _receiver?.SendInput(new byte[] { 0x13 });
            }
            catch
            {
            }

            if (_videoDisplay.IsMouseCaptured)
                _videoDisplay.ReleaseMouseCapture();
        }

        private bool IsRemoteInputActive()
        {
            var controlWindowActive = _remoteWindow == null
                ? IsActive && WindowState != WindowState.Minimized
                : _remoteWindow.IsActive &&
                  _remoteWindow.WindowState != WindowState.Minimized;

            return controlWindowActive &&
                _remoteGrid.Visibility == Visibility.Visible &&
                _passwordPanel?.Visibility != Visibility.Visible &&
                _receiver?.ConnectionState == SIPSorcery.Net.RTCPeerConnectionState.connected &&
                _receiver.InputChannelReady;
        }

        private void DisposeAllReceivers()
        {
            foreach (var entry in _receiverPool.ToArray())
            {
                if (_receiverPool.TryRemove(entry.Key, out var receiver))
                    DisposeReceiverSafely(receiver);
            }
            _receiver = null;
        }
        private static void DisposeReceiverSafely(VideoReceiver? receiver)
        {
            if (receiver == null) return;
            try
            {
                receiver.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[UI] Receiver cleanup warning: {ex.Message}");
            }
        }
    }
}
