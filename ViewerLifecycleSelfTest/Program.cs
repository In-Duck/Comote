using Viewer;
using Host;

var tests = new (string Name, Action Run)[]
{
    ("generation rejects stale callbacks", TestGenerationRejectsStale),
    ("answer is accepted once", TestAnswerAcceptedOnce),
    ("termination revokes connectivity", TestTermination),
    ("left and right modifiers are independent", TestModifierSides),
    ("modifier reset clears all sides", TestModifierReset),
    ("clipboard revoke invalidates an in-flight epoch", TestClipboardEpochRevocation),
    ("unexpected clipboard enable acknowledgement stays disabled", TestUnexpectedClipboardEnable),
    ("clipboard acknowledgement transition invalidates stale epoch", TestClipboardAcknowledgementTransition),
    ("clipboard revoke does not wait for an acquired lease", TestClipboardLeaseRevocation),
    ("clipboard projection rejects stale snapshots", TestClipboardProjectionRejectsStale),
    ("rapid clipboard toggles preserve the newest request", TestRapidClipboardToggles),
    ("host clipboard monitor rejects stale transitions", TestClipboardMonitoringTransition),
    ("clipboard STA worker coalesces pending work", TestClipboardWorkerCoalesces),
    ("clipboard STA worker can rejoin after timeout", TestClipboardWorkerDisposeRejoins),
};

foreach (var test in tests)
{
    test.Run();
    Console.WriteLine($"PASS: {test.Name}");
}

static void TestGenerationRejectsStale()
{
    var state = new ConnectionGenerationState();
    long initial = state.Current;
    Assert(initial > 0);
    long first = state.BeginNew();
    Assert(first == initial + 1);
    Assert(state.IsCurrent(first));
    Assert(state.MarkConnected(first));

    long second = state.BeginNew();
    Assert(second == first + 1);
    Assert(!state.IsCurrent(first));
    Assert(!state.IsConnected(first));
    Assert(state.IsCurrent(second));
}

static void TestAnswerAcceptedOnce()
{
    var state = new ConnectionGenerationState();
    long generation = state.BeginNew();
    Assert(state.TryAcceptAnswer(generation));
    Assert(!state.TryAcceptAnswer(generation));
}

static void TestTermination()
{
    var state = new ConnectionGenerationState();
    long generation = state.BeginNew();
    Assert(state.MarkConnected(generation));
    Assert(state.IsConnected(generation));
    Assert(state.Terminate(generation));
    Assert(!state.IsCurrent(generation));
    Assert(!state.IsConnected(generation));
    Assert(!state.Terminate(generation));
}

static void TestModifierSides()
{
    var state = new ModifierKeyState();
    state.Update(0xA2, true);
    state.Update(0xA3, true);
    state.Update(0xA2, false);
    Assert(state.Control);
    state.Update(0xA3, false);
    Assert(!state.Control);

    state.Update(0xA0, true);
    state.Update(0xA1, true);
    state.Update(0xA0, false);
    Assert(state.Shift);
}

static void TestModifierReset()
{
    var state = new ModifierKeyState();
    foreach (ushort key in new ushort[]
             {
                 0xA2, 0xA3, 0xA0, 0xA1,
                 0xA4, 0xA5, 0x5B, 0x5C
             })
    {
        state.Update(key, true);
    }

    state.Reset();
    Assert(!state.Control);
    Assert(!state.Shift);
    Assert(!state.Alt);
    Assert(!state.Windows);
}

static void TestClipboardEpochRevocation()
{
    var state = new ClipboardConsentState();
    state.Request(true);
    Assert(state.ApplyAcknowledgement(true).Enabled);
    Assert(state.TryCaptureActiveEpoch(out long activeEpoch));
    Assert(state.IsActive(activeEpoch));

    state.Request(false);
    Assert(!state.IsActive(activeEpoch));
    Assert(!state.TryCaptureActiveEpoch(out _));
}

static void TestUnexpectedClipboardEnable()
{
    var state = new ClipboardConsentState();
    Assert(!state.ApplyAcknowledgement(true).Enabled);
    Assert(!state.Enabled);

    state.Request(true);
    Assert(state.ApplyAcknowledgement(true).Enabled);
    state.Revoke();
    Assert(!state.IsActive(state.Epoch));
}

static void TestClipboardAcknowledgementTransition()
{
    var state = new ClipboardConsentState();
    state.Request(true);
    Assert(state.ApplyAcknowledgement(true).Enabled);
    Assert(state.TryCaptureActiveEpoch(out long originalEpoch));

    Assert(!state.ApplyAcknowledgement(false).Enabled);
    Assert(!state.IsActive(originalEpoch));
    Assert(state.ApplyAcknowledgement(true).Enabled);
    Assert(!state.IsActive(originalEpoch));
}

static void TestClipboardLeaseRevocation()
{
    var state = new ClipboardConsentState();
    state.Request(true);
    var enabled = state.ApplyAcknowledgement(true);
    Assert(enabled.Enabled);
    Assert(state.TryAcquireActiveLease(enabled.Epoch, out var lease));
    var heldLease = lease ?? throw new InvalidOperationException(
        "Expected an active clipboard lease.");
    Assert(state.ActiveLeaseCount == 1);

    try
    {
        var revokeTask = Task.Run(state.Revoke);
        Assert(revokeTask.Wait(TimeSpan.FromSeconds(1)));
        var revoked = revokeTask.Result;
        Assert(!revoked.Requested);
        Assert(!revoked.Enabled);
        Assert(!state.TryAcquireActiveLease(revoked.Epoch, out _));

        // The already-acquired operation is linearized before revoke;
        // revoke never waits for it, while later acquisitions are denied.
        Assert(state.ActiveLeaseCount == 1);
    }
    finally
    {
        heldLease.Dispose();
        heldLease.Dispose();
    }

    Assert(state.ActiveLeaseCount == 0);
}

static void TestClipboardProjectionRejectsStale()
{
    var projection = new ClipboardConsentProjection();
    Assert(projection.TryApply(new ClipboardConsentSnapshot(5, true, true)));
    Assert(!projection.TryApply(new ClipboardConsentSnapshot(4, false, false)));
    Assert(!projection.TryApply(new ClipboardConsentSnapshot(5, false, false)));
    Assert(projection.Epoch == 5);
    Assert(projection.Requested);
    Assert(projection.Enabled);
}

static void TestRapidClipboardToggles()
{
    var state = new ClipboardConsentState();
    var projection = new ClipboardConsentProjection();

    var firstEnable = state.Request(true);
    Assert(projection.TryApply(firstEnable));
    var firstRejection = state.ApplyAcknowledgement(false);
    Assert(!projection.TryApply(firstRejection));
    Assert(projection.Requested);
    Assert(!projection.Enabled);

    var disable = state.Request(false);
    Assert(projection.TryApply(disable));
    var disableAcknowledgement = state.ApplyAcknowledgement(false);
    Assert(!projection.TryApply(disableAcknowledgement));

    var newestEnable = state.Request(true);
    Assert(projection.TryApply(newestEnable));
    Assert(!projection.TryApply(disable));
    Assert(projection.Requested);
    Assert(!projection.Enabled);

    bool nextToggleRequest = !projection.Requested;
    Assert(!nextToggleRequest);
}

static void TestClipboardMonitoringTransition()
{
    var firstEnable = new ClipboardMonitoringTransition(10, true);
    var laterDisable = new ClipboardMonitoringTransition(11, false);
    var newestEnable = new ClipboardMonitoringTransition(12, true);

    Assert(ClipboardMonitoringTransitionPolicy.IsCurrent(
        12, true, newestEnable));
    Assert(!ClipboardMonitoringTransitionPolicy.IsCurrent(
        12, true, laterDisable));
    Assert(!ClipboardMonitoringTransitionPolicy.IsCurrent(
        12, true, firstEnable));
}

static void TestClipboardWorkerCoalesces()
{
    using var firstEntered = new ManualResetEventSlim();
    using var releaseFirst = new ManualResetEventSlim();
    using var latestExecuted = new ManualResetEventSlim();
    using var pollExecuted = new ManualResetEventSlim();
    var worker = new ClipboardStaWorker(TimeSpan.FromSeconds(1));
    int firstCount = 0;
    int replacedCount = 0;
    int latestCount = 0;
    int retainedPollCount = 0;
    int droppedPollCount = 0;

    try
    {
        Assert(worker.TryQueueClipboardSet(() =>
        {
            Interlocked.Increment(ref firstCount);
            firstEntered.Set();
            _ = releaseFirst.Wait(TimeSpan.FromSeconds(5));
        }));
        Assert(firstEntered.Wait(TimeSpan.FromSeconds(2)));

        Assert(worker.TryQueueClipboardSet(() =>
            Interlocked.Increment(ref replacedCount)));
        Assert(worker.TryQueueClipboardSet(() =>
        {
            Interlocked.Increment(ref latestCount);
            latestExecuted.Set();
        }));
        Assert(worker.TryQueuePoll(() =>
        {
            Interlocked.Increment(ref retainedPollCount);
            pollExecuted.Set();
        }));
        Assert(worker.TryQueuePoll(() =>
            Interlocked.Increment(ref droppedPollCount)));

        releaseFirst.Set();
        Assert(latestExecuted.Wait(TimeSpan.FromSeconds(2)));
        Assert(pollExecuted.Wait(TimeSpan.FromSeconds(2)));
        Assert(Volatile.Read(ref firstCount) == 1);
        Assert(Volatile.Read(ref replacedCount) == 0);
        Assert(Volatile.Read(ref latestCount) == 1);
        Assert(Volatile.Read(ref retainedPollCount) == 1);
        Assert(Volatile.Read(ref droppedPollCount) == 0);
    }
    finally
    {
        releaseFirst.Set();
        worker.Dispose();
    }

    Assert(!worker.IsAlive);
}

static void TestClipboardWorkerDisposeRejoins()
{
    using var entered = new ManualResetEventSlim();
    using var release = new ManualResetEventSlim();
    var worker = new ClipboardStaWorker(TimeSpan.FromMilliseconds(500));

    try
    {
        Assert(worker.TryQueueClipboardSet(() =>
        {
            entered.Set();
            _ = release.Wait(TimeSpan.FromSeconds(5));
        }));
        Assert(entered.Wait(TimeSpan.FromSeconds(2)));

        worker.Dispose();
        Assert(worker.IsAlive);

        release.Set();
        worker.Dispose();
        Assert(!worker.IsAlive);

        // The worker owns wait-handle cleanup, so repeated disposal after
        // the timed-out join remains safe and deterministic.
        worker.Dispose();
    }
    finally
    {
        release.Set();
        worker.Dispose();
    }
}
static void Assert(bool condition)
{
    if (!condition)
        throw new InvalidOperationException("Assertion failed.");
}
