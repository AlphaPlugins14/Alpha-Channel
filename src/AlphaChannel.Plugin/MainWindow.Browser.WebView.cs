using AlphaChannel.Plugin.Video;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private Task? webViewSetupTask;
    private string? pendingWebViewAddress;
    private void StartWebViewBrowser()
    {
        if (webViewSetupTask is not null) return;
        if (!screenController.Engine.TryPrepareBrowserPlayback())
        {
            browserError = screenController.Engine.LastError;
            return;
        }
        browserError = null;
        pendingWebViewAddress = NormalizeBrowserAddress(browserAddress);
        webViewSetupTask = WebViewBrowserRenderer.EnsureRuntimeAsync();
    }
    private void PollWebViewSetup()
    {
        if (webViewSetupTask is not { IsCompleted: true } setup) return;
        webViewSetupTask = null;
        if (setup.IsFaulted)
            browserError = setup.Exception?.GetBaseException().Message;
        else if (setup.IsCanceled)
            browserError = "Edge (WebView2) setup was cancelled. Please try again.";
        else if (!screenController.Engine.PlayBrowser(pendingWebViewAddress ?? browserAddress, useWebView: true))
            browserError = screenController.Engine.LastError;
        pendingWebViewAddress = null;
        if (browserError is not null && youtubeBrowserConnectionPending)
        {
            youtubeBrowserConnectionPending = false;
            youtubeBrowserConnectionMessage = browserError;
            youtubeBrowserConnectionMessageIsError = true;
            currentPage = HomePage.Settings;
            settingsTab = SettingsTab.YouTube;
        }
    }
}
