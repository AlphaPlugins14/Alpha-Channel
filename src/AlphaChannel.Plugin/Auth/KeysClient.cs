using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net;
using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Net;

namespace AlphaChannel.Plugin.Auth;

internal sealed class KeysClient(Configuration configuration)
{
    // A missing key is different from a failed request: only the former should prompt an upload.
    internal async Task<(bool Succeeded, string? PublicKeyBase64)> GetOwnPublicKeyAsync(string bearerToken)
    {
        using var http = PluginHttpClients.CreateApiClient(configuration, bearerToken);
        try
        {
            using var response = await http.GetAsync("/keys/me").ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return (true, null);
            if (!NetworkFailureClassifier.IsSuccess("Keys", "Check own public key", response))
                return (false, null);

            var dto = await response.Content.ReadFromJsonAsync<PublicKeyDto>().ConfigureAwait(false);
            return dto?.PublicKeyBase64 is { Length: > 0 } key
                ? (true, key)
                : (false, null);
        }
        catch (Exception exception)
        {
            NetworkFailureClassifier.FromException("Keys", "Check own public key", exception);
            return (false, null);
        }
    }

    internal async Task<bool> UploadPublicKeyAsync(string bearerToken, string publicKeyBase64)
    {
        using var http = PluginHttpClients.CreateApiClient(configuration, bearerToken);
        try
        {
            var response = await http.PutAsJsonAsync("/keys/me", new UploadPublicKeyRequest(publicKeyBase64)).ConfigureAwait(false);
            return NetworkFailureClassifier.IsSuccess("Keys", "Upload public key", response);
        }
        catch (Exception exception)
        {
            NetworkFailureClassifier.FromException("Keys", "Upload public key", exception);
            return false;
        }
    }

    internal async Task<string?> GetPublicKeyAsync(string bearerToken, string accountId)
    {
        var (_, key) = await CheckUserPublicKeyAsync(bearerToken, accountId).ConfigureAwait(false);
        return key;
    }

    // Succeeded + null means the account has no key; false means the check could not be completed.
    internal async Task<(bool Succeeded, string? PublicKeyBase64)> CheckUserPublicKeyAsync(
        string bearerToken, string accountId)
    {
        using var http = PluginHttpClients.CreateApiClient(configuration, bearerToken);
        try
        {
            using var response = await http.GetAsync($"/keys/users/{accountId}").ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return (true, null);
            if (!NetworkFailureClassifier.IsSuccess("Keys", "Fetch public key", response))
            {
                return (false, null);
            }

            var dto = await response.Content.ReadFromJsonAsync<PublicKeyDto>().ConfigureAwait(false);
            return dto?.PublicKeyBase64 is { Length: > 0 } key
                ? (true, key)
                : (false, null);
        }
        catch (Exception exception)
        {
            NetworkFailureClassifier.FromException("Keys", "Fetch public key", exception);
            return (false, null);
        }
    }
}
