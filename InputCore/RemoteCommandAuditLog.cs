using System.Text;

namespace Comote.Input;

/// <summary>
/// Append-only audit trail for remote fleet commands, written on both the
/// Manager (intent) and the Host (execution). Principle 6 requires dangerous
/// tasks to be auditable.
/// </summary>
/// <remarks>
/// Entries never contain access keys, PSKs, file contents, or process output —
/// only the metadata needed to answer "who asked this machine to do what, and
/// what happened." The log lives under the per-user LocalApplicationData root,
/// which NTFS already scopes to the interactive user, and rotates by size so a
/// flood of commands cannot grow it without bound or fill the disk.
/// </remarks>
public sealed class RemoteCommandAuditLog
{
    public const long MaximumFileBytes = 5L * 1024 * 1024;
    public const int RetainedFiles = 5;

    private static readonly char[] ForbiddenControlCharacters =
        ['\r', '\n', '\t', '\0'];

    private readonly object _gate = new();
    private readonly string _logPath;
    private readonly string _role;

    private RemoteCommandAuditLog(string logPath, string role)
    {
        _logPath = logPath;
        _role = role;
    }

    /// <summary>
    /// Opens (creating if needed) the audit log for a role such as "manager" or
    /// "host" under <c>%LOCALAPPDATA%\Comote\audit</c>.
    /// </summary>
    public static RemoteCommandAuditLog Open(string role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        var normalizedRole = Sanitize(role);
        if (normalizedRole.Length == 0 ||
            normalizedRole.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException(
                "The audit role must be a safe file-name segment.",
                nameof(role));
        }

        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException(
                "The current account has no LocalApplicationData directory.");
        }

        var directory = Path.Combine(localAppData, "Comote", "audit");
        Directory.CreateDirectory(directory);
        var logPath = Path.Combine(
            directory,
            $"remote-commands-{normalizedRole}.log");
        return new RemoteCommandAuditLog(logPath, normalizedRole);
    }

    /// <summary>
    /// Writes one audit line. Never throws for logging problems — an unwritable
    /// audit log must not take down the command path — but the caller decides
    /// the command outcome separately.
    /// </summary>
    public void Write(
        string action,
        string commandId,
        string peer,
        string result,
        string detail)
    {
        var line = string.Join(
            " | ",
            RemoteCommandProtocol.UnixTimeMilliseconds().ToString(),
            _role,
            Sanitize(action),
            Sanitize(commandId),
            Sanitize(peer),
            Sanitize(result),
            Sanitize(detail));

        try
        {
            lock (_gate)
            {
                RotateIfNeeded();
                File.AppendAllText(_logPath, line + "\n", new UTF8Encoding(false));
            }
        }
        catch (Exception ex) when (
            ex is IOException or
            UnauthorizedAccessException or
            System.Security.SecurityException)
        {
        }
    }

    private void RotateIfNeeded()
    {
        long length;
        try
        {
            var info = new FileInfo(_logPath);
            if (!info.Exists || info.Length < MaximumFileBytes)
            {
                return;
            }
            length = info.Length;
        }
        catch (IOException)
        {
            return;
        }

        if (length < MaximumFileBytes)
        {
            return;
        }

        var oldest = $"{_logPath}.{RetainedFiles - 1}";
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (var index = RetainedFiles - 2; index >= 1; index--)
        {
            var source = $"{_logPath}.{index}";
            if (File.Exists(source))
            {
                File.Move(source, $"{_logPath}.{index + 1}", overwrite: true);
            }
        }

        File.Move(_logPath, $"{_logPath}.1", overwrite: true);
    }

    private static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "-";
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(
                Array.IndexOf(ForbiddenControlCharacters, character) >= 0 ||
                char.IsControl(character)
                    ? ' '
                    : character);
        }

        var sanitized = builder.ToString().Trim();
        if (sanitized.Length > 512)
        {
            sanitized = sanitized[..512];
        }

        return sanitized.Length == 0 ? "-" : sanitized;
    }
}
