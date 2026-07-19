using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Viewer
{
    internal sealed record HubClientInfo(
        string Id,
        string Name,
        string Address,
        DateTime ConnectedAt);

    internal sealed class ManagerHubServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly byte[] _passwordBytes;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly ConcurrentDictionary<string, HubConnection> _clients =
            new(StringComparer.OrdinalIgnoreCase);

        public event Action<HubClientInfo>? ClientOnline;
        public event Action<string>? ClientOffline;
        public event Func<string, object, Task>? SignalReceived;
        public event Action<string, byte[]>? ThumbnailReceived;

        public ManagerHubServer(int port, string password)
        {
            _listener = new TcpListener(IPAddress.Any, port);
            _passwordBytes = Encoding.UTF8.GetBytes(password);
        }

        public IReadOnlyCollection<HubClientInfo> Clients =>
            _clients.Values.Select(connection => connection.Info).ToArray();

        public async Task RunAsync(CancellationToken cancellationToken)
        {
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
                {
                    break;
                }

                _ = ServeClientAsync(client, linked.Token);
            }
        }

        public async Task SendSignalAsync(string clientId, object signal)
        {
            if (!_clients.TryGetValue(clientId, out var connection))
                throw new IOException("Client가 Manager에 연결되어 있지 않습니다.");

            await connection.SendAsync(new JObject
            {
                ["type"] = "signal",
                ["payload"] = JToken.FromObject(signal),
            });
        }

        public async Task SendCommandAsync(string clientId, string action, string name, string folder, string value)
        {
            if (!_clients.TryGetValue(clientId, out var connection))
                throw new IOException("클라이언트가 연결되어 있지 않습니다.");
            await connection.SendAsync(new JObject
            {
                ["type"] = "command",
                ["action"] = action,
                ["name"] = name,
                ["folder"] = folder,
                ["value"] = value,
            });
        }
        private async Task ServeClientAsync(
            TcpClient client,
            CancellationToken cancellationToken)
        {
            client.NoDelay = true;
            await using var stream = client.GetStream();
            using var reader = new StreamReader(
                stream,
                Encoding.UTF8,
                false,
                8192,
                leaveOpen: true);
            await using var writer = new StreamWriter(
                stream,
                new UTF8Encoding(false),
                8192,
                leaveOpen: true)
            {
                AutoFlush = true,
            };

            HubConnection? connection = null;
            try
            {
                var nonce = RandomNumberGenerator.GetBytes(32);
                await writer.WriteLineAsync(new JObject
                {
                    ["type"] = "challenge",
                    ["nonce"] = Convert.ToBase64String(nonce),
                }.ToString(Formatting.None));

                var authLine = await reader.ReadLineAsync(cancellationToken);
                var authenticated = VerifyProof(authLine, nonce);
                await writer.WriteLineAsync(new JObject
                {
                    ["type"] = "auth_result",
                    ["ok"] = authenticated,
                }.ToString(Formatting.None));
                if (!authenticated) return;

                var registrationLine =
                    await reader.ReadLineAsync(cancellationToken);
                var registration = JObject.Parse(
                    registrationLine ??
                    throw new IOException("Client 등록 정보가 없습니다."));
                if (registration.Value<string>("type") != "register")
                    throw new IOException("Client 등록 형식이 올바르지 않습니다.");

                var clientId = registration.Value<string>("clientId")?.Trim();
                var clientName = registration.Value<string>("name")?.Trim();
                if (string.IsNullOrWhiteSpace(clientId) ||
                    clientId.Length > 128 ||
                    string.IsNullOrWhiteSpace(clientName) ||
                    clientName.Length > 128)
                    throw new IOException("Client ID 또는 이름이 올바르지 않습니다.");

                var remoteAddress =
                    client.Client.RemoteEndPoint?.ToString() ?? "unknown";
                var info = new HubClientInfo(
                    clientId,
                    clientName,
                    remoteAddress,
                    DateTime.UtcNow);
                connection = new HubConnection(info, writer);

                if (_clients.TryGetValue(clientId, out var previous))
                    previous.Close();
                _clients[clientId] = connection;
                ClientOnline?.Invoke(info);

                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(cancellationToken);
                    if (line == null) break;
                    if (line.Length > 1_000_000)
                        throw new IOException("Client 메시지가 너무 큽니다.");

                    var message = JObject.Parse(line);
                    var type = message.Value<string>("type");
                    if (type == "signal")
                    {
                        var payload = message["payload"];
                        if (payload != null && SignalReceived != null)
                            await SignalReceived(clientId, payload);
                    }
                    else if (type == "thumbnail")
                    {
                        var encoded = message.Value<string>("data");
                        if (string.IsNullOrWhiteSpace(encoded) ||
                            encoded.Length > 700_000)
                            continue;
                        try
                        {
                            var jpeg = Convert.FromBase64String(encoded);
                            if (jpeg.Length <= 500_000)
                                ThumbnailReceived?.Invoke(clientId, jpeg);
                        }
                        catch (FormatException)
                        {
                        }
                    }
                    else if (type == "ping")
                    {
                        await connection.SendAsync(
                            new JObject { ["type"] = "pong" });
                    }
                }
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Hub] Client connection ended: {ex.Message}");
            }
            finally
            {
                client.Dispose();
                if (connection != null &&
                    _clients.TryGetValue(
                        connection.Info.Id,
                        out var current) &&
                    ReferenceEquals(current, connection))
                {
                    _clients.TryRemove(connection.Info.Id, out _);
                    ClientOffline?.Invoke(connection.Info.Id);
                }
            }
        }

        private bool VerifyProof(string? authLine, byte[] nonce)
        {
            if (string.IsNullOrWhiteSpace(authLine)) return false;
            try
            {
                var auth = JObject.Parse(authLine);
                if (auth.Value<string>("type") != "auth") return false;
                var supplied = Convert.FromBase64String(
                    auth.Value<string>("proof") ?? "");
                using var hmac = new HMACSHA256(_passwordBytes);
                return CryptographicOperations.FixedTimeEquals(
                    supplied,
                    hmac.ComputeHash(nonce));
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            _lifetime.Cancel();
            _listener.Stop();
            foreach (var client in _clients.Values) client.Close();
            _clients.Clear();
            CryptographicOperations.ZeroMemory(_passwordBytes);
            _lifetime.Dispose();
        }

        private sealed class HubConnection
        {
            private readonly StreamWriter _writer;
            private readonly SemaphoreSlim _writeLock = new(1, 1);

            public HubClientInfo Info { get; }

            public HubConnection(HubClientInfo info, StreamWriter writer)
            {
                Info = info;
                _writer = writer;
            }

            public async Task SendAsync(JObject message)
            {
                await _writeLock.WaitAsync();
                try
                {
                    await _writer.WriteLineAsync(
                        message.ToString(Formatting.None));
                    await _writer.FlushAsync();
                }
                finally
                {
                    _writeLock.Release();
                }
            }

            public void Close()
            {
                try { _writer.Dispose(); } catch { }
            }
        }
    }
}

