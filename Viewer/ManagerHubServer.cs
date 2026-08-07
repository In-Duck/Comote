using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Comote.Input;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Viewer
{
    internal sealed record HubClientInfo(
        string Id,
        string Name,
        string Address,
        DateTime ConnectedAt,
        bool AllowsRemoteTasks,
        bool AllowsSystemCommands);

    internal sealed class ManagerHubDeviceCredential
    {
        public string RoutingId { get; }
        public string DisplayName { get; }
        public string AccessKey { get; }

        public ManagerHubDeviceCredential(
            string displayName,
            string accessKey)
        {
            var normalizedName = displayName?.Trim() ?? "";
            if (normalizedName.Length is <= 0 or > 128)
            {
                throw new ArgumentException(
                    "The device display name must contain 1 to 128 characters.",
                    nameof(displayName));
            }

            if (!ComoteAccessKey.TryParse(accessKey, out var parsedKey))
            {
                throw new ArgumentException(
                    "The device access key must be a canonical CMT1 256-bit key.",
                    nameof(accessKey));
            }

            try
            {
                RoutingId = ManagerHubProtocol.DeriveRoutingId(parsedKey);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(parsedKey);
            }

            DisplayName = normalizedName;
            AccessKey = accessKey;
        }
    }

    internal sealed class ManagerHubServerOptions
    {
        public int MaximumAuthenticatedConnections { get; init; } = 64;
        public int MaximumPendingAuthentications { get; init; } = 8;
        public int MaximumPendingAuthenticationsPerIp { get; init; } = 2;
        public int MaximumAuthenticationAttemptsPerWindow { get; init; } = 12;
        public TimeSpan AuthenticationAttemptWindow { get; init; } =
            TimeSpan.FromMinutes(1);
        public TimeSpan AuthenticationTimeout { get; init; } =
            TimeSpan.FromSeconds(15);
        public TimeSpan AuthenticatedIdleTimeout { get; init; } =
            TimeSpan.FromSeconds(50);

        public void Validate()
        {
            if (MaximumAuthenticatedConnections <= 0 ||
                MaximumPendingAuthentications <= 0 ||
                MaximumPendingAuthenticationsPerIp <= 0 ||
                MaximumPendingAuthenticationsPerIp >
                    MaximumPendingAuthentications ||
                MaximumAuthenticationAttemptsPerWindow <= 0 ||
                AuthenticationAttemptWindow <= TimeSpan.Zero ||
                AuthenticationTimeout <= TimeSpan.Zero ||
                AuthenticatedIdleTimeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(ManagerHubServerOptions));
            }
        }
    }

    internal sealed class ManagerHubServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly ManagerHubServerOptions _options;
        private readonly Dictionary<string, DeviceSecret> _deviceSecrets;
        private readonly object _deviceSecretsGate = new();
        private readonly byte[] _unknownRoutingKey =
            RandomNumberGenerator.GetBytes(ComoteAccessKey.KeySize);
        private readonly CancellationTokenSource _lifetime = new();
        private readonly SemaphoreSlim _unauthenticatedSlots;
        private readonly SemaphoreSlim _authenticatedSlots;
        private readonly ConcurrentDictionary<string, int> _pendingByAddress =
            new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, AuthenticationAttemptWindow>
            _attemptsByAddress = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, HubConnection> _clients =
            new(StringComparer.Ordinal);
        private long _acceptedConnectionCount;
        private int _disposed;

        public event Action<HubClientInfo>? ClientOnline;
        public event Action<string>? ClientOffline;
        public event Func<string, object, Task>? SignalReceived;
        public event Action<string, byte[]>? ThumbnailReceived;

        public ManagerHubServer(
            int port,
            IEnumerable<ManagerHubDeviceCredential> credentials,
            ManagerHubServerOptions? options = null)
        {
            if (port is < 1024 or > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(port));
            }

            ArgumentNullException.ThrowIfNull(credentials);
            _options = options ?? new ManagerHubServerOptions();
            _options.Validate();
            _listener = new TcpListener(IPAddress.Any, port);
            _unauthenticatedSlots = new SemaphoreSlim(
                _options.MaximumPendingAuthentications,
                _options.MaximumPendingAuthentications);
            _authenticatedSlots = new SemaphoreSlim(
                _options.MaximumAuthenticatedConnections,
                _options.MaximumAuthenticatedConnections);
            _deviceSecrets = new Dictionary<string, DeviceSecret>(
                StringComparer.Ordinal);

            try
            {
                foreach (var credential in credentials)
                {
                    ArgumentNullException.ThrowIfNull(credential);
                    if (!ComoteAccessKey.TryParse(
                            credential.AccessKey,
                            out var key))
                    {
                        throw new ArgumentException(
                            "A Manager Hub device key is invalid.",
                            nameof(credentials));
                    }

                    var routingId = ManagerHubProtocol.DeriveRoutingId(key);
                    if (!string.Equals(
                            routingId,
                            credential.RoutingId,
                            StringComparison.Ordinal) ||
                        !_deviceSecrets.TryAdd(
                            routingId,
                            new DeviceSecret(
                                credential.DisplayName,
                                key)))
                    {
                        CryptographicOperations.ZeroMemory(key);
                        throw new ArgumentException(
                            "Manager Hub device credentials must be valid and unique.",
                            nameof(credentials));
                    }
                }

                if (_deviceSecrets.Count == 0)
                {
                    throw new ArgumentException(
                        "At least one Manager Hub device credential is required.",
                        nameof(credentials));
                }
            }
            catch
            {
                foreach (var secret in _deviceSecrets.Values)
                {
                    secret.Dispose();
                }

                _deviceSecrets.Clear();
                throw;
            }
        }

        public IReadOnlyCollection<HubClientInfo> Clients =>
            _clients.Values.Select(connection => connection.Info).ToArray();

        internal int PendingAuthenticationCount =>
            _options.MaximumPendingAuthentications -
            _unauthenticatedSlots.CurrentCount;

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetime.Token);
            _listener.Start();

            while (!linked.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(linked.Token);
                }
                catch (OperationCanceledException)
                    when (linked.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException)
                    when (linked.IsCancellationRequested)
                {
                    break;
                }

                client.NoDelay = true;
                var address = GetRemoteAddress(client);
                if (!TryEnterAuthentication(address))
                {
                    client.Dispose();
                    continue;
                }

                _ = ServeClientAsync(client, address, linked.Token);
                if (Interlocked.Increment(ref _acceptedConnectionCount) % 128 == 0)
                {
                    PruneAuthenticationAttempts();
                }
            }
        }

        public async Task SendSignalAsync(
            string clientId,
            object signal)
        {
            var connection = GetCurrentConnection(clientId);
            await connection.SendAsync(new JObject
            {
                ["type"] = "signal",
                ["payload"] = JToken.FromObject(signal),
            });
        }

        public async Task SendCommandAsync(
            string clientId,
            string action,
            string name,
            string folder,
            string value)
        {
            var connection = GetCurrentConnection(clientId);
            if (!connection.Info.AllowsRemoteTasks)
            {
                throw new UnauthorizedAccessException(
                    "This Client did not opt in to remote task execution.");
            }
            if (action is not ("run" or "terminate") ||
                name.Length > 128 ||
                value.Length is <= 0 or > 1024 ||
                folder is not ("" or "desktop" or "custom"))
            {
                throw new ArgumentException(
                    "The remote task command is invalid.");
            }

            await connection.SendAsync(new JObject
            {
                ["type"] = "command",
                ["action"] = action,
                ["name"] = name,
                ["folder"] = folder,
                ["value"] = value,
            });
        }

        public async Task SendSystemCommandAsync(
            string clientId,
            string action,
            string name,
            string commandId,
            long issuedAtUnixMs,
            int delaySeconds)
        {
            var connection = GetCurrentConnection(clientId);
            if (!connection.Info.AllowsSystemCommands)
            {
                throw new UnauthorizedAccessException(
                    "This Client did not opt in to system commands.");
            }
            if (!RemoteCommandProtocol.IsSystemCommand(action) ||
                !RemoteCommandProtocol.IsValidName(name) ||
                !RemoteCommandProtocol.IsValidCommandId(commandId) ||
                !RemoteCommandProtocol.IsValidDelaySeconds(delaySeconds))
            {
                throw new ArgumentException(
                    "The system command is invalid.");
            }

            await connection.SendAsync(new JObject
            {
                ["type"] = "command",
                ["action"] = action,
                ["name"] = name,
                ["commandId"] = commandId,
                ["issuedAtUnixMs"] = issuedAtUnixMs,
                ["delaySeconds"] = delaySeconds,
            });
        }

        /// <summary>
        /// Removes a device from the Manager and drops any live connection, so
        /// the device can neither stay connected nor re-register. Returns true
        /// if the device was present. The caller persists the updated credential
        /// set separately.
        /// </summary>
        public bool RemoveDevice(string routingId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(routingId);
            DeviceSecret? removed;
            lock (_deviceSecretsGate)
            {
                if (!_deviceSecrets.Remove(routingId, out removed))
                {
                    return false;
                }
            }

            removed.Dispose();
            if (_clients.TryGetValue(routingId, out var connection))
            {
                connection.Close();
            }

            return true;
        }

        private HubConnection GetCurrentConnection(string clientId)
        {
            if (!_clients.TryGetValue(clientId, out var connection) ||
                !IsCurrentConnection(connection))
            {
                throw new IOException(
                    "The Client is not currently connected to this Manager.");
            }

            return connection;
        }

        private async Task ServeClientAsync(
            TcpClient client,
            string address,
            CancellationToken cancellationToken)
        {
            HubConnection? connection = null;
            var pendingAuthenticationHeld = true;
            var authenticatedSlotHeld = false;
            byte[]? clonedDeviceKey = null;
            try
            {
                var stream = client.GetStream();
                using var authenticationTimeout =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken);
                authenticationTimeout.CancelAfter(
                    _options.AuthenticationTimeout);

                var routingId =
                    await ManagerHubProtocol.ReadRoutingPrefaceAsync(
                        stream,
                        authenticationTimeout.Token);

                // Copy the device key under the lock so a concurrent deregister
                // that zeroes and removes the secret cannot corrupt this
                // in-flight handshake. The clone is zeroed in this method's
                // finally.
                bool knownDevice;
                string deviceDisplayName = "";
                byte[] selectedKey;
                lock (_deviceSecretsGate)
                {
                    knownDevice = _deviceSecrets.TryGetValue(
                        routingId,
                        out var deviceSecret);
                    if (knownDevice && deviceSecret is not null)
                    {
                        deviceDisplayName = deviceSecret.DisplayName;
                        clonedDeviceKey = (byte[])deviceSecret.Key.Clone();
                        selectedKey = clonedDeviceKey;
                    }
                    else
                    {
                        selectedKey = _unknownRoutingKey;
                    }
                }

                await using var channel =
                    await SecurePskChannel.AuthenticateServerAsync(
                        stream,
                        selectedKey,
                        ManagerHubProtocol.CreateSecureContext(routingId),
                        authenticationTimeout.Token);

                if (!knownDevice)
                {
                    throw new UnauthorizedAccessException(
                        "Manager Hub authentication failed.");
                }

                var registration =
                    await ReceiveAsync(channel, authenticationTimeout.Token);
                if (registration["type"]?.Type != JTokenType.String ||
                    registration.Value<string>("type") != "register" ||
                    registration["routingId"]?.Type != JTokenType.String ||
                    registration["name"]?.Type != JTokenType.String ||
                    registration["allowRemoteTasks"]?.Type !=
                        JTokenType.Boolean ||
                    registration["allowSystemCommands"]?.Type !=
                        JTokenType.Boolean ||
                    !HasOnlyProperties(
                        registration,
                        "type",
                        "routingId",
                        "name",
                        "allowRemoteTasks",
                        "allowSystemCommands"))
                {
                    throw new IOException(
                        "The Client registration envelope is invalid.");
                }

                var registeredRoutingId =
                    registration.Value<string>("routingId");
                var clientName = registration.Value<string>("name")?.Trim();
                if (!string.Equals(
                        registeredRoutingId,
                        routingId,
                        StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(clientName) ||
                    clientName.Length > 128)
                {
                    throw new UnauthorizedAccessException(
                        "The Client registration identity is invalid.");
                }

                if (!_authenticatedSlots.Wait(0))
                {
                    throw new IOException(
                        "The Manager Hub authenticated connection limit was reached.");
                }
                authenticatedSlotHeld = true;

                var allowRemoteTasks =
                    registration.Value<bool>("allowRemoteTasks");
                var allowSystemCommands =
                    registration.Value<bool>("allowSystemCommands");
                var info = new HubClientInfo(
                    routingId,
                    deviceDisplayName,
                    client.Client.RemoteEndPoint?.ToString() ?? address,
                    DateTime.UtcNow,
                    allowRemoteTasks,
                    allowSystemCommands);
                connection = new HubConnection(info, client, channel);

                // Re-check membership and publish the connection atomically so a
                // deregister that lands during this handshake cannot leave a
                // just-removed device connected. RemoveDevice removes the secret
                // under the same lock and then closes any published connection,
                // so one of the two orderings always wins cleanly.
                lock (_deviceSecretsGate)
                {
                    if (!_deviceSecrets.ContainsKey(routingId))
                    {
                        throw new UnauthorizedAccessException(
                            "The device was deregistered during authentication.");
                    }
                    if (!_clients.TryAdd(routingId, connection))
                    {
                        throw new UnauthorizedAccessException(
                            "This device already has an authenticated connection.");
                    }
                }

                ExitAuthentication(address);
                pendingAuthenticationHeld = false;

                await connection.SendAsync(
                    new JObject
                    {
                        ["type"] = "registered",
                        ["routingId"] = routingId,
                    },
                    authenticationTimeout.Token);
                ClientOnline?.Invoke(info);

                while (!cancellationToken.IsCancellationRequested &&
                       IsCurrentConnection(connection))
                {
                    JObject message;
                    using (var idleTimeout =
                           CancellationTokenSource.CreateLinkedTokenSource(
                               cancellationToken))
                    {
                        idleTimeout.CancelAfter(
                            _options.AuthenticatedIdleTimeout);
                        try
                        {
                            message = await ReceiveAsync(
                                channel,
                                idleTimeout.Token);
                        }
                        catch (OperationCanceledException)
                            when (!cancellationToken.IsCancellationRequested)
                        {
                            throw new IOException(
                                "The authenticated Client became idle.");
                        }
                    }

                    if (!IsCurrentConnection(connection))
                    {
                        break;
                    }

                    var type = message["type"]?.Type == JTokenType.String
                        ? message.Value<string>("type")
                        : null;
                    if (type == "signal")
                    {
                        if (!HasOnlyProperties(
                                message,
                                "type",
                                "payload") ||
                            message["payload"] is not JObject payload)
                        {
                            throw new IOException(
                                "The Client signal envelope is invalid.");
                        }

                        var handler = SignalReceived;
                        if (handler != null && IsCurrentConnection(connection))
                        {
                            await handler(routingId, payload);
                        }
                    }
                    else if (type == "thumbnail")
                    {
                        HandleThumbnail(connection, message);
                    }
                    else if (type == "ping")
                    {
                        if (!HasOnlyProperties(message, "type"))
                        {
                            throw new IOException(
                                "The Client ping envelope is invalid.");
                        }

                        if (IsCurrentConnection(connection))
                        {
                            await connection.SendAsync(
                                new JObject { ["type"] = "pong" },
                                cancellationToken);
                        }
                    }
                    else
                    {
                        throw new IOException(
                            "The Client message type is invalid.");
                    }
                }
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[Hub] Client connection ended: {ex.GetType().Name}");
            }
            finally
            {
                if (clonedDeviceKey is not null)
                {
                    CryptographicOperations.ZeroMemory(clonedDeviceKey);
                }
                client.Dispose();
                if (pendingAuthenticationHeld)
                {
                    ExitAuthentication(address);
                }
                if (authenticatedSlotHeld)
                {
                    _authenticatedSlots.Release();
                }
                if (connection != null &&
                    _clients.TryGetValue(
                        connection.Info.Id,
                        out var current) &&
                    ReferenceEquals(current, connection))
                {
                    _clients.TryRemove(
                        new KeyValuePair<string, HubConnection>(
                            connection.Info.Id,
                            connection));
                    ClientOffline?.Invoke(connection.Info.Id);
                }
            }
        }

        private void HandleThumbnail(
            HubConnection connection,
            JObject message)
        {
            if (!IsCurrentConnection(connection) ||
                !HasOnlyProperties(
                    message,
                    "type",
                    "contentType",
                    "data") ||
                message["type"]?.Type != JTokenType.String ||
                message["contentType"]?.Type != JTokenType.String ||
                message["data"]?.Type != JTokenType.String ||
                message.Value<string>("contentType") != "image/jpeg")
            {
                throw new IOException(
                    "The Client thumbnail envelope is invalid.");
            }

            var encoded = message.Value<string>("data");
            if (string.IsNullOrWhiteSpace(encoded) ||
                encoded.Length > 700_000)
            {
                throw new IOException("The Client thumbnail is invalid.");
            }

            try
            {
                var jpeg = Convert.FromBase64String(encoded);
                if (jpeg.Length > 500_000)
                {
                    throw new IOException(
                        "The Client thumbnail is too large.");
                }

                if (IsCurrentConnection(connection))
                {
                    ThumbnailReceived?.Invoke(connection.Info.Id, jpeg);
                }
            }
            catch (FormatException ex)
            {
                throw new IOException(
                    "The Client thumbnail encoding is invalid.",
                    ex);
            }
        }

        private bool IsCurrentConnection(HubConnection connection) =>
            _clients.TryGetValue(connection.Info.Id, out var current) &&
            ReferenceEquals(current, connection);

        private bool TryEnterAuthentication(string address)
        {
            if (!_unauthenticatedSlots.Wait(0))
            {
                return false;
            }

            var pendingCount = _pendingByAddress.AddOrUpdate(
                address,
                1,
                static (_, current) => checked(current + 1));
            if (pendingCount > _options.MaximumPendingAuthenticationsPerIp)
            {
                ExitAuthentication(address);
                return false;
            }

            var attempts = _attemptsByAddress.GetOrAdd(
                address,
                static _ => new AuthenticationAttemptWindow());
            if (!attempts.TryRecord(
                    DateTime.UtcNow,
                    _options.MaximumAuthenticationAttemptsPerWindow,
                    _options.AuthenticationAttemptWindow))
            {
                ExitAuthentication(address);
                return false;
            }

            return true;
        }

        private void ExitAuthentication(string address)
        {
            while (true)
            {
                if (!_pendingByAddress.TryGetValue(
                        address,
                        out var current))
                {
                    break;
                }

                if (current <= 1)
                {
                    if (_pendingByAddress.TryRemove(
                            new KeyValuePair<string, int>(address, current)))
                    {
                        break;
                    }
                }
                else if (_pendingByAddress.TryUpdate(
                             address,
                             current - 1,
                             current))
                {
                    break;
                }
            }

            _unauthenticatedSlots.Release();
        }

        private void PruneAuthenticationAttempts()
        {
            var threshold = DateTime.UtcNow -
                (_options.AuthenticationAttemptWindow +
                 _options.AuthenticationAttemptWindow);
            foreach (var entry in _attemptsByAddress)
            {
                if (entry.Value.LastAttemptUtc < threshold)
                {
                    _attemptsByAddress.TryRemove(
                        new KeyValuePair<
                            string,
                            AuthenticationAttemptWindow>(
                                entry.Key,
                                entry.Value));
                }
            }
        }

        private static string GetRemoteAddress(TcpClient client) =>
            (client.Client.RemoteEndPoint as IPEndPoint)?
                .Address.MapToIPv6()
                .ToString() ?? "unknown";

        private static async Task<JObject> ReceiveAsync(
            SecurePskChannel channel,
            CancellationToken cancellationToken)
        {
            var encoded = await channel.ReceiveAsync(cancellationToken);
            try
            {
                return StrictJsonObject.Parse(encoded);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encoded);
            }
        }

        private static bool HasOnlyProperties(
            JObject value,
            params string[] names)
        {
            var allowed = new HashSet<string>(
                names,
                StringComparer.Ordinal);
            return value.Properties().All(
                    property => allowed.Remove(property.Name)) &&
                allowed.Count == 0;
        }

        private void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _lifetime.Cancel();
            _listener.Stop();
            foreach (var client in _clients.Values)
            {
                client.Close();
            }
            _clients.Clear();
            lock (_deviceSecretsGate)
            {
                foreach (var secret in _deviceSecrets.Values)
                {
                    secret.Dispose();
                }
                _deviceSecrets.Clear();
            }
            CryptographicOperations.ZeroMemory(_unknownRoutingKey);
            _lifetime.Dispose();
        }

        private sealed class DeviceSecret : IDisposable
        {
            public string DisplayName { get; }
            public byte[] Key { get; }

            public DeviceSecret(string displayName, byte[] key)
            {
                DisplayName = displayName;
                Key = key;
            }

            public void Dispose() =>
                CryptographicOperations.ZeroMemory(Key);
        }

        private sealed class AuthenticationAttemptWindow
        {
            private readonly Queue<DateTime> _attempts = new();
            private readonly object _gate = new();

            public DateTime LastAttemptUtc { get; private set; }

            public bool TryRecord(
                DateTime now,
                int maximumAttempts,
                TimeSpan window)
            {
                lock (_gate)
                {
                    var cutoff = now - window;
                    while (_attempts.TryPeek(out var oldest) &&
                           oldest <= cutoff)
                    {
                        _attempts.Dequeue();
                    }

                    LastAttemptUtc = now;
                    if (_attempts.Count >= maximumAttempts)
                    {
                        return false;
                    }

                    _attempts.Enqueue(now);
                    return true;
                }
            }
        }

        private sealed class HubConnection
        {
            private readonly TcpClient _client;
            private readonly SecurePskChannel _channel;

            public HubClientInfo Info { get; }

            public HubConnection(
                HubClientInfo info,
                TcpClient client,
                SecurePskChannel channel)
            {
                Info = info;
                _client = client;
                _channel = channel;
            }

            public async Task SendAsync(
                JObject message,
                CancellationToken cancellationToken = default)
            {
                var encoded = Encoding.UTF8.GetBytes(
                    message.ToString(Formatting.None));
                try
                {
                    await _channel.SendAsync(
                        encoded,
                        cancellationToken);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(encoded);
                }
            }

            public void Close()
            {
                try
                {
                    _client.Dispose();
                }
                catch
                {
                }
            }
        }
    }
}
