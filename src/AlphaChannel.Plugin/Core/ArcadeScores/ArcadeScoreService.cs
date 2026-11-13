using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AlphaChannel.Plugin.ArcadeScores;

internal sealed record ArcadeScoreResult(string Game, string Session, int Round, long Score, long OccurredAt, string Name, long StartingBalance = 0);
internal sealed record ArcadeScoreEntry(string PlayerId, string Name, long Score, int Rank);
internal sealed record ArcadeLeaderboard(string Game, string Period, string PeriodKey, ArcadeScoreEntry[] Entries);

internal sealed class ArcadeScoreService : IDisposable
{
    internal const string WorkerUrl = "https://highscores.example.invalid";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AlphaChannel.ArcadeScores.v1");
    private readonly object gate = new();
    private readonly object storageGate = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken cancellation;
    private readonly HttpClient http;
    private readonly Func<long> clock;
    private readonly string path;
    private readonly Dictionary<string, Pending> pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Peak> peaks = new(StringComparer.Ordinal);
    private readonly HashSet<string> seen = new(StringComparer.Ordinal);
    private readonly Queue<string> seenOrder = new();
    private readonly Dictionary<string, Cache> cache = new(StringComparer.Ordinal);
    private bool dirty, disposed, healthy = true;
    private long nextSave, nextSubmit;
    private long storageRevision;
    private string? currentAccount, currentToken;
    private readonly Task pump;
    internal Task Completion => pump;
    private void MarkDirty() { dirty = true; storageRevision++; }
    private sealed record Pending(string Account, ArcadeScoreResult Result);
    private sealed record Peak(string Account, ArcadeScoreResult Result, string Reset);
    private sealed record Document(int Version, Pending[] Pending, Peak[] Peaks, string[] Seen);
    private sealed record Cache(long Expires, Task<ArcadeLeaderboard> Fetch);

    internal ArcadeScoreService(string directory, HttpClient? client = null, Func<long>? monotonicClock = null)
    {
        cancellation = lifetime.Token;
        clock = monotonicClock ?? (() => Environment.TickCount64);
        path = Path.Combine(directory, "Usage", "arcade-scores.dat");
        http = client ?? new HttpClient { BaseAddress = new Uri(WorkerUrl), Timeout = TimeSpan.FromSeconds(12) };
        http.MaxResponseContentBufferSize = 128 * 1024;
        Restore();
        pump = Task.Run(PumpAsync);
    }
    private static string Key(string account, ArcadeScoreResult result) => $"{account}:{result.Game}:{result.Session}:{result.Round}";
    private void AddPending(Pending item)
    {
        var minimum = item.Result.StartingBalance > 0 ? item.Result.StartingBalance : DefaultStartingBalance(item.Result.Game);
        if (item.Result.Score <= minimum) return;
        var key = Key(item.Account, item.Result);
        if (pending.Count >= 512 && !pending.ContainsKey(key))
        {
            // Preserve existing unsent results rather than silently replacing them.
            AepLog.Warning("[Arcade scores] Submission queue is full; this result could not be saved.");
            return;
        }
        pending[key] = item; MarkDirty();
    }
    internal void SetSession(string? account, string? token)
    {
        lock (gate)
        {
            if (currentAccount != account) FlushPeaks();
            currentAccount = account; currentToken = token;
        }
    }
    internal void RecordResult(string account, string name, string game, string session, int round, long score)
    {
        if (score <= 0) return;
        var result = new ArcadeScoreResult(game, session, round, score, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), name);
        lock (gate)
        {
            if (disposed || !healthy) return;
            var key = Key(account, result);
            if (seen.Contains(key)) return;
            AddPending(new(account, result));
            if (!pending.ContainsKey(key)) return;
            seen.Add(key); seenOrder.Enqueue(key);
            while (seenOrder.Count > 2048) seen.Remove(seenOrder.Dequeue());
        }
    }
    private static long DefaultStartingBalance(string game) => game switch
    {
        "roulette" => 200,
        "poker" or "choco-race" => 1000,
        "slot-machine" => 100,
        _ => 0,
    };

    internal void ObservePeak(string account, string name, string game, string? session, long balance, string reset = "", long startingBalance = 0)
    {
        lock (gate)
        {
            if (disposed || !healthy) return;
            var key = account + ":" + game;
            peaks.TryGetValue(key, out var previous);
            if (previous is not null && previous.Result.Session != session)
            { if (previous.Result.Score > 0) AddPending(new(previous.Account, previous.Result)); peaks.Remove(key); MarkDirty(); previous = null; }
            if (session is null) return;
            var minimum = startingBalance > 0 ? startingBalance : DefaultStartingBalance(game);
            var best = Math.Max(previous?.Result.Score ?? 0, balance > minimum ? balance : 0);
            if (previous is null || best > previous.Result.Score || previous.Reset != reset)
            {
                var result = new ArcadeScoreResult(game, session, 1, best,
                    previous is null || best > previous.Result.Score ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() : previous.Result.OccurredAt, name, minimum);
                var next = new Peak(account, result, reset);
                peaks[key] = next; MarkDirty();
                if (previous is not null && previous.Reset != reset && best > 0) AddPending(new(account, result));
            }
        }
    }
    internal void FlushPeaks()
    {
        lock (gate)
        {
            foreach (var peak in peaks.Values.Where(item => item.Result.Score > 0)) AddPending(new(peak.Account, peak.Result));
            if (peaks.Count > 0) { peaks.Clear(); MarkDirty(); }
        }
    }
    internal Task<ArcadeLeaderboard> GetAsync(string game, string period)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var key = game + ":" + period;
            var now = clock();
            if (cache.TryGetValue(key, out var existing) && now < existing.Expires) return existing.Fetch;
            var fetch = FetchAsync(game, period);
            // Failed requests also retain the cooldown: reopening cannot spam the Worker.
            cache[key] = new(now + 15 * 60 * 1000, fetch);
            return fetch;
        }
    }
    private async Task<ArcadeLeaderboard> FetchAsync(string game, string period)
    {
        using var response = await http.GetAsync($"/api/scores?game={Uri.EscapeDataString(game)}&period={Uri.EscapeDataString(period)}", cancellation).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var board = await response.Content.ReadFromJsonAsync<ArcadeLeaderboard>(cancellationToken: cancellation).ConfigureAwait(false);
        if (board is null || board.Game != game || board.Period != period || board.Entries is null || board.Entries.Length > 20 ||
            board.Entries.Any(row => row.Score < 0 || row.Rank < 1 || row.Name is null || row.Name.Length > 80 || row.PlayerId?.Length != 64))
            throw new InvalidDataException("Invalid scoreboard response.");
        return board;
    }
    private async Task PumpAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                Pending? work = null; string? token = null; bool save = false;
                lock (gate)
                {
                    if (disposed) return;
                    var now = Environment.TickCount64;
                    if (dirty && now >= nextSave) { save = true; nextSave = now + 2000; }
                    if (now >= nextSubmit && currentAccount is not null && currentToken is not null)
                    { work = pending.Values.FirstOrDefault(item => item.Account == currentAccount); token = currentToken; }
                }
                if (save) Save();
                if (work is not null)
                {
                    var completed = false;
                    try
                    {
                        if (work.Result.OccurredAt < DateTimeOffset.UtcNow.AddDays(-7).ToUnixTimeSeconds()) completed = true;
                        else
                        {
                            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/scores") { Content = JsonContent.Create(work.Result) };
                            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                            using var response = await http.SendAsync(request, cancellation).ConfigureAwait(false);
                            completed = response.IsSuccessStatusCode || (int)response.StatusCode is 400 or 413 or 415;
                        }
                    }
                    catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
                    catch (Exception) { /* Retain protected outbox and retry without blocking gameplay. */ }
                    lock (gate)
                    {
                        if (disposed) return;
                        var key = Key(work.Account, work.Result);
                        if (completed && pending.TryGetValue(key, out var current) && current == work)
                        { pending.Remove(key); MarkDirty(); }
                        nextSubmit = Environment.TickCount64 + (completed ? 2000 : 60000);
                    }
                }
                await Task.Delay(250, cancellation).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception) { AepLog.Warning("[Arcade scores] Background service stopped."); }
        finally { http.Dispose(); lifetime.Dispose(); }
    }
    private void Restore()
    {
        if (!File.Exists(path)) return;
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length > 2 * 1024 * 1024) throw new InvalidDataException("Protected score outbox is too large.");
            var plain = ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser);
            try
            {
                var state = JsonSerializer.Deserialize<Document>(plain) ?? throw new InvalidDataException("Empty score outbox.");
                if (state.Version != 1) throw new InvalidDataException("Unsupported score outbox.");
                foreach (var item in state.Pending.Take(512)) AddPending(item);
                // Interrupted sessions are finished locally; preserve their last observed peak.
                foreach (var item in state.Peaks.Take(32)) if (item.Result.Score > 0) AddPending(new(item.Account, item.Result));
                foreach (var key in state.Seen.TakeLast(2048)) { seen.Add(key); seenOrder.Enqueue(key); }
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (Exception) { healthy = false; AepLog.Warning("[Arcade scores] Protected outbox could not be opened."); }
    }
    private void Save()
    {
        lock (storageGate)
        {
        if (!healthy) return;
        try
        {
            Document snapshot; long revision;
            lock (gate) { snapshot = new(1, pending.Values.ToArray(), peaks.Values.ToArray(), seenOrder.ToArray()); revision = storageRevision; }
            var plain = JsonSerializer.SerializeToUtf8Bytes(snapshot);
            byte[] protectedBytes;
            try { protectedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path + ".tmp", protectedBytes);
            File.Move(path + ".tmp", path, true);
            lock (gate) { if (revision == storageRevision) dirty = false; }
        }
        catch (Exception) { AepLog.Warning("[Arcade scores] Could not save protected outbox."); }
        }
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            FlushPeaks(); disposed = true; lifetime.Cancel();
        }
        Save();
    }
}
