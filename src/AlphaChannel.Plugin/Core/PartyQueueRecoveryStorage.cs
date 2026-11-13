using System.Text.Json;

namespace AlphaChannel.Plugin.Core;

// Only a detached, small queue snapshot is written. Never serialize the full
// plugin configuration (or its secret vault) on a playback/framework tick.
internal sealed class PartyQueueRecoveryStorage(string directory)
{
    private readonly object gate = new();
    private readonly Dictionary<string, PartyQueueRecovery?> latest = new(StringComparer.OrdinalIgnoreCase);
    private Task pending = Task.CompletedTask;
    internal Task Completion { get { lock (gate) return pending; } }
    private string FilePath(string account) => Path.Combine(directory, Guid.Parse(account).ToString("D") + ".json");

    internal PartyQueueRecovery? Read(string account, PartyQueueRecovery? legacy)
    {
        lock (gate)
        try
        {
            if (latest.TryGetValue(account, out var current)) return current;
            var path = FilePath(account);
            if (File.Exists(path))
            {
                var saved = JsonSerializer.Deserialize<PartyQueueRecovery?>(File.ReadAllText(path));
                return latest[account] = string.Equals(saved?.AccountId, account, StringComparison.OrdinalIgnoreCase) ? saved : null;
            }
            if (string.Equals(legacy?.AccountId, account, StringComparison.OrdinalIgnoreCase)) { Save(account, legacy); return legacy; }
        }
        catch { /* A damaged recovery must never prevent starting a party. */ }
        return null;
    }

    internal void Save(string account, PartyQueueRecovery? snapshot)
    {
        var path = FilePath(account);
        lock (gate)
        {
            latest[account] = snapshot;
            pending = pending.ContinueWith(_ => WriteAsync(path, snapshot),
                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        }
    }

    private static async Task WriteAsync(string path, PartyQueueRecovery? snapshot)
    {
        // A null tombstone also prevents an old configuration backup resurfacing.
        var json = JsonSerializer.Serialize(snapshot);
        var temporary = path + ".tmp";
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(temporary, json).ConfigureAwait(false);
                File.Move(temporary, path, overwrite: true);
                return;
            }
            catch when (attempt < 2) { await Task.Delay(250).ConfigureAwait(false); }
            catch { /* The previous atomic snapshot remains available. */ }
        }
    }
}
