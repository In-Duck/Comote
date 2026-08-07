using Comote.Input;
using Viewer;

var root = Path.Combine(
    Path.GetTempPath(),
    $"Comote-RemoteFileSenderSelfTest-{Guid.NewGuid():N}");
Directory.CreateDirectory(root);
try
{
    await TestAcknowledgedTransferAsync(root);
    await TestStaleAcknowledgementAsync(root);
    await TestClosedChannelAsync(root);
    await TestStartedTransferFailureClosesChannelAsync(root);
    Console.WriteLine("All RemoteFileSender self-tests passed.");
}
finally
{
    Directory.Delete(root, recursive: true);
}

static async Task TestAcknowledgedTransferAsync(string root)
{
    var path = Path.Combine(root, "payload.bin");
    await File.WriteAllBytesAsync(
        path,
        Enumerable.Range(0, 40_000)
            .Select(value => checked((byte)(value % 251)))
            .ToArray());

    var sent = new List<byte[]>();
    RemoteFileSender? sender = null;
    sender = new RemoteFileSender(
        payload =>
        {
            var message = payload.ToArray();
            sent.Add(message);
            if (RemoteFileTransferProtocol.TryParseTransferOnly(
                    message,
                    RemoteFileTransferProtocol.EndMessage,
                    out var transferId))
            {
                _ = Task.Run(() => sender!.TryProcessResponse(
                    RemoteFileTransferProtocol.CreateAcknowledged(
                        transferId)));
            }
            return true;
        },
        () => (true, 0));
    using (sender)
    {
        sender.ProgressChanged += _ =>
            throw new InvalidOperationException(
                "A UI callback must not abort the transfer.");
        await sender.SendAsync(path);
    }

    Assert(
        sent.Count >= 5 &&
        sent[0][0] == RemoteFileTransferProtocol.StartMessage &&
        sent[^1][0] == RemoteFileTransferProtocol.EndMessage,
        "The sender did not emit a complete framed transfer.");
    Console.WriteLine("PASS: acknowledged transfer");
}

static async Task TestStaleAcknowledgementAsync(string root)
{
    var path = Path.Combine(root, "stale.bin");
    await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);

    RemoteFileSender? sender = null;
    sender = new RemoteFileSender(
        payload =>
        {
            var message = payload.ToArray();
            if (RemoteFileTransferProtocol.TryParseTransferOnly(
                    message,
                    RemoteFileTransferProtocol.EndMessage,
                    out var transferId))
            {
                Assert(
                    sender!.TryProcessResponse(
                        RemoteFileTransferProtocol.CreateAcknowledged(
                            Guid.NewGuid())),
                    "A well-formed stale response was not consumed.");
                _ = Task.Run(() => sender.TryProcessResponse(
                    RemoteFileTransferProtocol.CreateAcknowledged(
                        transferId)));
            }
            return true;
        },
        () => (true, 0));
    using (sender)
    {
        sender.ProgressChanged += _ =>
            throw new InvalidOperationException(
                "A UI callback must not abort the transfer.");
        await sender.SendAsync(path);
    }
    Console.WriteLine("PASS: stale acknowledgement ignored");
}

static async Task TestClosedChannelAsync(string root)
{
    var path = Path.Combine(root, "closed.bin");
    await File.WriteAllBytesAsync(path, new byte[20_000]);
    var abortCount = 0;
    using var sender = new RemoteFileSender(
        _ => false,
        () => (false, 0),
        _ => Interlocked.Increment(ref abortCount));
    await AssertThrowsAsync<IOException>(
        () => sender.SendAsync(path),
        "A closed channel did not fail the transfer.");
    Assert(
        abortCount == 1,
        "An uncertain start send did not close its channel.");
    Console.WriteLine("PASS: closed channel abort");
}

static async Task TestStartedTransferFailureClosesChannelAsync(
    string root)
{
    var path = Path.Combine(root, "mid-transfer.bin");
    await File.WriteAllBytesAsync(path, new byte[20_000]);
    var abortCount = 0;
    var sentStart = false;
    using var sender = new RemoteFileSender(
        payload =>
        {
            if (payload.Span[0] ==
                RemoteFileTransferProtocol.StartMessage)
            {
                sentStart = true;
                return true;
            }

            return false;
        },
        () => (true, 0),
        _ => Interlocked.Increment(ref abortCount));

    await AssertThrowsAsync<IOException>(
        () => sender.SendAsync(path),
        "A mid-transfer send failure was not surfaced.");
    Assert(sentStart, "The transfer never reached the started state.");
    Assert(
        abortCount == 1,
        "A started failed transfer did not close its poisoned channel.");
    Console.WriteLine("PASS: started transfer failure closes channel");
}

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static async Task AssertThrowsAsync<TException>(
    Func<Task> action,
    string message)
    where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException(message);
}
