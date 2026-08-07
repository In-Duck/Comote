using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Comote.Input;

var tests = new (string Name, Func<Task> Run)[]
{
    ("access-key-format", TestAccessKeyFormatAsync),
    ("mutual-round-trip", TestMutualRoundTripAsync),
    ("wrong-key-rejected", TestWrongKeyRejectedAsync),
    ("replayed-frame-rejected", TestReplayedFrameRejectedAsync),
    ("tampered-frame-rejected", TestTamperedFrameRejectedAsync),
    ("dispose-unblocks-receive", TestDisposeUnblocksReceiveAsync),
};

var passed = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        passed++;
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"FAIL {test.Name}: {ex}");
    }
}

Console.WriteLine($"{passed}/{tests.Length} secure-channel tests passed.");
return passed == tests.Length ? 0 : 1;

static Task TestAccessKeyFormatAsync()
{
    var text = ComoteAccessKey.Generate();
    Require(text.StartsWith(ComoteAccessKey.Prefix, StringComparison.Ordinal));
    Require(ComoteAccessKey.TryParse(text, out var parsed));
    try
    {
        Require(parsed.Length == ComoteAccessKey.KeySize);
    }
    finally
    {
        CryptographicOperations.ZeroMemory(parsed);
    }

    Require(!ComoteAccessKey.TryParse(text.ToLowerInvariant(), out _));
    Require(!ComoteAccessKey.TryParse("password123", out _));
    Require(!ComoteAccessKey.TryParse(text + "A", out _));
    return Task.CompletedTask;
}

static async Task TestMutualRoundTripAsync()
{
    var keyText = ComoteAccessKey.Generate();
    Require(ComoteAccessKey.TryParse(keyText, out var key));
    try
    {
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(10));
        var (listener, port) = StartListener();
        using (listener)
        {
            var serverTask = Task.Run(async () =>
            {
                using var tcp =
                    await listener.AcceptTcpClientAsync(timeout.Token);
                await using var channel =
                    await SecurePskChannel.AuthenticateServerAsync(
                        tcp.GetStream(),
                        key,
                        "self-test",
                        timeout.Token);
                var request =
                    await channel.ReceiveAsync(timeout.Token);
                Require(Encoding.UTF8.GetString(request) == "hello");
                CryptographicOperations.ZeroMemory(request);
                await channel.SendAsync(
                    "world"u8.ToArray(),
                    timeout.Token);
            }, timeout.Token);

            using var client = new TcpClient();
            await client.ConnectAsync(
                IPAddress.Loopback,
                port,
                timeout.Token);
            await using var clientChannel =
                await SecurePskChannel.AuthenticateClientAsync(
                    client.GetStream(),
                    key,
                    "self-test",
                    timeout.Token);
            await clientChannel.SendAsync(
                "hello"u8.ToArray(),
                timeout.Token);
            var response =
                await clientChannel.ReceiveAsync(timeout.Token);
            try
            {
                Require(Encoding.UTF8.GetString(response) == "world");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(response);
            }
            await serverTask;
        }
    }
    finally
    {
        CryptographicOperations.ZeroMemory(key);
    }
}

static async Task TestWrongKeyRejectedAsync()
{
    var serverKey = RandomNumberGenerator.GetBytes(32);
    var clientKey = RandomNumberGenerator.GetBytes(32);
    try
    {
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(10));
        var (listener, port) = StartListener();
        using (listener)
        {
            var serverTask = Task.Run(async () =>
            {
                using var tcp =
                    await listener.AcceptTcpClientAsync(timeout.Token);
                await ExpectFailureAsync(async () =>
                {
                    await using var ignored =
                        await SecurePskChannel.AuthenticateServerAsync(
                            tcp.GetStream(),
                            serverKey,
                            "self-test",
                            timeout.Token);
                });
            }, timeout.Token);

            using var client = new TcpClient();
            await client.ConnectAsync(
                IPAddress.Loopback,
                port,
                timeout.Token);
            await ExpectFailureAsync(async () =>
            {
                await using var ignored =
                    await SecurePskChannel.AuthenticateClientAsync(
                        client.GetStream(),
                        clientKey,
                        "self-test",
                        timeout.Token);
            });
            await serverTask;
        }
    }
    finally
    {
        CryptographicOperations.ZeroMemory(serverKey);
        CryptographicOperations.ZeroMemory(clientKey);
    }
}

static async Task TestDisposeUnblocksReceiveAsync()
{
    var key = RandomNumberGenerator.GetBytes(32);
    try
    {
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(10));
        var (listener, port) = StartListener();
        using (listener)
        {
            var serverTask = Task.Run(async () =>
            {
                using var tcp =
                    await listener.AcceptTcpClientAsync(timeout.Token);
                await using var channel =
                    await SecurePskChannel.AuthenticateServerAsync(
                        tcp.GetStream(),
                        key,
                        "self-test",
                        timeout.Token);
                await ExpectFailureAsync(async () =>
                {
                    var message =
                        await channel.ReceiveAsync(timeout.Token);
                    CryptographicOperations.ZeroMemory(message);
                });
            }, timeout.Token);

            using var client = new TcpClient();
            await client.ConnectAsync(
                IPAddress.Loopback,
                port,
                timeout.Token);
            var clientChannel =
                await SecurePskChannel.AuthenticateClientAsync(
                    client.GetStream(),
                    key,
                    "self-test",
                    timeout.Token);
            var pendingReceive = clientChannel.ReceiveAsync(timeout.Token);
            await Task.Delay(50, timeout.Token);
            await clientChannel.DisposeAsync().AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5));
            await ExpectFailureAsync(async () =>
            {
                var message = await pendingReceive;
                CryptographicOperations.ZeroMemory(message);
            });
            await serverTask;
        }
    }
    finally
    {
        CryptographicOperations.ZeroMemory(key);
    }
}
static Task TestReplayedFrameRejectedAsync() =>
    TestModifiedFrameAsync(replay: true);

static Task TestTamperedFrameRejectedAsync() =>
    TestModifiedFrameAsync(replay: false);

static async Task TestModifiedFrameAsync(bool replay)
{
    var key = RandomNumberGenerator.GetBytes(32);
    try
    {
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(10));
        var (serverListener, serverPort) = StartListener();
        var (proxyListener, proxyPort) = StartListener();
        using (serverListener)
        using (proxyListener)
        {
            var serverTask = Task.Run(async () =>
            {
                using var tcp =
                    await serverListener.AcceptTcpClientAsync(timeout.Token);
                await using var channel =
                    await SecurePskChannel.AuthenticateServerAsync(
                        tcp.GetStream(),
                        key,
                        "self-test",
                        timeout.Token);
                if (replay)
                {
                    var first =
                        await channel.ReceiveAsync(timeout.Token);
                    CryptographicOperations.ZeroMemory(first);
                }
                await ExpectFailureAsync(async () =>
                {
                    var invalid =
                        await channel.ReceiveAsync(timeout.Token);
                    CryptographicOperations.ZeroMemory(invalid);
                });
            }, timeout.Token);

            var proxyTask = Task.Run(async () =>
            {
                using var inbound =
                    await proxyListener.AcceptTcpClientAsync(timeout.Token);
                using var outbound = new TcpClient();
                await outbound.ConnectAsync(
                    IPAddress.Loopback,
                    serverPort,
                    timeout.Token);
                var inboundStream = inbound.GetStream();
                var outboundStream = outbound.GetStream();
                var serverToClient = outboundStream.CopyToAsync(
                    inboundStream,
                    timeout.Token);

                var clientAuth = new byte[72];
                await ReadExactAsync(
                    inboundStream,
                    clientAuth,
                    timeout.Token);
                await outboundStream.WriteAsync(
                    clientAuth,
                    timeout.Token);
                CryptographicOperations.ZeroMemory(clientAuth);

                var header = new byte[12];
                await ReadExactAsync(
                    inboundStream,
                    header,
                    timeout.Token);
                var length = BinaryPrimitives.ReadUInt32BigEndian(header);
                Require(length is > 0 and <= 1024);
                var body = new byte[checked((int)length) + 16];
                await ReadExactAsync(
                    inboundStream,
                    body,
                    timeout.Token);
                if (!replay)
                {
                    body[0] ^= 0x80;
                }
                await outboundStream.WriteAsync(header, timeout.Token);
                await outboundStream.WriteAsync(body, timeout.Token);
                if (replay)
                {
                    await outboundStream.WriteAsync(header, timeout.Token);
                    await outboundStream.WriteAsync(body, timeout.Token);
                }
                await outboundStream.FlushAsync(timeout.Token);
                CryptographicOperations.ZeroMemory(header);
                CryptographicOperations.ZeroMemory(body);
                outbound.Dispose();
                try
                {
                    await serverToClient;
                }
                catch
                {
                }
            }, timeout.Token);

            using var client = new TcpClient();
            await client.ConnectAsync(
                IPAddress.Loopback,
                proxyPort,
                timeout.Token);
            await using var channel =
                await SecurePskChannel.AuthenticateClientAsync(
                    client.GetStream(),
                    key,
                    "self-test",
                    timeout.Token);
            await channel.SendAsync(
                "protected"u8.ToArray(),
                timeout.Token);
            await Task.WhenAll(serverTask, proxyTask);
        }
    }
    finally
    {
        CryptographicOperations.ZeroMemory(key);
    }
}

static (TcpListener Listener, int Port) StartListener()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var endpoint = (IPEndPoint)listener.LocalEndpoint;
    return (listener, endpoint.Port);
}

static async Task ReadExactAsync(
    Stream stream,
    Memory<byte> buffer,
    CancellationToken cancellationToken)
{
    var offset = 0;
    while (offset < buffer.Length)
    {
        var count = await stream.ReadAsync(
            buffer[offset..],
            cancellationToken);
        if (count == 0)
        {
            throw new EndOfStreamException();
        }
        offset += count;
    }
}

static async Task ExpectFailureAsync(Func<Task> action)
{
    try
    {
        await action();
    }
    catch (Exception ex)
        when (ex is IOException or
              UnauthorizedAccessException or
              EndOfStreamException or
              OperationCanceledException)
    {
        return;
    }

    throw new InvalidOperationException(
        "The operation unexpectedly succeeded.");
}

static void Require(bool condition)
{
    if (!condition)
    {
        throw new InvalidOperationException(
            "Self-test assertion failed.");
    }
}
