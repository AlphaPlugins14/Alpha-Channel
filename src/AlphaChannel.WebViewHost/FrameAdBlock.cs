using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace AlphaChannel.WebViewHost;

internal static class FrameAdBlock
{
    internal static async Task ApplyAsync(CoreWebView2Frame frame, Func<bool> isClosing,
        Func<string, string> cosmeticScript)
    {
        try
        {
            if (isClosing() || frame.IsDestroyed() != 0) return;
            var source = await frame.ExecuteScriptAsync("location.href");
            if (isClosing() || frame.IsDestroyed() != 0) return;
            var address = JsonSerializer.Deserialize<string>(source) ?? "";
            var script = await Task.Run(() => cosmeticScript(address));
            // Navigation, frame destruction or shutdown can happen at either
            // await. Resume on the WebView UI context and recheck before COM use.
            if (isClosing() || frame.IsDestroyed() != 0) return;
            await frame.ExecuteScriptAsync(script);
        }
        catch (Exception) { /* A frame can disappear during navigation or shutdown. */ }
    }
}
