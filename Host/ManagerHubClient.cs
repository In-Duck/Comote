using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Host
{
    internal sealed class ManagerHubClient : IDisposable
    {
        private readonly string _managerHost;
        private readonly int _managerPort;
        private readonly string _clientId;
        private readonly string _clientName;
        private readonly byte[] _passwordBytes;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private StreamWriter? _writer;
        private TcpClient? _client;

        public event Func<object, Task>? SignalReceived;
        public event Func<JObject, Task>? CommandReceived;
        public event Action<bool, string>? ConnectionChanged;

        public ManagerHubClient(
            string managerHost,
            int managerPort,
            string password,
            string clientId,
            string clientName)
        {
            _managerHost = managerHost;
            _managerPort = managerPort;
            _passwordBytes = Encoding.UTF8.GetBytes(password);
            _clientId = clientId;
            _clientName = clientName;
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
                    _writer = null;
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
            var writer = _writer ??
                throw new IOException("Manager Hub에 연결되지 않았습니다.");
            await SendAsync(writer, new JObject
            {
                ["type"] = "signal",
                ["payload"] = JToken.FromObject(signal),
            });
        }

        public async Task SendThumbnailAsync(byte[] jpeg)
        {
            var writer = _writer;
            if (writer == null || jpeg.Length == 0) return;
            await SendAsync(writer, new JObject
            {
                ["type"] = "thumbnail",
                ["contentType"] = "image/jpeg",
                ["data"] = Convert.ToBase64String(jpeg),
            });
        }

        private async Task ConnectAndServeAsync(
            CancellationToken cancellationToken)
        {
            _client = new TcpClient { NoDelay = true };
            await _client.ConnectAsync(
                _managerHost,
                _managerPort,
                cancellationToken);
            var stream = _client.GetStream();
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

            var challengeLine =
                await reader.ReadLineAsync(cancellationToken);
            var challenge = JObject.Parse(
                challengeLine ??
                throw new IOException("Manager 인증 응답이 없습니다."));
            if (challenge.Value<string>("type") != "challenge")
                throw new IOException("Manager 인증 형식이 올바르지 않습니다.");

            var nonce = Convert.FromBase64String(
                challenge.Value<string>("nonce") ?? "");
            using var hmac = new HMACSHA256(_passwordBytes);
            var proof = hmac.ComputeHash(nonce);
            await SendAsync(writer, new JObject
            {
                ["type"] = "auth",
                ["proof"] = Convert.ToBase64String(proof),
            });
            CryptographicOperations.ZeroMemory(proof);

            var resultLine = await reader.ReadLineAsync(cancellationToken);
            var result = JObject.Parse(
                resultLine ??
                throw new IOException("Manager 인증 결과가 없습니다."));
            if (result.Value<string>("type") != "auth_result" ||
                result.Value<bool?>("ok") != true)
                throw new UnauthorizedAccessException(
                    "Manager 등록 암호가 일치하지 않습니다.");

            await SendAsync(writer, new JObject
            {
                ["type"] = "register",
                ["clientId"] = _clientId,
                ["name"] = _clientName,
            });
            _writer = writer;
            ConnectionChanged?.Invoke(
                true,
                $"{_managerHost}:{_managerPort}");

            using var pingTimer = new PeriodicTimer(
                TimeSpan.FromSeconds(20));
            var pingTask = Task.Run(async () =>
            {
                while (await pingTimer.WaitForNextTickAsync(
                           cancellationToken))
                {
                    await SendAsync(
                        writer,
                        new JObject { ["type"] = "ping" });
                }
            }, cancellationToken);

            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line == null) break;
                if (line.Length > 1_000_000)
                    throw new IOException("Manager 메시지가 너무 큽니다.");

                var message = JObject.Parse(line);
                var messageType = message.Value<string>("type");
                if (messageType == "signal")
                {
                    var payload = message["payload"];
                    if (payload != null && SignalReceived != null)
                        await SignalReceived(payload);
                }
                else if (messageType == "command" && CommandReceived != null)
                {
                    await CommandReceived(message);
                }
            }

            ConnectionChanged?.Invoke(false, "Manager 연결 종료");
        }

        private async Task SendAsync(
            StreamWriter writer,
            JObject message)
        {
            await _writeLock.WaitAsync();
            try
            {
                await writer.WriteLineAsync(
                    message.ToString(Formatting.None));
                await writer.FlushAsync();
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public void Dispose()
        {
            _lifetime.Cancel();
            _client?.Dispose();
            CryptographicOperations.ZeroMemory(_passwordBytes);
            _writeLock.Dispose();
            _lifetime.Dispose();
        }
    }
}

