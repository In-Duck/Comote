namespace Host
{
    public enum InputBackendMode
    {
        SendInput = 1,
        VirtualHid = 2,
    }

    public enum InputDispatchCode
    {
        Accepted = 0,
        InvalidMessage = 1,
        Unsupported = 2,
        BackendUnavailable = 3,
        BackendRejected = 4,
    }

    public readonly record struct InputDispatchResult(
        InputDispatchCode Code,
        string? Detail = null)
    {
        public bool IsAccepted => Code == InputDispatchCode.Accepted;

        public static InputDispatchResult Success() =>
            new(InputDispatchCode.Accepted);

        public static InputDispatchResult Invalid(string detail) =>
            new(InputDispatchCode.InvalidMessage, detail);

        public static InputDispatchResult NotSupported(string detail) =>
            new(InputDispatchCode.Unsupported, detail);

        public static InputDispatchResult Unavailable(string detail) =>
            new(InputDispatchCode.BackendUnavailable, detail);

        public static InputDispatchResult Rejected(string detail) =>
            new(InputDispatchCode.BackendRejected, detail);
    }

    public sealed record InputBackendStatus(
        InputBackendMode Mode,
        string Name,
        bool IsAvailable,
        string? Detail = null);

    public interface IInputBackend : IDisposable
    {
        InputBackendMode Mode { get; }

        string Name { get; }

        InputBackendStatus GetStatus();

        void UpdateScreenBounds(
            int left,
            int top,
            int width,
            int height);

        InputDispatchResult ProcessMessage(ReadOnlySpan<byte> data);

        void ReleaseAllInputs();
    }
}
