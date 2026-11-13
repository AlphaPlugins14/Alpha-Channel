namespace AlphaChannel.Plugin.Video;

internal enum LocalVideoBroadcastQuality
{
    DataSaver,
    Standard,
    High,
}

internal readonly record struct LocalVideoBroadcastEncodingSettings(
    int MaximumWidth,
    int MaximumHeight,
    double MaximumFrameRate,
    int VideoBitrateKbps,
    int AudioBitrateKbps);

internal static class LocalVideoBroadcastQualityProfiles
{
    internal static int NormalizeFrameRate(int frameRate) => frameRate is 15 or 24 or 30 ? frameRate : 30;

    internal static LocalVideoBroadcastEncodingSettings Settings(
        this LocalVideoBroadcastQuality quality, int frameRate = 30) =>
        quality switch
        {
            LocalVideoBroadcastQuality.High => new(
                1920,
                1080,
                NormalizeFrameRate(frameRate),
                5000,
                160),
            LocalVideoBroadcastQuality.Standard => new(
                1280,
                720,
                NormalizeFrameRate(frameRate),
                2800,
                128),
            _ => new(
                854,
                480,
                NormalizeFrameRate(frameRate),
                1000,
                96),
        };
}
