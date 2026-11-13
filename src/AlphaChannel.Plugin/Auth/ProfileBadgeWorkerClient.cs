using System.Net.Http.Headers;
using System.Net.Http.Json;
using AlphaChannel.Plugin.Net;

namespace AlphaChannel.Plugin.Auth;

internal sealed class ProfileBadgeWorkerClient
{
    internal const string WorkerUrl = "https://badges.example.invalid";

    internal async Task<IReadOnlySet<string>> GetUnlockedAsync(
        string bearerToken, string accountId, CancellationToken cancellationToken, bool manager = false, bool publicProfile = false)
    {
        if (!Guid.TryParse(accountId, out var id))
            throw new ArgumentException("Invalid profile account ID.", nameof(accountId));
        using var http = PluginHttpClients.CreateMetadataClient();
        http.BaseAddress = new Uri(WorkerUrl);
        http.Timeout = TimeSpan.FromSeconds(15);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        using var response = await http.GetAsync(manager ? $"/api/manager/accounts/{id:D}/badges" :
            publicProfile ? $"/api/public/accounts/{id:D}/badges" : $"/api/accounts/{id:D}/badges", cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<BadgeResponse>(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (result?.BadgeIds is null || !Guid.TryParse(result.AccountId, out var returnedId) || returnedId != id)
            throw new InvalidOperationException("Invalid profile badge response.");
        // Ignore unknown/reserved designs, including awards from a newer server.
        var available = ProfileBadgeCatalog.Available.Select(badge => badge.Id).ToHashSet(StringComparer.Ordinal);
        return result.BadgeIds.Where(badge => badge is not null && available.Contains(badge))
            .ToHashSet(StringComparer.Ordinal);
    }

    private sealed record BadgeResponse(string AccountId, string[] BadgeIds);

    internal async Task<IReadOnlySet<string>> CheckEligibilityAsync(string token, string accountId,
        string check, CancellationToken cancellationToken) =>
        await PostAwardAsync(token, accountId, $"/api/eligibility/{check}", null, cancellationToken);

    internal async Task<IReadOnlySet<string>> ClaimMovieBuffAsync(string token, string accountId,
        CancellationToken cancellationToken)
        => await ClaimCosmeticAsync(token, accountId, "popcorn-pal", cancellationToken);

    internal async Task<IReadOnlySet<string>> ClaimCosmeticAsync(string token, string accountId, string badgeId,
        CancellationToken cancellationToken)
    {
        var id = Guid.Parse(accountId).ToString("D");
        var code = CosmeticClaimCode(token, id, badgeId, DateTime.UtcNow.ToString("yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture));
        return await PostAwardAsync(token, id, "/api/cosmetic/claim",
            new { userId = id, badgeId, code }, cancellationToken);
    }

    internal static string CosmeticClaimCode(string token, string accountId, string badgeId, string utcDate)
    {
        var data = System.Text.Encoding.UTF8.GetBytes($"alpha-badge-v1\n{accountId}\n{badgeId}\n{utcDate}");
        var key = System.Text.Encoding.UTF8.GetBytes(token);
        try { return Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(key, data)).ToLowerInvariant(); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(key); }
    }

    private static async Task<IReadOnlySet<string>> PostAwardAsync(string token, string accountId,
        string path, object? body, CancellationToken cancellationToken)
    {
        using var http = PluginHttpClients.CreateMetadataClient();
        http.Timeout = TimeSpan.FromSeconds(25);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.PostAsJsonAsync(WorkerUrl + path, body ?? new { }, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<BadgeResponse>(cancellationToken: cancellationToken);
        if (result?.BadgeIds is null || !Guid.TryParse(result.AccountId, out var id) || id != Guid.Parse(accountId))
            throw new InvalidOperationException("Invalid automatic badge response.");
        return result.BadgeIds.ToHashSet(StringComparer.Ordinal);
    }

#if DEBUG
    internal async Task<bool> CanManageAsync(string bearerToken, string accountId, CancellationToken cancellationToken)
    {
        using var http = CreateManagerClient(bearerToken);
        using var response = await http.GetAsync("/api/manager", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ManagerAccess>(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (result is null || !Guid.TryParse(result.AccountId, out var id) || id != Guid.Parse(accountId))
            throw new InvalidOperationException("Invalid badge manager response.");
        return result.CanManage;
    }

    internal async Task SetUnlockedAsync(string bearerToken, string accountId, string badgeId,
        bool unlocked, CancellationToken cancellationToken)
    {
        var id = Guid.Parse(accountId);
        if (!ProfileBadgeCatalog.Available.Any(badge => badge.Id == badgeId))
            throw new ArgumentException("Unavailable badge.", nameof(badgeId));
        using var http = CreateManagerClient(bearerToken);
        using var request = new HttpRequestMessage(unlocked ? HttpMethod.Put : HttpMethod.Delete,
            $"/api/accounts/{id:D}/badges/{Uri.EscapeDataString(badgeId)}");
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<AssignmentResponse>(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (result is null || !Guid.TryParse(result.AccountId, out var returnedId) || returnedId != id ||
            result.BadgeId != badgeId || result.Unlocked != unlocked)
            throw new InvalidOperationException("Invalid badge assignment response.");
    }

    private static HttpClient CreateManagerClient(string bearerToken)
    {
        var http = PluginHttpClients.CreateMetadataClient();
        http.BaseAddress = new Uri(WorkerUrl);
        http.Timeout = TimeSpan.FromSeconds(15);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        return http;
    }

    private sealed record ManagerAccess(string AccountId, bool CanManage);
    private sealed record AssignmentResponse(string AccountId, string BadgeId, bool Unlocked);
#endif
}
