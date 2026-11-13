namespace AlphaChannel.Contracts;

public static class TweeterLimits
{
    public const int MaxPostLength = 280;
}

// ParentPostId set = a reply; ImageUrl is a link (not an uploaded file - see Post.ImageUrl's
// server-side doc comment), rendered client-side by fetching and decoding it, same as thumbnail
// handling elsewhere in this plugin.
public sealed record CreatePostRequest(string Body, string? ParentPostId = null, string? ImageUrl = null);

// Body is an optional quote-comment - empty/null for a plain repost.
public sealed record RepostRequest(string? Body);

public sealed record PostDto(
    string Id,
    string AuthorAccountId,
    string AuthorHandle,
    string AuthorDisplayName,
    string Body,
    long CreatedAtUnix,
    int LikeCount,
    bool LikedByMe,
    string? ParentPostId,
    int ReplyCount,
    string? ImageUrl,
    string? RepostOfPostId,
    string? RepostOfAuthorDisplayName,
    string? RepostOfBody,
    string? RepostOfImageUrl);

public sealed record TimelinePage(PostDto[] Items, string? NextCursor);

public sealed record FollowSummaryDto(string AccountId, string Handle, string DisplayName);
