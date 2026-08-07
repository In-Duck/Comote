using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Comote.Input;
using Host;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Viewer;

var tests = new (string Name, Func<Task> Run)[]
{
    ("routing-id-stable-and-bounded", TestRoutingIdAsync),
    ("encrypted-hub-round-trip", TestRoundTripAsync),
    ("remote-tasks-default-deny", TestRemoteTasksDefaultDenyAsync),
    ("remote-tasks-explicit-opt-in", TestRemoteTasksOptInAsync),
    ("distinct-device-keys-are-isolated", TestCrossDeviceIsolationAsync),
    ("wrong-key-never-registers", TestWrongKeyAsync),
    ("spoofed-routing-id-never-registers", TestSpoofedRoutingIdAsync),
    ("spoofed-registration-id-never-registers", TestSpoofedRegistrationIdAsync),
    ("duplicate-cannot-take-over-live-session", TestDuplicateTakeoverAsync),
    ("pending-authentication-is-per-ip-bounded", TestPendingLimitAsync),
    ("authentication-attempts-are-rate-limited", TestRateLimitAsync),
    ("authenticated-idle-session-expires", TestIdleExpiryAsync),
    ("system-commands-default-deny", TestSystemCommandsDefaultDenyAsync),
    ("system-commands-explicit-opt-in", TestSystemCommandsOptInAsync),
    ("deregister-blocks-reregistration", TestDeregisterBlocksReregistrationAsync),
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

Console.WriteLine($"{passed}/{tests.Length} hardened hub transport tests passed.");
return passed == tests.Length ? 0 : 1;

static Task TestRoutingIdAsync()
{
    var key = ComoteAccessKey.Generate();
    Require(ComoteAccessKey.TryParse(key, out var parsed));
    try
    {
        var first = ManagerHubProtocol.DeriveRoutingId(parsed);
        var second = ManagerHubProtocol.DeriveRoutingId(key);
        Require(first == second);
        Require(first.Length == ManagerHubProtocol.RoutingIdLength);
        Require(ManagerHubProtocol.IsCanonicalRoutingId(first));
        Require(!ManagerHubProtocol.IsCanonicalRoutingId(
            first.ToUpperInvariant()));
        Require(ManagerHubProtocol.CreateSecureContext(first).EndsWith(
            first,
            StringComparison.Ordinal));
    }
    finally
    {
        CryptographicOperations.ZeroMemory(parsed);
    }

    return Task.CompletedTask;
}

static async Task TestRoundTripAsync()
{
    await WithConnectionAsync(
        allowRemoteTasks: false,
        async (server, client, info, cancellationToken) =>
        {
            Require(!info.AllowsRemoteTasks);
            Require(info.Id == client.RoutingId);
            var serverSignal =
                new TaskCompletionSource<JToken>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            var clientSignal =
                new TaskCompletionSource<JToken>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            var thumbnail =
                new TaskCompletionSource<byte[]>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

            server.SignalReceived += (clientId, signal) =>
            {
                Require(clientId == info.Id);
                serverSignal.TrySetResult(JToken.FromObject(signal));
                return Task.CompletedTask;
            };
            client.SignalReceived += signal =>
            {
                clientSignal.TrySetResult(JToken.FromObject(signal));
                return Task.CompletedTask;
            };
            server.ThumbnailReceived += (clientId, jpeg) =>
            {
                Require(clientId == info.Id);
                thumbnail.TrySetResult(jpeg);
            };

            await client.SendSignalAsync(new { value = "from-client" });
            var receivedByServer =
                await serverSignal.Task.WaitAsync(cancellationToken);
            Require(receivedByServer.Value<string>("value") ==
                    "from-client");

            await server.SendSignalAsync(
                info.Id,
                new { value = "from-manager" });
            var receivedByClient =
                await clientSignal.Task.WaitAsync(cancellationToken);
            Require(receivedByClient.Value<string>("value") ==
                    "from-manager");

            var expectedThumbnail =
                Enumerable.Range(0, 4096)
                    .Select(index => (byte)(index % 251))
                    .ToArray();
            await client.SendThumbnailAsync(expectedThumbnail);
            var receivedThumbnail =
                await thumbnail.Task.WaitAsync(cancellationToken);
            Require(receivedThumbnail.SequenceEqual(expectedThumbnail));
        });
}

static async Task TestRemoteTasksDefaultDenyAsync()
{
    await WithConnectionAsync(
        allowRemoteTasks: false,
        async (server, _, info, _) =>
        {
            try
            {
                await server.SendCommandAsync(
                    info.Id,
                    "run",
                    "test",
                    "desktop",
                    "Tool.exe");
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }

            throw new InvalidOperationException(
                "A default-deny Client accepted a remote task.");
        });
}

static async Task TestRemoteTasksOptInAsync()
{
    await WithConnectionAsync(
        allowRemoteTasks: true,
        async (server, client, info, cancellationToken) =>
        {
            Require(info.AllowsRemoteTasks);
            var command =
                new TaskCompletionSource<JObject>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            client.CommandReceived += value =>
            {
                command.TrySetResult(value);
                return Task.CompletedTask;
            };
            await server.SendCommandAsync(
                info.Id,
                "terminate",
                "test",
                "",
                "notepad.exe");
            var received =
                await command.Task.WaitAsync(cancellationToken);
            Require(received.Value<string>("action") == "terminate");
            Require(received.Value<string>("value") == "notepad.exe");
        });
}

static async Task TestSystemCommandsDefaultDenyAsync()
{
    // Opting into remote app tasks must not grant system commands.
    await WithConnectionAsync(
        allowRemoteTasks: true,
        allowSystemCommands: false,
        test: async (server, _, info, _) =>
        {
            Require(!info.AllowsSystemCommands);
            try
            {
                await server.SendSystemCommandAsync(
                    info.Id,
                    "reboot",
                    "reboot",
                    RemoteCommandProtocol.NewCommandId(),
                    RemoteCommandProtocol.UnixTimeMilliseconds(),
                    30);
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }

            throw new InvalidOperationException(
                "A remote-tasks-only Client accepted a system command.");
        });
}

static async Task TestSystemCommandsOptInAsync()
{
    await WithConnectionAsync(
        allowRemoteTasks: false,
        allowSystemCommands: true,
        test: async (server, client, info, cancellationToken) =>
        {
            Require(info.AllowsSystemCommands);
            var command =
                new TaskCompletionSource<JObject>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            client.CommandReceived += value =>
            {
                command.TrySetResult(value);
                return Task.CompletedTask;
            };
            var commandId = RemoteCommandProtocol.NewCommandId();
            await server.SendSystemCommandAsync(
                info.Id,
                "reboot",
                "reboot",
                commandId,
                RemoteCommandProtocol.UnixTimeMilliseconds(),
                30);
            var received =
                await command.Task.WaitAsync(cancellationToken);
            Require(received.Value<string>("action") == "reboot");
            Require(received.Value<string>("commandId") == commandId);
            Require(received.Value<long>("delaySeconds") == 30);
        });
}

static async Task TestDeregisterBlocksReregistrationAsync()
{
    var port = GetFreePort();
    var credential = Credential("managed-deregister-device");
    using var timeout = new CancellationTokenSource(
        TimeSpan.FromSeconds(10));
    using var server = new ManagerHubServer(port, [credential]);
    var online =
        new TaskCompletionSource<HubClientInfo>(
            TaskCreationOptions.RunContinuationsAsynchronously);
    server.ClientOnline += info => online.TrySetResult(info);
    var serverTask = server.RunAsync(timeout.Token);
    try
    {
        using (var client = new ManagerHubClient(
                   IPAddress.Loopback.ToString(),
                   port,
                   credential.AccessKey,
                   "before-removal"))
        {
            var clientTask = client.RunAsync(timeout.Token);
            await online.Task.WaitAsync(timeout.Token);
            Require(server.RemoveDevice(credential.RoutingId));
        }

        // A fresh connection with the same key must no longer register.
        try
        {
            await using var raw = await RawHubConnection.ConnectAsync(
                port,
                credential.AccessKey,
                "after-removal",
                cancellationToken: timeout.Token);
            throw new InvalidOperationException(
                "A deregistered device re-registered.");
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException or
            IOException or
            EndOfStreamException)
        {
        }
    }
    finally
    {
        timeout.Cancel();
        await IgnoreCancellationAsync(serverTask);
    }
}

static async Task TestCrossDeviceIsolationAsync()
{
    var port = GetFreePort();
    var first = Credential("managed-device-a");
    var second = Credential("managed-device-b");
    using var timeout = new CancellationTokenSource(
        TimeSpan.FromSeconds(10));
    using var server = new ManagerHubServer(port, [first, second]);
    var serverTask = server.RunAsync(timeout.Token);
    await using var firstClient = await RawHubConnection.ConnectAsync(
        port,
        first.AccessKey,
        "client-claims-an-untrusted-name",
        cancellationToken: timeout.Token);
    await using var secondClient = await RawHubConnection.ConnectAsync(
        port,
        second.AccessKey,
        "another-untrusted-name",
        cancellationToken: timeout.Token);
    try
    {
        await WaitUntilAsync(
            () => server.Clients.Count == 2,
            timeout.Token);
        var clients = server.Clients.ToDictionary(info => info.Id);
        Require(clients[first.RoutingId].Name == "managed-device-a");
        Require(clients[second.RoutingId].Name == "managed-device-b");

        await server.SendSignalAsync(
            first.RoutingId,
            new { marker = "only-a" });
        await server.SendSignalAsync(
            second.RoutingId,
            new { marker = "only-b" });
        var firstMessage = await firstClient.ReceiveAsync(timeout.Token);
        var secondMessage = await secondClient.ReceiveAsync(timeout.Token);
        Require(
            firstMessage["payload"]?.Value<string>("marker") == "only-a");
        Require(
            secondMessage["payload"]?.Value<string>("marker") == "only-b");

        var receivedIdentity =
            new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        server.SignalReceived += (routingId, _) =>
        {
            receivedIdentity.TrySetResult(routingId);
            return Task.CompletedTask;
        };
        await firstClient.SendAsync(
            new JObject
            {
                ["type"] = "signal",
                ["payload"] = new JObject { ["source"] = "a" },
            },
            timeout.Token);
        Require(await receivedIdentity.Task.WaitAsync(timeout.Token) ==
                first.RoutingId);
    }
    finally
    {
        timeout.Cancel();
        await IgnoreCancellationAsync(serverTask);
    }
}

static async Task TestWrongKeyAsync()
{
    var credential = Credential("known-device");
    var wrongKey = ComoteAccessKey.Generate();
    await RequireRegistrationRejectedAsync(
        credential,
        wrongKey,
        ManagerHubProtocol.DeriveRoutingId(wrongKey),
        ManagerHubProtocol.DeriveRoutingId(wrongKey));
}

static async Task TestSpoofedRoutingIdAsync()
{
    var credential = Credential("known-device");
    var wrongKey = ComoteAccessKey.Generate();
    await RequireRegistrationRejectedAsync(
        credential,
        wrongKey,
        credential.RoutingId,
        credential.RoutingId);
}

static async Task TestSpoofedRegistrationIdAsync()
{
    var credential = Credential("known-device");
    var otherKey = ComoteAccessKey.Generate();
    await RequireRegistrationRejectedAsync(
        credential,
        credential.AccessKey,
        credential.RoutingId,
        ManagerHubProtocol.DeriveRoutingId(otherKey));
}

static async Task RequireRegistrationRejectedAsync(
    ManagerHubDeviceCredential credential,
    string connectionKey,
    string prefaceRoutingId,
    string registrationRoutingId)
{
    var port = GetFreePort();
    using var timeout = new CancellationTokenSource(
        TimeSpan.FromSeconds(5));
    using var server = new ManagerHubServer(port, [credential]);
    var online = false;
    server.ClientOnline += _ => online = true;
    var serverTask = server.RunAsync(timeout.Token);
    try
    {
        var rejected = false;
        try
        {
            await using var ignored = await RawHubConnection.ConnectAsync(
                port,
                connectionKey,
                "spoof-attempt",
                prefaceRoutingId,
                registrationRoutingId,
                timeout.Token);
        }
        catch (Exception ex) when (
            ex is IOException or
                UnauthorizedAccessException or
                OperationCanceledException)
        {
            rejected = true;
        }

        Require(rejected);
        await Task.Delay(100, timeout.Token);
        Require(!online);
        Require(server.Clients.Count == 0);
    }
    finally
    {
        timeout.Cancel();
        await IgnoreCancellationAsync(serverTask);
    }
}

static async Task TestDuplicateTakeoverAsync()
{
    var port = GetFreePort();
    var credential = Credential("single-live-device");
    using var timeout = new CancellationTokenSource(
        TimeSpan.FromSeconds(8));
    using var server = new ManagerHubServer(port, [credential]);
    var onlineCount = 0;
    server.ClientOnline += _ => Interlocked.Increment(ref onlineCount);
    var serverTask = server.RunAsync(timeout.Token);
    await using var original = await RawHubConnection.ConnectAsync(
        port,
        credential.AccessKey,
        "original",
        cancellationToken: timeout.Token);
    try
    {
        var wrongKey = ComoteAccessKey.Generate();
        var spoofRejected = false;
        try
        {
            await using var ignored = await RawHubConnection.ConnectAsync(
                port,
                wrongKey,
                "unauthenticated-takeover",
                credential.RoutingId,
                credential.RoutingId,
                timeout.Token);
        }
        catch (Exception ex) when (
            ex is IOException or
                UnauthorizedAccessException)
        {
            spoofRejected = true;
        }
        Require(spoofRejected);

        var duplicateRejected = false;
        try
        {
            await using var ignored = await RawHubConnection.ConnectAsync(
                port,
                credential.AccessKey,
                "duplicate",
                cancellationToken: timeout.Token);
        }
        catch (Exception ex) when (
            ex is IOException or
                UnauthorizedAccessException)
        {
            duplicateRejected = true;
        }
        Require(duplicateRejected);
        Require(Volatile.Read(ref onlineCount) == 1);
        Require(server.Clients.Count == 1);

        await server.SendSignalAsync(
            credential.RoutingId,
            new { marker = "original-still-current" });
        var message = await original.ReceiveAsync(timeout.Token);
        Require(
            message["payload"]?.Value<string>("marker") ==
            "original-still-current");
    }
    finally
    {
        timeout.Cancel();
        await IgnoreCancellationAsync(serverTask);
    }
}

static async Task TestPendingLimitAsync()
{
    var port = GetFreePort();
    var credential = Credential("pending-limit-device");
    var options = new ManagerHubServerOptions
    {
        MaximumPendingAuthentications = 2,
        MaximumPendingAuthenticationsPerIp = 1,
        MaximumAuthenticationAttemptsPerWindow = 10,
        AuthenticationTimeout = TimeSpan.FromSeconds(3),
    };
    using var timeout = new CancellationTokenSource(
        TimeSpan.FromSeconds(6));
    using var server = new ManagerHubServer(
        port,
        [credential],
        options);
    var serverTask = server.RunAsync(timeout.Token);
    using var first = new TcpClient();
    using var second = new TcpClient();
    try
    {
        await first.ConnectAsync(
            IPAddress.Loopback,
            port,
            timeout.Token);
        await WaitUntilAsync(
            () => server.PendingAuthenticationCount == 1,
            timeout.Token);
        await second.ConnectAsync(
            IPAddress.Loopback,
            port,
            timeout.Token);
        var buffer = new byte[1];
        var read = await second.GetStream()
            .ReadAsync(buffer, timeout.Token)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));
        Require(read == 0);
        Require(server.PendingAuthenticationCount == 1);
        first.Dispose();
        await WaitUntilAsync(
            () => server.PendingAuthenticationCount == 0,
            timeout.Token);
    }
    finally
    {
        timeout.Cancel();
        await IgnoreCancellationAsync(serverTask);
    }
}

static async Task TestRateLimitAsync()
{
    var port = GetFreePort();
    var credential = Credential("rate-limit-device");
    var options = new ManagerHubServerOptions
    {
        MaximumAuthenticationAttemptsPerWindow = 1,
        AuthenticationAttemptWindow = TimeSpan.FromSeconds(10),
        AuthenticationTimeout = TimeSpan.FromSeconds(1),
    };
    using var timeout = new CancellationTokenSource(
        TimeSpan.FromSeconds(6));
    using var server = new ManagerHubServer(port, [credential], options);
    var serverTask = server.RunAsync(timeout.Token);
    try
    {
        using (var first = new TcpClient())
        {
            await first.ConnectAsync(
                IPAddress.Loopback,
                port,
                timeout.Token);
            await first.GetStream().WriteAsync(
                new byte[ManagerHubProtocol.RoutingPrefaceLength],
                timeout.Token);
        }
        await WaitUntilAsync(
            () => server.PendingAuthenticationCount == 0,
            timeout.Token);

        var rejected = false;
        try
        {
            await using var ignored = await RawHubConnection.ConnectAsync(
                port,
                credential.AccessKey,
                "rate-limited",
                cancellationToken: timeout.Token);
        }
        catch (Exception ex) when (
            ex is IOException or
                UnauthorizedAccessException)
        {
            rejected = true;
        }
        Require(rejected);
        Require(server.Clients.Count == 0);
    }
    finally
    {
        timeout.Cancel();
        await IgnoreCancellationAsync(serverTask);
    }
}

static async Task TestIdleExpiryAsync()
{
    var port = GetFreePort();
    var credential = Credential("idle-device");
    var options = new ManagerHubServerOptions
    {
        AuthenticatedIdleTimeout = TimeSpan.FromMilliseconds(350),
    };
    using var timeout = new CancellationTokenSource(
        TimeSpan.FromSeconds(5));
    using var server = new ManagerHubServer(port, [credential], options);
    var offline =
        new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
    server.ClientOffline += id => offline.TrySetResult(id);
    var serverTask = server.RunAsync(timeout.Token);
    await using var client = await RawHubConnection.ConnectAsync(
        port,
        credential.AccessKey,
        "idle-client",
        cancellationToken: timeout.Token);
    try
    {
        Require(await offline.Task.WaitAsync(timeout.Token) ==
                credential.RoutingId);
        Require(server.Clients.Count == 0);
    }
    finally
    {
        timeout.Cancel();
        await IgnoreCancellationAsync(serverTask);
    }
}

static async Task WithConnectionAsync(
    bool allowRemoteTasks,
    Func<
        ManagerHubServer,
        ManagerHubClient,
        HubClientInfo,
        CancellationToken,
        Task> test,
    bool allowSystemCommands = false)
{
    var port = GetFreePort();
    var credential = Credential("managed-self-test-device");
    using var timeout = new CancellationTokenSource(
        TimeSpan.FromSeconds(10));
    using var server = new ManagerHubServer(port, [credential]);
    using var client = new ManagerHubClient(
        IPAddress.Loopback.ToString(),
        port,
        credential.AccessKey,
        "self-test-client",
        allowRemoteTasks,
        allowSystemCommands);
    var online =
        new TaskCompletionSource<HubClientInfo>(
            TaskCreationOptions.RunContinuationsAsynchronously);
    server.ClientOnline += info => online.TrySetResult(info);
    var serverTask = server.RunAsync(timeout.Token);
    var clientTask = client.RunAsync(timeout.Token);
    try
    {
        var info = await online.Task.WaitAsync(timeout.Token);
        await test(server, client, info, timeout.Token);
    }
    finally
    {
        timeout.Cancel();
        await IgnoreCancellationAsync(serverTask);
        await IgnoreCancellationAsync(clientTask);
    }
}

static ManagerHubDeviceCredential Credential(string name) =>
    new(name, ComoteAccessKey.Generate());

static int GetFreePort()
{
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    return ((IPEndPoint)listener.LocalEndpoint).Port;
}

static async Task WaitUntilAsync(
    Func<bool> condition,
    CancellationToken cancellationToken)
{
    while (!condition())
    {
        await Task.Delay(20, cancellationToken);
    }
}

static async Task IgnoreCancellationAsync(Task task)
{
    try
    {
        await task;
    }
    catch (OperationCanceledException)
    {
    }
    catch (ObjectDisposedException)
    {
    }
}

static void Require(bool condition)
{
    if (!condition)
    {
        throw new InvalidOperationException(
            "Self-test assertion failed.");
    }
}

internal sealed class RawHubConnection : IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly SecurePskChannel _channel;

    private RawHubConnection(
        TcpClient client,
        SecurePskChannel channel)
    {
        _client = client;
        _channel = channel;
    }

    public static async Task<RawHubConnection> ConnectAsync(
        int port,
        string accessKey,
        string clientName,
        string? prefaceRoutingId = null,
        string? registrationRoutingId = null,
        CancellationToken cancellationToken = default)
    {
        if (!ComoteAccessKey.TryParse(accessKey, out var parsedKey))
        {
            throw new ArgumentException("Invalid test access key.");
        }

        var client = new TcpClient { NoDelay = true };
        SecurePskChannel? channel = null;
        try
        {
            var derivedRoutingId =
                ManagerHubProtocol.DeriveRoutingId(parsedKey);
            var prefaceId = prefaceRoutingId ?? derivedRoutingId;
            var registeredId = registrationRoutingId ?? derivedRoutingId;
            await client.ConnectAsync(
                IPAddress.Loopback,
                port,
                cancellationToken);
            var stream = client.GetStream();
            await ManagerHubProtocol.WriteRoutingPrefaceAsync(
                stream,
                prefaceId,
                cancellationToken);
            channel = await SecurePskChannel.AuthenticateClientAsync(
                stream,
                parsedKey,
                ManagerHubProtocol.CreateSecureContext(prefaceId),
                cancellationToken);
            await SendAsync(
                channel,
                new JObject
                {
                    ["type"] = "register",
                    ["routingId"] = registeredId,
                    ["name"] = clientName,
                    ["allowRemoteTasks"] = false,
                    ["allowSystemCommands"] = false,
                },
                cancellationToken);
            var acknowledgement = await ReceiveAsync(
                channel,
                cancellationToken);
            if (acknowledgement["type"]?.Value<string>() != "registered" ||
                acknowledgement["routingId"]?.Value<string>() !=
                    derivedRoutingId)
            {
                throw new UnauthorizedAccessException(
                    "Raw test registration was rejected.");
            }

            return new RawHubConnection(client, channel);
        }
        catch
        {
            if (channel is not null)
            {
                await channel.DisposeAsync();
            }
            client.Dispose();
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(parsedKey);
        }
    }

    public Task SendAsync(
        JObject message,
        CancellationToken cancellationToken) =>
        SendAsync(_channel, message, cancellationToken);

    public Task<JObject> ReceiveAsync(
        CancellationToken cancellationToken) =>
        ReceiveAsync(_channel, cancellationToken);

    private static async Task SendAsync(
        SecurePskChannel channel,
        JObject message,
        CancellationToken cancellationToken)
    {
        var encoded = Encoding.UTF8.GetBytes(
            message.ToString(Formatting.None));
        try
        {
            await channel.SendAsync(encoded, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded);
        }
    }

    private static async Task<JObject> ReceiveAsync(
        SecurePskChannel channel,
        CancellationToken cancellationToken)
    {
        var encoded = await channel.ReceiveAsync(cancellationToken);
        try
        {
            return JObject.Parse(
                new UTF8Encoding(false, true).GetString(encoded));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _channel.DisposeAsync();
        _client.Dispose();
    }
}


