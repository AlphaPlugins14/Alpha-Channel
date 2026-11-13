using AlphaChannel.BrowserShared;
using CefSharp;
using CefSharp.Handler;

internal sealed class AdBlockRequestHandler(EasyListBlocker blocker, NavigationGuard guard) : RequestHandler
{
    protected override bool OnBeforeBrowse(IWebBrowser chromium, IBrowser browser, IFrame frame, IRequest request, bool userGesture, bool isRedirect) =>
        frame.IsMain && guard.Cancel(request.Url, frame.Url, redirect: isRedirect);
    protected override IResourceRequestHandler GetResourceRequestHandler(IWebBrowser chromium, IBrowser browser, IFrame frame, IRequest request, bool navigation, bool download, string initiator, ref bool disableDefaultHandling) => new AdBlockResourceHandler(blocker, guard);
}
internal sealed class AdBlockResourceHandler(EasyListBlocker blocker, NavigationGuard guard) : ResourceRequestHandler
{
    protected override void OnResourceRedirect(IWebBrowser chromium, IBrowser browser, IFrame frame, IRequest request, IResponse response, ref string newUrl)
    {
        if (request.ResourceType == ResourceType.MainFrame && guard.Cancel(newUrl, request.Url, redirect: true))
        {
            // OnBeforeBrowse is not raised for every HTTP redirect in CEF.
            // Break the redirect chain and stop before its target is loaded.
            newUrl = "about:blank";
            browser.StopLoad();
        }
    }
    protected override CefReturnValue OnBeforeResourceLoad(IWebBrowser chromium, IBrowser browser, IFrame frame, IRequest request, IRequestCallback callback)
    {
        if (request.ResourceType == ResourceType.MainFrame && guard.Cancel(request.Url, frame.Url, redirect: true)) return CefReturnValue.Cancel;
        var type = request.ResourceType.ToString().ToLowerInvariant() switch
        {
            "mainframe" => "document", "subframe" => "subdocument", "xhr" => "xmlhttprequest",
            "fontresource" => "font", "script" => "script", "image" => "image", "stylesheet" => "stylesheet",
            "media" => "media", "ping" => "ping", _ => "other"
        };
        return blocker.Blocks(request.Url, request.Headers["Referer"] ?? frame.Url, type) ? CefReturnValue.Cancel : CefReturnValue.Continue;
    }
}
internal sealed class AdBlockLifeSpanHandler(EasyListBlocker blocker, NavigationGuard guard) : LifeSpanHandler
{
    protected override bool OnBeforePopup(IWebBrowser chromium, IBrowser browser, IFrame frame, string targetUrl, string targetFrameName, WindowOpenDisposition disposition, bool userGesture, IPopupFeatures features, IWindowInfo windowInfo, IBrowserSettings settings, ref bool noJavascriptAccess, out IWebBrowser newBrowser)
    {
        newBrowser = null!;
        if (!guard.Cancel(targetUrl, frame.Url, popup: true) && !blocker.Blocks(targetUrl, frame.Url, "popup")) browser.MainFrame.LoadUrl(targetUrl);
        return true;
    }
}
