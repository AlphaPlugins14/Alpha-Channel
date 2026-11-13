using System.Text.Json;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;

namespace AlphaChannel.BrowserShared;

// Shared by the two isolated browser helpers. No network or disk access occurs
// on a browser request callback; a complete immutable snapshot is swapped in.
internal sealed class EasyListBlocker
{
    private volatile Rules rules = new("");
    internal volatile bool Enabled = true;
    private readonly Action<string> log;
    internal EasyListBlocker(Action<string> log) => this.log = log;
    internal void Load(string text) => rules = new Rules(text);
    internal async Task InitializeAsync(string browserCache, TimeSpan? refreshDelay = null)
    {
        var directory = Path.Combine(Path.GetDirectoryName(browserCache)!, "AdBlock");
        var file = Path.Combine(directory, "easylist.txt");
        try
        {
            Directory.CreateDirectory(directory);
            if (File.Exists(file)) Load(await File.ReadAllTextAsync(file));
            else Load(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "easylist.txt")));
            if (File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromDays(4)) return;
            _ = UpdateAsync(file, refreshDelay ?? TimeSpan.Zero);
        }
        catch (Exception e) { log("EasyList cache unavailable: " + e.Message); }
    }
    private async Task UpdateAsync(string file, TimeSpan delay)
    {
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay).ConfigureAwait(false);
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var text = await client.GetStringAsync("https://easylist.to/easylist/easylist.txt");
            if (!text.StartsWith("[Adblock", StringComparison.Ordinal) || text.Length < 10000 || text.Length > 10_000_000)
                throw new InvalidDataException("Unexpected EasyList response");
            var next = new Rules(text);
            var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { await File.WriteAllTextAsync(temporary, text); File.Move(temporary, file, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            rules = next;
            log("EasyList updated.");
        }
        catch (Exception e) { log("EasyList update unavailable; keeping existing filters: " + e.Message); }
    }
    internal bool Blocks(string url, string source, string type)
    {
        if (!Enabled || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "ws" or "wss")) return false;
        var snapshot = rules;
        var host = Uri.TryCreate(source, UriKind.Absolute, out var page) ? page.Host : "";
        if (page is not null && snapshot.Candidates(page.AbsoluteUri.ToLowerInvariant()).Any(r => r.Exception && r.Include.Contains("document") && r.Matches(page, host, "document"))) return false;
        var candidates = snapshot.Candidates(url.ToLowerInvariant()).ToArray();
        if (candidates.Any(r => r.Exception && r.Matches(uri, host, type))) return false;
        return candidates.Any(r => !r.Exception && r.Matches(uri, host, type));
    }
    internal string CosmeticScript(string url)
    {
        const string remove = "document.getElementById('alpha-channel-adblock')?.remove();";
        if (!Enabled) return remove;
        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "";
        var snapshot = rules;
        if (uri is not null && snapshot.Candidates(uri.AbsoluteUri.ToLowerInvariant()).Any(r => r.Exception && (r.Include.Contains("document") && r.Matches(uri, host, "document") || r.Include.Contains("elemhide") && r.Matches(uri, host, "elemhide"))))
            return remove;
        // A single stylesheet parse avoids thousands of separate CSSOM inserts.
        // Each selector has its own rule, so invalid selectors remain isolated.
        return "(()=>{" + remove + "const s=document.createElement('style');s.id='alpha-channel-adblock';s.textContent=" + snapshot.Selectors(host) + ".map(x=>x+'{display:none!important;}').join('\\n');(document.head||document.documentElement).append(s);})();";
    }
    private static bool DomainMatches(string host, string domain) => host.Equals(domain, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
    private static bool DomainApplies(string[] domains, string host) =>
        !domains.Any(d => d.StartsWith('~') && DomainMatches(host, d[1..])) &&
        (!domains.Any(d => !d.StartsWith('~')) || domains.Any(d => !d.StartsWith('~') && DomainMatches(host, d)));
    private sealed record Cosmetic(string[] Domains, string Selector, bool Exception);
    private sealed class Rules
    {
        internal readonly List<Cosmetic> Cosmetics = [];
        private readonly ConcurrentDictionary<string, string> selectors = new(StringComparer.OrdinalIgnoreCase);
        internal string Selectors(string host)
        {
            if (selectors.Count > 64) selectors.Clear();
            return selectors.GetOrAdd(host, domain =>
            {
                var exceptions = Cosmetics.Where(c => c.Exception && DomainApplies(c.Domains, domain)).Select(c => c.Selector).ToHashSet();
                return JsonSerializer.Serialize(Cosmetics.Where(c => !c.Exception && DomainApplies(c.Domains, domain) && !exceptions.Contains(c.Selector)).Select(c => c.Selector).Distinct().ToArray());
            });
        }
        private readonly Dictionary<string, List<Rule>> index = new(StringComparer.Ordinal);
        private readonly List<Rule> general = [];
        internal Rules(string text)
        {
            var disabled = text.Split('\n').Select(l => l.Trim()).Where(l => l.EndsWith(",badfilter", StringComparison.Ordinal)).Select(l => l[..^10]).ToHashSet();
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] is '!' or '[' || disabled.Contains(line)) continue;
                var separator = line.IndexOf("#@#", StringComparison.Ordinal);
                var exception = separator >= 0;
                if (separator < 0) separator = line.IndexOf("##", StringComparison.Ordinal);
                if (separator >= 0)
                {
                    var selector = line[(separator + (exception ? 3 : 2))..];
                    if (!selector.Contains(":-abp-") && !selector.StartsWith('+')) Cosmetics.Add(new(line[..separator].Split(',', StringSplitOptions.RemoveEmptyEntries), selector, exception));
                    continue;
                }
                if (line.Contains('#')) continue;
                var rule = Rule.Parse(line);
                if (rule is null) continue;
                if (rule.Key.Length == 0) general.Add(rule);
                else { if (!index.TryGetValue(rule.Key, out var list)) index[rule.Key] = list = []; list.Add(rule); }
            }
        }
        internal IEnumerable<Rule> Candidates(string url)
        {
            foreach (var r in general) yield return r;
            var seen = new HashSet<string>();
            for (var i = 0; i <= url.Length - 4; i++)
            {
                var key = url.Substring(i, 4);
                if (seen.Add(key) && index.TryGetValue(key, out var list)) foreach (var r in list) yield return r;
            }
        }
    }
    private sealed record Rule(Regex Pattern, bool Exception, string Key, string[] Domains, string[] Include, string[] Exclude, bool? ThirdParty)
    {
        private static readonly HashSet<string> Types = ["script", "image", "stylesheet", "object", "xmlhttprequest", "subdocument", "document", "media", "font", "websocket", "ping", "other", "popup", "elemhide"];
        internal static Rule? Parse(string line)
        {
            var exception = line.StartsWith("@@");
            if (exception) line = line[2..];
            var dollar = line.LastIndexOf('$');
            var options = dollar >= 0 ? line[(dollar + 1)..].Split(',') : [];
            var pattern = dollar >= 0 ? line[..dollar] : line;
            var domains = Array.Empty<string>(); var include = new List<string>(); var exclude = new List<string>(); bool? third = null; var matchCase = false;
            foreach (var option in options)
            {
                var name = option.TrimStart('~');
                if (name.StartsWith("domain=")) domains = name[7..].Split('|');
                else if (name == "third-party") third = !option.StartsWith('~');
                else if (Types.Contains(name)) (option.StartsWith('~') ? exclude : include).Add(name);
                else if (name == "match-case") matchCase = true;
                else if (name is "collapse") { }
                else return null; // Unsupported options must never become broader blocking rules.
            }
            if (pattern.Length == 0) return null;
            var regex = pattern.StartsWith('/') && pattern.EndsWith('/') ? pattern[1..^1] : Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\^", "(?:[^a-zA-Z0-9_.%-]|$)");
            if (!pattern.StartsWith('/'))
            {
                if (pattern.StartsWith("||")) regex = @"^[a-z]+://(?:[^/?#]*\.)?" + regex[4..];
                else if (pattern.StartsWith('|')) regex = "^" + regex[2..];
                if (pattern.EndsWith('|')) regex = regex[..^2] + "$";
            }
            var literal = pattern.StartsWith('/') ? "" : Regex.Matches(pattern, "[a-zA-Z0-9_%/-]{4,}").Select(m => m.Value).OrderByDescending(s => s.Length).FirstOrDefault() ?? "";
            try { return new(new Regex(regex, matchCase ? RegexOptions.None : RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(20)), exception, literal.Length >= 4 ? literal[..4].ToLowerInvariant() : "", domains, include.ToArray(), exclude.ToArray(), third); }
            catch (ArgumentException) { return null; }
        }
        internal bool Matches(Uri uri, string host, string type)
        {
            if (!DomainApplies(Domains, host) || Exclude.Contains(type) || (Include.Length > 0 && !Include.Contains(type))) return false;
            if (ThirdParty is { } third && third == (host.Length == 0 || DomainMatches(uri.Host, host) || DomainMatches(host, uri.Host))) return false;
            try { return Pattern.IsMatch(uri.AbsoluteUri); } catch (RegexMatchTimeoutException) { return false; }
        }
    }
}
