using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Comote.Input;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Host
{
    internal sealed class LanSignalServer : IDisposable
    {
        private const string SecureContext = "comote-direct-signal-v1";
        private static readonly TimeSpan AuthenticationTimeout =
            TimeSpan.FromSeconds(15);

        private readonly TcpListener _listener;
        private readonly byte[] _accessKey;
        private SecurePskChannel? _channel;

        public LanSignalServer(int port, string accessKey)
        {
            _listener = new TcpListener(IPAddress.Any, port);
            if (!ComoteAccessKey.TryParse(accessKey, out _accessKey))
            {
                throw new ArgumentException(
                    "Direct connection access key must be a CMT1 256-bit key.",
                    nameof(accessKey));
            }
        }

        public async Task RunAsync(
            Func<object, Task> onSignal,
            CancellationToken cancellationToken)
        {
            _listener.Start();
            Console.WriteLine(
                $"[Direct] Listening on TCP {_listener.LocalEndpoint}. " +
                "Signaling is mutually authenticated and encrypted.");

            while (!cancellationToken.IsCancellationRequested)
            {
                using var client =
                    await _listener.AcceptTcpClientAsync(cancellationToken);
                client.NoDelay = true;
                Console.WriteLine(
                    $"[Direct] Manager connected: " +
                    $"{client.Client.RemoteEndPoint}");

                try
                {
                    await ServeClientAsync(
                        client,
                        onSignal,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"[Direct] Connection ended: {ex.Message}");
                }
                finally
                {
                    _channel = null;
                }
            }
        }

        public async Task SendSignalAsync(object signal)
        {
            var channel = _channel;
            if (channel == null) return;

            await SendAsync(channel, new JObject
            {
                ["type"] = "signal",
                ["payload"] = JToken.FromObject(signal),
            });
        }

        private async Task ServeClientAsync(
            TcpClient client,
            Func<object, Task> onSignal,
            CancellationToken cancellationToken)
        {
            var stream = client.GetStream();
            using var authenticationTimeout =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
            authenticationTimeout.CancelAfter(AuthenticationTimeout);
            await using var channel =
                await SecurePskChannel.AuthenticateServerAsync(
                    stream,
                    _accessKey,
                    SecureContext,
                    authenticationTimeout.Token);
            _channel = channel;
            Console.WriteLine(
                "[Direct] Manager mutually authenticated.");

            while (!cancellationToken.IsCancellationRequested)
            {
                var message =
                    await ReceiveAsync(channel, cancellationToken);
                if (message["type"]?.Type != JTokenType.String ||
                    message.Value<string>("type") != "signal" ||
                    message.Properties().Count() != 2 ||
                    message["payload"] is not JObject)
                {
                    throw new IOException(
                        "Direct signaling envelope is invalid.");
                }

                await onSignal(message["payload"]!);
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

        public void Dispose()
        {
            _channel = null;
            _listener.Stop();
            CryptographicOperations.ZeroMemory(_accessKey);
        }
    }
}