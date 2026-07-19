using System.Diagnostics;
using Newtonsoft.Json.Linq;

namespace Host
{
    internal static class RemoteTaskExecutor
    {
        public static Task<JObject> ExecuteAsync(JObject command)
        {
            var action = command.Value<string>("action") ?? "";
            var value = command.Value<string>("value")?.Trim() ?? "";
            try
            {
                return Task.FromResult(action switch
                {
                    "run" => Run(command.Value<string>("folder") ?? "desktop", value),
                    "terminate" => Terminate(value),
                    _ => Result(false, "알 수 없는 작업입니다."),
                });
            }
            catch (Exception ex)
            {
                return Task.FromResult(Result(false, ex.Message));
            }
        }

        private static JObject Run(string folder, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return Result(false, "실행 파일이 비어 있습니다.");
            var path = folder == "desktop"
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), value)
                : value;
            path = Path.GetFullPath(path);
            if (!File.Exists(path)) return Result(false, "파일을 찾을 수 없습니다: " + path);
            Process.Start(new ProcessStartInfo { FileName = path, WorkingDirectory = Path.GetDirectoryName(path)!, UseShellExecute = true });
            return Result(true, "실행됨: " + Path.GetFileName(path));
        }

        private static JObject Terminate(string processName)
        {
            var name = Path.GetFileNameWithoutExtension(processName);
            if (string.IsNullOrWhiteSpace(name)) return Result(false, "프로세스명이 비어 있습니다.");
            var processes = Process.GetProcessesByName(name);
            foreach (var process in processes)
            {
                try { process.Kill(entireProcessTree: true); }
                finally { process.Dispose(); }
            }
            return Result(true, processes.Length == 0 ? "실행 중인 프로세스가 없습니다." : $"{processes.Length}개 프로세스 종료 요청" );
        }

        private static JObject Result(bool ok, string message) => new() { ["ok"] = ok, ["message"] = message };
    }
}
