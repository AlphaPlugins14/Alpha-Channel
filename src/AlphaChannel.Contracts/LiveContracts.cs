namespace AlphaChannel.Contracts;

public sealed record LiveStatusDto(bool HasKey, bool IsLive, string? HlsUrl);

public sealed record RotateStreamKeyResponse(string StreamKey);

// Null means the account already has a key, which must not be replaced automatically.
public sealed record EnsureStreamKeyResponse(string? StreamKey);

public sealed record LiveFriendDto(string AccountId, string DisplayName, string HlsUrl, long StartedAtUnix);
