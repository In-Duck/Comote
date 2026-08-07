using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Comote.Input;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Viewer
{
    internal sealed class LanSignalClient : IDisposable
    {
        private const string SecureContext = "comote-direct-signal-v1";
        private static readonly TimeSpan ConnectionTimeout =
            TimeSpan.FromSeconds(15);

        private readonly CancellationTokenSource _lifetime = new();
        private TcpClient? _client;
        private SecurePskChannel? _channel;

        public event Func<object, Task>? SignalReceived;
        public event Action<string>? Disconnected;

        public async Task ConnectAsync(
            string host,
            int port,
            string accessKey,
            CancellationToken cancellationToken = default)
        {
            if (!ComoteAccessKey.TryParse(accessKey, out var key))
            {
                throw new ArgumentException(
                    "접속 키는 CMT1 형식의 256비트 키여야 합니다.",
                    nameof(accessKey));
            }

            using var linked =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    _lifetime.Token);
            try
            {
                linked.CancelAfter(ConnectionTimeout);
                _client = new TcpClient { NoDelay = true };
                await _client.ConnectAsync(host, port, linked.Token);
                var channel =
                    await SecurePskChannel.AuthenticateClientAsync(
                        _client.GetStream(),
                        key,
                        SecureContext,
                        linked.Token);
                _channel = channel;
                _ = ReadLoopAsync(channel, _lifetime.Token);
            }
            catch
            {
                _client?.Dispose();
                _client = null;
                throw;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }

        public async Task SendSignalAsync(object signal)
        {
            var channel = _channel ??
                throw new InvalidOperationException(
                    "Direct Host에 연결되지 않았습니다.");
            var encoded = Encoding.UTF8.GetBytes(
                new JObject
                {
                    ["type"] = "signal",
                    ["payload"] = JToken.FromObject(signal),
                }.ToString(Formatting.None));
            try
            {
                await channel.SendAsync(encoded);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encoded);
            }
        }

        private async Task ReadLoopAsync(
            SecurePskChannel channel,
            CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var encoded =
                        await channel.ReceiveAsync(cancellationToken);
                    JObject message;
                    try
                    {
                        message = StrictJsonObject.Parse(encoded);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(encoded);
                    }

                    if (message["type"]?.Type != JTokenType.String ||
                        message.Value<string>("type") != "signal" ||
                        message.Properties().Count() != 2 ||
                        message["payload"] is not JObject)
                    {
                        throw new IOException(
                            "Direct signaling envelope is invalid.");
                    }
                    if (SignalReceived != null)
                    {
                        await SignalReceived(message["payload"]!);
                    }
                }
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Disconnected?.Invoke(ex.Message);
            }
            finally
            {
                _channel = null;
                await channel.DisposeAsync();
            }
        }

        public void Dispose()
        {
            _lifetime.Cancel();
            _client?.Dispose();
            _client = null;
            _lifetime.Dispose();
        }
    }
}