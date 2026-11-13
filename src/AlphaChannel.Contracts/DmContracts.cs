namespace AlphaChannel.Contracts;

// 1 member = a 1:1 DM (an existing one with exactly this pair is reused, same "start or resume"
// behavior as before); 2+ members = always creates a new group. Every member must be an accepted
// friend of the caller - see DmService.CreateConversationAsync.
public sealed record CreateConversationRequest(string[] MemberAccountIds, string? Name);

// One ciphertext envelope per other conversation member - the server relays each blind, and never
// sees a key. See DmMessage's doc comment for why this is N independently-encrypted copies rather
// than one shared ciphertext (sender-side pairwise fan-out, not new crypto).
public sealed record MessageEnvelope(string RecipientAccountId, string Ciphertext, string Nonce, string Tag, string CommitmentTag);

public sealed record SendMessageRequest(MessageEnvelope[] Envelopes);

// RecipientAccountId is which member's pairwise key this specific Ciphertext was encrypted with -
// the client needs it to pick the right public key when decrypting (its own key if
// RecipientAccountId is them, otherwise it's their own sent message and they decrypt using that
// recipient's public key instead - see DmCipher's doc comment on why that reproduces the same
// shared secret either way). ReadAtUnix is only ever populated for a message you sent in a 1:1
// conversation - see DmService.GetMessagesAsync's own doc comment on why group read receipts
// aren't a thing here.
public sealed record MessageDto(
    string Id,
    string GroupId,
    string SenderAccountId,
    string RecipientAccountId,
    string Ciphertext,
    string Nonce,
    string Tag,
    long SentAtUnix,
    long? ReadAtUnix);

public sealed record MessagePage(MessageDto[] Items, string? NextCursor);

public sealed record ConversationMemberDto(string AccountId, string Handle, string DisplayName);

// Members excludes the caller themselves - for a 1:1 this is exactly one entry (the other party,
// same shape client code already expected), for a group it's everyone else. No last-message
// preview - the server never has plaintext to preview.
public sealed record ConversationSummaryDto(
    string ConversationId,
    bool IsGroup,
    string? Name,
    ConversationMemberDto[] Members,
    long? LastMessageAtUnix,
    int UnreadCount);

public sealed record UploadPublicKeyRequest(string PublicKeyBase64);

public sealed record PublicKeyDto(string PublicKeyBase64);
