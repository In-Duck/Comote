namespace Comote.Input;

/// <summary>
/// Single source of truth for remote fleet-command rules shared by the
/// Manager (sender) and the Host (receiver).
/// </summary>
/// <remarks>
/// The action allowlist is enforced independently at several hops, so keeping
/// the rules in one place stops those hops from drifting apart. This type has
/// no JSON dependency on purpose; each side extracts primitives from its own
/// envelope and calls in here for the decision, which also lets the pure
/// self-tests exercise the rules without a transport.
/// </remarks>
public static class RemoteCommandProtocol
{
    // Tier A app tasks — low risk, gated by the remote-tasks opt-in.
    public const string ActionRun = "run";
    public const string ActionTerminate = "terminate";

    // Tier A system commands — interrupt or restart the machine, gated by the
    // separate system-commands opt-in and the guard fields below.
    public const string ActionReboot = "reboot";
    public const string ActionShutdown = "shutdown";
    public const string ActionRestartHost = "restart-host";
    public const string ActionCancelShutdown = "cancel-shutdown";

    public const int NameMaximumLength = 128;
    public const int ValueMaximumLength = 1024;
    public const int DelayMaximumSeconds = 600;

    /// <summary>How long a system command stays valid after it is issued.</summary>
    public static readonly TimeSpan SystemCommandLifetime =
        TimeSpan.FromSeconds(60);

    /// <summary>
    /// How many recently seen command IDs the deduplicator retains. Sized far
    /// above any realistic fleet command volume within the (symmetric) lifetime
    /// window so an ID cannot be evicted while it is still replayable.
    /// </summary>
    public const int DeduplicationHistory = 4096;

    private static readonly string[] AppTaskProperties =
        ["type", "action", "name", "folder", "value"];

    private static readonly string[] SystemCommandProperties =
        ["type", "action", "name", "commandId", "issuedAtUnixMs", "delaySeconds"];

    public static bool IsAppTask(string? action) =>
        action is ActionRun or ActionTerminate;

    public static bool IsSystemCommand(string? action) =>
        action is ActionReboot or ActionShutdown or ActionRestartHost
            or ActionCancelShutdown;

    public static bool IsKnownAction(string? action) =>
        IsAppTask(action) || IsSystemCommand(action);

    /// <summary>Exact property-name set an envelope for this action may carry.</summary>
    public static IReadOnlyList<string> AllowedProperties(string action) =>
        IsSystemCommand(action) ? SystemCommandProperties : AppTaskProperties;

    public static bool IsValidName(string? name) =>
        name is not null && name.Length <= NameMaximumLength;

    public static bool IsValidAppTaskValue(string? value) =>
        value is not null && value.Length is > 0 and <= ValueMaximumLength;

    public static bool IsValidFolder(string? folder) =>
        folder is "" or "desktop" or "custom";

    public static bool IsValidDelaySeconds(long delaySeconds) =>
        delaySeconds is >= 0 and <= DelayMaximumSeconds;

    /// <summary>
    /// A command ID must be a lowercase 32-hex-digit token so it is a stable,
    /// case-insensitive-safe deduplication key with no delimiters to normalize.
    /// </summary>
    public static bool IsValidCommandId(string? commandId)
    {
        if (commandId is null || commandId.Length != 32)
        {
            return false;
        }

        foreach (var character in commandId)
        {
            if (character is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }

    public static string NewCommandId() =>
        Guid.NewGuid().ToString("n");

    public static long UnixTimeMilliseconds() =>
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>
    /// True when a system command issued at <paramref name="issuedAtUnixMs"/> is
    /// within its lifetime relative to <paramref name="nowUnixMs"/>. The window
    /// is symmetric so that clock skew between the Manager and the Host — up to
    /// the lifetime in either direction — does not wrongly reject a fresh
    /// command. Replay is bounded separately by <see cref="RemoteCommandDeduplicator"/>.
    /// </summary>
    public static bool IsWithinLifetime(long issuedAtUnixMs, long nowUnixMs)
    {
        var age = nowUnixMs - issuedAtUnixMs;
        var lifetimeMs = (long)SystemCommandLifetime.TotalMilliseconds;
        return age >= -lifetimeMs && age <= lifetimeMs;
    }
}

/// <summary>
/// Thread-safe recent-command-ID tracker. A duplicate or replayed system
/// command is accepted at most once, so a re-delivered reboot does not fire
/// twice.
/// </summary>
public sealed class RemoteCommandDeduplicator
{
    private readonly object _gate = new();
    private readonly Queue<string> _order = new();
    private readonly HashSet<string> _seen =
        new(StringComparer.Ordinal);
    private readonly int _capacity;

    public RemoteCommandDeduplicator(
        int capacity = RemoteCommandProtocol.DeduplicationHistory)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
    }

    /// <summary>
    /// Records the command ID and returns true only the first time it is seen.
    /// A malformed ID is never accepted.
    /// </summary>
    public bool TryAccept(string commandId)
    {
        if (!RemoteCommandProtocol.IsValidCommandId(commandId))
        {
            return false;
        }

        lock (_gate)
        {
            if (!_seen.Add(commandId))
            {
                return false;
            }

            _order.Enqueue(commandId);
            while (_order.Count > _capacity)
            {
                _seen.Remove(_order.Dequeue());
            }

            return true;
        }
    }
}
