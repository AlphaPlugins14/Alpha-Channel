namespace AlphaChannel.Plugin.Video;

// Keep installed binaries replaceable; each running helper owns an isolated copy.
internal sealed class HelperRuntimeLease : IDisposable
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AlphaChannel", "Helpers"));
    private FileStream? marker;
    private int disposed;
    internal string DirectoryPath { get; }
    internal HelperRuntimeLease(string source, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        Scavenge();
        DirectoryPath = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            marker = new FileStream(Path.Combine(DirectoryPath, ".lease"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                cancellation.ThrowIfCancellationRequested();
                var destination = Path.Combine(DirectoryPath, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination);
            }
            cancellation.ThrowIfCancellationRequested();
        }
        catch { Dispose(); throw; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        marker?.Dispose();
        if (TryDelete(DirectoryPath)) return;
        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 12; attempt++)
            {
                await Task.Delay(500).ConfigureAwait(false);
                if (TryDelete(DirectoryPath)) return;
            }
            AepLog.Warning("[HELPER] Temporary runtime remains locked; cleanup will retry on a later launch.");
        });
    }

    private static bool IsOwnedDirectory(string directory)
    {
        var full = Path.GetFullPath(directory);
        return string.Equals(Path.GetDirectoryName(full), Root, StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParseExact(Path.GetFileName(full), "N", out _) &&
            (!Directory.Exists(full) || (File.GetAttributes(full) & FileAttributes.ReparsePoint) == 0);
    }

    private static bool TryDelete(string directory)
    {
        try
        {
            // Check the resolved target before every recursive deletion.
            if (!IsOwnedDirectory(directory)) return false;
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            return true;
        }
        catch (IOException) { PreserveCleanupMarker(directory); return false; }
        catch (UnauthorizedAccessException) { PreserveCleanupMarker(directory); return false; }
    }

    private static void PreserveCleanupMarker(string directory)
    {
        // Recursive deletion can remove the marker before encountering a locked
        // DLL. Leave an inactive marker so a later launch can still reclaim it.
        try
        {
            if (IsOwnedDirectory(directory) && Directory.Exists(directory) && !File.Exists(Path.Combine(directory, ".lease")))
                using (new FileStream(Path.Combine(directory, ".lease"), FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void Scavenge()
    {
        try
        {
            if (!Directory.Exists(Root)) return;
            // Only our GUID directories, and only inactive leases at least a day
            // old. A second game instance keeps its marker exclusively open.
            foreach (var directory in Directory.EnumerateDirectories(Root).Take(64))
            {
                if (!IsOwnedDirectory(directory) || Directory.GetLastWriteTimeUtc(directory) > DateTime.UtcNow.AddDays(-1)) continue;
                var lease = Path.Combine(directory, ".lease");
                try
                {
                    using (new FileStream(lease, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                    TryDelete(directory);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
