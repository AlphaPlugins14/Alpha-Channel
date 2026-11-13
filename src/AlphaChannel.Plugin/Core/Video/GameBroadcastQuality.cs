namespace AlphaChannel.Plugin.Video;

internal enum GameBroadcastQuality
{
    DataSaver,
    StandardNative,
    // Legacy saved value; existing Low CPU selections are treated as Data Saver.
    LowCpu,
}

internal readonly record struct GameBroadcastEncodingSettings(
    double? OutputFrameRate,
    int VideoBitrateKbps,
    int AudioBitrateKbps,
    int? EncoderThreads);

internal static class GameBroadcastQualityProfiles
{
    internal static int NormalizeFrameRate(int frameRate) => frameRate is 0 or 15 or 24 ? frameRate : 24;

    internal static GameBroadcastEncodingSettings Settings(
        this GameBroadcastQuality quality,
        double nativeFrameRate, int frameRate = 24) =>
        quality switch
        {
            GameBroadcastQuality.StandardNative => new(
                NormalizeFrameRate(frameRate) == 0 ? null : Math.Min(nativeFrameRate, NormalizeFrameRate(frameRate)),
                1200,
                128,
                null),
            _ => new(
                NormalizeFrameRate(frameRate) == 0 ? null : Math.Min(nativeFrameRate, NormalizeFrameRate(frameRate)),
                500,
                96,
                2),
        };
}
