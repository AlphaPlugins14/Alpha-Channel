using System.Net.Http.Headers;
using System.Net.Http.Json;
using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Net;

namespace AlphaChannel.Plugin.Auth;

internal sealed class RadioClient(Configuration configuration)
{
    private HttpClient Http(string bearerToken)
    {
        return PluginHttpClients.CreateApiClient(configuration, bearerToken);
    }

    internal async Task<RadioCredentialsDto?> IssueAsync(string bearerToken)
    {
        using var http = Http(bearerToken);
        try
        {
            var response = await http.PostAsync("/radio/me", null).ConfigureAwait(false);
            return NetworkFailureClassifier.IsSuccess("Radio", "Issue DJ credentials", response)
                ? await response.Content.ReadFromJsonAsync<RadioCredentialsDto>().ConfigureAwait(false)
                : null;
        }
        catch (Exception exception)
        {
            NetworkFailureClassifier.FromException("Radio", "Issue DJ credentials", exception);
            return null;
        }
    }
    internal async Task<bool> IsOnAirAsync(
    string listenUrl,
    CancellationToken cancellationToken = default,
    bool requireAudio = false)
    {
        if (!Uri.TryCreate(
                listenUrl,
                UriKind.Absolute,
                out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp &&
             uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        using var timeout =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);

        timeout.CancelAfter(
            TimeSpan.FromSeconds(
                4));

        using var http =
            PluginHttpClients.CreateProbeClient();

        try
        {
            //
            // Icecast streams do not complete while broadcasting.
            // ResponseHeadersRead lets us check the HTTP response without
            // downloading or beginning playback of the live audio.
            //

            using var request =
                new HttpRequestMessage(
                    HttpMethod.Get,
                    uri);

            using var response =
                await http.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        timeout.Token)
                    .ConfigureAwait(false);

            if (!NetworkFailureClassifier.IsSuccess("RadioProbe", "Check Icecast stream", response)) return false;
            if (!requireAudio) return true;
            // A successful header can precede a source disconnect. Studio TV
            // startup also requires the listener endpoint to deliver audio.
            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (contentType == null || !contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) return false;
            using var audio = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var buffer = new byte[1024];
            await audio.ReadExactlyAsync(buffer, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException exception)
        {
            NetworkFailureClassifier.FromException(
                "RadioProbe",
                "Check Icecast stream",
                exception,
                cancellationToken.IsCancellationRequested);
            return false;
        }
        catch (HttpRequestException exception)
        {
            NetworkFailureClassifier.FromException("RadioProbe", "Check Icecast stream", exception);
            return false;
        }
        catch (Exception exception)
        {
            NetworkFailureClassifier.FromException("RadioProbe", "Check Icecast stream", exception);

            return false;
        }
    }
}
