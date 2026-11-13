using System.Net.Http.Headers;
using System.Net.Http.Json;
using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Net;
using AlphaChannel.Plugin.Crypto;

namespace AlphaChannel.Plugin.Auth;

internal sealed partial class DmClient(Configuration configuration)
{
    internal GroupChatClient Groups { get; } = new();
    private HttpClient Http(string bearerToken)
    {
        return PluginHttpClients.CreateApiClient(configuration, bearerToken);
    }

    // The server transport is now strictly 1:1. New groups always use Cloudflare;
    // legacy server group support is retained in the protocol but disabled here.
    internal async Task<string?> CreateConversationAsync(string bearerToken, string[] memberAccountIds, string? name = null)
    {
        lock (directConversationGate) if (!AcceptDirectConversationAccount(bearerToken)) return null;
        var members = memberAccountIds.Select(id => Guid.TryParse(id, out var parsed) ? parsed.ToString() : null).ToArray();
        if (members.Length == 0 || members.Any(id => id is null)) return null;
        var uniqueMembers = members.Select(id => id!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (uniqueMembers.Length > 1)
            return await Groups.CreateAsync(bearerToken, uniqueMembers, name ?? "Group chat").ConfigureAwait(false);
        using var http = Http(bearerToken);
        try
        {
            using var response = await http.PostAsJsonAsync("/dm/conversations", new CreateConversationRequest(uniqueMembers, null)).ConfigureAwait(false);
            if (!NetworkFailureClassifier.IsSuccess("Messages", "Create conversation", response))
            {
                return null;
            }

            using var doc = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            var id = doc.RootElement.TryGetProperty("conversationId", out var idEl) ? idEl.GetString() : null;
            RememberCreatedDirectConversation(bearerToken, id);
            return id;
        }
        catch (Exception exception)
        {
            NetworkFailureClassifier.FromException("Messages", "Create conversation", exception);
            return null;
        }
    }

    internal async Task<ConversationSummaryDto[]?> GetConversationsAsync(string bearerToken)
    {
        lock (directConversationGate) if (!AcceptDirectConversationAccount(bearerToken)) return null;
        var direct = await LoadDirectConversationsAsync(bearerToken).ConfigureAwait(false);
        lock (directConversationGate) if (!AcceptDirectConversationAccount(bearerToken)) return null;
        return direct is null ? (Groups.Ready ? Groups.Conversations(bearerToken) : null) : [.. direct, .. Groups.Conversations(bearerToken)];
    }

    internal async Task<MessagePage?> GetMessagesAsync(string bearerToken, string conversationId, string? before)
    {
        if (GroupChatClient.IsGroup(conversationId))
            return await GetGroupMessagesAsync(bearerToken, conversationId, before is null ? null : long.Parse(before, System.Globalization.CultureInfo.InvariantCulture)).ConfigureAwait(false);
        if (!await IsDirectConversationAsync(bearerToken, conversationId).ConfigureAwait(false)) return null;
        if (before is not null && !DmPageCursor.TryDecode(before, out _, out _)) throw new InvalidOperationException("Invalid message history cursor.");
        return await GetAsync<MessagePage>(bearerToken, $"/dm/conversations/{conversationId}/messages?pagination=precise" +
            (before is null ? "" : "&cursor=" + Uri.EscapeDataString(before))).ConfigureAwait(false);
    }

    private async Task<MessagePage?> GetGroupMessagesAsync(string token, string conversationId, long? before) =>
        await Groups.MessagesAsync(token, conversationId, before).ConfigureAwait(false);

    internal async Task<MessageDto?> SendMessageAsync(string bearerToken, string conversationId, SendMessageRequest request)
    {
        if (!await IsDirectConversationAsync(bearerToken, conversationId).ConfigureAwait(false)) return null;
        if (request.Envelopes is not { Length: > 0 and <= 64 } ||
            request.Envelopes.Select(e=>e.RecipientAccountId).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=request.Envelopes.Length ||
            request.Envelopes.Any(e=>!Guid.TryParseExact(e.RecipientAccountId,"D",out _) || !ValidEnvelope(e.Ciphertext,e.Nonce,e.Tag) || !ValidBase64(e.CommitmentTag,DmCipher.CommitmentTagLength,DmCipher.CommitmentTagLength)))
            throw new InvalidOperationException("Invalid encrypted message envelopes.");
        using var http = Http(bearerToken);
        try
        {
            var response = await http.PostAsJsonAsync($"/dm/conversations/{conversationId}/messages", request).ConfigureAwait(false);
            return NetworkFailureClassifier.IsSuccess("Messages", "Send message", response)
                ? await response.Content.ReadFromJsonAsync<MessageDto>().ConfigureAwait(false)
                : null;
        }
        catch (Exception exception)
        {
            NetworkFailureClassifier.FromException("Messages", "Send message", exception);
            return null;
        }
    }

    private static bool ValidBase64(string? value,int minimum,int maximum)
    {
        if (value is null || value.Length>4*((maximum+2)/3)) return false;
        try {var bytes=Convert.FromBase64String(value);return bytes.Length>=minimum && bytes.Length<=maximum;}
        catch(FormatException){return false;}
    }
    internal static bool ValidEnvelope(string? ciphertext,string? nonce,string? tag) =>
        ValidBase64(ciphertext,32,65536) && ValidBase64(nonce,12,12) && ValidBase64(tag,16,16);

    internal async Task MarkReadAsync(string bearerToken, string conversationId)
    {
        if (GroupChatClient.IsGroup(conversationId)) { await Groups.MarkReadAsync(bearerToken, conversationId).ConfigureAwait(false); return; }
        if (!await IsDirectConversationAsync(bearerToken, conversationId).ConfigureAwait(false)) return;
        using var http = Http(bearerToken);
        try
        {
            using var response = await http.PostAsync($"/dm/conversations/{conversationId}/read", null).ConfigureAwait(false);
            _ = NetworkFailureClassifier.IsSuccess("Messages", "Mark conversation read", response);
        }
        catch (Exception exception)
        {
            NetworkFailureClassifier.FromException("Messages", "Mark conversation read", exception);
        }
    }

    internal async Task<bool> DeleteMessageForAllAsync(string bearerToken, string conversationId,
        string groupId)
    {
        if (GroupChatClient.IsGroup(conversationId)) return await Groups.DeleteAsync(bearerToken, conversationId, groupId).ConfigureAwait(false);
        if (!await IsDirectConversationAsync(bearerToken, conversationId).ConfigureAwait(false)) return false;
        using var http = Http(bearerToken);
        try
        {
            using var response = await http.DeleteAsync($"/dm/conversations/{conversationId}/messages/{groupId}")
                .ConfigureAwait(false);
            return NetworkFailureClassifier.IsSuccess("Messages", "Delete message", response);
        }
        catch (Exception exception)
        {
            NetworkFailureClassifier.FromException("Messages", "Delete message", exception);
            return false;
        }
    }

    private async Task<T?> GetAsync<T>(string bearerToken, string path)
    {
        using var http = Http(bearerToken);
        try
        {
            var response = await http.GetAsync(path).ConfigureAwait(false);
            return NetworkFailureClassifier.IsSuccess("Messages", $"Request {path}", response)
                ? await response.Content.ReadFromJsonAsync<T>().ConfigureAwait(false)
                : default;
        }
        catch (Exception exception)
        {
            NetworkFailureClassifier.FromException("Messages", $"Request {path}", exception);
            return default;
        }
    }
}
