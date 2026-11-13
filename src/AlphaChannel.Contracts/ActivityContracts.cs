namespace AlphaChannel.Contracts;

public sealed record ActivityEventDto(
    string Id,
    string ActorAccountId,
    string ActorHandle,
    string ActorDisplayName,
    string Type,
    string? Metadata,
    long CreatedAtUnix);

// NextCursor is opaque to the client - pass it back as `before` on the next page request, null
// means there's nothing older.
public sealed record ActivityPage(ActivityEventDto[] Items, string? NextCursor);

public sealed record MarkActivityReadRequest(long UpToUnix);

public sealed record UnreadCountResponse(int Count);
