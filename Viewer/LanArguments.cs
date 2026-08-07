using Comote.Input;
using System.Security.Cryptography;

namespace Viewer
{
    internal sealed record LanArguments(
        string Host,
        int Port,
        string Password)
    {
        public static bool TryParse(
            string[] args,
            out LanArguments? result,
            out string? error)
        {
            result = null;
            error = null;
            var lanIndex = Array.FindIndex(
                args,
                value =>
                    value.Equals(
                        "--direct",
                        StringComparison.OrdinalIgnoreCase) ||
                    value.Equals(
                        "--lan",
                        StringComparison.OrdinalIgnoreCase));
            if (lanIndex < 0) return false;

            if (lanIndex + 1 >= args.Length ||
                args[lanIndex + 1].StartsWith("--"))
            {
                error = "LAN Host IP가 필요합니다.";
                return true;
            }

            var host = args[lanIndex + 1];
            var portText = GetValue(args, "--port") ?? "45820";
            var password = GetValue(args, "--access-key") ??
                GetValue(args, "--password");
            if (!int.TryParse(portText, out var port) ||
                port is < 1024 or > 65535)
            {
                error = "LAN 포트는 1024~65535 범위여야 합니다.";
                return true;
            }

            if (!ComoteAccessKey.TryParse(password, out var parsedKey))
            {
                error = "Direct 접속 키는 CMT1 형식의 256비트 키여야 합니다.";
                return true;
            }
            CryptographicOperations.ZeroMemory(parsedKey);

            result = new LanArguments(host, port, password!);
            return true;
        }

        private static string? GetValue(string[] args, string name)
        {
            for (var index = 0; index < args.Length - 1; index++)
            {
                if (args[index].Equals(
                    name,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return args[index + 1];
                }
            }

            return null;
        }
    }
}
