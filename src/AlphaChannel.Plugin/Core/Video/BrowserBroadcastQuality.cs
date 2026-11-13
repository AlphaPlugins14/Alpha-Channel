namespace AlphaChannel.Plugin.Video;

internal enum BrowserBroadcastQuality
{
    DataSaver,
    Standard,
    High,
}

internal readonly record struct BrowserBroadcastEncodingSettings(
    int OutputWidth,
    int OutputHeight,
    double OutputFrameRate,
    int VideoBitrateKbps,
    int AudioBitrateKbps);

internal static class BrowserBroadcastQualityProfiles
{
    internal static int NormalizeFrameRate(int frameRate) =>
        frameRate is 15 or 20 or 25 or 30 ? frameRate : 20;

    internal static BrowserBroadcastQuality ForPatreonTier(this BrowserBroadcastQuality quality, int tier) => quality switch
    {
        BrowserBroadcastQuality.High when tier >= 3 => BrowserBroadcastQuality.High,
        BrowserBroadcastQuality.Standard or BrowserBroadcastQuality.High when tier >= 2 => BrowserBroadcastQuality.Standard,
        _ => BrowserBroadcastQuality.DataSaver,
    };

    internal static BrowserBroadcastEncodingSettings Settings(
        this BrowserBroadcastQuality quality, int frameRate = 20) =>
        quality switch
        {
            BrowserBroadcastQuality.High => new(960, 540, NormalizeFrameRate(frameRate), 1900, 128),
            BrowserBroadcastQuality.Standard => new(
                960,
                540,
                NormalizeFrameRate(frameRate),
                1500,
                128),
            _ => new(
                640,
                360,
                NormalizeFrameRate(frameRate),
                700,
                96),
        };
}
