namespace Host
{
    internal static class ClientAutoUpdater
    {
        public static Task<bool> TryStageUpdateAsync(
            string manifestUrl,
            string[] restartArguments,
            CancellationToken cancellationToken = default)
        {
            _ = manifestUrl;
            _ = restartArguments;
            _ = cancellationToken;
            Console.WriteLine(
                "[Updater] Remote client updates are disabled until " +
                "signed release metadata is configured.");
            return Task.FromResult(false);
        }
    }
}