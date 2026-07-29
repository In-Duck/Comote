namespace Host
{
    public enum InputBackendMode
    {
        SendInput = 1,
        VirtualHid = 2,
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

        void UpdateScreenSize(int width, int height);

        void UpdateScreenBounds(int left, int top, int width, int height);

        void ProcessMessage(byte[] data);

        void ReleaseAllInputs();
    }
}
