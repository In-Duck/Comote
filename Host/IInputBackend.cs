namespace Host
{
    public enum InputBackendMode
    {
        SendInput = 1,
        VirtualHidMode2 = 2,
        VirtualHidMode3 = 3,
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

        void ProcessMessage(byte[] data);

        void ReleaseAllInputs();
    }
}
