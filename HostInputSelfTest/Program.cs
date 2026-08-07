using System.Buffers.Binary;
using Comote.Input;
using Host;
using Newtonsoft.Json.Linq;

RunProtocolTests();
RunFactoryTests();
RunCoordinateTests();
RunKeyboardStateTests();
RunInboundFileTransferTests();
RunSystemCommandExecutorTests();

Console.WriteLine(
    "Host input pure self-test passed. " +
    "Only isolated temporary files were used; no SendInput API, " +
    "broker pipe, device, or driver was opened.");

static void RunSystemCommandExecutorTests()
{
    static JObject Command(
        string action, string commandId, long issuedAt, int delay) =>
        new()
        {
            ["type"] = "command",
            ["action"] = action,
            ["name"] = action,
            ["commandId"] = commandId,
            ["issuedAtUnixMs"] = issuedAt,
            ["delaySeconds"] = delay,
        };

    var now = RemoteCommandProtocol.UnixTimeMilliseconds();

    // A valid, fresh reboot must actually invoke the executor with its delay.
    var recorder = new RecordingSystemExecutor();
    var executor = new RemoteTaskExecutor(
        recorder, new RemoteCommandDeduplicator());
    var id = RemoteCommandProtocol.NewCommandId();
    var reboot = executor
        .ExecuteAsync(Command("reboot", id, now, 45), now)
        .GetAwaiter().GetResult();
    Assert(reboot.Value<bool>("ok"), "A valid reboot was not accepted.");
    Assert(recorder.Reboots == 1, "Reboot was not executed once.");
    Assert(recorder.LastDelay == 45, "Reboot delay was not honored.");

    // Replaying the same command ID must not reboot again.
    var replay = executor
        .ExecuteAsync(Command("reboot", id, now, 45), now)
        .GetAwaiter().GetResult();
    Assert(!replay.Value<bool>("ok"), "A duplicate reboot was accepted.");
    Assert(recorder.Reboots == 1, "A duplicate reboot executed twice.");

    // An expired command must be rejected before any side effect.
    var expired = executor
        .ExecuteAsync(
            Command("shutdown", RemoteCommandProtocol.NewCommandId(),
                now - 600_000, 0),
            now)
        .GetAwaiter().GetResult();
    Assert(!expired.Value<bool>("ok"), "An expired shutdown was accepted.");
    Assert(recorder.Shutdowns == 0, "An expired shutdown was executed.");

    // cancel-shutdown and restart-host route to their own side effects.
    executor
        .ExecuteAsync(
            Command("cancel-shutdown",
                RemoteCommandProtocol.NewCommandId(), now, 0),
            now)
        .GetAwaiter().GetResult();
    Assert(recorder.Cancels == 1, "cancel-shutdown was not executed.");
    executor
        .ExecuteAsync(
            Command("restart-host",
                RemoteCommandProtocol.NewCommandId(), now, 0),
            now)
        .GetAwaiter().GetResult();
    Assert(recorder.Restarts == 1, "restart-host was not executed.");
}

static void RunProtocolTests()
{
    var mouseMove = new byte[HostInputProtocol.MouseMoveSize];
    mouseMove[0] = HostInputProtocol.MouseMoveMessage;
    WriteSingle(mouseMove.AsSpan(1), 0.25f);
    WriteSingle(mouseMove.AsSpan(5), 0.75f);
    var result = HostInputProtocol.Parse(mouseMove, out var message);
    Assert(result.IsAccepted, "valid mouse move");
    Assert(
        message.Kind == HostInputMessageKind.MouseMove &&
        message.NormalizedX == 0.25f &&
        message.NormalizedY == 0.75f,
        "mouse move fields");

    var trailingMouseMove = mouseMove.Concat([byte.MinValue]).ToArray();
    result = HostInputProtocol.Parse(trailingMouseMove, out _);
    Assert(
        result.Code == InputDispatchCode.InvalidMessage,
        "mouse move with trailing bytes");

    var nonFiniteMouseMove = mouseMove.ToArray();
    WriteSingle(nonFiniteMouseMove.AsSpan(1), float.NaN);
    result = HostInputProtocol.Parse(nonFiniteMouseMove, out _);
    Assert(
        result.Code == InputDispatchCode.InvalidMessage,
        "non-finite mouse coordinate");

    var xButton = new byte[HostInputProtocol.MouseButtonSize];
    xButton[0] = HostInputProtocol.MouseDownMessage;
    xButton[1] = HostInputProtocol.XButton2;
    WriteSingle(xButton.AsSpan(2), 1f);
    WriteSingle(xButton.AsSpan(6), 0f);
    result = HostInputProtocol.Parse(xButton, out message);
    Assert(
        result.IsAccepted &&
        message.Button == HostInputProtocol.XButton2,
        "fifth mouse button");

    var verticalWheel = new byte[HostInputProtocol.MouseWheelSize];
    verticalWheel[0] = HostInputProtocol.MouseWheelMessage;
    BinaryPrimitives.WriteInt32LittleEndian(
        verticalWheel.AsSpan(1),
        -120);
    result = HostInputProtocol.Parse(verticalWheel, out message);
    Assert(
        result.IsAccepted &&
        message.Kind == HostInputMessageKind.MouseWheel &&
        message.WheelDelta == -120,
        "vertical wheel");

    var horizontalWheel = verticalWheel.ToArray();
    horizontalWheel[0] = HostInputProtocol.MouseHorizontalWheelMessage;
    result = HostInputProtocol.Parse(horizontalWheel, out message);
    Assert(
        result.IsAccepted &&
        message.Kind == HostInputMessageKind.MouseHorizontalWheel,
        "horizontal wheel");

    var extendedKey = new byte[HostInputProtocol.ExtendedKeySize];
    extendedKey[0] = HostInputProtocol.KeyDownMessage;
    BinaryPrimitives.WriteUInt16LittleEndian(
        extendedKey.AsSpan(1),
        0x41);
    BinaryPrimitives.WriteUInt16LittleEndian(
        extendedKey.AsSpan(3),
        0x1E);
    result = HostInputProtocol.Parse(extendedKey, out message);
    Assert(
        result.IsAccepted &&
        message.HasExtendedKeyData &&
        message.VirtualKey == 0x41 &&
        message.ScanCode == 0x1E,
        "extended key packet");

    var legacyKey = extendedKey.AsSpan(0, 3).ToArray();
    result = HostInputProtocol.Parse(legacyKey, out message);
    Assert(
        result.IsAccepted && !message.HasExtendedKeyData,
        "legacy key packet");

    var truncatedKey = extendedKey.AsSpan(0, 8).ToArray();
    result = HostInputProtocol.Parse(truncatedKey, out _);
    Assert(
        result.Code == InputDispatchCode.InvalidMessage,
        "truncated extended key packet");

    var injectedKey = extendedKey.ToArray();
    BinaryPrimitives.WriteUInt32LittleEndian(
        injectedKey.AsSpan(5),
        0x10);
    result = HostInputProtocol.Parse(injectedKey, out _);
    Assert(
        result.Code == InputDispatchCode.InvalidMessage,
        "injected key packet");

    result = HostInputProtocol.Parse([0x7F], out _);
    Assert(
        result.Code == InputDispatchCode.Unsupported,
        "unknown message type");
}

static void RunFactoryTests()
{
    Assert(
        InputBackendFactory.ResolveMode([]) ==
            InputBackendMode.SendInput,
        "default backend");
    Assert(
        InputBackendFactory.ResolveMode(["--virtual-hid"]) ==
            InputBackendMode.VirtualHid,
        "virtual HID flag");
    Assert(
        InputBackendFactory.ResolveMode(
            ["--input-backend=sendinput"],
            "virtualhid") == InputBackendMode.SendInput,
        "command line overrides environment");
    Assert(
        InputBackendFactory.ResolveMode([], "vhf") ==
            InputBackendMode.VirtualHid,
        "environment backend");
    ExpectArgumentException(
        () => InputBackendFactory.ResolveMode(
            ["--virtual-hid", "--send-input"]),
        "conflicting backend flags");
    ExpectArgumentException(
        () => InputBackendFactory.ResolveMode(
            ["--input-backend=unknown"]),
        "unknown backend");
}

static void RunCoordinateTests()
{
    Assert(
        VirtualHidCoordinateMapper.TryMapNormalizedPoint(
            0,
            0,
            1920,
            1080,
            0,
            0,
            1920,
            1080,
            0,
            0,
            out var topLeftX,
            out var topLeftY) &&
        topLeftX == 0 &&
        topLeftY == 0,
        "single-monitor top-left coordinate");

    Assert(
        VirtualHidCoordinateMapper.TryMapNormalizedPoint(
            0,
            0,
            1920,
            1080,
            0,
            0,
            1920,
            1080,
            1,
            1,
            out var bottomRightX,
            out var bottomRightY) &&
        bottomRightX == VirtualHidCoordinateMapper.MaximumCoordinate &&
        bottomRightY == VirtualHidCoordinateMapper.MaximumCoordinate,
        "single-monitor bottom-right coordinate");

    Assert(
        VirtualHidCoordinateMapper.TryMapNormalizedPoint(
            -1920,
            0,
            1920,
            1080,
            -1920,
            0,
            3840,
            1080,
            1,
            0.5f,
            out var leftMonitorRight,
            out _) &&
        leftMonitorRight <
            VirtualHidCoordinateMapper.MaximumCoordinate / 2 + 2,
        "negative-origin monitor mapping");

    Assert(
        !VirtualHidCoordinateMapper.TryMapNormalizedPoint(
            0,
            0,
            0,
            1080,
            0,
            0,
            1920,
            1080,
            0.5f,
            0.5f,
            out _,
            out _),
        "zero-width screen rejection");
}

static void RunKeyboardStateTests()
{
    var state = new HidKeyboardState();
    for (byte usage = 4; usage < 10; usage++)
    {
        Assert(
            state.TrySetKey(
                new HidKey(usage, 0),
                true,
                out _),
            $"6KRO usage {usage}");
    }
    Assert(
        !state.TrySetKey(
            new HidKey(10, 0),
            true,
            out _),
        "seventh non-modifier rejection");
    Assert(
        state.TrySetKey(
            new HidKey(0, 0x01),
            true,
            out _) &&
        state.Modifiers == 0x01,
        "modifier outside 6KRO slots");
    state.Clear();
    Assert(
        state.Modifiers == 0 && state.Keys.Count == 0,
        "keyboard state clear");
}

static void RunInboundFileTransferTests()
{
    var root = Path.Combine(
        Path.GetTempPath(),
        $"comote-file-self-test-{Guid.NewGuid():N}");
    var incoming = Path.Combine(root, "incoming");
    var destination = Path.Combine(root, "destination");
    try
    {
        var transferId = Guid.NewGuid();
        var content = "verified file content"u8.ToArray();
        var hash = System.Security.Cryptography.SHA256.HashData(content);
        var name = System.Text.Encoding.UTF8.GetBytes("report.txt");
        var start = RemoteFileTransferProtocol.CreateStart(
            transferId,
            checked((uint)content.Length),
            name,
            hash);
        var chunk = RemoteFileTransferProtocol.CreateChunk(
            transferId,
            0,
            content);
        var end = RemoteFileTransferProtocol.CreateEnd(transferId);

        using (var receiver = new InboundFileTransferReceiver(
                   incoming,
                   destination))
        {
            Assert(
                receiver.Process(start).Status ==
                    InboundFileTransferStatus.Continue,
                "valid file start");
            Assert(
                receiver.Process(chunk).Status ==
                    InboundFileTransferStatus.Continue,
                "valid file chunk");
            var completed = receiver.Process(end);
            Assert(
                completed.Status == InboundFileTransferStatus.Completed &&
                completed.TransferId == transferId &&
                completed.SavedPath != null &&
                File.ReadAllBytes(completed.SavedPath)
                    .SequenceEqual(content),
                "verified file completion");
        }

        using (var receiver = new InboundFileTransferReceiver(
                   incoming,
                   destination))
        {
            var badHash = hash.ToArray();
            badHash[^1] ^= 0xFF;
            var badHashStart = RemoteFileTransferProtocol.CreateStart(
                transferId,
                checked((uint)content.Length),
                name,
                badHash);
            Assert(
                receiver.Process(badHashStart).Status ==
                    InboundFileTransferStatus.Continue,
                "hash mismatch start");
            _ = receiver.Process(chunk);
            var mismatch = receiver.Process(end);
            Assert(
                mismatch.Status ==
                    InboundFileTransferStatus.HashMismatch &&
                mismatch.TransferId == transferId,
                "hash mismatch rejection");
            Assert(
                Directory.GetFiles(destination).Length == 1,
                "hash mismatch did not publish a destination file");
        }

        using (var receiver = new InboundFileTransferReceiver(
                   incoming,
                   destination))
        {
            var emptyTransferId = Guid.NewGuid();
            var emptyStart = RemoteFileTransferProtocol.CreateStart(
                emptyTransferId,
                0,
                System.Text.Encoding.UTF8.GetBytes("empty.txt"),
                System.Security.Cryptography.SHA256.HashData([]));
            Assert(
                receiver.Process(emptyStart).Status ==
                    InboundFileTransferStatus.Continue,
                "empty file start");
            var emptyCompleted = receiver.Process(
                RemoteFileTransferProtocol.CreateEnd(emptyTransferId));
            Assert(
                emptyCompleted.Status ==
                    InboundFileTransferStatus.Completed &&
                emptyCompleted.TransferId == emptyTransferId &&
                emptyCompleted.SavedPath != null &&
                new FileInfo(emptyCompleted.SavedPath).Length == 0,
                "empty file completion");
        }

        using (var receiver = new InboundFileTransferReceiver(
                   incoming,
                   destination))
        {
            var unsafeStart = RemoteFileTransferProtocol.CreateStart(
                transferId,
                0,
                System.Text.Encoding.UTF8.GetBytes("../x"),
                System.Security.Cryptography.SHA256.HashData([]));
            Assert(
                receiver.Process(unsafeStart).Status ==
                    InboundFileTransferStatus.ProtocolViolation,
                "unsafe file name rejection");
        }

        using (var receiver = new InboundFileTransferReceiver(
                   incoming,
                   destination))
        {
            Assert(
                receiver.Process(start).Status ==
                    InboundFileTransferStatus.Continue,
                "offset test start");
            var wrongOffset = RemoteFileTransferProtocol.CreateChunk(
                transferId,
                1,
                content);
            var violation = receiver.Process(wrongOffset);
            Assert(
                violation.Status ==
                    InboundFileTransferStatus.ProtocolViolation &&
                violation.TransferId == transferId,
                "out-of-order file chunk rejection");
        }

        Assert(
            !Directory.Exists(incoming) ||
            Directory.GetFiles(incoming).Length == 0,
            "temporary transfer files were cleaned up");
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
static void WriteSingle(Span<byte> destination, float value)
{
    BinaryPrimitives.WriteInt32LittleEndian(
        destination,
        BitConverter.SingleToInt32Bits(value));
}

static void ExpectArgumentException(
    Action action,
    string description)
{
    try
    {
        action();
    }
    catch (ArgumentException)
    {
        return;
    }

    throw new InvalidOperationException(
        $"Self-test failed: {description} was accepted.");
}

static void Assert(bool condition, string description)
{
    if (!condition)
    {
        throw new InvalidOperationException(
            $"Self-test failed: {description}.");
    }
}

internal sealed class RecordingSystemExecutor : ISystemCommandExecutor
{
    public int Reboots { get; private set; }
    public int Shutdowns { get; private set; }
    public int Restarts { get; private set; }
    public int Cancels { get; private set; }
    public int LastDelay { get; private set; } = -1;

    public void Reboot(int delaySeconds)
    {
        Reboots++;
        LastDelay = delaySeconds;
    }

    public void Shutdown(int delaySeconds)
    {
        Shutdowns++;
        LastDelay = delaySeconds;
    }

    public void RestartHost() => Restarts++;

    public void CancelShutdown() => Cancels++;
}
