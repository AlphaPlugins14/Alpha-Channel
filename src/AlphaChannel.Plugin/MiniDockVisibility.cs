namespace AlphaChannel.Plugin;

public enum MiniDockVisibility
{
    AlwaysDisplay,
    PlayingContentOrInWatchParty,
    AlwaysHidden,
}

internal static class MiniDockVisibilityPolicy
{
    internal static MiniDockVisibility FromLegacy(bool hidden) =>
        hidden ? MiniDockVisibility.AlwaysHidden : MiniDockVisibility.AlwaysDisplay;
    internal static bool ShouldShow(MiniDockVisibility mode,bool contentActive,bool inWatchParty) =>
        mode switch
        {
            MiniDockVisibility.AlwaysHidden => false,
            MiniDockVisibility.PlayingContentOrInWatchParty => contentActive || inWatchParty,
            _ => true,
        };
}
