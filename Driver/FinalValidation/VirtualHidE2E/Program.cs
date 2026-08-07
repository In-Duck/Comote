using Comote.Input;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Comote.VirtualHidE2E;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var parsed = ParseArguments(args);
            var config = AtomicJson.Read<ValidationConfig>(parsed.ConfigPath);
            if (parsed.CleanupOnly)
            {
                _ = Phase2EnvironmentGuard.Assert(
                    config,
                    parsed.Acknowledgement);
                return RunEmergencyCleanup(config) ? 0 : 5;
            }

            return RunValidation(
                config,
                parsed.Acknowledgement);
        }
        catch
        {
            return 2;
        }
    }

    private static int RunValidation(
        ValidationConfig config,
        string acknowledgement)
    {
        var report = new ValidationReport
        {
            RunId = config.RunId,
            StartedUtc = DateTime.UtcNow,
            RecoverySnapshotName = config.RecoverySnapshotName,
        };
        RawInputValidationForm? form = null;
        NativeMethods.Point originalCursor = default;
        var cursorCaptured = false;
        var exitCode = 1;

        try
        {
            report.Environment = Phase2EnvironmentGuard.Assert(
                config,
                acknowledgement);
            if (!NativeMethods.GetCursorPos(out originalCursor))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "GetCursorPos failed.");
            }
            cursorCaptured = true;
            AtomicJson.Write(
                config.RecoveryStatePath,
                new RecoveryState
                {
                    RunId = config.RunId,
                    CapturedUtc = DateTime.UtcNow,
                    CursorX = originalCursor.X,
                    CursorY = originalCursor.Y,
                });

            ApplicationConfiguration.Initialize();
            var activeForm = new RawInputValidationForm();
            form = activeForm;
            activeForm.Shown += async (_, _) =>
            {
                try
                {
                    var runner = new ValidationRunner(
                        config,
                        report,
                        activeForm);
                    await runner.RunAsync().ConfigureAwait(true);
                    report.Status = "passed";
                    exitCode = 0;
                }
                catch (Exception ex)
                {
                    report.Status = "failed";
                    report.FailureType = ex.GetType().Name;
                    report.FailureMessage = ex.Message;
                    exitCode = 3;
                }
                finally
                {
                    activeForm.Close();
                }
            };
            Application.Run(activeForm);
        }
        catch (Exception ex)
        {
            report.Status = "failed";
            report.FailureType = ex.GetType().Name;
            report.FailureMessage = ex.Message;
            exitCode = 4;
        }
        finally
        {
            report.Cleanup.ReleaseAllAttempted = true;
            report.Cleanup.ReleaseAllSucceeded = TryReleaseAll();

            report.Cleanup.CursorRestoreAttempted = cursorCaptured;
            if (cursorCaptured)
            {
                report.Cleanup.CursorRestoreSucceeded =
                    NativeMethods.SetCursorPos(
                        originalCursor.X,
                        originalCursor.Y);
            }

            if (form is not null)
            {
                report.RawInputEvents.AddRange(form.Snapshot());
                report.Cleanup.RawInputRegistrationRemoved =
                    form.RemoveRegistration();
                form.Dispose();
            }

            report.CompletedUtc = DateTime.UtcNow;
            if (report.Status == "passed" &&
                (!report.Cleanup.ReleaseAllSucceeded ||
                 !report.Cleanup.CursorRestoreSucceeded ||
                 !report.Cleanup.RawInputRegistrationRemoved))
            {
                report.Status = "failed";
                report.FailureType = "CleanupFailure";
                report.FailureMessage =
                    "One or more mandatory cleanup operations failed.";
                exitCode = 6;
            }

            UpdateRecoveryState(
                config,
                report.Cleanup);
            AtomicJson.Write(config.ReportPath, report);
        }

        return exitCode;
    }

    private static bool RunEmergencyCleanup(ValidationConfig config)
    {
        var releaseSucceeded = TryReleaseAll();
        var cursorSucceeded = false;
        var cursorAttempted = false;
        if (File.Exists(config.RecoveryStatePath))
        {
            var state =
                AtomicJson.Read<RecoveryState>(config.RecoveryStatePath);
            if (!state.RunId.Equals(
                    config.RunId,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Recovery state run ID did not match the configuration.");
            }
            cursorAttempted = true;
            cursorSucceeded = NativeMethods.SetCursorPos(
                state.CursorX,
                state.CursorY);
            state.ReleaseAllAttempted = true;
            state.CursorRestoreAttempted = true;
            state.CursorRestoreSucceeded = cursorSucceeded;
            state.CleanupCompletedUtc = DateTime.UtcNow;
            AtomicJson.Write(config.RecoveryStatePath, state);
        }

        return releaseSucceeded &&
            (!cursorAttempted || cursorSucceeded);
    }

    private static bool TryReleaseAll()
    {
        try
        {
            using var broker = new InputBrokerClient();
            return broker.ReleaseAll().IsSuccess;
        }
        catch
        {
            return false;
        }
    }

    private static void UpdateRecoveryState(
        ValidationConfig config,
        CleanupEvidence cleanup)
    {
        if (!File.Exists(config.RecoveryStatePath))
        {
            return;
        }

        try
        {
            var state =
                AtomicJson.Read<RecoveryState>(config.RecoveryStatePath);
            if (!state.RunId.Equals(
                    config.RunId,
                    StringComparison.Ordinal))
            {
                return;
            }
            state.ReleaseAllAttempted = cleanup.ReleaseAllAttempted;
            state.CursorRestoreAttempted =
                cleanup.CursorRestoreAttempted;
            state.CursorRestoreSucceeded =
                cleanup.CursorRestoreSucceeded;
            state.CleanupCompletedUtc = DateTime.UtcNow;
            AtomicJson.Write(config.RecoveryStatePath, state);
        }
        catch
        {
            // The primary validation report still records cleanup state.
        }
    }

    private static ParsedArguments ParseArguments(string[] args)
    {
        string? config = null;
        string? acknowledgement = null;
        var cleanupOnly = false;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--config" when index + 1 < args.Length:
                    config = args[++index];
                    break;
                case "--acknowledge-disposable-vm"
                    when index + 1 < args.Length:
                    acknowledgement = args[++index];
                    break;
                case "--cleanup-only":
                    cleanupOnly = true;
                    break;
                default:
                    throw new ArgumentException(
                        $"Unknown or incomplete argument: {args[index]}");
            }
        }

        if (string.IsNullOrWhiteSpace(config) ||
            string.IsNullOrWhiteSpace(acknowledgement))
        {
            throw new ArgumentException(
                "--config and --acknowledge-disposable-vm are required.");
        }
        return new ParsedArguments(
            Path.GetFullPath(config),
            acknowledgement,
            cleanupOnly);
    }

    private sealed record ParsedArguments(
        string ConfigPath,
        string Acknowledgement,
        bool CleanupOnly);
}
