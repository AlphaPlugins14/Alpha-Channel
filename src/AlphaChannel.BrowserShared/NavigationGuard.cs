using System.Text.Json;

namespace AlphaChannel.BrowserShared;

// Shared by both helpers. User gesture alone is insufficient: click hijacks
// deliberately run from a real click. Never trust a destination permanently.
internal sealed class NavigationGuard(Func<bool> enabled, Action<byte[]> notify, Action<string>? diagnostic = null)
{
    private readonly object gate = new();
    private string? permitted;
    private string? acceptedAddress;
    private bool permitRedirectChain;
    private bool youtubeSignInHandoff;
    private DateTime historyUntil;
    private Notice? pending;
    private string? pendingSource;
    private readonly HashSet<string> ignoredPages = new(StringComparer.Ordinal);
    // Kept separate from the editable, persisted user whitelist. Never use
    // suffix matching: approval of a site does not approve its hosted tenants.
    private static readonly HashSet<string> preapprovedHosts = LoadPreapprovedHosts();
    private bool preapprovedHostsEnabled = true;
    private HashSet<string> allowedHosts = new(StringComparer.OrdinalIgnoreCase);
    private bool IsApproved(string host) => (preapprovedHostsEnabled && preapprovedHosts.Contains(host)) || allowedHosts.Contains(host);
    private static HashSet<string> LoadPreapprovedHosts()
    {
        using var stream = typeof(NavigationGuard).Assembly.GetManifestResourceStream("AlphaChannel.PreapprovedRedirectHosts.txt");
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (stream is null) return result; // Fail closed if a helper was packaged incorrectly.
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            var host = line.Trim().ToLowerInvariant();
            if (host.Length == 0 || host.StartsWith('#')) continue;
            if (Uri.CheckHostName(host) != UriHostNameType.Dns || !host.Contains('.') || host.StartsWith("www.") || host.EndsWith('.'))
                throw new InvalidDataException($"Invalid built-in redirect hostname: {host}");
            result.Add(host);
        }
        return result;
    }
    internal sealed record Notice(string Id, string Url, string SourceHost);
    internal sealed record Decision(string Id, bool Allow, bool IgnoreAll = false);
    private static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        ? (uri.IdnHost.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.IdnHost[4..] : uri.IdnHost).ToLowerInvariant() : "";
    internal void SetWhitelist(byte[] payload)
    {
        var hosts = JsonSerializer.Deserialize<string[]>(payload) ?? [];
        lock (gate) allowedHosts = hosts.Where(h => !string.IsNullOrWhiteSpace(h)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
    internal void SetPreapprovedWhitelistEnabled(bool enabled)
    {
        lock (gate)
        {
            preapprovedHostsEnabled = enabled;
            if (!enabled) permitRedirectChain = false;
        }
    }

    internal void Permit(string url, bool allowRedirects = false)
    {
        lock (gate)
        {
            permitted = url;
            permitRedirectChain = allowRedirects;
            youtubeSignInHandoff = IsExplicitYouTubeSignInUrl(url);
            historyUntil = default;
            Clear();
        }
    }
    internal void CompleteNavigation(string url)
    {
        lock (gate)
        {
            if (acceptedAddress == url) permitRedirectChain = false;
            if (youtubeSignInHandoff && Uri.TryCreate(url, UriKind.Absolute, out _) &&
                Host(url) == "youtube.com")
                youtubeSignInHandoff = false;
        }
    }
    internal void PermitHistory()
    {
        lock (gate) { permitted = null; permitRedirectChain = false; youtubeSignInHandoff = false; historyUntil = DateTime.UtcNow.AddSeconds(2); Clear(); }
    }
    internal bool Cancel(string target, string source, bool popup = false, bool redirect = false)
    {
        // No external applications or script URLs may be opened by Allow.
        if (!Uri.TryCreate(target, UriKind.Absolute, out var next) || next.Scheme is not ("http" or "https"))
            return popup || next?.Scheme is not ("about" or "data");
        lock (gate)
        {
            if (!popup && permitted is not null && Uri.TryCreate(permitted, UriKind.Absolute, out var grant) && grant == next)
            { permitted = null; acceptedAddress = target; return false; }
            // YouTube's sign-in flow moves between YouTube and Google's
            // account hosts. Keep this exception limited to those hosts and
            // only after a YouTube -> account handoff; other page redirects
            // still go through the normal blocker.
            if (!popup && IsYouTubeSignInHandoff(acceptedAddress ?? source, next))
            { youtubeSignInHandoff = true; acceptedAddress = target; return false; }
            if (!popup && youtubeSignInHandoff && IsYouTubeSignInHost(next))
            { acceptedAddress = target; return false; }
            // Only HTTP redirects in the explicitly requested navigation are
            // trusted. A new page-driven navigation ends that exception.
            if (!popup && redirect && permitRedirectChain)
            { acceptedAddress = target; return false; }
            // Navigation and resource callbacks may both describe the same
            // initial request. The second callback must not revoke a chain
            // already approved by the address bar or a whitelisted source.
            var repeatsAcceptedRequest = acceptedAddress is not null &&
                Uri.TryCreate(acceptedAddress, UriKind.Absolute, out var accepted) && accepted == next;
            if (!popup && !redirect && !repeatsAcceptedRequest) permitRedirectChain = false;
            if (!popup && !redirect && historyUntil > DateTime.UtcNow)
            { historyUntil = default; acceptedAddress = target; return false; }
            if (!enabled()) { acceptedAddress = target; return false; }
            var pageUrl = acceptedAddress ?? source;
            var sourceHost = Host(pageUrl);
            if (IsApproved(sourceHost))
            {
                if (!popup) { acceptedAddress = target; permitRedirectChain = true; }
                return false;
            }
            // Browser Source/Frame.Url may already reflect an HTTP redirect.
            // Compare against the last accepted navigation instead.
            if (!popup && (!Uri.TryCreate(acceptedAddress ?? source, UriKind.Absolute, out var current) || current.Scheme is not ("http" or "https") ||
                (current.IdnHost.Equals(next.IdnHost, StringComparison.OrdinalIgnoreCase) && current.Port == next.Port)))
            { acceptedAddress = target; return false; }
            if (ignoredPages.Contains(pageUrl)) return true;
            if (pending is null)
            {
                diagnostic?.Invoke($"Redirect confirmation: source={sourceHost}; destination={Host(target)}; " +
                    $"popup={popup}; redirect={redirect}; built-in-whitelist={preapprovedHostsEnabled}; " +
                    $"built-in-hosts={preapprovedHosts.Count}; source-approved={IsApproved(sourceHost)}.");
                pendingSource = pageUrl;
                pending = new Notice(Guid.NewGuid().ToString("N"), next.AbsoluteUri, sourceHost);
                notify(JsonSerializer.SerializeToUtf8Bytes(pending));
            }
            return true;
        }
    }
    private static bool IsYouTubeSignInHost(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps &&
        Host(uri.AbsoluteUri) is "youtube.com" or "accounts.youtube.com" or "accounts.google.com";

    private static bool IsYouTubeSignInHandoff(string source, Uri target)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var current) ||
            current.Scheme != Uri.UriSchemeHttps || Host(source) != "youtube.com")
            return false;

        return target.Scheme == Uri.UriSchemeHttps &&
               (Host(target.AbsoluteUri) == "accounts.google.com" ||
                (Host(target.AbsoluteUri) == "accounts.youtube.com" &&
                 target.AbsolutePath.Equals("/accounts/SetSID", StringComparison.OrdinalIgnoreCase)));
    }
    private static bool IsExplicitYouTubeSignInUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        Host(url) == "accounts.google.com" &&
        uri.AbsolutePath.StartsWith("/ServiceLogin", StringComparison.OrdinalIgnoreCase) &&
        uri.Query.Contains("service=youtube", StringComparison.OrdinalIgnoreCase);
    internal string? Resolve(byte[] payload)
    {
        var decision = JsonSerializer.Deserialize<Decision>(payload);
        lock (gate)
        {
            if (decision is null || pending?.Id != decision.Id) return null;
            if (decision.IgnoreAll && pendingSource is not null) ignoredPages.Add(pendingSource);
            var url = decision.Allow && !decision.IgnoreAll ? pending.Url : null;
            var trustedSource = IsApproved(pending.SourceHost);
            Clear();
            if (url is not null) { permitted = url; permitRedirectChain = trustedSource; }
            return url;
        }
    }
    private void Clear() { pending = null; pendingSource = null; notify("null"u8.ToArray()); }
}
