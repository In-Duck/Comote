namespace Viewer
{
    internal sealed record HubArguments(
        int Port,
        string Password,
        string? AutoConnectId)
    {
        public static bool TryParse(
            string[] args,
            out HubArguments? result,
            out string? error)
        {
            result = null;
            error = null;
            if (!args.Any(value => value.Equals(
                    "--hub",
                    StringComparison.OrdinalIgnoreCase)))
                return false;

            var portText = GetValue(args, "--port") ?? "45820";
            var password = GetValue(args, "--password");
            if (!int.TryParse(portText, out var port) ||
                port is < 1024 or > 65535)
            {
                error = "Hub 포트는 1024~65535 범위여야 합니다.";
                return true;
            }
            if (string.IsNullOrWhiteSpace(password) ||
                password.Length < 8)
            {
                error = "Hub 등록 암호는 최소 8자 이상이어야 합니다.";
                return true;
            }
            result = new HubArguments(
                port, password, GetValue(args, "--auto-connect"));
            return true;
        }

        private static string? GetValue(string[] args, string name)
        {
            for (var index = 0; index < args.Length - 1; index++)
            {
                if (args[index].Equals(
                    name,
                    StringComparison.OrdinalIgnoreCase))
                    return args[index + 1];
            }
            return null;
        }
    }
}
