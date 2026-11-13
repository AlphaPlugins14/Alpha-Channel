namespace AlphaChannel.Plugin.Core;

// Design units, independent of page contents, activity state and online counts.
internal readonly record struct SharedHeaderLayout(float Width, bool Compact, float Left,
    float ProfileY, float ActionsY, float UtilitiesX, float TitleRight, float SearchY, float Height)
{
    internal const float UtilityTop = 12f;
    internal const float UtilityRowGap = 47f;

    internal float CountsLeft => UtilitiesX + 33f - UsersWidth - 8f;
    internal float CountsY => UtilityTop;
    internal float SocialY => UtilityTop + UtilityRowGap;
    internal float UsersWidth => Compact ? 100f : 116f;
    internal float FriendsWidth => Compact ? 42f : 48f;
    internal float ChatsWidth => FriendsWidth;
    internal float CountGap => Compact ? 6f : 10f;
    internal float FriendsLeft => ChatsLeft - FriendsWidth - CountGap;
    internal float ChatsLeft => UtilitiesX - CountGap - ChatsWidth;
    internal float CountsRight => CountsLeft + UsersWidth;
    internal float IdentityRight => (Compact ? FriendsLeft : CountsLeft) - 8f;
    internal float ProfileWidth => IdentityRight - Left;
    internal float ActionLeft(float actionWidth) => Compact ? Left : MathF.Max(12f, MathF.Min(Left, FriendsLeft - 12f - actionWidth));
    internal float SearchRight(bool hasActions, float actionWidth = 280f) => Compact ? Width - 12f :
        hasActions ? MathF.Min(TitleRight, ActionLeft(actionWidth) - 12f) : FriendsLeft - 12f;
    internal static SharedHeaderLayout Calculate(float width, bool home, float? desiredProfileWidth = null)
    {
        width = MathF.Max(320f, width);
        var compact = width < 660f;
        var left = compact ? 12f : width - 416f;
        if (desiredProfileWidth is { } desired && !compact)
        {
            var identityRight = width - 199f;
            left = Math.Clamp(identityRight - desired, MathF.Max(120f, width - 540f), identityRight - 96f);
        }
        return new(width, compact, left, compact ? 57f : 10f, compact ? 92f : 58f,
            width - 100f, compact ? width - 187f : left - 12f,
            compact ? 132f : 56f, compact ? home ? 174f : 130f : 82f);
    }
}
