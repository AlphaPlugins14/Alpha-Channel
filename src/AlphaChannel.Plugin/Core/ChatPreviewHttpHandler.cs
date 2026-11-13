using System.Net;
using System.Net.Sockets;

namespace AlphaChannel.Plugin.Core;

// Untrusted chat links must never reach loopback/LAN services. Pin each socket to
// a validated DNS result, including redirects, rather than resolving twice.
internal sealed class ChatPreviewHttpHandler : DelegatingHandler
{
    internal int ImageByteLimit { get; set; } = 4 * 1024 * 1024;
    internal ChatPreviewHttpHandler() : base(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(6), MaxConnectionsPerServer = 2,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectCallback = ConnectPublicAsync
    }) { }

    internal ChatPreviewHttpHandler(HttpMessageHandler transport) : base(transport) { }

    private static async ValueTask<Stream> ConnectPublicAsync(SocketsHttpConnectionContext context, CancellationToken token)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token).ConfigureAwait(false);
        if (addresses.Length == 0 || addresses.Any(a => !ChatLinkPreview.IsPublicAddress(a)))
            throw new HttpRequestException("Preview destination is not public.");
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); token.ThrowIfCancellationRequested(); }
        }
        throw new HttpRequestException("Preview destination could not be reached.");
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var current = request.RequestUri;
        for (var hop = 0; hop <= 4; hop++)
        {
            if (!ChatLinkPreview.TryPublicUrl(current?.AbsoluteUri, out _))
                throw new HttpRequestException("Unsupported preview URL.");
            using var next = new HttpRequestMessage(HttpMethod.Get, current);
            next.Headers.UserAgent.ParseAdd("AlphaChannel-LinkPreview/1.0");
            var response = await base.SendAsync(next, token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or
                HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null || hop == 4) throw new HttpRequestException("Preview redirect limit reached.");
                current = new Uri(current!, location);
                continue;
            }
            try
            {
                response.EnsureSuccessStatusCode();
                var type = response.Content.Headers.ContentType?.MediaType ?? "";
                var image = type.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
                if (!image && type is not ("text/html" or "application/xhtml+xml"))
                    throw new HttpRequestException("Unsupported preview content.");
                var limit = image ? ImageByteLimit : 1024 * 1024;
                if (response.Content.Headers.ContentLength > limit) throw new HttpRequestException("Preview is too large.");
                using var body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                using var buffer = new MemoryStream();
                var chunk = new byte[8192];
                int read;
                while ((read = await body.ReadAsync(chunk, token).ConfigureAwait(false)) != 0)
                {
                    if (buffer.Length + read > limit) throw new HttpRequestException("Preview is too large.");
                    buffer.Write(chunk, 0, read);
                }
                var content = new ByteArrayContent(buffer.ToArray());
                foreach (var header in response.Content.Headers)
                    if (!header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                        content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                response.Content.Dispose();
                response.Content = content;
                response.RequestMessage = request;
                // Preserve the final URL for relative Open Graph image paths.
                request.RequestUri = current;
                return response;
            }
            catch { response.Dispose(); throw; }
        }
        throw new HttpRequestException("Preview redirect limit reached.");
    }
}
