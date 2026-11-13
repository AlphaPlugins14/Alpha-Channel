namespace AlphaChannel.Plugin.Video;

internal enum AudioTextAlignment
{
    Left,
    Middle,
    Right,
}

internal enum AudioTextFont
{
    InterSemiBold,
    InterRegular,
    InterBold,
    Consolas,
    Georgia,
    Impact,
    TrebuchetMs,
    ComicSansMs,
    CourierNew,
    Gabriola,
}

internal enum AudioTextGradient
{
    Solid,
    Rainbow,
    Aurora,
    Sunset,
    Ocean,
}

internal sealed record AudioScreenTextSelection(
    string TopText,
    AudioTextAlignment TopAlignment,
    uint TopColor,
    AudioTextFont TopFont,
    AudioTextGradient TopGradient,
    string BottomText,
    AudioTextAlignment BottomAlignment,
    uint BottomColor,
    AudioTextFont BottomFont,
    AudioTextGradient BottomGradient)
{
    internal string MiddleText { get; init; } = string.Empty;
    internal string CoverId { get; init; } = string.Empty;
    // Local pixels never enter a room message or saved scene URL.
    internal string? CoverBase64 { get; init; }
    internal AudioTextAlignment MiddleAlignment { get; init; } = AudioTextAlignment.Middle;
    internal uint MiddleColor { get; init; } = 0xFFFFFF;
    internal AudioTextFont MiddleFont { get; init; } = AudioTextFont.InterSemiBold;
    internal const int DefaultFontSize = 47;
    internal const int MinFontSize = 20;
    internal const int MaxFontSize = 80;
    internal int TopFontSize { get; init; } = DefaultFontSize;
    internal int MiddleFontSize { get; init; } = DefaultFontSize;
    internal int BottomFontSize { get; init; } = DefaultFontSize;
    internal static int ClampFontSize(int size) => Math.Clamp(size, MinFontSize, MaxFontSize);
    internal AudioTextGradient MiddleGradient { get; init; } = AudioTextGradient.Solid;
    internal const int MaxTextLength = 128;

    internal static AudioScreenTextSelection Empty { get; } =
        new(string.Empty, AudioTextAlignment.Middle, 0xFFFFFF,
            AudioTextFont.InterSemiBold, AudioTextGradient.Solid,
            string.Empty, AudioTextAlignment.Middle, 0xFFFFFF,
            AudioTextFont.InterSemiBold, AudioTextGradient.Solid);

    internal static string GradientName(AudioTextGradient gradient) => gradient switch
    {
        AudioTextGradient.Rainbow => "Rainbow",
        AudioTextGradient.Aurora => "Aurora",
        AudioTextGradient.Sunset => "Sunset",
        AudioTextGradient.Ocean => "Ocean",
        _ => "Solid colour",
    };

    internal static string FontName(AudioTextFont font) => font switch
    {
        AudioTextFont.InterRegular => "Inter Regular",
        AudioTextFont.InterBold => "Inter Bold",
        AudioTextFont.Consolas => "Consolas (arcade)",
        AudioTextFont.Georgia => "Georgia (classic)",
        AudioTextFont.Impact => "Impact (bold)",
        AudioTextFont.TrebuchetMs => "Trebuchet MS (clean)",
        AudioTextFont.ComicSansMs => "Comic Sans MS (playful)",
        AudioTextFont.CourierNew => "Courier New (typewriter)",
        AudioTextFont.Gabriola => "Gabriola (script)",
        _ => "Inter SemiBold",
    };

    internal bool HasText =>
        !string.IsNullOrEmpty(CoverBase64) ||
        !string.IsNullOrWhiteSpace(TopText) ||
        !string.IsNullOrWhiteSpace(MiddleText) ||
        !string.IsNullOrWhiteSpace(BottomText);

    internal static string SanitizeText(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var allowed = new char[MaxTextLength];
        var count = 0;
        foreach (var character in value)
        {
            if (char.IsControl(character) || char.IsSurrogate(character)) continue;
            allowed[count++] = character;
            if (count == MaxTextLength) break;
        }
        return new string(allowed, 0, count);
    }

    internal static AudioScreenTextSelection Parse(string? url)
    {
        var fragmentIndex = url?.IndexOf('#') ?? -1;
        if (fragmentIndex < 0)
            return Empty;

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in url![(fragmentIndex + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2)
                continue;
            try { values[pair[0]] = Uri.UnescapeDataString(pair[1]); }
            catch (UriFormatException) { }
        }

        return new AudioScreenTextSelection(
            ReadText("top"), ReadAlignment("topAlign"), ReadColor("topColor"),
            ReadFont("topFont"), ReadGradient("topGradient"),
            ReadText("bottom"), ReadAlignment("bottomAlign"), ReadColor("bottomColor"),
            ReadFont("bottomFont"), ReadGradient("bottomGradient"))
        {
            CoverId = values.TryGetValue("spotifyCover", out var cover) && Guid.TryParseExact(cover, "N", out _) ? cover : "",
            MiddleText = ReadText("middle"), MiddleAlignment = ReadAlignment("middleAlign"),
            MiddleColor = ReadColor("middleColor"), MiddleFont = ReadFont("middleFont"),
            MiddleGradient = ReadGradient("middleGradient"),
            TopFontSize = ReadFontSize("topSize"), MiddleFontSize = ReadFontSize("middleSize"),
            BottomFontSize = ReadFontSize("bottomSize")
        };

        string ReadText(string key)
        {
            if (!values.TryGetValue(key, out var value)) return string.Empty;
            return SanitizeText(value).Trim();
        }
        AudioTextAlignment ReadAlignment(string key) =>
            values.TryGetValue(key, out var value) &&
            Enum.TryParse<AudioTextAlignment>(value, true, out var alignment)
                ? alignment : AudioTextAlignment.Middle;
        uint ReadColor(string key) =>
            values.TryGetValue(key, out var value) &&
            uint.TryParse(value, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var color) && color <= 0xFFFFFF
                ? color : 0xFFFFFF;
        AudioTextFont ReadFont(string key) =>
            values.TryGetValue(key, out var value) &&
            Enum.TryParse<AudioTextFont>(value, true, out var font) &&
            Enum.IsDefined(font)
                ? font : AudioTextFont.InterSemiBold;
        int ReadFontSize(string key) => values.TryGetValue(key, out var value) &&
            int.TryParse(value, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var size)
                ? ClampFontSize(size) : DefaultFontSize;
        AudioTextGradient ReadGradient(string key) =>
            values.TryGetValue(key, out var value) &&
            Enum.TryParse<AudioTextGradient>(value, true, out var gradient) &&
            Enum.IsDefined(gradient)
                ? gradient : AudioTextGradient.Solid;
    }

    internal static string AddToUrl(string url, AudioScreenTextSelection selection)
    {
        var fragmentIndex = url.IndexOf('#');
        var baseUrl = fragmentIndex < 0 ? url : url[..fragmentIndex];
        var parts = fragmentIndex < 0
            ? new List<string>()
            : url[(fragmentIndex + 1)..]
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Where(part => !IsTextKey(part.Split('=', 2)[0]))
                .ToList();

        var topText = SanitizeText(selection.TopText).Trim();
        var bottomText = SanitizeText(selection.BottomText).Trim();
        var middleText = SanitizeText(selection.MiddleText).Trim();
        if (Guid.TryParseExact(selection.CoverId, "N", out _)) parts.Add("spotifyCover=" + selection.CoverId);

        if (topText.Length > 0)
        {
            parts.Add("top=" + Uri.EscapeDataString(topText));
            parts.Add("topAlign=" + selection.TopAlignment);
            parts.Add("topColor=" + selection.TopColor.ToString("X6"));
            parts.Add("topFont=" + selection.TopFont);
            parts.Add("topGradient=" + selection.TopGradient);
        }
        if (middleText.Length > 0)
        {
            parts.Add("middle=" + Uri.EscapeDataString(middleText));
            parts.Add("middleAlign=" + selection.MiddleAlignment);
            parts.Add("middleColor=" + selection.MiddleColor.ToString("X6"));
            parts.Add("middleFont=" + selection.MiddleFont);
            parts.Add("middleGradient=" + selection.MiddleGradient);
        }
        if (bottomText.Length > 0)
        {
            parts.Add("bottom=" + Uri.EscapeDataString(bottomText));
            parts.Add("bottomAlign=" + selection.BottomAlignment);
            parts.Add("bottomColor=" + selection.BottomColor.ToString("X6"));
            parts.Add("bottomFont=" + selection.BottomFont);
            parts.Add("bottomGradient=" + selection.BottomGradient);
        }
        AddSize("topSize", selection.TopFontSize);
        AddSize("middleSize", selection.MiddleFontSize);
        AddSize("bottomSize", selection.BottomFontSize);
        return parts.Count == 0 ? baseUrl : baseUrl + "#" + string.Join("&", parts);

        void AddSize(string key, int value)
        {
            var size = ClampFontSize(value);
            if (size != DefaultFontSize) parts.Add(key + "=" + size.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private static bool IsTextKey(string key) => key.Equals("top", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("spotifyCover", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("topSize", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("middleSize", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("bottomSize", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("topAlign", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("topColor", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("topFont", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("topGradient", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("middle", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("middleAlign", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("middleColor", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("middleFont", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("middleGradient", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("bottom", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("bottomAlign", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("bottomColor", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("bottomFont", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("bottomGradient", StringComparison.OrdinalIgnoreCase);
}
