namespace AlphaChannel.Plugin.Video;

internal static class ResourceCleanup
{
    // Partial construction and shutdown must release the remaining resources
    // even if one native wrapper fails to dispose.
    internal static void DisposeAll(params IDisposable?[] resources)
    {
        foreach (var resource in resources)
        {
            try { resource?.Dispose(); }
            catch (Exception) { AepLog.Warning("[Video] Resource cleanup failed."); }
        }
    }
}
