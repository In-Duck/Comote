using System.Diagnostics;
using Newtonsoft.Json.Linq;
using Comote.Shared;

namespace Host
{
    internal static class RemoteTaskExecutor
    {
        private static readonly string[] AllowedRunRoots =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        };

        public static async Task<JObject> ExecuteAsync(
            JObject command,
            IProgress<ClientUpdateProgress>? updateProgress = null)
        {
            var action = command.Value<string>("action") ?? "";
            var value = command.Value<string>("value")?.Trim() ?? "";
            try
            {
                return action switch
                {
                    "run" => Run(command.Value<string>("folder") ?? "desktop", value),
                    "terminate" => Terminate(value),
                    "update" => await UpdateAsync(value, updateProgress),
                    _ => Result(false, "지원하지 않는 작업입니다."),
                };
            }
            catch (Exception ex)
            {
                return Result(false, ex.Message);
            }
        }

        private static JObject Run(string folder, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return Result(false, "실행 파일이 비어 있습니다.");
            if (value.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                return Result(false, "실행 경로가 올바르지 않습니다.");

            var path = folder.Equals("desktop", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), value)
                : value;
            path = Path.GetFullPath(path);
            if (new Uri(path).IsUnc)
                return Result(false, "네트워크 경로의 원격 실행은 허용하지 않습니다.");
            if (!IsInAllowedRoot(path))
                return Result(false, "바탕 화면 또는 Program Files 아래의 프로그램만 실행할 수 있습니다.");
            if (!new[] { ".exe", ".lnk" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                return Result(false, "EXE 또는 바로가기만 실행할 수 있습니다.");
            if (!File.Exists(path))
                return Result(false, "파일을 찾을 수 없습니다: " + path);

            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                WorkingDirectory = Path.GetDirectoryName(path)!,
                UseShellExecute = true,
            });
            return Result(true, "실행: " + Path.GetFileName(path));
        }

        private static bool IsInAllowedRoot(string path)
        {
            foreach (var root in AllowedRunRoots.Where(root => !string.IsNullOrWhiteSpace(root)))
            {
                var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
                if (path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static JObject Terminate(string processName)
        {
            var name = Path.GetFileNameWithoutExtension(processName);
            if (string.IsNullOrWhiteSpace(name) || name.Length > 128 ||
                name.Any(character => !(char.IsLetterOrDigit(character) || character is '_' or '-' or '.')))
                return Result(false, "프로세스 이름이 올바르지 않습니다.");

            var processes = Process.GetProcessesByName(name);
            foreach (var process in processes)
            {
                try { process.Kill(entireProcessTree: true); }
                finally { process.Dispose(); }
            }
            return Result(true, processes.Length == 0
                ? "실행 중인 프로세스가 없습니다."
                : $"{processes.Length}개 프로세스 종료 요청");
        }

        private static async Task<JObject> UpdateAsync(
            string manifestUrl,
            IProgress<ClientUpdateProgress>? progress)
        {
            if (string.IsNullOrWhiteSpace(manifestUrl))
                manifestUrl = ProductUpdateSettings.ClientManifestUrl;
            if (!manifestUrl.Equals(
                    ProductUpdateSettings.ClientManifestUrl,
                    StringComparison.OrdinalIgnoreCase))
                return Result(false, "공식 Comote 업데이트 주소만 사용할 수 있습니다.");

            var restartArguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
            var staged = await ClientAutoUpdater.TryStageUpdateAsync(
                manifestUrl, restartArguments, progress);
            if (!staged)
                return Result(false, "새 버전이 없거나 업데이트 준비에 실패했습니다.");

            _ = Task.Run(async () =>
            {
                await Task.Delay(750);
                Environment.Exit(0);
            });
            return Result(true, "업데이트를 검증했습니다. Client를 다시 시작합니다.");
        }

        private static JObject Result(bool ok, string message) =>
            new() { ["ok"] = ok, ["message"] = message };
    }
}
