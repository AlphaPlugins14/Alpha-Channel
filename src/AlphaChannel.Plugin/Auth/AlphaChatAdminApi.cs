using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace AlphaChannel.Plugin.Auth;

internal static class AlphaChatAdminApi
{
    internal static async Task<JsonElement> RequestAsync(string token, string path, object? body = null)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = body is null
            ? await http.GetAsync(AlphaChatClient.WorkerUrl + path).ConfigureAwait(false)
            : await http.PostAsJsonAsync(AlphaChatClient.WorkerUrl + path, body, AlphaChatClient.JsonOptions).ConfigureAwait(false);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(document.RootElement.TryGetProperty("error", out var error) ? error.GetString() : "Request failed.");
        return document.RootElement.Clone();
    }
}
