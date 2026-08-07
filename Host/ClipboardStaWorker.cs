using System;
using System.Threading;

namespace Host
{
    internal sealed class ClipboardStaWorker : IDisposable
    {
        private readonly object _gate = new();
        private readonly AutoResetEvent _wake = new(initialState: false);
        private readonly Thread _thread;
        private readonly TimeSpan _disposeJoinTimeout;
        private Action? _pendingClipboardSet;
        private Action? _pendingPoll;
        private bool _stopping;

        public ClipboardStaWorker(TimeSpan? disposeJoinTimeout = null)
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException(
                    "The clipboard STA worker requires Windows.");
            }

            _disposeJoinTimeout = disposeJoinTimeout ??
                TimeSpan.FromSeconds(2);
            if (_disposeJoinTimeout <= TimeSpan.Zero ||
                _disposeJoinTimeout > TimeSpan.FromSeconds(5))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(disposeJoinTimeout));
            }

            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "Comote Clipboard STA",
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        internal bool IsAlive => _thread.IsAlive;

        public bool TryQueueClipboardSet(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            lock (_gate)
            {
                if (_stopping)
                {
                    return false;
                }

                // Clipboard state is replaceable. Retaining only the newest
                // pending value bounds memory and thread usage under bursts.
                _pendingClipboardSet = action;
                _wake.Set();
                return true;
            }
        }

        public bool TryQueuePoll(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            lock (_gate)
            {
                if (_stopping)
                {
                    return false;
                }

                // Timer ticks coalesce while the single STA worker is busy.
                _pendingPoll ??= action;
                _wake.Set();
                return true;
            }
        }

        public void CancelPending()
        {
            lock (_gate)
            {
                _pendingClipboardSet = null;
                _pendingPoll = null;
            }
        }

        private void Run()
        {
            try
            {
                while (true)
                {
                    _wake.WaitOne();

                    Action? clipboardSet;
                    Action? poll;
                    lock (_gate)
                    {
                        if (_stopping)
                        {
                            return;
                        }

                        clipboardSet = _pendingClipboardSet;
                        poll = _pendingPoll;
                        _pendingClipboardSet = null;
                        _pendingPoll = null;
                    }

                    RunSafely(clipboardSet);
                    RunSafely(poll);
                }
            }
            finally
            {
                _wake.Dispose();
            }
        }

        private static void RunSafely(Action? action)
        {
            if (action == null)
            {
                return;
            }

            try
            {
                action();
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[Clipboard] STA worker action failed: {ex.GetType().Name}");
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (!_stopping)
                {
                    _stopping = true;
                    _pendingClipboardSet = null;
                    _pendingPoll = null;
                    _wake.Set();
                }
            }

            // Every caller may perform the same bounded re-join. The worker
            // owns and disposes the wait handle when it actually exits, so a
            // first timeout cannot leak the handle or make later cleanup inert.
            if (!ReferenceEquals(Thread.CurrentThread, _thread))
            {
                _ = _thread.Join(_disposeJoinTimeout);
            }
        }
    }
}
