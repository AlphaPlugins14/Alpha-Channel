using System.Text.Json.Serialization;

namespace AlphaChannel.Plugin.Auth;

internal sealed record GroupReadRange(long StartSequence, long EndSequence);
internal sealed record GroupDevice(string AccountId, string DeviceId, string EncryptionKey, string SigningKey);
internal sealed record GroupMember(string AccountId, string Handle, string DisplayName);
internal sealed record GroupKeyPackage(string AccountId, string DeviceId, string Ciphertext, string Nonce, string Tag,
    string? SenderAccountId = null, string? SenderDeviceId = null, string? SenderEncryptionKey = null);
internal sealed record GroupSummary(string Id, string Name, string OwnerId, int Version, long JoinedSequence,
    long LastSequence, long? LastMessageAtUnix, int UnreadCount, GroupMember[] Members, bool IsPublic = false, string Description = "", string Category = "Social", string? AvatarIcon = null, string? AvatarColorHex = null, bool JoinLocked = false, int SlowSeconds = 0, GroupReadRange[]? SeenRanges = null, long? SeenAtUnix = null);
internal sealed record GroupInvitation(string Id, string Name, string Inviter);
internal sealed record GroupState(GroupSummary[] Groups, GroupInvitation[] Invitations, long Revision = 0);
internal sealed record GroupEvent(long Revision, string? GroupId, string? MessageId, string? SenderAccountId,
    GroupSummary? Group, GroupInvitation? Invitation, string? RemoveGroup, string? RemoveInvite, bool KeysChanged = false);
internal sealed record GroupInfo(string Id, string Name, string OwnerId, int Version, long JoinedSequence,
    long LastSequence, long? LastMessageAtUnix, int UnreadCount, GroupMember[] Members,
    GroupKeyPackage[] Packages, GroupDevice[] Missing, GroupDevice[] Devices, string[] Invitations, bool IsPublic = false, string? PublicKey = null, string? AvatarIcon = null, string? AvatarColorHex = null, bool JoinLocked = false, int SlowSeconds = 0);
internal sealed record PublicGroupListing(string Id, string Name, string Description, string Category, string OwnerId, int MemberCount, long CreatedAt, bool Closed = false, string? AvatarIcon = null, string? AvatarColorHex = null, string? OwnerDisplayName = null, bool JoinLocked = false, int SlowSeconds = 0, long ReportCount = 0);
internal sealed record PublicGroupDirectory(PublicGroupListing[] Groups, string? NextCursor = null);
internal sealed record GroupBans(GroupMember[] Members);
internal sealed record GroupMessage(string Id, string SenderAccountId, string DeviceId, string Ciphertext,
    string Nonce, string Tag, string Commitment, string Signature, string SigningKey, long Sequence = 0,
    long SentAtUnix = 0, bool Deleted = false, string? MediaId = null, string? MediaDigest = null, long MediaExpiresAt = 0);
internal sealed record GroupMessagePage(GroupMessage[] Items, string? NextCursor);
internal sealed record GroupReport(string Id, string GroupId, string MessageId, string ReporterId, string SenderId,
    string Plaintext, long CreatedAt, bool Verified, string? MediaId = null, Crypto.GroupChatCrypto.Sealed? Evidence = null,
    GroupKeyPackage[]? Packages = null, GroupMessage? Message = null, bool ImageDeleted = false, bool Direct = false, string Status = "Open", string[]? Notes = null, bool IsPublic = false);
internal sealed record GroupDevices(GroupDevice[] Devices);
internal sealed record GroupReports(GroupReport[] Reports, string? NextCursor = null);
internal sealed record GroupSession(string AccountId, string DisplayName, string Handle, string VaultKey, string Token, long Expires,
    bool RenewInPlace = false);
internal sealed record GroupImage(string Id, string GroupId, string MessageId, string SenderId, string DeviceId,
    string Key, string Nonce, string Tag, string Digest, int Width, int Height, string Format = "webp",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Direct = false);
internal sealed record GroupImageContent(string Text, GroupImage Image, string? ReplyTo = null);
internal sealed record GroupImageAllowance(int Used, int Limit);
internal sealed record GroupImageStorage(long StoredBytes, long StoredImages, long HeldBytes, long HeldImages, long ActiveImages,
    long ReservedBytes, long AllocatedBytes, long UploadCeilingBytes, long AccountLimitBytes, long MeasuredAt);
internal static class GroupImagePolicy
{
    internal const int RetentionHours = 48;
    internal static long ExpiresAt(GroupMessage message) => message.SentAtUnix > 0
        ? Math.Min(message.MediaExpiresAt > 0 ? message.MediaExpiresAt : long.MaxValue, message.SentAtUnix + RetentionHours * 3600L)
        : message.MediaExpiresAt;
    internal static string Countdown(long expiry, long now)
    {
        var minutes = (Math.Max(0,expiry-now) + 59) / 60;
        return $"Expires: {minutes / 60:00}h:{minutes % 60:00}m";
    }
}

internal sealed record PendingDirectImage(string Text,string Signature,string PayloadDigest,long ExpiresAt,long CreatedAt);
