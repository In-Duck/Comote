using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Viewer
{
    internal sealed class LanSignalClient : IDisposable
    {
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly CancellationTokenSource _lifetime = new();
        private TcpClient? _client;
        private StreamReader? _reader;
        private StreamWriter? _writer;

        public event Func<object, Task>? SignalReceived;
        public event Action<string>? Disconnected;

        public async Task ConnectAsync(
            string host,
            int port,
            string password,
            CancellationToken cancellationToken = default)
        {
            _client = new TcpClient { NoDelay = true };
            await _client.ConnectAsync(host, port, cancellationToken);
            var stream = _client.GetStream();
            _reader = new StreamReader(
                stream,
                Encoding.UTF8,
                false,
                4096,
                leaveOpen: true);
            _writer = new StreamWriter(
                stream,
                new UTF8Encoding(false),
                4096,
                leaveOpen: true)
            {
                AutoFlush = true,
            };

            var challengeLine =
                await _reader.ReadLineAsync(cancellationToken);
            var challenge = JObject.Parse(
                challengeLine ??
                throw new IOException("LAN 인증 응답이 없습니다."));
            if (challenge.Value<string>("type") != "challenge")
                throw new IOException("LAN 인증 형식이 올바르지 않습니다.");

            var nonce = Convert.FromBase64String(
                challenge.Value<string>("nonce") ?? "");
            var passwordBytes = Encoding.UTF8.GetBytes(password);
            byte[] proof;
            try
            {
                using var hmac = new HMACSHA256(passwordBytes);
                proof = hmac.ComputeHash(nonce);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(passwordBytes);
            }

            await _writer.WriteLineAsync(
                new JObject
                {
                    ["type"] = "auth",
                    ["proof"] = Convert.ToBase64String(proof),
                }.ToString(Formatting.None));
            CryptographicOperations.ZeroMemory(proof);

            var resultLine = await _reader.ReadLineAsync(cancellationToken);
            var result = JObject.Parse(
                resultLine ??
                throw new IOException("LAN 인증 결과가 없습니다."));
            if (result.Value<string>("type") != "auth_result" ||
                result.Value<bool?>("ok") != true)
            {
                throw new UnauthorizedAccessException(
                    "LAN 테스트 암호가 일치하지 않습니다.");
            }

            _ = ReadLoopAsync(_lifetime.Token);
        }

        public async Task SendSignalAsync(object signal)
        {
            var writer = _writer ??
                throw new InvalidOperationException(
                    "LAN Host에 연결되지 않았습니다.");
            var envelope = new JObject
            {
                ["type"] = "signal",
                ["payload"] = JToken.FromObject(signal),
            };

            await _writeLock.WaitAsync();
            try
            {
                await writer.WriteLineAsync(
                    envelope.ToString(Formatting.None));
                await writer.FlushAsync();
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private async Task ReadLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await _reader!.ReadLineAsync(cancellationToken);
                    if (line == null) break;

                    var message = JObject.Parse(line);
                    if (message.Value<string>("type") != "signal") continue;
                    var payload = message["payload"];
                    if (payload != null && SignalReceived != null)
                    {
                        await SignalReceived(payload);
                    }
                }

                Disconnected?.Invoke("Host가 연결을 종료했습니다.");
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Disconnected?.Invoke(ex.Message);
            }
        }

        public void Dispose()
        {
            _lifetime.Cancel();
            _reader?.Dispose();
            _writer?.Dispose();
            _client?.Dispose();
            _writeLock.Dispose();
            _lifetime.Dispose();
        }
    }
}
