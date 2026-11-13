namespace AlphaChannel.Plugin.Video;

internal enum AudioVisualizerTheme
{
    AlphaPurple,
    ElectricBlue,
    NeonCyan,
    Emerald,
    HotPink,
    SunsetOrange,
    Crimson,
    White,
    PurplePink,
    BlueCyan,
    Sunset,
    Rainbow,
    Yellow,
    Lime,
    Lavender,
    BluePurple,
    PinkCyan,
    GreenYellow,
    Aurora,
    CandyWave,
    Halloween
}

internal sealed record AudioVisualizerSelection(
    string MediaUrl,
    AudioVisualizerMode Mode,
    AudioVisualizerTheme Theme)
{
    internal static AudioVisualizerSelection Parse(
        string transmittedUrl)
    {
        if (string.IsNullOrWhiteSpace(transmittedUrl))
        {
            return new AudioVisualizerSelection(
                transmittedUrl,
                AudioVisualizerMode.ClassicBars,
                AudioVisualizerTheme.AlphaPurple);
        }

        // A webpage video choice is synchronization metadata, not a visualizer.
        if (transmittedUrl.Contains("#alphachannel-media-v1=", StringComparison.Ordinal))
            return new(transmittedUrl, AudioVisualizerMode.ClassicBars, AudioVisualizerTheme.AlphaPurple);

        var fragmentIndex =
            transmittedUrl.IndexOf('#');

        if (fragmentIndex < 0)
        {
            return new AudioVisualizerSelection(
                transmittedUrl,
                AudioVisualizerMode.ClassicBars,
                AudioVisualizerTheme.AlphaPurple);
        }

        var mediaUrl =
            transmittedUrl[..fragmentIndex];

        var fragment =
            transmittedUrl[(fragmentIndex + 1)..];

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var metadata = fragment;
        if (metadata.Length <= 32768)
        {
            foreach (var part in metadata.Split('&', 65, StringSplitOptions.RemoveEmptyEntries).Take(64))
            {
                var separator = part.IndexOf('=');
                if (separator <= 0) continue;
                try { values.TryAdd(part[..separator], Uri.UnescapeDataString(part[(separator + 1)..])); }
                catch (UriFormatException) { /* Invalid optional metadata is ignored. */ }
            }
        }

        values.TryGetValue(
            "vis",
            out var visualizerId);

        values.TryGetValue(
            "theme",
            out var themeId);

        var mode =
            visualizerId?.ToLowerInvariant() switch
            {
                "classicbars" =>
    AudioVisualizerMode.ClassicBars,
                "frequencybars" =>
                    AudioVisualizerMode.FfmpegSpectrum,

                "mirrorspectrum" =>
                    AudioVisualizerMode.MirrorSpectrum,

                "neonwave" =>
AudioVisualizerMode.NeonWave,

                "waterfall" =>
                    AudioVisualizerMode.Waterfall,

                "orbitalscope" =>
AudioVisualizerMode.OrbitalScope,

                _ =>
                    AudioVisualizerMode.ClassicBars
            };

        var theme =
            themeId?.ToLowerInvariant() switch
            {
                "blue" =>
                    AudioVisualizerTheme.ElectricBlue,

                "cyan" =>
                    AudioVisualizerTheme.NeonCyan,

                "emerald" =>
                    AudioVisualizerTheme.Emerald,

                "pink" =>
                    AudioVisualizerTheme.HotPink,

                "orange" =>
                    AudioVisualizerTheme.SunsetOrange,

                "crimson" =>
                    AudioVisualizerTheme.Crimson,

                "white" =>
                    AudioVisualizerTheme.White,

                "purplepink" =>
                    AudioVisualizerTheme.PurplePink,

                "bluecyan" =>
                    AudioVisualizerTheme.BlueCyan,

                "sunset" =>
                    AudioVisualizerTheme.Sunset,

                "rainbow" =>
                    AudioVisualizerTheme.Rainbow,

                "yellow" => AudioVisualizerTheme.Yellow,
                "lime" => AudioVisualizerTheme.Lime,
                "lavender" => AudioVisualizerTheme.Lavender,
                "bluepurple" => AudioVisualizerTheme.BluePurple,
                "pinkcyan" => AudioVisualizerTheme.PinkCyan,
                "greenyellow" => AudioVisualizerTheme.GreenYellow,
                "aurora" => AudioVisualizerTheme.Aurora,
                "candywave" => AudioVisualizerTheme.CandyWave,
                "halloween" => AudioVisualizerTheme.Halloween,

                _ =>
                    AudioVisualizerTheme.AlphaPurple
            };

        return new AudioVisualizerSelection(
            mediaUrl,
            mode,
            theme);
    }

    internal static bool IsSameMedia(
        string? firstUrl,
        string? secondUrl)
    {
        if (string.IsNullOrWhiteSpace(firstUrl) ||
            string.IsNullOrWhiteSpace(secondUrl))
        {
            return string.Equals(
                firstUrl,
                secondUrl,
                StringComparison.Ordinal);
        }

        return string.Equals(
            Parse(firstUrl).MediaUrl,
            Parse(secondUrl).MediaUrl,
            StringComparison.Ordinal);
    }

    internal static string GetId(
        AudioVisualizerMode mode)
    {
        return mode switch
        {
            AudioVisualizerMode.FfmpegSpectrum =>
                "frequencybars",

            AudioVisualizerMode.MirrorSpectrum =>
                "mirrorspectrum",

            AudioVisualizerMode.NeonWave =>
"neonwave",

            AudioVisualizerMode.Waterfall =>
                "waterfall",

            AudioVisualizerMode.OrbitalScope =>
      "orbitalscope",

            _ =>
                "classicbars"
        };
    }

    internal static string GetDisplayName(
        AudioVisualizerMode mode)
    {
        return mode switch
        {
            AudioVisualizerMode.FfmpegSpectrum =>
                "Frequency Bars",

            AudioVisualizerMode.MirrorSpectrum =>
                "Mirror Spectrum",

            AudioVisualizerMode.NeonWave =>
"Neon Wave",

            AudioVisualizerMode.Waterfall =>
                "Waterfall",

            AudioVisualizerMode.OrbitalScope =>
"Orbital Scope",

            _ =>
                "Classic Bars"
        };
    }

    internal static string GetThemeId(
        AudioVisualizerTheme theme)
    {
        return theme switch
        {
            AudioVisualizerTheme.ElectricBlue => "blue",
            AudioVisualizerTheme.NeonCyan => "cyan",
            AudioVisualizerTheme.Emerald => "emerald",
            AudioVisualizerTheme.HotPink => "pink",
            AudioVisualizerTheme.SunsetOrange => "orange",
            AudioVisualizerTheme.Crimson => "crimson",
            AudioVisualizerTheme.White => "white",
            AudioVisualizerTheme.PurplePink => "purplepink",
            AudioVisualizerTheme.BlueCyan => "bluecyan",
            AudioVisualizerTheme.Sunset => "sunset",
            AudioVisualizerTheme.Rainbow => "rainbow",
            AudioVisualizerTheme.Yellow => "yellow",
            AudioVisualizerTheme.Lime => "lime",
            AudioVisualizerTheme.Lavender => "lavender",
            AudioVisualizerTheme.BluePurple => "bluepurple",
            AudioVisualizerTheme.PinkCyan => "pinkcyan",
            AudioVisualizerTheme.GreenYellow => "greenyellow",
            AudioVisualizerTheme.Aurora => "aurora",
            AudioVisualizerTheme.CandyWave => "candywave",
            AudioVisualizerTheme.Halloween => "halloween",
            _ => "purple"
        };
    }

    internal static string GetThemeDisplayName(
        AudioVisualizerTheme theme)
    {
        return theme switch
        {
            AudioVisualizerTheme.AlphaPurple => "Alpha Purple",
            AudioVisualizerTheme.ElectricBlue => "Electric Blue",
            AudioVisualizerTheme.NeonCyan => "Neon Cyan",
            AudioVisualizerTheme.Emerald => "Emerald",
            AudioVisualizerTheme.HotPink => "Hot Pink",
            AudioVisualizerTheme.SunsetOrange => "Orange",
            AudioVisualizerTheme.Crimson => "Crimson",
            AudioVisualizerTheme.White => "White",
            AudioVisualizerTheme.PurplePink => "Purple → Pink",
            AudioVisualizerTheme.BlueCyan => "Blue → Cyan",
            AudioVisualizerTheme.Sunset => "Sunset",
            AudioVisualizerTheme.Rainbow => "Rainbow",
            AudioVisualizerTheme.Yellow => "Yellow",
            AudioVisualizerTheme.Lime => "Lime",
            AudioVisualizerTheme.Lavender => "Lavender",
            AudioVisualizerTheme.BluePurple => "Blue → Purple",
            AudioVisualizerTheme.PinkCyan => "Pink → Cyan",
            AudioVisualizerTheme.GreenYellow => "Green → Yellow",
            AudioVisualizerTheme.Aurora => "Aurora (Cyan → Purple → Cyan)",
            AudioVisualizerTheme.CandyWave => "Candy Wave (Pink → Cyan)",
            AudioVisualizerTheme.Halloween => "Halloween (Orange → Purple)",
            _ => "Alpha Purple"
        };
    }

    /// <summary>
    /// Visualizer metadata is added only to audio streams. This provides a
    /// reliable audio-only hint when mpv temporarily retains the preceding
    /// video's track state or reports embedded artwork as a video track.
    /// </summary>
    internal static bool HasAudioVisualizerMetadata(
        string transmittedUrl)
    {
        if (string.IsNullOrWhiteSpace(
                transmittedUrl))
        {
            return false;
        }

        var fragmentIndex =
            transmittedUrl.IndexOf('#');

        if (fragmentIndex < 0 ||
            fragmentIndex >=
                transmittedUrl.Length - 1)
        {
            return false;
        }

        var fragment =
            transmittedUrl[
                (fragmentIndex + 1)..];

        return fragment
            .Split(
                '&',
                StringSplitOptions.RemoveEmptyEntries)
            .Any(
                part => part.StartsWith(
                    "vis=",
                    StringComparison.OrdinalIgnoreCase));
    }

    internal static string AddToUrl(
     string mediaUrl,
     AudioVisualizerMode mode,
     AudioVisualizerTheme theme)
    {
        var cleanUrl =
            Parse(mediaUrl).MediaUrl;

        var visualizerId =
            GetId(mode);

        //
        // All audio streams carry visualizer metadata, including Classic
        // Bars. Apart from synchronizing the selected design, this lets the
        // player identify audio-only media without relying entirely on mpv's
        // track list.
        //
        var visualizerUrl =
            $"{cleanUrl}#vis={Uri.EscapeDataString(visualizerId)}" +
            $"&theme={Uri.EscapeDataString(GetThemeId(theme))}";
        return MusicSceneEffects.AddToUrl(AudioScreenTextSelection.AddToUrl(
            visualizerUrl, AudioScreenTextSelection.Parse(mediaUrl)), MusicSceneEffects.Parse(mediaUrl));
    }
}
