using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Comote.Input;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Host
{
    internal sealed class ManagerHubClient : IDisposable
    {
        private static readonly TimeSpan ConnectionTimeout =
            TimeSpan.FromSeconds(15);

        private readonly string _managerHost;
        private readonly int _managerPort;
        private readonly string _routingId;
        private readonly string _clientName;
        private readonly byte[] _accessKey;
        private readonly bool _allowRemoteTasks;
        private readonly bool _allowSystemCommands;
        private readonly CancellationTokenSource _lifetime = new();
        private SecurePskChannel? _channel;
        private TcpClient? _client;

        public event Func<object, Task>? SignalReceived;
        public event Func<JObject, Task>? CommandReceived;
        public event Action<bool, string>? ConnectionChanged;

        internal string RoutingId => _routingId;

        public ManagerHubClient(
            string managerHost,
            int managerPort,
            string accessKey,
            string clientName,
            bool allowRemoteTasks = false,
            bool allowSystemCommands = false)
        {
            if (string.IsNullOrWhiteSpace(managerHost))
            {
                throw new ArgumentException(
                    "A Manager address is required.",
                    nameof(managerHost));
            }
            if (managerPort is < 1024 or > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(managerPort));
            }

            var normalizedName = clientName?.Trim() ?? "";
            if (normalizedName.Length is <= 0 or > 128)
            {
                throw new ArgumentException(
                    "The Client name must contain 1 to 128 characters.",
                    nameof(clientName));
            }
            if (!ComoteAccessKey.TryParse(accessKey, out _accessKey))
            {
                throw new ArgumentException(
                    "Manager access key must be a canonical CMT1 256-bit key.",
                    nameof(accessKey));
            }

            _managerHost = managerHost.Trim();
            _managerPort = managerPort;
            _routingId = ManagerHubProtocol.DeriveRoutingId(_accessKey);
            _clientName = normalizedName;
            _allowRemoteTasks = allowRemoteTasks;
            _allowSystemCommands = allowSystemCommands;
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetime.Token);
            var delaySeconds = 2;

            while (!linked.IsCancellationRequested)
            {
                try
                {
                    await ConnectAndServeAsync(linked.Token);
                    delaySeconds = 2;
                }
                catch (OperationCanceledException)
                    when (linked.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    ConnectionChanged?.Invoke(false, ex.Message);
                }
                finally
                {
                    _channel = null;
                    _client?.Dispose();
                    _client = null;
                }

                try
                {
                    await Task.Delay(
                        TimeSpan.FromSeconds(delaySeconds),
                        linked.Token);
                    delaySeconds = Math.Min(delaySeconds * 2, 30);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        public async Task SendSignalAsync(object signal)
        {
            var channel = _channel ??
                throw new IOException(
                    "The Client is not connected to the Manager Hub.");
            await SendAsync(channel, new JObject
            {
                ["type"] = "signal",
                ["payload"] = JToken.FromObject(signal),
            });
        }

        public async Task SendThumbnailAsync(byte[] jpeg)
        {
            ArgumentNullException.ThrowIfNull(jpeg);
            var channel = _channel;
            if (channel == null || jpeg.Length == 0)
            {
                return;
            }
            if (jpeg.Length > 500_000)
            {
                throw new IOException("The thumbnail is too large.");
            }

            await SendAsync(channel, new JObject
            {
                ["type"] = "thumbnail",
                ["contentType"] = "image/jpeg",
                ["data"] = Convert.ToBase64String(jpeg),
            });
        }

        private async Task ConnectAndServeAsync(
            CancellationToken cancellationToken)
        {
            using var connectionTimeout =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
            connectionTimeout.CancelAfter(ConnectionTimeout);
            _client = new TcpClient { NoDelay = true };
            await _client.ConnectAsync(
                _managerHost,
                _managerPort,
                connectionTimeout.Token);
            var stream = _client.GetStream();
            await ManagerHubProtocol.WriteRoutingPrefaceAsync(
                stream,
                _routingId,
                connectionTimeout.Token);
            await using var channel =
                await SecurePskChannel.AuthenticateClientAsync(
                    stream,
                    _accessKey,
                    ManagerHubProtocol.CreateSecureContext(_routingId),
                    connectionTimeout.Token);

            await SendAsync(channel, new JObject
            {
                ["type"] = "register",
                ["routingId"] = _routingId,
                ["name"] = _clientName,
                ["allowRemoteTasks"] = _allowRemoteTasks,
                ["allowSystemCommands"] = _allowSystemCommands,
            }, connectionTimeout.Token);
            var registration = await ReceiveAsync(
                channel,
                connectionTimeout.Token);
            if (!HasOnlyProperties(
                    registration,
                    "type",
                    "routingId") ||
                registration["type"]?.Type != JTokenType.String ||
                registration.Value<string>("type") != "registered" ||
                registration["routingId"]?.Type != JTokenType.String ||
                !string.Equals(
                    registration.Value<string>("routingId"),
                    _routingId,
                    StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException(
                    "The Manager rejected the device registration.");
            }

            _channel = channel;
            ConnectionChanged?.Invoke(
                true,
                $"{_managerHost}:{_managerPort}");

            using var connectionLifetime =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
            using var pingTimer = new PeriodicTimer(
                TimeSpan.FromSeconds(20));
            var pingTask = RunPingLoopAsync(
                channel,
                pingTimer,
                connectionLifetime.Token);

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var message = await ReceiveAsync(
                        channel,
                        cancellationToken);
                    var messageType = message["type"]?.Type == JTokenType.String
                        ? message.Value<string>("type")
                        : null;
                    if (messageType == "signal" &&
                        HasOnlyProperties(message, "type", "payload") &&
                        message["payload"] is JObject payload)
                    {
                        if (SignalReceived != null)
                        {
                            await SignalReceived(payload);
                        }
                    }
                    else if (messageType == "command")
                    {
                        // A malformed or unauthorized command is dropped, not
                        // treated as a fatal protocol violation, so a future
                        // validation drift degrades to "ignored" rather than a
                        // reconnect loop that never delivers commands.
                        var action = message.Value<string>("action");
                        if (!IsValidCommand(message) ||
                            !IsActionAllowed(action))
                        {
                            Console.WriteLine(
                                "[Hub] A remote command was ignored " +
                                "(invalid or not opted in).");
                            continue;
                        }
                        if (CommandReceived != null)
                        {
                            await CommandReceived(message);
                        }
                    }
                    else if (messageType == "pong" &&
                             HasOnlyProperties(message, "type"))
                    {
                    }
                    else
                    {
                        throw new IOException(
                            "The Manager Hub message schema is invalid.");
                    }
                }
            }
            finally
            {
                connectionLifetime.Cancel();
                try
                {
                    await pingTask;
                }
                catch (OperationCanceledException)
                    when (connectionLifetime.IsCancellationRequested)
                {
                }
            }
        }

        private static async Task RunPingLoopAsync(
            SecurePskChannel channel,
            PeriodicTimer timer,
            CancellationToken cancellationToken)
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await SendAsync(
                    channel,
                    new JObject { ["type"] = "ping" },
                    cancellationToken);
            }
        }

        private static async Task SendAsync(
            SecurePskChannel channel,
            JObject message,
            CancellationToken cancellationToken = default)
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
                return StrictJsonObject.Parse(encoded);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encoded);
            }
        }

        private bool IsActionAllowed(string? action) =>
            (RemoteCommandProtocol.IsAppTask(action) && _allowRemoteTasks) ||
            (RemoteCommandProtocol.IsSystemCommand(action) &&
                _allowSystemCommands);

        private static bool IsValidCommand(JObject value)
        {
            if (value["action"]?.Type != JTokenType.String)
            {
                return false;
            }

            var action = value.Value<string>("action")!;
            if (!HasOnlyProperties(
                    value,
                    [.. RemoteCommandProtocol.AllowedProperties(action)]))
            {
                return false;
            }

            return RemoteCommandProtocol.IsSystemCommand(action)
                ? IsValidSystemCommand(value, action)
                : IsValidAppTask(value, action);
        }

        private static bool IsValidAppTask(JObject value, string action)
        {
            if (value["name"]?.Type != JTokenType.String ||
                value["folder"]?.Type != JTokenType.String ||
                value["value"]?.Type != JTokenType.String)
            {
                return false;
            }

            return RemoteCommandProtocol.IsAppTask(action) &&
                RemoteCommandProtocol.IsValidName(value.Value<string>("name")) &&
                RemoteCommandProtocol.IsValidAppTaskValue(
                    value.Value<string>("value")) &&
                RemoteCommandProtocol.IsValidFolder(
                    value.Value<string>("folder"));
        }

        private static bool IsValidSystemCommand(JObject value, string action)
        {
            if (value["name"]?.Type != JTokenType.String ||
                value["commandId"]?.Type != JTokenType.String ||
                value["issuedAtUnixMs"]?.Type != JTokenType.Integer ||
                value["delaySeconds"]?.Type != JTokenType.Integer)
            {
                return false;
            }

            return RemoteCommandProtocol.IsSystemCommand(action) &&
                RemoteCommandProtocol.IsValidName(value.Value<string>("name")) &&
                RemoteCommandProtocol.IsValidCommandId(
                    value.Value<string>("commandId")) &&
                RemoteCommandProtocol.IsValidDelaySeconds(
                    value.Value<long>("delaySeconds"));
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

        public void Dispose()
        {
            _lifetime.Cancel();
            _client?.Dispose();
            CryptographicOperations.ZeroMemory(_accessKey);
            _lifetime.Dispose();
        }
    }
}
