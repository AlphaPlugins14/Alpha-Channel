namespace AlphaChannel.Plugin.YouTube;

// Used only by the guest metadata client. The transport
// also disables its cookie jar so redirects cannot silently add cookies back.
internal sealed class YouTubeGuestNoCookieHandler(HttpMessageHandler transport) : DelegatingHandler(transport)
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        request.Headers.Remove("Cookie");
        return base.SendAsync(request, token);
    }
}
