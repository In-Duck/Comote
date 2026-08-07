using System.Diagnostics;
using Comote.Input;
using Newtonsoft.Json.Linq;

namespace Host
{
    /// <summary>
    /// Performs the OS-facing side effect of a system command. Injectable so the
    /// pure self-tests can exercise the command path without rebooting a
    /// machine.
    /// </summary>
    internal interface ISystemCommandExecutor
    {
        void Reboot(int delaySeconds);
        void Shutdown(int delaySeconds);
        void RestartHost();
        void CancelShutdown();
    }

    internal sealed class WindowsSystemCommandExecutor : ISystemCommandExecutor
    {
        private readonly Func<bool> _restartHost;

        public WindowsSystemCommandExecutor(Func<bool> restartHost)
        {
            _restartHost = restartHost;
        }

        public void Reboot(int delaySeconds) =>
            RunShutdown("/r", delaySeconds);

        public void Shutdown(int delaySeconds) =>
            RunShutdown("/s", delaySeconds);

        public void RestartHost()
        {
            if (!_restartHost())
            {
                throw new InvalidOperationException(
                    "Host 재실행을 시작하지 못했습니다.");
            }
        }

        public void CancelShutdown()
        {
            // A non-zero exit here usually means "no shutdown was pending"
            // (error 1116), which is an acceptable outcome for a cancel, so the
            // exit code is not treated as a failure.
            RunShutdownExe(["/a"], requireSuccess: false);
        }

        private static void RunShutdown(string mode, int delaySeconds) =>
            RunShutdownExe(
                [
                    mode,
                    "/t",
                    delaySeconds.ToString(),
                    "/c",
                    "Comote 원격 명령으로 예약되었습니다.",
                ],
                requireSuccess: true);

        private static void RunShutdownExe(
            string[] arguments,
            bool requireSuccess)
        {
            var info = new ProcessStartInfo
            {
                FileName = "shutdown.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            foreach (var argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }

            using var process = Process.Start(info)
                ?? throw new InvalidOperationException(
                    "shutdown.exe를 시작할 수 없습니다.");
            // Drain both pipes concurrently so a full buffer on one stream can
            // never block the read of the other (pipe-deadlock anti-pattern).
            var errorTask = process.StandardError.ReadToEndAsync();
            var outputTask = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(10_000))
            {
                try { process.Kill(); } catch { }
                throw new InvalidOperationException(
                    "shutdown.exe가 응답하지 않습니다.");
            }
            var error = errorTask.GetAwaiter().GetResult();
            _ = outputTask.GetAwaiter().GetResult();
            if (requireSuccess && process.ExitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(error)
                    ? $"exit {process.ExitCode}"
                    : error.Trim();
                throw new InvalidOperationException(
                    "shutdown.exe 실패: " + detail);
            }
        }
    }

    internal sealed class RemoteTaskExecutor
    {
        private readonly ISystemCommandExecutor _system;
        private readonly RemoteCommandDeduplicator _deduplicator;
        private readonly RemoteCommandAuditLog? _audit;

        public RemoteTaskExecutor(
            ISystemCommandExecutor system,
            RemoteCommandDeduplicator deduplicator,
            RemoteCommandAuditLog? audit = null)
        {
            _system = system;
            _deduplicator = deduplicator;
            _audit = audit;
        }

        public Task<JObject> ExecuteAsync(JObject command) =>
            ExecuteAsync(command, RemoteCommandProtocol.UnixTimeMilliseconds());

        public Task<JObject> ExecuteAsync(JObject command, long nowUnixMs)
        {
            var action = command.Value<string>("action") ?? "";
            try
            {
                if (RemoteCommandProtocol.IsSystemCommand(action))
                {
                    return Task.FromResult(
                        ExecuteSystemCommand(command, action, nowUnixMs));
                }

                var value = command.Value<string>("value")?.Trim() ?? "";
                return Task.FromResult(action switch
                {
                    RemoteCommandProtocol.ActionRun =>
                        Run(command.Value<string>("folder") ?? "desktop", value),
                    RemoteCommandProtocol.ActionTerminate => Terminate(value),
                    _ => Result(false, "알 수 없는 작업입니다."),
                });
            }
            catch (Exception ex)
            {
                return Task.FromResult(Result(false, ex.Message));
            }
        }

        private JObject ExecuteSystemCommand(
            JObject command,
            string action,
            long nowUnixMs)
        {
            var commandId = command.Value<string>("commandId") ?? "";
            var issuedAt = command.Value<long?>("issuedAtUnixMs") ?? -1;
            var delaySeconds = (int)(command.Value<long?>("delaySeconds") ?? 0);

            if (!RemoteCommandProtocol.IsWithinLifetime(issuedAt, nowUnixMs))
            {
                _audit?.Write(action, commandId, "manager", "rejected", "expired");
                return Result(false, "명령이 만료되었습니다.");
            }
            if (!_deduplicator.TryAccept(commandId))
            {
                _audit?.Write(
                    action, commandId, "manager", "ignored", "duplicate");
                return Result(false, "이미 처리된 명령입니다.");
            }

            try
            {
                var message = action switch
                {
                    RemoteCommandProtocol.ActionReboot =>
                        RunSystem(
                            () => _system.Reboot(delaySeconds),
                            $"{delaySeconds}초 후 재부팅 예약됨"),
                    RemoteCommandProtocol.ActionShutdown =>
                        RunSystem(
                            () => _system.Shutdown(delaySeconds),
                            $"{delaySeconds}초 후 종료 예약됨"),
                    RemoteCommandProtocol.ActionRestartHost =>
                        RunSystem(_system.RestartHost, "Host 재실행 요청됨"),
                    RemoteCommandProtocol.ActionCancelShutdown =>
                        RunSystem(
                            _system.CancelShutdown, "예약된 재부팅·종료 취소됨"),
                    _ => null,
                };
                if (message is null)
                {
                    return Result(false, "알 수 없는 시스템 명령입니다.");
                }

                _audit?.Write(action, commandId, "manager", "executed", action);
                return Result(true, message);
            }
            catch (Exception ex)
            {
                _audit?.Write(action, commandId, "manager", "failed", ex.Message);
                return Result(false, ex.Message);
            }
        }

        private static string RunSystem(Action effect, string message)
        {
            effect();
            return message;
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
