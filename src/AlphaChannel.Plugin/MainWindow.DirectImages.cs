using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Auth;
using AlphaChannel.Plugin.Crypto;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private ConversationMemberDto? DirectImageMember(string conversation) =>
        openConversationId == conversation && !openConversationIsGroup && openConversationMembers.Length == 1
            ? openConversationMembers[0]
            : conversations.FirstOrDefault(c => c.ConversationId == conversation && !c.IsGroup && c.Members.Length == 1)?.Members[0];

    private async Task<MessageDto?> RelayDirectImageMessage(CharacterSession session,string conversation,string plaintext)
    {
        // The existing pairwise DM encryption and relay are unchanged. The
        // attachment descriptor (including its image key) exists only inside it.
        var member = DirectImageMember(conversation)
            ?? throw new InvalidOperationException("Open a private one-to-one chat to send an image.");
        if (!IsCurrentMessageSession(session)) throw new InvalidOperationException("Sign in again before sending.");
        if (!await keyVault.EnsureIdentityRegisteredAsync(session.AccountId,session.Token))
            throw new InvalidOperationException("Your encryption key is not registered yet. Please retry shortly.");
        var mine = await keyVault.EnsureIdentityAsync(session.AccountId,session.Token);
        var other = await keyVault.GetOtherPartyKeyAsync(member.AccountId,session.Token)
            ?? throw new InvalidOperationException("The recipient needs to sign in and set up encryption before receiving an image.");
        if (!IsCurrentMessageSession(session)) throw new InvalidOperationException("Sign in again before sending.");
        // Reconcile an uncertain earlier relay before submitting the retained descriptor.
        // Keep the existing server contract: inspect authenticated encrypted history locally.
        var image=GroupChatClient.ImageContent(plaintext)?.Image ?? throw new InvalidOperationException("Invalid image attachment.");
        string? before=null;
        do {
            var page=await dmClient.GetMessagesAsync(session.Token,conversation,before) ?? throw new InvalidOperationException("Could not check image delivery. Retry when connected.");
            foreach(var message in page.Items.Where(m=>m.SenderAccountId==session.AccountId && DmClient.ValidEnvelope(m.Ciphertext,m.Nonce,m.Tag))) {
                var opened=DmCipher.Decrypt(mine,other,Convert.FromBase64String(message.Ciphertext),Convert.FromBase64String(message.Nonce),Convert.FromBase64String(message.Tag));
                if(GroupChatClient.ImageContent(opened?.Plaintext)?.Image.Id==image.Id)return message;
            }
            if(page.Items.Length==0 || page.Items.Min(m=>m.SentAtUnix)<DateTimeOffset.UtcNow.AddHours(-48).ToUnixTimeSeconds() || page.NextCursor is not { } next)break;
            if(before is { } old && next==old)throw new InvalidOperationException("Invalid message history cursor.");
            before=next;
        }while(before is not null);
        if (!IsCurrentMessageSession(session)) throw new InvalidOperationException("Sign in again before sending.");
        var packet = DmCipher.Encrypt(mine,other,plaintext);
        return await dmClient.SendMessageAsync(session.Token,conversation,new SendMessageRequest([
            new MessageEnvelope(member.AccountId,Convert.ToBase64String(packet.Ciphertext),Convert.ToBase64String(packet.Nonce),
                Convert.ToBase64String(packet.Tag),Convert.ToBase64String(packet.CommitmentTag))]));
    }
}
