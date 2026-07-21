namespace Host;

/// <summary>
/// Serialises remote input on a thread attached to the desktop currently
/// receiving Windows input. The worker is replaced on Default/Winlogon
/// transitions and releases held input before leaving the previous desktop.
/// </summary>
internal sealed class DesktopAwareInputBackend : IInputBackend
{
    private readonly object _sync = new();
    private readonly IInputBackend _inner;
    private readonly AutoResetEvent _request = new(false);
    private readonly ManualResetEventSlim _completed = new(false);
    private readonly ManualResetEventSlim _started = new(false);
    private Thread? _worker;
    private Action? _pending;
    private Exception? _failure;
    private string? _desktopName;
    private bool _stopWorker;
    private bool _disposed;

    public DesktopAwareInputBackend(IInputBackend inner)
    {
        _inner = inner;
    }

    public InputBackendMode Mode => _inner.Mode;
    public string Name => _inner.Name + " / secure desktop";
    public InputBackendStatus GetStatus() => _inner.GetStatus();

    public void ProcessMessage(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        Invoke(() => _inner.ProcessMessage(data));
    }

    public void ReleaseAllInputs() => Invoke(_inner.ReleaseAllInputs);

    public void UpdateScreenSize(int width, int height) =>
        Invoke(() => _inner.UpdateScreenSize(width, height));

    public void UpdateScreenBounds(int left, int top, int width, int height) =>
        Invoke(() => _inner.UpdateScreenBounds(left, top, width, height));

    private void Invoke(Action action)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureWorker();
            _failure = null;
            _pending = action;
            _completed.Reset();
            _request.Set();
            if (!_completed.Wait(TimeSpan.FromSeconds(2)))
            {
                StopWorker();
                throw new TimeoutException("The secure desktop input worker did not respond.");
            }
            if (_failure != null)
                throw new InvalidOperationException(
                    "Secure desktop input failed.", _failure);
        }
    }

    private void EnsureWorker()
    {
        var activeDesktop = SessionManager.GetInputDesktopName() ?? string.Empty;
        if (_worker is { IsAlive: true } &&
            string.Equals(activeDesktop, _desktopName,
                StringComparison.OrdinalIgnoreCase))
            return;

        StopWorker();
        _desktopName = activeDesktop;
        _stopWorker = false;
        _started.Reset();
        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "Comote Secure Desktop Input",
        };
        _worker.Start();
        if (!_started.Wait(TimeSpan.FromSeconds(2)) || _failure != null)
            throw new InvalidOperationException(
                "The active Windows input desktop could not be opened.",
                _failure);
    }

    private void WorkerLoop()
    {
        try
        {
            if (!SessionManager.SwitchToInputDesktop())
                throw new InvalidOperationException(
                    "SetThreadDesktop rejected the input worker.");
        }
        catch (Exception ex)
        {
            _failure = ex;
            _started.Set();
            return;
        }

        _started.Set();
        while (!_stopWorker)
        {
            _request.WaitOne();
            if (_stopWorker) break;
            try
            {
                _pending?.Invoke();
            }
            catch (Exception ex)
            {
                _failure = ex;
            }
            finally
            {
                _pending = null;
                _completed.Set();
            }
        }

        try { _inner.ReleaseAllInputs(); } catch { }
        _completed.Set();
    }

    private void StopWorker()
    {
        if (_worker is not { IsAlive: true }) return;
        _stopWorker = true;
        _request.Set();
        _worker.Join(TimeSpan.FromSeconds(2));
        _worker = null;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            StopWorker();
            _disposed = true;
            _inner.Dispose();
            _request.Dispose();
            _completed.Dispose();
            _started.Dispose();
        }
    }
}
