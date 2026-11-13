using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace AlphaChannel.Plugin.Core;

internal sealed record PartyCountdown(Guid Id, string HostId, Guid SessionId, string Label, int DurationSeconds, long EndsAt)
{
    internal bool Valid => Id != Guid.Empty && Guid.TryParse(HostId, out _) && SessionId != Guid.Empty && Label is { Length: <= 96 } &&
        !Label.Any(char.IsControl) && DurationSeconds is >= 1 and <= 3600 && EndsAt > 0;
}
internal sealed record PartyCountdownReply(long ServerNow, PartyCountdown[] Countdowns);
internal sealed record PartyCountdownView(PartyCountdown State, double RemainingAtReceive, long ReceivedTimestamp, bool Halloween = false)
{
    internal double Remaining => RemainingAfter(Stopwatch.GetElapsedTime(ReceivedTimestamp));
    internal double RemainingAfter(TimeSpan elapsed) => Math.Max(-15, RemainingAtReceive - Math.Max(0, elapsed.TotalSeconds));
    internal bool Visible => Remaining > -15;
    internal int Seconds => Math.Max(0, (int)Math.Ceiling(Remaining));
    internal string Clock => Format(Seconds);
    internal float Progress => (float)Math.Clamp(Remaining / State.DurationSeconds, 0, 1);
    internal static string Format(int seconds) => $"{seconds / 60:00}:{seconds % 60:00}";
}
// Reserved signalling URL only. It must never be passed to a media decoder or fetched.
internal static class PartyCountdownSignal
{
    internal const string Prefix = "https://countdown.alphachannel/";
    internal static string Encode(PartyCountdown timer) => Encode(timer.Id, timer.EndsAt);
    internal static string Encode(Guid id, long endsAt) => $"{Prefix}{id:N}/{endsAt}";
    internal static bool IsCountdown(string? url) => TryRead(url, out _, out _);
    internal static bool TryRead(string? url, out Guid id, out long endsAt)
    {
        id = default; endsAt = 0;
        if (url is null || !url.StartsWith(Prefix, StringComparison.Ordinal) || url.Length > 120) return false;
        var parts = url[Prefix.Length..].Split('/');
        return parts.Length == 2 && Guid.TryParseExact(parts[0], "N", out id) && id != Guid.Empty &&
            long.TryParse(parts[1], out endsAt) && endsAt > 0;
    }
}

internal sealed class PartyCountdownRateLimitException(TimeSpan retryAfter) : Exception("Countdown service is busy")
{
    internal TimeSpan RetryAfter { get; } = retryAfter;
}

internal sealed class PartyCountdownClient : IDisposable
{
    internal const string Origin = "https://countdowns.example.invalid";
    private readonly HttpClient http;
    internal PartyCountdownClient(HttpMessageHandler? handler = null)
    {
        http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.BaseAddress = new(Origin); http.Timeout = TimeSpan.FromSeconds(8);
    }
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    internal async Task<PartyCountdownView[]> GetAsync(IEnumerable<string> hosts, CancellationToken cancellationToken)
    {
        var ids = hosts.Where(x => Guid.TryParse(x, out _)).Select(x => x.ToLowerInvariant()).Distinct().Take(32).ToArray();
        if (ids.Length == 0) return [];
        return await SendAsync(HttpMethod.Get, "/countdowns?hosts=" + string.Join(',', ids), null, null, cancellationToken);
    }
    internal Task<PartyCountdownView[]> StartAsync(string token, string host, Guid id, Guid session, int seconds, string label, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "/countdowns/" + host.ToLowerInvariant(), token,
            new { Id = id, SessionId = session, DurationSeconds = seconds, Label = label }, ct);
    internal Task<PartyCountdownView[]> StartNowAsync(string token, string host, Guid id, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "/countdowns/" + host.ToLowerInvariant(), token, new { Id = id, Operation = "now" }, ct);
    internal async Task CancelAsync(string token, string host, Guid id) =>
        _ = await SendAsync(HttpMethod.Delete, "/countdowns/" + host.ToLowerInvariant(), token, new { Id = id }, CancellationToken.None);
    private async Task<PartyCountdownView[]> SendAsync(HttpMethod method, string path, string? token, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body, options: new JsonSerializerOptions { PropertyNamingPolicy = null });
        var sent = Stopwatch.GetTimestamp();
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if ((int)response.StatusCode == 429)
        {
            var delay = response.Headers.RetryAfter?.Delta ??
                (response.Headers.RetryAfter?.Date is { } retryAt ? retryAt - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(60));
            throw new PartyCountdownRateLimitException(TimeSpan.FromSeconds(Math.Clamp(delay.TotalSeconds, 15, 300)));
        }
        response.EnsureSuccessStatusCode();
        var reply = await response.Content.ReadFromJsonAsync<PartyCountdownReply>(Json, ct).ConfigureAwait(false);
        var received = Stopwatch.GetTimestamp();
        if (reply is null || reply.ServerNow <= 0 || reply.Countdowns is not { Length: <= 32 }) return [];
        var transit = Stopwatch.GetElapsedTime(sent, received).TotalSeconds / 2;
        return reply.Countdowns.Where(x => x is not null && x.Valid && x.EndsAt - reply.ServerNow <= 3600000 && x.EndsAt - reply.ServerNow > -15000)
            .Select(x => new PartyCountdownView(x, (x.EndsAt - reply.ServerNow) / 1000d - transit, received)).ToArray();
    }
    public void Dispose() => http.Dispose();
}
