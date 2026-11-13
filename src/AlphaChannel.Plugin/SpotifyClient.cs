using System.Diagnostics;
using System.Text.Json;

namespace AlphaChannel.Plugin;

internal sealed record SpotifySnapshot(string Status, DateTime SampledAtUtc, Dictionary<string, string> Fields)
{
    public string? ArtworkBase64 { get; init; }
}

internal sealed class SpotifyClient : IDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly object gate = new();
    private readonly string helper;
    private Task? request;
    private int pendingCommands;
    internal bool CommandPending => Volatile.Read(ref pendingCommands) > 0;
    private DateTime nextPoll;
    private bool disposed;
    private SpotifySnapshot snapshot = new("Waiting for first Spotify sample", default, new());
    private SpotifySnapshot? lastCommandSnapshot;
    private long completedCommands;
    internal long CompletedCommands => Interlocked.Read(ref completedCommands);
    internal SpotifySnapshot? LastCommandSnapshot => Volatile.Read(ref lastCommandSnapshot);
    internal SpotifySnapshot Snapshot => Volatile.Read(ref snapshot);
    internal bool Loading => request is { IsCompleted: false };
    internal SpotifyClient(string helperPath) => helper = helperPath;

    // Polled while the Spotify page or its TV session is active. No background listener is
    // installed and no server/account/Spotify credentials are accessed.
    internal void Tick(bool force = false)
    {
        lock (gate)
        {
            if (disposed || Loading || !force && DateTime.UtcNow < nextPoll) return;
            nextPoll = DateTime.UtcNow.AddSeconds(3);
            request = Task.Run(() => ReadAsync("read", null));
        }
    }

    internal bool SendCommand(string command, double? positionSeconds = null, string? spotifyUri = null, bool queue = false)
    {
        lock (gate)
        {
            if (disposed || CommandPending && !queue) return false;
            if (!Snapshot.Fields.TryGetValue("Session / Selected application ID", out var selected) && command != "play-link") return false;
            if (command == "play-link" && !SpotifyLinks.TryParse(spotifyUri, out _)) return false;
            Interlocked.Increment(ref pendingCommands);
            var previous = request;
            request = Task.Run(async () =>
            {
                try
                {
                    if (previous is not null) await previous.ConfigureAwait(false);
                    if (!stop.IsCancellationRequested) await ReadAsync(command, selected, positionSeconds, spotifyUri).ConfigureAwait(false);
                }
                finally { Interlocked.Decrement(ref pendingCommands); }
            });
            return true;
        }
    }

    private async Task ReadAsync(string command, string? selected, double? positionSeconds = null, string? spotifyUri = null)
    {
        try
        {
            if (!File.Exists(helper)) throw new FileNotFoundException("Spotify player helper is missing. Rebuild or reinstall the complete plugin package.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(command == "play-link" ? 30 : 12));
            var start = new ProcessStartInfo(helper)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(helper)!
            };
            if (command != "read") { start.ArgumentList.Add(command); start.ArgumentList.Add(selected ?? ""); }
            if (spotifyUri is not null) start.ArgumentList.Add(spotifyUri);
            if (positionSeconds is { } seconds && double.IsFinite(seconds))
                start.ArgumentList.Add(seconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var process = Process.Start(start) ?? throw new IOException("Could not connect to the Spotify player helper.");
            // A one-shot helper must never outlive an unload or stalled WinRT call.
            using var cancel = timeout.Token.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception) { }
            });
            async Task ReadOutput()
            {
                var total = 0;
                while (await process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false) is { } json)
                {
                    total += json.Length;
                    if (json.Length > 1024 * 1024 || total > 8 * 1024 * 1024) throw new IOException("Spotify response was too large.");
                    var result = JsonSerializer.Deserialize<SpotifySnapshot>(json)
                        ?? throw new IOException("Spotify player helper returned no data.");
                    if (result.Fields is null || string.IsNullOrWhiteSpace(result.Status)) throw new IOException("Spotify player helper returned invalid data.");
                    if (!stop.IsCancellationRequested) Volatile.Write(ref snapshot, result);
                }
            }
            var output = ReadOutput();
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                await output.ConfigureAwait(false);
                await errors.ConfigureAwait(false);
                if (process.ExitCode != 0) throw new IOException("Spotify player helper exited with code " + process.ExitCode + ".");

            }
            finally
            {
                timeout.Cancel();
                try { await output.ConfigureAwait(false); } catch (Exception) { }
                try { await errors.ConfigureAwait(false); } catch (Exception) { }
            }
        }
        catch (Exception e)
        {
            if (!stop.IsCancellationRequested)
            {
                Volatile.Write(ref snapshot, new(e is OperationCanceledException ? "Spotify connection timed out. Try Refresh." : e.Message, DateTime.UtcNow, new()));
            }
        }
        finally
        {
            if (command != "read")
            {
                Volatile.Write(ref lastCommandSnapshot, Snapshot);
                Interlocked.Increment(ref completedCommands);
            }
            lock (gate) { nextPoll = DateTime.UtcNow.AddSeconds(2); }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            stop.Cancel();
            if (request is { } pending) _ = pending.ContinueWith(_ => stop.Dispose(), TaskScheduler.Default);
            else stop.Dispose();
        }
    }
}
