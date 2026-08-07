using Comote.Input;
using Host;
using System.Buffers.Binary;
using System.Diagnostics;

namespace Comote.VirtualHidE2E;

internal sealed class ValidationRunner(
    ValidationConfig config,
    ValidationReport report,
    RawInputValidationForm form)
{
    private const ushort AllMouseButtonsDown =
        NativeMethods.RiMouseLeftButtonDown |
        NativeMethods.RiMouseRightButtonDown |
        NativeMethods.RiMouseMiddleButtonDown |
        NativeMethods.RiMouseButton4Down |
        NativeMethods.RiMouseButton5Down;

    private const ushort AllMouseButtonsUp =
        NativeMethods.RiMouseLeftButtonUp |
        NativeMethods.RiMouseRightButtonUp |
        NativeMethods.RiMouseMiddleButtonUp |
        NativeMethods.RiMouseButton4Up |
        NativeMethods.RiMouseButton5Up;

    private static readonly ushort[] SixKeyVirtualKeys =
        [0x7C, 0x7D, 0x7E, 0x7F, 0x80, 0x81];

    private readonly HashSet<string> _expectedKeyboardIds =
        config.ExpectedKeyboardInstanceIds
            .Select(NormalizeInstanceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _expectedMouseIds =
        config.ExpectedMouseInstanceIds
            .Select(NormalizeInstanceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public async Task RunAsync()
    {
        await Task.Delay(350).ConfigureAwait(true);
        form.AssertOwnsForeground();

        await RunDirectBrokerTestsAsync().ConfigureAwait(true);
        await RunHostBackendTestsAsync().ConfigureAwait(true);
    }

    private async Task RunDirectBrokerTestsAsync()
    {
        using var broker = new InputBrokerClient();
        var status = broker.GetStatus();
        AssertBrokerSuccess(status, "broker status");

        await RunTestAsync(
            "Broker to driver to Raw Input keyboard (F24 press/release)",
            "broker-keyboard-f24",
            async startIndex =>
            {
                AssertBrokerSuccess(
                    broker.SetKeyboardState(0, [0x73]),
                    "F24 key-down");
                await Task.Delay(90).ConfigureAwait(true);
                AssertBrokerSuccess(
                    broker.SetKeyboardState(0, []),
                    "F24 key-up");

                await WaitForAsync(
                    startIndex,
                    events => HasKeyboardPair(
                        events,
                        "broker-keyboard-f24",
                        expectedVirtualKey: 0x87),
                    "F24 Raw Input make/break events")
                    .ConfigureAwait(true);
            }).ConfigureAwait(true);

        await RunTestAsync(
            "Broker to driver to Raw Input six-key rollover",
            "broker-keyboard-6kro",
            async startIndex =>
            {
                AssertBrokerSuccess(
                    broker.SetKeyboardState(
                        0,
                        [0x68, 0x69, 0x6A, 0x6B, 0x6C, 0x6D]),
                    "six-key key-down");
                await Task.Delay(120).ConfigureAwait(true);
                AssertBrokerSuccess(
                    broker.SetKeyboardState(0, []),
                    "six-key key-up");

                await WaitForAsync(
                    startIndex,
                    events => SixKeyVirtualKeys.All(virtualKey =>
                        HasKeyboardPair(
                            events,
                            "broker-keyboard-6kro",
                            virtualKey)),
                    "all six Raw Input make/break pairs")
                    .ConfigureAwait(true);
            }).ConfigureAwait(true);

        await RunTestAsync(
            "Broker to driver to Raw Input relative mouse",
            "broker-relative-mouse",
            async startIndex =>
            {
                AssertBrokerSuccess(
                    broker.MouseRelative(0, 11, 7, 0, 0),
                    "relative mouse");
                await WaitForAsync(
                    startIndex,
                    events => events.Any(e =>
                        IsExpectedMouse(e) &&
                        e.Stage == "broker-relative-mouse" &&
                        !e.MouseAbsolute &&
                        e.DeltaX == 11 &&
                        e.DeltaY == 7),
                    "relative Raw Input event")
                    .ConfigureAwait(true);
            }).ConfigureAwait(true);

        await RunTestAsync(
            "Broker to driver to Raw Input absolute mouse",
            "broker-absolute-mouse",
            async startIndex =>
            {
                AssertBrokerSuccess(
                    broker.MouseAbsolute(0, 16384, 16384, 0, 0),
                    "absolute mouse");
                await WaitForAsync(
                    startIndex,
                    events => events.Any(e =>
                        IsExpectedMouse(e) &&
                        e.Stage == "broker-absolute-mouse" &&
                        e.MouseAbsolute),
                    "absolute Raw Input event")
                    .ConfigureAwait(true);
            }).ConfigureAwait(true);

        await RunTestAsync(
            "Broker to driver to Raw Input five mouse buttons",
            "broker-mouse-five-buttons",
            async startIndex =>
            {
                AssertBrokerSuccess(
                    broker.MouseRelative(0x1F, 0, 0, 0, 0),
                    "five mouse buttons down");
                await Task.Delay(90).ConfigureAwait(true);
                AssertBrokerSuccess(
                    broker.MouseRelative(0, 0, 0, 0, 0),
                    "five mouse buttons up");

                await WaitForAsync(
                    startIndex,
                    events => HasMouseButtonTransitions(
                        events,
                        "broker-mouse-five-buttons",
                        AllMouseButtonsDown,
                        AllMouseButtonsUp),
                    "all five Raw Input mouse-button transitions")
                    .ConfigureAwait(true);
            }).ConfigureAwait(true);

        await RunTestAsync(
            "Broker to driver to Raw Input vertical and horizontal wheel",
            "broker-mouse-wheels",
            async startIndex =>
            {
                AssertBrokerSuccess(
                    broker.MouseRelative(0, 0, 0, 120, 0),
                    "vertical wheel");
                await Task.Delay(90).ConfigureAwait(true);
                AssertBrokerSuccess(
                    broker.MouseRelative(0, 0, 0, 0, -120),
                    "horizontal wheel");

                await WaitForAsync(
                    startIndex,
                    events =>
                        HasWheelDelta(
                            events,
                            "broker-mouse-wheels",
                            NativeMethods.RiMouseWheel,
                            120) &&
                        HasWheelDelta(
                            events,
                            "broker-mouse-wheels",
                            NativeMethods.RiMouseHorizontalWheel,
                            -120),
                    "vertical and horizontal Raw Input wheel events")
                    .ConfigureAwait(true);
            }).ConfigureAwait(true);

        AssertBrokerSuccess(broker.ReleaseAll(), "direct broker release-all");
    }

    private async Task RunHostBackendTestsAsync()
    {
        var virtualLeft = GetSystemMetrics(76);
        var virtualTop = GetSystemMetrics(77);
        var virtualWidth = GetSystemMetrics(78);
        var virtualHeight = GetSystemMetrics(79);
        if (virtualWidth <= 0 || virtualHeight <= 0)
        {
            throw new InvalidOperationException(
                "Windows virtual-desktop metrics were invalid.");
        }

        using var backend = new VirtualHidInputBackend(
            virtualLeft,
            virtualTop,
            virtualWidth,
            virtualHeight);
        var status = backend.GetStatus();
        if (!status.IsAvailable)
        {
            throw new InvalidOperationException(
                $"VirtualHidInputBackend was unavailable: {status.Detail}");
        }

        await RunTestAsync(
            "HostInputProtocol to VirtualHidInputBackend keyboard",
            "host-backend-keyboard-f12",
            async startIndex =>
            {
                AssertDispatchAccepted(
                    backend.ProcessMessage(CreateLegacyKeyMessage(
                        HostInputProtocol.KeyDownMessage,
                        0x7B)),
                    "Host F12 key-down");
                await Task.Delay(90).ConfigureAwait(true);
                AssertDispatchAccepted(
                    backend.ProcessMessage(CreateLegacyKeyMessage(
                        HostInputProtocol.KeyUpMessage,
                        0x7B)),
                    "Host F12 key-up");

                await WaitForAsync(
                    startIndex,
                    events => HasKeyboardPair(
                        events,
                        "host-backend-keyboard-f12",
                        expectedVirtualKey: 0x7B),
                    "Host-path F12 Raw Input make/break events")
                    .ConfigureAwait(true);
            }).ConfigureAwait(true);

        await RunTestAsync(
            "HostInputProtocol to VirtualHidInputBackend left Control",
            "host-backend-left-control",
            async startIndex =>
            {
                AssertDispatchAccepted(
                    backend.ProcessMessage(CreateExtendedKeyMessage(
                        HostInputProtocol.KeyDownMessage,
                        virtualKey: 0xA2,
                        scanCode: 0x1D,
                        flags: 0)),
                    "Host left-Control key-down");
                await Task.Delay(90).ConfigureAwait(true);
                AssertDispatchAccepted(
                    backend.ProcessMessage(CreateExtendedKeyMessage(
                        HostInputProtocol.KeyUpMessage,
                        virtualKey: 0xA2,
                        scanCode: 0x1D,
                        flags: 0)),
                    "Host left-Control key-up");

                await WaitForAsync(
                    startIndex,
                    events => HasKeyboardScanCodePair(
                        events,
                        "host-backend-left-control",
                        expectedMakeCode: 0x1D),
                    "Host-path left-Control Raw Input make/break events")
                    .ConfigureAwait(true);
            }).ConfigureAwait(true);

        await RunTestAsync(
            "HostInputProtocol to VirtualHidInputBackend absolute mouse",
            "host-backend-absolute-mouse",
            async startIndex =>
            {
                AssertDispatchAccepted(
                    backend.ProcessMessage(CreateMouseMoveMessage(0.25f, 0.25f)),
                    "Host absolute mouse");
                await WaitForAsync(
                    startIndex,
                    events => events.Any(e =>
                        IsExpectedMouse(e) &&
                        e.Stage == "host-backend-absolute-mouse" &&
                        e.MouseAbsolute),
                    "Host-path absolute Raw Input event")
                    .ConfigureAwait(true);
            }).ConfigureAwait(true);

        await RunTestAsync(
            "HostInputProtocol to VirtualHidInputBackend X2 mouse button",
            "host-backend-x2-button",
            async startIndex =>
            {
                AssertDispatchAccepted(
                    backend.ProcessMessage(CreateMouseButtonMessage(
                        HostInputProtocol.MouseDownMessage,
                        HostInputProtocol.XButton2,
                        0.25f,
                        0.25f)),
                    "Host X2 button-down");
                await Task.Delay(90).ConfigureAwait(true);
                AssertDispatchAccepted(
                    backend.ProcessMessage(CreateMouseButtonMessage(
                        HostInputProtocol.MouseUpMessage,
                        HostInputProtocol.XButton2,
                        0.25f,
                        0.25f)),
                    "Host X2 button-up");

                await WaitForAsync(
                    startIndex,
                    events => HasMouseButtonTransitions(
                        events,
                        "host-backend-x2-button",
                        NativeMethods.RiMouseButton5Down,
                        NativeMethods.RiMouseButton5Up),
                    "Host-path X2 Raw Input button transitions")
                    .ConfigureAwait(true);
            }).ConfigureAwait(true);

        await RunTestAsync(
            "HostInputProtocol to VirtualHidInputBackend vertical wheel",
            "host-backend-vertical-wheel",
            async startIndex =>
            {
                AssertDispatchAccepted(
                    backend.ProcessMessage(CreateMouseWheelMessage(
                        HostInputProtocol.MouseWheelMessage,
                        240)),
                    "Host vertical wheel");
                await WaitForAsync(
                    startIndex,
                    events => HasWheelDelta(
                        events,
                        "host-backend-vertical-wheel",
                        NativeMethods.RiMouseWheel,
                        240),
                    "Host-path vertical Raw Input wheel total")
                    .ConfigureAwait(true);
            }).ConfigureAwait(true);

        await RunTestAsync(
            "HostInputProtocol to VirtualHidInputBackend horizontal wheel",
            "host-backend-horizontal-wheel",
            async startIndex =>
            {
                AssertDispatchAccepted(
                    backend.ProcessMessage(CreateMouseWheelMessage(
                        HostInputProtocol.MouseHorizontalWheelMessage,
                        -240)),
                    "Host horizontal wheel");
                await WaitForAsync(
                    startIndex,
                    events => HasWheelDelta(
                        events,
                        "host-backend-horizontal-wheel",
                        NativeMethods.RiMouseHorizontalWheel,
                        -240),
                    "Host-path horizontal Raw Input wheel total")
                    .ConfigureAwait(true);
            }).ConfigureAwait(true);

        backend.ReleaseAllInputs();
    }

    private async Task RunTestAsync(
        string name,
        string stage,
        Func<int, Task> action)
    {
        form.AssertOwnsForeground();
        var evidence = new TestEvidence
        {
            Name = name,
            StartedUtc = DateTime.UtcNow,
        };
        report.Tests.Add(evidence);

        form.Stage = stage;
        var startIndex = form.EventCount;
        try
        {
            await action(startIndex).ConfigureAwait(true);
            evidence.Passed = true;
            evidence.Detail = "Observed from the exact Phase 2 VHF child.";
        }
        catch (Exception ex)
        {
            evidence.Passed = false;
            evidence.Detail = $"{ex.GetType().Name}: {ex.Message}";
            throw;
        }
        finally
        {
            evidence.CompletedUtc = DateTime.UtcNow;
        }
    }

    private async Task WaitForAsync(
        int startIndex,
        Func<IReadOnlyList<RawInputEvidence>, bool> predicate,
        string description)
    {
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(4))
        {
            var events = form.Snapshot();
            if (startIndex <= events.Count &&
                predicate(events.Skip(startIndex).ToArray()))
            {
                return;
            }
            await Task.Delay(25).ConfigureAwait(true);
        }

        throw new TimeoutException(
            $"Timed out waiting for {description}.");
    }

    private bool HasKeyboardPair(
        IEnumerable<RawInputEvidence> events,
        string stage,
        ushort expectedVirtualKey)
    {
        var matches = events
            .Where(e =>
                IsExpectedKeyboard(e) &&
                e.Stage == stage &&
                e.VirtualKey == expectedVirtualKey)
            .ToArray();
        return matches.Any(e => !e.KeyBreak) &&
            matches.Any(e => e.KeyBreak);
    }

    private bool HasKeyboardScanCodePair(
        IEnumerable<RawInputEvidence> events,
        string stage,
        ushort expectedMakeCode)
    {
        var matches = events
            .Where(e =>
                IsExpectedKeyboard(e) &&
                e.Stage == stage &&
                e.MakeCode == expectedMakeCode)
            .ToArray();
        return matches.Any(e => !e.KeyBreak) &&
            matches.Any(e => e.KeyBreak);
    }

    private bool HasMouseButtonTransitions(
        IEnumerable<RawInputEvidence> events,
        string stage,
        ushort expectedDownFlags,
        ushort expectedUpFlags)
    {
        ushort observedFlags = 0;
        foreach (var evidence in events.Where(e =>
                     IsExpectedMouse(e) && e.Stage == stage))
        {
            observedFlags |= evidence.MouseButtonFlags;
        }

        return (observedFlags & expectedDownFlags) == expectedDownFlags &&
            (observedFlags & expectedUpFlags) == expectedUpFlags;
    }

    private bool HasWheelDelta(
        IEnumerable<RawInputEvidence> events,
        string stage,
        ushort wheelFlag,
        int expectedTotal)
    {
        var total = events
            .Where(e =>
                IsExpectedMouse(e) &&
                e.Stage == stage &&
                (e.MouseButtonFlags & wheelFlag) != 0)
            .Sum(e => (int)e.MouseButtonData);
        return total == expectedTotal;
    }

    private bool IsExpectedKeyboard(RawInputEvidence evidence) =>
        evidence.Kind == RawInputKind.Keyboard &&
        _expectedKeyboardIds.Contains(evidence.NormalizedInstanceId);

    private bool IsExpectedMouse(RawInputEvidence evidence) =>
        evidence.Kind == RawInputKind.Mouse &&
        _expectedMouseIds.Contains(evidence.NormalizedInstanceId);

    private static byte[] CreateLegacyKeyMessage(
        byte type,
        ushort virtualKey)
    {
        var message = new byte[HostInputProtocol.LegacyKeySize];
        message[0] = type;
        BinaryPrimitives.WriteUInt16LittleEndian(
            message.AsSpan(1),
            virtualKey);
        return message;
    }

    private static byte[] CreateExtendedKeyMessage(
        byte type,
        ushort virtualKey,
        ushort scanCode,
        uint flags)
    {
        var message = new byte[HostInputProtocol.ExtendedKeySize];
        message[0] = type;
        BinaryPrimitives.WriteUInt16LittleEndian(
            message.AsSpan(1),
            virtualKey);
        BinaryPrimitives.WriteUInt16LittleEndian(
            message.AsSpan(3),
            scanCode);
        BinaryPrimitives.WriteUInt32LittleEndian(
            message.AsSpan(5),
            flags);
        return message;
    }

    private static byte[] CreateMouseMoveMessage(float x, float y)
    {
        var message = new byte[HostInputProtocol.MouseMoveSize];
        message[0] = HostInputProtocol.MouseMoveMessage;
        WriteNormalizedCoordinates(message.AsSpan(1), x, y);
        return message;
    }

    private static byte[] CreateMouseButtonMessage(
        byte type,
        byte button,
        float x,
        float y)
    {
        var message = new byte[HostInputProtocol.MouseButtonSize];
        message[0] = type;
        message[1] = button;
        WriteNormalizedCoordinates(message.AsSpan(2), x, y);
        return message;
    }

    private static byte[] CreateMouseWheelMessage(byte type, int delta)
    {
        var message = new byte[HostInputProtocol.MouseWheelSize];
        message[0] = type;
        BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(1), delta);
        return message;
    }

    private static void WriteNormalizedCoordinates(
        Span<byte> destination,
        float x,
        float y)
    {
        BinaryPrimitives.WriteInt32LittleEndian(
            destination,
            BitConverter.SingleToInt32Bits(x));
        BinaryPrimitives.WriteInt32LittleEndian(
            destination[4..],
            BitConverter.SingleToInt32Bits(y));
    }

    private static string NormalizeInstanceId(string value) =>
        value.Trim().TrimEnd('\\').ToUpperInvariant();

    private static void AssertBrokerSuccess(
        BrokerResponse response,
        string operation)
    {
        if (!response.IsSuccess)
        {
            throw new InvalidOperationException(
                $"{operation} failed: {response.Status}: {response.Detail}");
        }
    }

    private static void AssertDispatchAccepted(
        InputDispatchResult result,
        string operation)
    {
        if (!result.IsAccepted)
        {
            throw new InvalidOperationException(
                $"{operation} failed: {result.Code}: {result.Detail}");
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}