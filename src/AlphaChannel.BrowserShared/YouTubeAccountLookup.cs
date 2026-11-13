using System.Text.Json;

namespace AlphaChannel.BrowserShared;

internal sealed record YouTubeAccountProfile(string? DisplayName, string? ChannelName,
    string? Handle, string? AvatarUrl);
internal sealed record YouTubeAccountLookupResult(string RequestId, YouTubeAccountProfile? Profile,
    bool PageRestored);

internal static class YouTubeAccountLookup
{
    // Open the site's own account menu. Its browser session supplies all
    // authentication; no cookie/header values are read by this script.
    internal const string Script = """
        (() => {
          if (location.hostname !== 'www.youtube.com') return null;
          const text = v => typeof v === 'string' ? v :
            v?.simpleText || v?.runs?.map(r => r.text || '').join('') || '';
          let header = null;
          for (const menu of document.querySelectorAll('ytd-multi-page-menu-renderer')) {
            const h = menu.data?.header?.activeAccountHeaderRenderer;
            if (h) { header = h; break; }
          }
          const dom = document.querySelector('ytd-active-account-header-renderer');
          const name = text(header?.accountName) || dom?.querySelector('#account-name')?.textContent?.trim() || '';
          const handle = text(header?.channelHandle) || dom?.querySelector('#channel-handle')?.textContent?.trim() || '';
          const photos = header?.accountPhoto?.thumbnails || [];
          const photo = photos.length ? photos[photos.length - 1].url :
            dom?.querySelector('img')?.src || '';
          const channel = text(header?.channelName) || dom?.querySelector('#channel-name')?.textContent?.trim() || name;
          if (name && photo) return JSON.stringify({ DisplayName: name,
            ChannelName: channel, Handle: handle || null, AvatarUrl: photo });
          if (!window.__alphaChannelAccountMenuOpened) {
            const button = document.querySelector('ytd-masthead #avatar-btn');
            if (button && !button.disabled) {
              window.__alphaChannelAccountMenuOpened = true;
              button.click();
            }
          }
          return null;
        })()
        """;

    internal static async Task<YouTubeAccountLookupResult> RunAsync(string requestId,
        Func<string> address, Func<bool> loading, Action<string> navigate,
        Func<string, Task<string?>> evaluate)
    {
        var previous = address();
        YouTubeAccountProfile? profile = null;
        var restored = false;
        try
        {
            navigate("https://www.youtube.com/");
            // Navigation is asynchronous. Do not inspect the document that was
            // present before the command reached the browser.
            await Task.Delay(600);
            var deadline = DateTime.UtcNow.AddSeconds(14);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    var json = await evaluate(Script).WaitAsync(TimeSpan.FromSeconds(2));
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        var candidate = JsonSerializer.Deserialize<YouTubeAccountProfile>(json);
                        if (candidate is not null && IsValid(candidate))
                        {
                            profile = candidate;
                            break;
                        }
                    }
                }
                catch (Exception) { /* A document can disappear during navigation. */ }
                await Task.Delay(500);
            }
        }
        catch (Exception) { /* Profile lookup must never invalidate the cookie connection. */ }
        finally
        {
            try
            {
                if (!Uri.TryCreate(previous, UriKind.Absolute, out var uri) ||
                    uri.Scheme is not ("http" or "https"))
                    previous = "https://www.youtube.com/";
                var marker = Guid.NewGuid().ToString("N");
                await evaluate($"window.__alphaChannelRestoreMarker = '{marker}'")
                    .WaitAsync(TimeSpan.FromSeconds(2));
                navigate(previous);
                await Task.Delay(600);
                var deadline = DateTime.UtcNow.AddSeconds(8);
                while (DateTime.UtcNow < deadline)
                {
                    // Allow the page to paint before the plugin lifts its
                    // temporary privacy guard. Redirected destinations are OK.
                    if (!loading() && await evaluate(
                            $"document.readyState === 'complete' && window.__alphaChannelRestoreMarker !== '{marker}' ? 'ready' : 'waiting'")
                            .WaitAsync(TimeSpan.FromSeconds(2)) == "ready")
                    {
                        await Task.Delay(600);
                        restored = true;
                        break;
                    }
                    await Task.Delay(250);
                }
            }
            catch (Exception) { /* Keep privacy enabled if restoration cannot be confirmed. */ }
        }
        return new(requestId, profile, restored);
    }

    private static bool IsValid(YouTubeAccountProfile profile) =>
        !string.IsNullOrWhiteSpace(profile.DisplayName) && profile.DisplayName.Length <= 200 &&
        (profile.ChannelName?.Length ?? 0) <= 200 && (profile.Handle?.Length ?? 0) <= 200 &&
        Uri.TryCreate(profile.AvatarUrl, UriKind.Absolute, out var avatar) && avatar.Scheme == "https" &&
        (avatar.Host == "yt3.ggpht.com" || avatar.Host == "yt3.googleusercontent.com" ||
         avatar.Host.EndsWith(".googleusercontent.com", StringComparison.OrdinalIgnoreCase) ||
         avatar.Host.EndsWith(".ytimg.com", StringComparison.OrdinalIgnoreCase));
}
