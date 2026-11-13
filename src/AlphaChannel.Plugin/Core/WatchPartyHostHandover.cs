using AlphaChannel.Contracts;

namespace AlphaChannel.Plugin;

internal static class WatchPartyHostHandover
{
    internal static StreamControl PreserveScreen(StreamControl state, StreamControl inherited) => state with
    {
        Reason = inherited.Reason,
        ScreenX = inherited.ScreenX,
        ScreenY = inherited.ScreenY,
        ScreenZ = inherited.ScreenZ,
        ScreenYaw = inherited.ScreenYaw,
        ScreenScale = inherited.ScreenScale,
        ScreenDisableFixedScaleRatio = inherited.ScreenDisableFixedScaleRatio,
        ScreenWidthScale = inherited.ScreenWidthScale,
        ScreenHeightScale = inherited.ScreenHeightScale,
    };

    internal static StreamControl AtTime(StreamControl state, DateTime receivedUtc, DateTime nowUtc)
    {
        var position = Math.Max(0d, state.PositionSeconds ?? 0d);
        if (state.Paused != true && !string.IsNullOrWhiteSpace(state.Url))
            position += Math.Max(0d, (nowUtc - receivedUtc).TotalSeconds);
        return state with { PositionSeconds = position };
    }
}
