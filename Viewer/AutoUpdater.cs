using System.Threading.Tasks;

namespace Viewer
{
    public static class AutoUpdater
    {
        public static Task CheckAndApplyUpdate(bool isService = false)
        {
            _ = isService;
            Console.WriteLine(
                "[Updater] Automatic updates are disabled until signed " +
                "release metadata is configured.");
            return Task.CompletedTask;
        }
    }
}