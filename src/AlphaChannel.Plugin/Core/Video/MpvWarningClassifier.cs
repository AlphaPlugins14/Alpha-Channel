namespace AlphaChannel.Plugin.Video;

internal enum MpvWarningKind { Network, SourceAccess, SourceLookup, Timing, Audio, Other }

// Classify internally; raw native messages can contain signed URLs, cookies or paths.
internal static class MpvWarningClassifier
{
    internal static MpvWarningKind Classify(string? prefix,string message)
    {
        var text=message.ToLowerInvariant();
        if(text.Contains("403")||text.Contains("forbidden")||text.Contains("unauthorized")||
            text.Contains("410 gone")||text.Contains("sign in")||text.Contains("video unavailable"))return MpvWarningKind.SourceAccess;
        if(text.Contains("timed out")||text.Contains("timeout")||text.Contains("connection")||
            text.Contains("http error")||text.Contains("network")||text.Contains("tls")||text.Contains("failed to reconnect"))return MpvWarningKind.Network;
        if((prefix??"").Contains("ytdl",StringComparison.OrdinalIgnoreCase)||text.Contains("yt-dlp")||
            text.Contains("extractor")||text.Contains("youtube-dl"))return MpvWarningKind.SourceLookup;
        if(text.Contains("desync")||text.Contains("dropping")||text.Contains("too slow")||
            text.Contains("late frame")||text.Contains("timestamp")||text.Contains("non-monoton"))return MpvWarningKind.Timing;
        if((prefix??"").StartsWith("ao/",StringComparison.OrdinalIgnoreCase)||text.Contains("audio device"))return MpvWarningKind.Audio;
        return MpvWarningKind.Other;
    }
}
