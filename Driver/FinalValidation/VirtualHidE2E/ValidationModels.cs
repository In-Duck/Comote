using System.Text.Json.Serialization;

namespace Comote.VirtualHidE2E;

internal sealed class ValidationConfig
{
    public int SchemaVersion { get; set; }

    public string RunId { get; set; } = "";

    public string RecoverySnapshotName { get; set; } = "";

    public string[] ExpectedKeyboardInstanceIds { get; set; } = [];

    public string[] ExpectedMouseInstanceIds { get; set; } = [];

    public string ReportPath { get; set; } = "";

    public string RecoveryStatePath { get; set; } = "";
}

internal sealed class RecoveryState
{
    public int SchemaVersion { get; set; } = 1;

    public string RunId { get; set; } = "";

    public DateTime CapturedUtc { get; set; }

    public int CursorX { get; set; }

    public int CursorY { get; set; }

    public bool ReleaseAllAttempted { get; set; }

    public bool CursorRestoreAttempted { get; set; }

    public bool CursorRestoreSucceeded { get; set; }

    public DateTime? CleanupCompletedUtc { get; set; }
}

internal sealed class ValidationReport
{
    public int SchemaVersion { get; set; } = 1;

    public string RunId { get; set; } = "";

    public string Status { get; set; } = "running";

    public DateTime StartedUtc { get; set; }

    public DateTime? CompletedUtc { get; set; }

    public string RecoverySnapshotName { get; set; } = "";

    public EnvironmentEvidence Environment { get; set; } = new();

    public List<TestEvidence> Tests { get; set; } = [];

    public List<RawInputEvidence> RawInputEvents { get; set; } = [];

    public CleanupEvidence Cleanup { get; set; } = new();

    public string? FailureType { get; set; }

    public string? FailureMessage { get; set; }
}

internal sealed class EnvironmentEvidence
{
    public string SystemManufacturer { get; set; } = "";

    public string SystemProductName { get; set; } = "";

    public string ProductName { get; set; } = "";

    public int OsBuild { get; set; }

    public bool Is64BitOperatingSystem { get; set; }

    public string UserName { get; set; } = "";

    public bool UserInteractive { get; set; }

    public string[] ExpectedKeyboardInstanceIds { get; set; } = [];

    public string[] ExpectedMouseInstanceIds { get; set; } = [];
}

internal sealed class TestEvidence
{
    public string Name { get; set; } = "";

    public bool Passed { get; set; }

    public string Detail { get; set; } = "";

    public DateTime StartedUtc { get; set; }

    public DateTime CompletedUtc { get; set; }
}

internal sealed class CleanupEvidence
{
    public bool ReleaseAllAttempted { get; set; }

    public bool ReleaseAllSucceeded { get; set; }

    public bool CursorRestoreAttempted { get; set; }

    public bool CursorRestoreSucceeded { get; set; }

    public bool RawInputRegistrationRemoved { get; set; }
}

internal sealed class RawInputEvidence
{
    public DateTime ObservedUtc { get; set; }

    public string Stage { get; set; } = "";

    public string DevicePath { get; set; } = "";

    public string NormalizedInstanceId { get; set; } = "";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RawInputKind Kind { get; set; }

    public ushort Flags { get; set; }

    public ushort VirtualKey { get; set; }

    public ushort MakeCode { get; set; }

    public bool KeyBreak { get; set; }

    public int DeltaX { get; set; }

    public int DeltaY { get; set; }

    public bool MouseAbsolute { get; set; }

    public ushort MouseButtonFlags { get; set; }

    public short MouseButtonData { get; set; }
}

internal enum RawInputKind
{
    Keyboard,
    Mouse,
}
