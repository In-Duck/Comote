using Comote.Input;

namespace Viewer
{
    internal sealed record HubArguments(
        int Port,
        IReadOnlyList<ManagerHubDeviceCredential> Devices,
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
            {
                return false;
            }

            if (ContainsArgument(args, "--access-key") ||
                ContainsArgument(args, "--password"))
            {
                error =
                    "Hub 접속 키는 명령줄에서 받을 수 없습니다. " +
                    "--manager-hub에서 Windows 보호 저장소를 사용하세요.";
                return true;
            }

            if (!ManagerHubSettingsStore.TryLoad(out var settings) ||
                settings.Devices.Count == 0)
            {
                error =
                    "저장된 Manager Hub 기기 키가 없습니다. " +
                    "먼저 --manager-hub를 실행해 Client 키를 추가하세요.";
                return true;
            }

            var portText = GetValue(args, "--port");
            var port = settings.Port;
            if (portText != null &&
                (!int.TryParse(portText, out port) ||
                 port is < 1024 or > 65535))
            {
                error = "Hub 포트는 1024~65535 범위여야 합니다.";
                return true;
            }

            var autoConnectId = GetValue(args, "--auto-connect");
            if (autoConnectId != null &&
                !ManagerHubProtocol.IsCanonicalRoutingId(autoConnectId))
            {
                error = "자동 연결 기기 ID 형식이 올바르지 않습니다.";
                return true;
            }

            result = new HubArguments(
                port,
                settings.Devices,
                autoConnectId);
            return true;
        }

        private static bool ContainsArgument(
            IEnumerable<string> args,
            string name) =>
            args.Any(value => value.Equals(
                name,
                StringComparison.OrdinalIgnoreCase));

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
