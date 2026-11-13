using System.Net.Http.Headers;
using System.Net.Http.Json;
using AlphaChannel.Contracts;

namespace AlphaChannel.Plugin.Auth;

internal sealed partial class AuthClient
{
    internal async Task<(GuestRegistrationResponse? Result, string? Error)> RegisterGuestAsync(string credential, string name, CancellationToken ct)
    {
        using var http = Http;
        using var response = await http.PostAsJsonAsync("/auth/guest/register", new GuestRegistrationRequest(credential, name), ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return (null, response.StatusCode == System.Net.HttpStatusCode.Conflict ? "That username is already taken. Please choose another." :
                response.StatusCode == System.Net.HttpStatusCode.BadRequest ? "Choose a valid username." :
                response.StatusCode == System.Net.HttpStatusCode.Unauthorized ? "This saved login is no longer available. Sign in with XIV Auth if you previously verified your character." :
                "Account registration is temporarily unavailable. Your local settings are safe; please try again later.");
        return (await response.Content.ReadFromJsonAsync<GuestRegistrationResponse>(cancellationToken: ct).ConfigureAwait(false), null);
    }

    internal async Task<bool> RecordGuestReferenceAsync(string token, GuestCharacterReferenceRequest reference, CancellationToken ct)
    {
        using var http = Http;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.PutAsJsonAsync("/me/guest-reference", reference, ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    internal async Task<AccountGuestStatus[]> GetGuestStatusesAsync(string token, string[] ids, CancellationToken ct)
    {
        using var http = Http;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.PostAsJsonAsync("/accounts/guest-status", new AccountGuestStatusRequest(ids), ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AccountGuestStatus[]>(cancellationToken: ct).ConfigureAwait(false) ?? [];
    }
}
