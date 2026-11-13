using System.Net.Http.Headers;
using System.Net.Http.Json;
using AlphaChannel.Plugin.Net;

namespace AlphaChannel.Plugin.Auth;

// Patreon checks are temporary Cloudflare-only. The existing relay still owns
// Alpha Channel account authentication, which the Worker verifies via /me.
internal sealed class PatreonWorkerClient
{
    private const string WorkerUrl =
        "https://patreon.example.invalid";

    internal async Task<PatreonAuthorizationSession> CreateAuthorizationUriAsync(
        string accountToken, CancellationToken cancellationToken)
    {
        using var http = CreateClient(accountToken);
        using var response = await http.PostAsync(
            "/oauth/session", content: null, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<AuthorizationSession>(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result?.AuthorizationUrl is not { Length: > 0 } url ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            uri.Host != "patreon.example.invalid" ||
            !Guid.TryParse(result.FlowId, out _))
        {
            throw new InvalidOperationException("Patreon Worker returned an invalid authorization URL.");
        }

        return new PatreonAuthorizationSession(uri, result.FlowId);
    }

    internal async Task<bool> IsConnectionCompleteAsync(
        string accountToken, string flowId, CancellationToken cancellationToken)
    {
        using var http = CreateClient(accountToken);
        using var response = await http.GetAsync(
            $"/oauth/status?flowId={Uri.EscapeDataString(flowId)}", cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ConnectionStatus>(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return result?.Complete == true;
    }

    internal Task<PatreonEntitlement> GetEntitlementAsync(
        string accountToken, CancellationToken cancellationToken) =>
        ReadEntitlementAsync(accountToken, false, cancellationToken);

    internal Task<PatreonEntitlement> RefreshOwnEntitlementAsync(
        string accountToken, CancellationToken cancellationToken) =>
        ReadEntitlementAsync(accountToken, true, cancellationToken);

    private async Task<PatreonEntitlement> ReadEntitlementAsync(
        string accountToken, bool force, CancellationToken cancellationToken)
    {
        using var http = CreateClient(accountToken);
        using var response = force
            ? await http.PostAsync("/api/entitlement/refresh", null, cancellationToken).ConfigureAwait(false)
            : await http.GetAsync("/api/entitlement", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PatreonEntitlement>(
            cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Patreon Worker returned an empty entitlement.");
    }

    internal async Task<Dictionary<string, int>> GetBadgesAsync(
        string accountToken, IReadOnlyCollection<string> accountIds,
        CancellationToken cancellationToken)
    {
        using var http = CreateClient(accountToken);
        using var response = await http.PostAsJsonAsync(
            "/api/badges", new BadgeRequest(accountIds.ToArray()), cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<BadgeResponse>(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return result?.Badges is { } badges
            ? new Dictionary<string, int>(badges, StringComparer.OrdinalIgnoreCase)
            : throw new InvalidOperationException("Patreon Worker returned an empty badge response.");
    }

    private static HttpClient CreateClient(string accountToken)
    {
        var http = PluginHttpClients.CreateMetadataClient();
        http.BaseAddress = new Uri(WorkerUrl);
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", accountToken);
        return http;
    }

    internal async Task<PatreonProfileTheme> GetProfileThemeAsync(string token, CancellationToken ct, string? accountId = null)
    {
        using var http = CreateClient(token);
        using var response = await http.GetAsync(accountId is null ? "/api/profile-theme" :
            "/api/profile-theme/" + Guid.Parse(accountId).ToString("D"), ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PatreonProfileTheme>(cancellationToken:ct) ?? throw new InvalidDataException();
    }

    internal async Task<PatreonProfileTheme> SaveProfileThemeAsync(string token, string? theme, CancellationToken ct)
    {
        using var http = CreateClient(token);
        using var response = await http.PutAsJsonAsync("/api/profile-theme", new { theme }, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PatreonProfileTheme>(cancellationToken:ct) ?? throw new InvalidDataException();
    }

    private sealed record AuthorizationSession(string AuthorizationUrl, string FlowId);
    private sealed record ConnectionStatus(bool Complete);
    private sealed record BadgeRequest(string[] AccountIds);
    private sealed record BadgeResponse(Dictionary<string, int> Badges);
}

internal sealed record PatreonAuthorizationSession(Uri AuthorizationUri, string FlowId);
internal sealed record PatreonProfileTheme(string AccountId, string? Theme, string? SelectedTheme, bool Entitled, long ValidUntil);

internal sealed record PatreonEntitlement(
    bool Linked, bool Entitled, int Tier, bool Stale, long? CheckedAt,
    long? ProbeUntil);
