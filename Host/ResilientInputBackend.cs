namespace Host
{
    /// <summary>
    /// Keeps remote input usable if the FakerInput control device disappears.
    /// A failed virtual-HID session is downgraded to SendInput until Client restart.
    /// </summary>
    internal sealed class ResilientInputBackend : IInputBackend
    {
        private readonly object _sync = new();
        private readonly SendInputBackend _fallback;
        private FakerInputBackend? _fakerInput;
        private bool _disposed;
        private string? _fallbackReason;

        public ResilientInputBackend(int left, int top, int width, int height)
        {
            _fallback = new SendInputBackend(width, height);
            _fallback.UpdateScreenBounds(left, top, width, height);
            try
            {
                _fakerInput = new FakerInputBackend(left, top, width, height);
            }
            catch (Exception ex)
            {
                ActivateFallback(ex);
            }
        }

        public InputBackendMode Mode => InputBackendMode.VirtualHid;
        public string Name => _fakerInput != null
            ? "Virtual HID (FakerInput)"
            : "Windows SendInput (automatic fallback)";

        public InputBackendStatus GetStatus()
        {
            lock (_sync)
            {
                if (_disposed)
                    return new InputBackendStatus(Mode, Name, false, "Input backend is closed");
                if (_fakerInput != null)
                    return _fakerInput.GetStatus();
                return new InputBackendStatus(
                    Mode,
                    Name,
                    true,
                    "FakerInput unavailable; using SendInput until Client restart" +
                    (string.IsNullOrWhiteSpace(_fallbackReason) ? "" : $" ({_fallbackReason})"));
            }
        }

        public void UpdateScreenSize(int width, int height) =>
            UpdateScreenBounds(0, 0, width, height);

        public void UpdateScreenBounds(int left, int top, int width, int height)
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                _fallback.UpdateScreenBounds(left, top, width, height);
                _fakerInput?.UpdateScreenBounds(left, top, width, height);
            }
        }

        public void ProcessMessage(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);
            lock (_sync)
            {
                ThrowIfDisposed();
                if (_fakerInput == null)
                {
                    _fallback.ProcessMessage(data);
                    return;
                }

                try
                {
                    _fakerInput.ProcessMessage(data);
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
                {
                    ActivateFallback(ex);
                    _fallback.ProcessMessage(data);
                }
            }
        }

        public void ReleaseAllInputs()
        {
            lock (_sync)
            {
                if (_disposed) return;
                try { _fakerInput?.ReleaseAllInputs(); }
                catch (Exception ex) { ActivateFallback(ex); }
                try { _fallback.ReleaseAllInputs(); }
                catch (Exception ex) { Console.WriteLine($"[Input] Fallback release warning: {ex.Message}"); }
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                ReleaseAllInputs();
                _disposed = true;
                try { _fakerInput?.Dispose(); } catch { }
                _fakerInput = null;
                _fallback.Dispose();
            }
        }

        private void ActivateFallback(Exception ex)
        {
            _fallbackReason = ex.Message;
            Console.WriteLine($"[Input] FakerInput failed; switching to SendInput: {ex.Message}");
            var failed = _fakerInput;
            _fakerInput = null;
            if (failed != null)
            {
                try { failed.ReleaseAllInputs(); } catch { }
                try { failed.Dispose(); } catch { }
            }
        }

        private void ThrowIfDisposed() =>
            ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
