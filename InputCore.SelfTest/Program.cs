using System.Security.Cryptography;
using Comote.Input;
using Viewer;

var tests = new (string Name, Action Run)[]
{
    ("remote authentication round-trip", TestRemoteAuthentication),
    ("remote server session binding", TestRemoteServerSession),
    ("remote client requires hello", TestRemoteClientRequiresHello),
    ("remote client token is single-assignment", TestRemoteClientTokenAssignment),
    ("remote session revocation", TestRemoteSessionRevocation),
    ("remote envelope replay rejection", TestRemoteEnvelope),
    ("remote file framing and transfer IDs", TestRemoteFileProtocol),
    ("keyboard state and 6KRO", TestKeyboardState),
    ("virtual-key and scan-code mapping", TestKeyMapping),
    ("mouse payload bounds", TestMousePayloads),
    ("clipboard consent framing", TestClipboardConsentProtocol),
    ("explicit FFmpeg override boundary", TestNativeLibraryOverride),
    ("remote command categorization", TestRemoteCommandCategorization),
    ("remote command deduplication", TestRemoteCommandDeduplication),
    ("remote command expiry window", TestRemoteCommandExpiry),
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS: {test.Name}");
    }
    catch (Exception ex)
    {
        failures.Add($"{test.Name}: {ex.Message}");
        Console.Error.WriteLine($"FAIL: {test.Name}: {ex}");
    }
}

if (failures.Count != 0)
{
    Environment.ExitCode = 1;
    return;
}

Console.WriteLine($"All {tests.Length} InputCore self-tests passed.");

static void TestRemoteCommandCategorization()
{
    Assert(
        RemoteCommandProtocol.IsAppTask("run") &&
        RemoteCommandProtocol.IsAppTask("terminate"),
        "run/terminate were not app tasks.");
    Assert(
        RemoteCommandProtocol.IsSystemCommand("reboot") &&
        RemoteCommandProtocol.IsSystemCommand("shutdown") &&
        RemoteCommandProtocol.IsSystemCommand("restart-host") &&
        RemoteCommandProtocol.IsSystemCommand("cancel-shutdown"),
        "A system command was not categorized as one.");
    Assert(
        !RemoteCommandProtocol.IsSystemCommand("run") &&
        !RemoteCommandProtocol.IsAppTask("reboot") &&
        !RemoteCommandProtocol.IsKnownAction("wipe-disk"),
        "Command categories leaked across each other.");

    Assert(
        !RemoteCommandProtocol.IsValidCommandId("") &&
        !RemoteCommandProtocol.IsValidCommandId("XYZ") &&
        !RemoteCommandProtocol.IsValidCommandId(
            RemoteCommandProtocol.NewCommandId().ToUpperInvariant()) &&
        RemoteCommandProtocol.IsValidCommandId(
            RemoteCommandProtocol.NewCommandId()),
        "Command ID validation was wrong.");
    Assert(
        RemoteCommandProtocol.IsValidDelaySeconds(0) &&
        RemoteCommandProtocol.IsValidDelaySeconds(600) &&
        !RemoteCommandProtocol.IsValidDelaySeconds(-1) &&
        !RemoteCommandProtocol.IsValidDelaySeconds(601),
        "Delay-seconds validation was wrong.");
}

static void TestRemoteCommandDeduplication()
{
    var deduplicator = new RemoteCommandDeduplicator(capacity: 4);
    var first = RemoteCommandProtocol.NewCommandId();
    Assert(deduplicator.TryAccept(first), "A fresh command was rejected.");
    Assert(!deduplicator.TryAccept(first), "A duplicate command was accepted.");
    Assert(
        !deduplicator.TryAccept("not-a-valid-id"),
        "A malformed command ID was accepted.");

    // Evicting the oldest beyond capacity must not resurrect it silently as
    // seen; it may be accepted again, but a still-tracked ID must not.
    var second = RemoteCommandProtocol.NewCommandId();
    Assert(deduplicator.TryAccept(second), "A second fresh command was rejected.");
    Assert(!deduplicator.TryAccept(second), "A duplicate was accepted after growth.");
}

static void TestRemoteCommandExpiry()
{
    var now = RemoteCommandProtocol.UnixTimeMilliseconds();
    Assert(
        RemoteCommandProtocol.IsWithinLifetime(now, now),
        "A just-issued command was expired.");
    Assert(
        RemoteCommandProtocol.IsWithinLifetime(now - 30_000, now),
        "A 30s-old command was expired.");
    Assert(
        !RemoteCommandProtocol.IsWithinLifetime(now - 120_000, now),
        "A 2-minute-old command was accepted.");
    Assert(
        RemoteCommandProtocol.IsWithinLifetime(now + 30_000, now),
        "A command from a 30s-fast Manager was rejected (clock skew).");
    Assert(
        !RemoteCommandProtocol.IsWithinLifetime(now + 120_000, now),
        "A far-future command was accepted.");
}

static void TestClipboardConsentProtocol()
{
    var enabledMessage = RemoteClipboardConsentProtocol.Create(true);
    Assert(
        RemoteClipboardConsentProtocol.TryParse(
            enabledMessage,
            out var enabled) &&
        enabled,
        "Enabled clipboard consent did not round-trip.");

    var disabledMessage = RemoteClipboardConsentProtocol.Create(false);
    Assert(
        RemoteClipboardConsentProtocol.TryParse(
            disabledMessage,
            out enabled) &&
        !enabled,
        "Disabled clipboard consent did not round-trip.");

    Assert(
        !RemoteClipboardConsentProtocol.TryParse(
            [RemoteClipboardConsentProtocol.MessageType, 2],
            out _),
        "An invalid clipboard consent value was accepted.");
    Assert(
        !RemoteClipboardConsentProtocol.TryParse(
            [RemoteClipboardConsentProtocol.MessageType],
            out _),
        "A truncated clipboard consent message was accepted.");
}
static void TestRemoteAuthentication()
{
    var token = Enumerable.Range(
        0,
        RemoteControlProtocol.TokenSize).Select(
            value => checked((byte)value)).ToArray();
    var sessionId = Guid.NewGuid();
    var encoded = RemoteControlProtocol.CreateAuthHello(
        RemoteChannelKind.Input,
        token,
        sessionId);
    Assert(
        RemoteControlProtocol.TryParseAuthHello(encoded, out var parsed),
        "Auth hello did not parse.");
    Assert(parsed.Channel == RemoteChannelKind.Input, "Channel changed.");
    Assert(parsed.ClientSessionId == sessionId, "Session ID changed.");
    Assert(parsed.Token.SequenceEqual(token), "Token changed.");

    encoded[^1] ^= 0x5A;
    Assert(
        RemoteControlProtocol.TryParseAuthHello(encoded, out var changed) &&
        changed.ClientSessionId != sessionId,
        "A changed session identifier was not observed.");

    var accepted = RemoteControlProtocol.CreateAuthAccepted(
        RemoteChannelKind.Input,
        RemoteInputMode.VirtualHid,
        0x7FF);
    Assert(
        RemoteControlProtocol.TryParseAuthAccepted(
            accepted,
            out var parsedAccepted),
        "Auth acceptance did not parse.");
    Assert(
        parsedAccepted.InputMode == RemoteInputMode.VirtualHid &&
        parsedAccepted.Capabilities == 0x7FF,
        "Auth acceptance fields changed.");
}

static void TestRemoteServerSession()
{
    var token = RandomNumberGenerator.GetBytes(
        RemoteControlProtocol.TokenSize);
    using var session = new RemoteControlSession(token);
    var input = new RemoteChannelServerState(
        session,
        RemoteChannelKind.Input,
        RemoteInputMode.VirtualHid,
        0x400);
    var file = new RemoteChannelServerState(
        session,
        RemoteChannelKind.File,
        RemoteInputMode.VirtualHid,
        0x400);
    var clientSessionId = Guid.NewGuid();

    var result = input.ProcessClientMessage(
        RemoteControlProtocol.CreateAuthHello(
            RemoteChannelKind.Input,
            token,
            clientSessionId),
        out var response,
        out _);
    Assert(
        result == RemoteServerReceiveResult.Authenticated &&
        RemoteControlProtocol.TryParseAuthAccepted(response, out _),
        "Input-channel authentication failed.");

    result = file.ProcessClientMessage(
        RemoteControlProtocol.CreateAuthHello(
            RemoteChannelKind.File,
            token,
            clientSessionId),
        out response,
        out _);
    Assert(
        result == RemoteServerReceiveResult.Authenticated,
        "File channel did not bind to the same client session.");

    using var otherSession = new RemoteControlSession(token);
    var mismatched = new RemoteChannelServerState(
        otherSession,
        RemoteChannelKind.Input,
        RemoteInputMode.VirtualHid,
        0);
    result = mismatched.ProcessClientMessage(
        RemoteControlProtocol.CreateAuthHello(
            RemoteChannelKind.File,
            token,
            Guid.NewGuid()),
        out _,
        out _);
    Assert(
        result == RemoteServerReceiveResult.Rejected,
        "A channel-kind mismatch was accepted.");
}
static void TestRemoteClientRequiresHello()
{
    var token = RandomNumberGenerator.GetBytes(
        RemoteControlProtocol.TokenSize);
    using var client = new RemoteChannelClientState(
        RemoteChannelKind.Input);
    client.Reset(Guid.NewGuid());
    client.SetToken(token);

    var result = client.ProcessServerMessage(
        RemoteControlProtocol.CreateAuthAccepted(
            RemoteChannelKind.Input,
            RemoteInputMode.VirtualHid,
            0),
        out _,
        out _);
    Assert(
        result == RemoteChannelReceiveResult.ProtocolViolation &&
        !client.IsAuthenticated,
        "An AuthAccepted message was accepted before the hello was sent.");
}

static void TestRemoteClientTokenAssignment()
{
    var token = RandomNumberGenerator.GetBytes(
        RemoteControlProtocol.TokenSize);
    using var client = new RemoteChannelClientState(
        RemoteChannelKind.Input);
    client.Reset(Guid.NewGuid());
    client.SetToken(token);
    AssertThrows<InvalidOperationException>(
        () => client.SetToken(token),
        "A second token was accepted for the same connection.");
}

static void TestRemoteSessionRevocation()
{
    var token = RandomNumberGenerator.GetBytes(
        RemoteControlProtocol.TokenSize);
    using var session = new RemoteControlSession(token);
    using var state = new RemoteChannelServerState(
        session,
        RemoteChannelKind.Input,
        RemoteInputMode.VirtualHid,
        0);

    var result = state.ProcessClientMessage(
        RemoteControlProtocol.CreateAuthHello(
            RemoteChannelKind.Input,
            token,
            Guid.NewGuid()),
        out _,
        out _);
    Assert(
        result == RemoteServerReceiveResult.Authenticated,
        "The setup authentication failed.");

    session.Revoke();
    Assert(
        !session.IsActive && !state.IsAuthenticated,
        "A revoked session remained active.");

    result = state.ProcessClientMessage(
        RemoteControlProtocol.WrapPayload(1, [0x21]),
        out _,
        out _);
    Assert(
        result == RemoteServerReceiveResult.ProtocolViolation,
        "A revoked session accepted a payload.");
    AssertThrows<InvalidOperationException>(
        () => state.WrapPayload([0x22]),
        "A revoked session wrapped a payload.");
}
static void TestRemoteEnvelope()
{
    byte[] payload = [0x10, 0x41, 0x00];
    var message = RemoteControlProtocol.WrapPayload(1, payload);
    Assert(
        RemoteControlProtocol.TryUnwrapPayload(
            message,
            1,
            out var decoded) &&
        decoded.SequenceEqual(payload),
        "Envelope round-trip failed.");
    Assert(
        !RemoteControlProtocol.TryUnwrapPayload(
            message,
            2,
            out _),
        "A replay/out-of-order envelope was accepted.");

    var truncated = message[..^1];
    Assert(
        !RemoteControlProtocol.TryUnwrapPayload(
            truncated,
            1,
            out _),
        "A truncated envelope was accepted.");
}

static void TestRemoteFileProtocol()
{
    var transferId = Guid.NewGuid();
    var hash = SHA256.HashData([1, 2, 3, 4]);
    var start = RemoteFileTransferProtocol.CreateStart(
        transferId,
        4,
        "test.bin"u8,
        hash);
    Assert(
        RemoteFileTransferProtocol.TryParseStart(
            start,
            out var parsedId,
            out var parsedSize,
            out var parsedName,
            out var parsedHash) &&
        parsedId == transferId &&
        parsedSize == 4 &&
        parsedName.SequenceEqual("test.bin"u8.ToArray()) &&
        parsedHash.SequenceEqual(hash),
        "The file-start frame did not round-trip.");

    var chunk = RemoteFileTransferProtocol.CreateChunk(
        transferId,
        0,
        [1, 2, 3, 4]);
    Assert(
        RemoteFileTransferProtocol.TryParseChunk(
            chunk,
            out parsedId,
            out var offset,
            out var data) &&
        parsedId == transferId &&
        offset == 0 &&
        data.SequenceEqual(new byte[] { 1, 2, 3, 4 }),
        "The file-chunk frame did not round-trip.");

    Assert(
        !RemoteFileTransferProtocol.TryParseChunk(
            [.. chunk, 0],
            out _,
            out _,
            out _),
        "A file frame with trailing bytes was accepted.");
    Assert(
        RemoteFileTransferProtocol.TryParseTransferOnly(
            RemoteFileTransferProtocol.CreateAcknowledged(transferId),
            RemoteFileTransferProtocol.AcknowledgedMessage,
            out parsedId) &&
        parsedId == transferId,
        "The acknowledgement lost its transfer identifier.");
    AssertThrows<ArgumentOutOfRangeException>(
        () => RemoteFileTransferProtocol.CreateEnd(Guid.Empty),
        "An empty transfer identifier was accepted.");
}
static void TestKeyboardState()
{
    var state = new HidKeyboardState();
    Assert(
        state.TrySetKey(new HidKey(0, 0x01), true, out _),
        "Left control was rejected.");
    Assert(state.Modifiers == 0x01, "Left control was not retained.");

    for (byte usage = 4; usage < 10; usage++)
    {
        Assert(
            state.TrySetKey(new HidKey(usage, 0), true, out _),
            "A valid 6KRO key was rejected.");
    }
    Assert(
        state.TrySetKey(new HidKey(4, 0), true, out _),
        "A repeated key-down was not idempotent.");

    var before = state.Capture();
    Assert(
        !state.TrySetKey(new HidKey(10, 0), true, out _),
        "The seventh non-modifier key was accepted.");
    Assert(
        state.GetOrderedKeys().SequenceEqual(before.Keys),
        "6KRO rejection corrupted the existing state.");

    state.Clear();
    Assert(
        state.Modifiers == 0 &&
        state.GetOrderedKeys().All(value => value == 0),
        "Release-all state was not neutral.");
}

static void TestKeyMapping()
{
    Assert(
        VirtualKeyToHidUsage.TryMap(
            0x41,
            0x1E,
            0,
            out var a) &&
        a.Usage == 0x04,
        "A key mapping is wrong.");
    Assert(
        VirtualKeyToHidUsage.TryMap(
            0x0D,
            0x1C,
            0x01,
            out var numpadEnter) &&
        numpadEnter.Usage == 0x58,
        "Extended Enter mapping is wrong.");
    Assert(
        VirtualKeyToHidUsage.TryMap(
            0xA3,
            0x1D,
            0x01,
            out var rightControl) &&
        rightControl.ModifierMask == 0x10,
        "Right-control mapping is wrong.");
    Assert(
        VirtualKeyToHidUsage.IsInjected(0x10) &&
        VirtualKeyToHidUsage.IsInjected(0x02) &&
        !VirtualKeyToHidUsage.IsInjected(0x01),
        "Injected-key filtering is wrong.");
}

static void TestMousePayloads()
{
    var relative = BrokerProtocol.CreateRelativeMousePayload(
        0x1F,
        32767,
        -32767,
        127,
        -127);
    Assert(
        relative.Length == BrokerProtocol.RelativeMousePayloadSize,
        "Relative mouse payload size is wrong.");

    var absolute = BrokerProtocol.CreateAbsoluteMousePayload(
        0x1F,
        32767,
        0,
        127,
        -127);
    Assert(
        absolute.Length == BrokerProtocol.AbsoluteMousePayloadSize,
        "Absolute mouse payload size is wrong.");
    AssertThrows<ArgumentOutOfRangeException>(
        () => BrokerProtocol.CreateAbsoluteMousePayload(
            0,
            32768,
            0,
            0,
            0),
        "An out-of-range absolute coordinate was accepted.");
}

static void TestNativeLibraryOverride()
{
    const string variable = "COMOTE_FFMPEG_SELFTEST_DIR";
    var original = Environment.GetEnvironmentVariable(variable);
    var directory = Path.Combine(
        Path.GetTempPath(),
        "Comote-NativeOverride-SelfTest-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        File.WriteAllBytes(Path.Combine(directory, "one.dll"), [1]);
        File.WriteAllBytes(Path.Combine(directory, "two.dll"), [2]);
        Environment.SetEnvironmentVariable(variable, directory);

        AssertThrows<InvalidOperationException>(
            () => EmbeddedNativeLibraryExtractor
                .ExtractVerifiedOrUseExplicitOverride(
                    typeof(EmbeddedNativeLibraryExtractor).Assembly,
                    "UnusedCache",
                    "unused.",
                    ["one.dll", "two.dll"],
                    variable,
                    allowExplicitOverride: false),
            "An override was accepted without the explicit opt-in.");

        var resolved =
            EmbeddedNativeLibraryExtractor
                .ExtractVerifiedOrUseExplicitOverride(
                    typeof(EmbeddedNativeLibraryExtractor).Assembly,
                    "UnusedCache",
                    "unused.",
                    ["one.dll", "two.dll"],
                    variable,
                    allowExplicitOverride: true);
        Assert(
            string.Equals(
                resolved,
                Path.GetFullPath(directory),
                StringComparison.OrdinalIgnoreCase),
            "An explicit local override was not selected.");

        Environment.SetEnvironmentVariable(variable, "relative");
        AssertThrows<InvalidOperationException>(
            () => EmbeddedNativeLibraryExtractor
                .ExtractVerifiedOrUseExplicitOverride(
                    typeof(EmbeddedNativeLibraryExtractor).Assembly,
                    "UnusedCache",
                    "unused.",
                    ["one.dll"],
                    variable,
                    allowExplicitOverride: true),
            "A relative native-library override was accepted.");
    }
    finally
    {
        Environment.SetEnvironmentVariable(variable, original);
        Directory.Delete(directory, recursive: true);
    }
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertThrows<TException>(
    Action action,
    string message)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }
    throw new InvalidOperationException(message);
}
