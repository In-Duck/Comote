using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Host
{
    internal sealed class LanSignalServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly byte[] _passwordBytes;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private StreamWriter? _writer;

        public LanSignalServer(int port, string password)
        {
            _listener = new TcpListener(IPAddress.Any, port);
            _passwordBytes = Encoding.UTF8.GetBytes(password);
        }

        public async Task RunAsync(
            Func<object, Task> onSignal,
            CancellationToken cancellationToken)
        {
            _listener.Start();
            Console.WriteLine(
                $"[LAN] Listening on TCP {_listener.LocalEndpoint}. " +
                "Media and input use encrypted WebRTC after signaling.");

            while (!cancellationToken.IsCancellationRequested)
            {
                using var client =
                    await _listener.AcceptTcpClientAsync(cancellationToken);
                client.NoDelay = true;
                Console.WriteLine(
                    $"[LAN] Manager connected: {client.Client.RemoteEndPoint}");

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
                    Console.WriteLine($"[LAN] Connection ended: {ex.Message}");
                }
                finally
                {
                    _writer = null;
                }
            }
        }

        public async Task SendSignalAsync(object signal)
        {
            var writer = _writer;
            if (writer == null) return;

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

        private async Task ServeClientAsync(
            TcpClient client,
            Func<object, Task> onSignal,
            CancellationToken cancellationToken)
        {
            await using var stream = client.GetStream();
            using var reader = new StreamReader(
                stream,
                Encoding.UTF8,
                false,
                4096,
                leaveOpen: true);
            await using var writer = new StreamWriter(
                stream,
                new UTF8Encoding(false),
                4096,
                leaveOpen: true)
            {
                AutoFlush = true,
            };

            var nonce = RandomNumberGenerator.GetBytes(32);
            await writer.WriteLineAsync(
                new JObject
                {
                    ["type"] = "challenge",
                    ["nonce"] = Convert.ToBase64String(nonce),
                }.ToString(Formatting.None));

            var authLine = await reader.ReadLineAsync(cancellationToken);
            var authenticated = VerifyProof(authLine, nonce);
            await writer.WriteLineAsync(
                new JObject
                {
                    ["type"] = "auth_result",
                    ["ok"] = authenticated,
                }.ToString(Formatting.None));

            if (!authenticated)
            {
                Console.WriteLine("[LAN] Authentication rejected.");
                return;
            }

            Console.WriteLine("[LAN] Manager authenticated.");
            _writer = writer;

            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line == null) break;

                var message = JObject.Parse(line);
                if (message.Value<string>("type") != "signal") continue;
                var payload = message["payload"];
                if (payload != null)
                {
                    await onSignal(payload);
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
                var expected = hmac.ComputeHash(nonce);
                return CryptographicOperations.FixedTimeEquals(
                    supplied,
                    expected);
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            _writer = null;
            _listener.Stop();
            _writeLock.Dispose();
            CryptographicOperations.ZeroMemory(_passwordBytes);
        }
    }
}
