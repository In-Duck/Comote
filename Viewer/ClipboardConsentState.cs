using System;
using System.Threading;

namespace Viewer
{
    public readonly record struct ClipboardConsentSnapshot(
        long Epoch,
        bool Requested,
        bool Enabled);

    internal sealed class ClipboardConsentState
    {
        private readonly object _gate = new();
        private long _epoch;
        private bool _requested;
        private bool _enabled;
        private int _activeLeases;

        public bool Requested
        {
            get
            {
                lock (_gate)
                {
                    return _requested;
                }
            }
        }

        public bool Enabled
        {
            get
            {
                lock (_gate)
                {
                    return _enabled;
                }
            }
        }

        public long Epoch
        {
            get
            {
                lock (_gate)
                {
                    return _epoch;
                }
            }
        }

        public ClipboardConsentSnapshot Snapshot
        {
            get
            {
                lock (_gate)
                {
                    return SnapshotLocked();
                }
            }
        }

        internal int ActiveLeaseCount
        {
            get
            {
                lock (_gate)
                {
                    return _activeLeases;
                }
            }
        }

        public ClipboardConsentSnapshot Request(bool enabled)
        {
            lock (_gate)
            {
                IncrementEpochLocked();
                _requested = enabled;
                _enabled = false;
                return SnapshotLocked();
            }
        }

        public ClipboardConsentSnapshot ApplyAcknowledgement(bool enabled)
        {
            lock (_gate)
            {
                bool applied = enabled && _requested;
                if (_enabled != applied)
                {
                    IncrementEpochLocked();
                    _enabled = applied;
                }

                return SnapshotLocked();
            }
        }

        public bool TryCaptureActiveEpoch(out long epoch)
        {
            lock (_gate)
            {
                epoch = _epoch;
                return _requested && _enabled;
            }
        }

        public bool IsActive(long expectedEpoch)
        {
            lock (_gate)
            {
                return _requested &&
                    _enabled &&
                    _epoch == expectedEpoch;
            }
        }

        public bool TryAcquireActiveLease(
            long expectedEpoch,
            out ClipboardConsentLease? lease)
        {
            lock (_gate)
            {
                if (!_requested ||
                    !_enabled ||
                    _epoch != expectedEpoch)
                {
                    lease = null;
                    return false;
                }

                _activeLeases = checked(_activeLeases + 1);
                lease = new ClipboardConsentLease(this, expectedEpoch);
                return true;
            }
        }

        public ClipboardConsentSnapshot Revoke()
        {
            lock (_gate)
            {
                // Revocation is non-blocking: it invalidates the epoch so no
                // new lease can start. Work that already acquired a lease is
                // linearized before this revoke and may finish safely.
                IncrementEpochLocked();
                _requested = false;
                _enabled = false;
                return SnapshotLocked();
            }
        }

        private void ReleaseLease(long leaseEpoch)
        {
            lock (_gate)
            {
                if (_activeLeases <= 0)
                {
                    throw new InvalidOperationException(
                        "Clipboard consent lease accounting underflowed.");
                }

                _activeLeases--;
                _ = leaseEpoch;
            }
        }

        private ClipboardConsentSnapshot SnapshotLocked() =>
            new(_epoch, _requested, _enabled);

        private void IncrementEpochLocked()
        {
            if (_epoch == long.MaxValue)
            {
                throw new InvalidOperationException(
                    "Clipboard consent epoch was exhausted.");
            }

            _epoch++;
        }

        internal sealed class ClipboardConsentLease : IDisposable
        {
            private ClipboardConsentState? _owner;

            internal ClipboardConsentLease(
                ClipboardConsentState owner,
                long epoch)
            {
                _owner = owner;
                Epoch = epoch;
            }

            public long Epoch { get; }

            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                owner?.ReleaseLease(Epoch);
            }
        }
    }

    internal sealed class ClipboardConsentProjection
    {
        public long Epoch { get; private set; } = -1;
        public bool Requested { get; private set; }
        public bool Enabled { get; private set; }

        public bool TryApply(ClipboardConsentSnapshot snapshot)
        {
            if (snapshot.Epoch <= Epoch)
            {
                return false;
            }

            Epoch = snapshot.Epoch;
            Requested = snapshot.Requested;
            Enabled = snapshot.Enabled;
            return true;
        }

        public void Reset(ClipboardConsentSnapshot? snapshot = null)
        {
            if (snapshot is { } value)
            {
                Epoch = value.Epoch;
                Requested = value.Requested;
                Enabled = value.Enabled;
                return;
            }

            Epoch = -1;
            Requested = false;
            Enabled = false;
        }
    }
}
