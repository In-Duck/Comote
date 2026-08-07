namespace Host
{
    internal readonly record struct ClipboardMonitoringTransition(
        long Epoch,
        bool Enabled);

    internal static class ClipboardMonitoringTransitionPolicy
    {
        public static bool IsCurrent(
            long currentEpoch,
            bool currentEnabled,
            ClipboardMonitoringTransition transition) =>
            transition.Epoch == currentEpoch &&
            transition.Enabled == currentEnabled;
    }
}