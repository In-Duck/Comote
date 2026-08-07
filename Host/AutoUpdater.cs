using System.Threading.Tasks;

namespace Host
{
    public static class AutoUpdater
    {
        public static Task CheckAndApplyUpdate(bool isService)
        {
            _ = isService;
            Console.WriteLine(
                "[Updater] Automatic updates are disabled until signed " +
                "release metadata is configured.");
            return Task.CompletedTask;
        }
    }
}