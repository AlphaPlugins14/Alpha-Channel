using System.Text.Json;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace AlphaChannel.Plugin.Video;

// Cache the designed wheel face; each frame rotates those same pixels on the TV and preview.
internal sealed class LuckyWheelScreenRenderer
{
    internal const int Width = 1280, Height = 720;
    private const int FaceSize = 550;
    // The slowest piece starts up to 339px above the screen. Leave time for it
    // to pass the bottom, plus a clear frame before the animation cache stops.
    internal const int ConfettiDurationMs = 6500;
    internal const int SpinButtonX = 890, SpinButtonY = 560, SpinButtonWidth = 300, SpinButtonHeight = 64;
    internal static bool IsSpinButtonAt(float u, float v) =>
        float.IsFinite(u) && float.IsFinite(v) &&
        u * Width >= SpinButtonX && u * Width < SpinButtonX + SpinButtonWidth &&
        v * Height >= SpinButtonY && v * Height < SpinButtonY + SpinButtonHeight;
    private readonly FontFamily font;
    private readonly FontFamily? iconFont;
    private byte[] face = [];
    private string? faceKey;
    private byte[] textLayer = [];
    private string? textKey;
    internal LuckyWheelScreenRenderer(string fontPath, string? iconFontPath = null)
    {
        var collection = new FontCollection();
        font = collection.Add(fontPath);
        if (iconFontPath is not null && File.Exists(iconFontPath)) iconFont = collection.Add(iconFontPath);
    }
    private static Color Ink(uint rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    private Font Font(float size) => font.CreateFont(size, FontStyle.Regular);
    internal byte[] Render(LuckyWheelDesign design, LuckyWheelSegment[] segments, double angle,
        string? result, int spin, bool spinning, double resultAgeMs, bool optionsRemain = true, LuckyWheelResult? previous = null, bool canControl = true, string? currentSpinner = null)
    {
        EnsureFace(design, segments);
        var pixels = new byte[Width * Height * 4];
        Rect(pixels, 0, 0, Width, Height, design.Background);
        // A subtle inset for the result keeps custom text legible on any background.
        var panel = Darken(design.Background, .72);
        var resultColour = result is null ? 0x8C55F6u : segments.FirstOrDefault(s => s.Text == result)?.Colour ?? design.BorderColour;
        RoundedRect(pixels, 850, 180, 380, 365, 20, resultColour);
        RoundedRect(pixels, 852, 182, 376, 361, 18, panel);
        // Soft rim and marquee lights remain fixed while the cached face rotates.
        Circle(pixels, 430, 405, 286, Darken(design.BorderColour, .23));
        Circle(pixels, 430, 405, 279, Darken(design.BorderColour, .5));
        var cosine = Math.Cos(angle); var sine = Math.Sin(angle);
        const int cx = 430, cy = 405, half = FaceSize / 2;
        for (var y = -half; y < half; y++)
        for (var x = -half; x < half; x++)
        {
            var distance = x * x + y * y;
            if (distance > half * half) continue;
            var offset = ((cy + y) * Width + cx + x) * 4;
            if (distance >= (half - 6) * (half - 6))
            {
                Put(pixels, offset, design.BorderColour); continue;
            }
            var sx = Math.Clamp((int)Math.Round(cosine * x + sine * y + half), 0, FaceSize - 1);
            var sy = Math.Clamp((int)Math.Round(-sine * x + cosine * y + half), 0, FaceSize - 1);
            var source = (sy * FaceSize + sx) * 4;
            // The face is opaque inside the circle; no alpha blending is needed here.
            pixels[offset] = face[source]; pixels[offset + 1] = face[source + 1];
            pixels[offset + 2] = face[source + 2]; pixels[offset + 3] = 255;
        }
        // Fixed pointer points at the top of the wheel.
        for (var y = 109; y <= 150; y++)
        {
            var halfWidth = (150 - y) * 23 / 41;
            Rect(pixels, cx - halfWidth, y, halfWidth * 2 + 1, 1, design.BorderColour);
        }
        for (var y = -28; y <= 28; y++)
        for (var x = -28; x <= 28; x++)
            if (x * x + y * y <= 28 * 28) Put(pixels, ((cy + y) * Width + cx + x) * 4, design.BorderColour);
        for (var i = 0; i < 12; i++)
        {
            var a = i * Math.PI / 6;
            Circle(pixels, cx + (int)(Math.Cos(a) * 272), cy + (int)(Math.Sin(a) * 272), 5, design.BorderColour);
        }
        Sparkle(pixels, cx, cy, 18, Darken(design.BorderColour, .35));
        Sparkle(pixels, 1040, 292, 42, result is null ? design.BorderColour : resultColour);
        if (previous is not null)
        {
            RoundedRect(pixels, 850, 661, 380, 43, 10, 0x343D5D);
            RoundedRect(pixels, 852, 663, 376, 39, 8, panel);
            Circle(pixels, 968, 683, 6, previous.Colour);
        }
        if (design.Confetti && result is not null && resultAgeMs >= 0 && resultAgeMs < ConfettiDurationMs)
        {
            for (var i = 0; i < 55; i++)
            {
                var x = (i * 193 + spin * 71) % Width;
                var y = (int)(resultAgeMs * (.17 + (i % 5) * .025)) - (i * 83 % 340);
                Rect(pixels, x, y, 7, 12, LuckyWheelRules.Rainbow[i % LuckyWheelRules.Rainbow.Length]);
            }
        }
        // Everyone sees the control; only the host can activate it through TV input.
        var buttonColour = spinning || !optionsRemain || !canControl ? 0x392953u : 0x8C55F6u;
        RoundedRect(pixels, SpinButtonX, SpinButtonY, SpinButtonWidth, SpinButtonHeight, 16, buttonColour);
        EnsureText(design, result, spin, spinning, optionsRemain, buttonColour, previous, canControl, currentSpinner);
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            var alpha = textLayer[offset + 3];
            if (alpha == 0) continue;
            for (var channel = 0; channel < 3; channel++)
                pixels[offset + channel] = (byte)((textLayer[offset + channel] * alpha + pixels[offset + channel] * (255 - alpha) + 127) / 255);
        }
        return pixels;
    }
    private void EnsureText(LuckyWheelDesign design, string? result, int spin, bool spinning, bool optionsRemain, uint buttonColour, LuckyWheelResult? previous, bool canControl, string? currentSpinner)
    {
        var key = JsonSerializer.Serialize(new { design.Title, design.Subtitle, design.TextColour, result, spin, spinning, optionsRemain, buttonColour, previous, canControl, currentSpinner });
        if (textKey == key) return;
        using var image = new Image<Bgra32>(Width, Height);
        image.Mutate(ctx =>
        {
            Text(ctx, design.Title, 48, Width / 2f, 25, Width - 90, design.TextColour);
            Text(ctx, design.Subtitle, 23, Width / 2f, 86, Width - 90, design.TextColour);
            Text(ctx, spinning ? "ROUND AND ROUND..." : result is null ? "FEELING LUCKY?" : "THE WHEEL HAS SPOKEN!", 21, 1040, 205, 330, design.TextColour);
            if (result is not null) WrappedResult(ctx, result, design.TextColour);
            else Text(ctx, spinning ? "Where will it land?" : canControl ? "Give it a spin!" : currentSpinner is null ? "Waiting for a spin…" : $"{currentSpinner}’s turn", 32, 1040, 364, 330, design.TextColour);
            Text(ctx, result == "Dance break" ? "Show us your best moves!" : result is not null ? "Luck has made its choice!" : "Let luck decide", 20, 1040, 495, 330, design.TextColour);
            Text(ctx, currentSpinner is null ? $"Round {spin}" : $"{currentSpinner}’s turn", 18, 1040, 635, 330, design.TextColour);
            Text(ctx, spinning ? "SPINNING..." : !canControl ? "WAITING FOR A SPIN" : optionsRemain ? spin > 0 ? "SPIN AGAIN" : "SPIN" : "NO OPTIONS LEFT", 25,
                SpinButtonX + SpinButtonWidth / 2f, SpinButtonY + 17, SpinButtonWidth - 20,
                LuckyWheelRules.LabelColour(buttonColour));
            if (previous is not null)
            {
                Text(ctx, "LAST SPIN", 14, 906, 674, 100, design.TextColour);
                Text(ctx, previous.Text, 16, 1100, 674, 240, design.TextColour);
            }
        });
        textLayer = new byte[Width * Height * 4];
        image.CopyPixelDataTo(textLayer);
        textKey = key;
    }
    private void EnsureFace(LuckyWheelDesign design, LuckyWheelSegment[] segments)
    {
        var key = JsonSerializer.Serialize(new { Segments = segments, design.AutomaticLabelColour, design.TextColour });
        if (faceKey == key) return;
        faceKey = key;
        var data = new byte[FaceSize * FaceSize * 4];
        const double half = FaceSize / 2d;
        for (var y = 0; y < FaceSize; y++)
        for (var x = 0; x < FaceSize; x++)
        {
            var index = Math.Min(segments.Length - 1, (int)(LuckyWheelRules.Normalize(Math.Atan2(y - half, x - half) + Math.PI / 2) / LuckyWheelRules.Tau * segments.Length));
            var colour = segments[index].Colour;
            var position = LuckyWheelRules.Normalize(Math.Atan2(y - half, x - half) + Math.PI / 2) / LuckyWheelRules.Tau * segments.Length;
            if (position % 1 < .008) colour = Darken(colour, .7);
            Put(data, (y * FaceSize + x) * 4, colour);
        }
        using var image = Image.LoadPixelData<Bgra32>(data, FaceSize, FaceSize);
        for (var i = 0; i < segments.Length; i++)
        {
            var radians = -Math.PI / 2 + (i + .5) * LuckyWheelRules.Tau / segments.Length;
            var text = segments[i].Text;
            var maximum = segments.Length > 24 ? 10 : segments.Length > 12 ? 15 : 20;
            if (text.Length > maximum) text = text[..(maximum - 1)] + "…";
            var fontSize = segments.Length > 24 ? 14 : segments.Length > 12 ? 18 : 23;
            var maximumLabelWidth = segments[i].Icon is not null && iconFont is not null ? 150 : 190;
            var labelFont = Font(fontSize);
            while (fontSize > 12 && TextMeasurer.MeasureSize(text, new TextOptions(labelFont)).Width > maximumLabelWidth)
                labelFont = Font(--fontSize);
            while (text.Length > 2 && TextMeasurer.MeasureSize(text, new TextOptions(labelFont)).Width > maximumLabelWidth)
                text = text[..^2] + "…";
            var glyph = LuckyWheelIcons.Glyph(segments[i].Icon);
            var glyphFont = iconFont?.CreateFont(fontSize, FontStyle.Regular);
            var iconWidth = glyph is not null && glyphFont is not null ? (int)Math.Ceiling(TextMeasurer.MeasureSize(glyph, new TextOptions(glyphFont)).Width) + 8 : 0;
            var bounds = TextMeasurer.MeasureSize(text, new TextOptions(labelFont));
            using var label = new Image<Bgra32>((int)Math.Ceiling(bounds.Width) + 6 + iconWidth, (int)Math.Ceiling(bounds.Height) + 10);
            var ink = design.AutomaticLabelColour ? LuckyWheelRules.LabelColour(segments[i].Colour) : design.TextColour;
            label.Mutate(ctx =>
            {
                if (iconWidth > 0) ctx.DrawText(glyph!, glyphFont!, Ink(ink), new PointF(3, 2));
                ctx.DrawText(text, labelFont, Ink(ink), new PointF(3 + iconWidth, 2));
            });
            var degrees = radians * 180 / Math.PI;
            // Read outward labels on the left side in the opposite direction.
            if (Math.Cos(radians) < 0) degrees += 180;
            label.Mutate(ctx => ctx.Rotate((float)degrees));
            var centerX = half + Math.Cos(radians) * half * .62;
            var centerY = half + Math.Sin(radians) * half * .62;
            image.Mutate(ctx => ctx.DrawImage(label, new Point((int)(centerX - label.Width / 2d), (int)(centerY - label.Height / 2d)), 1f));
        }
        image.CopyPixelDataTo(data);
        face = data;
    }
    private void Text(IImageProcessingContext ctx, string text, float size, float centerX, float y, float width, uint colour)
    {
        var f = Font(size);
        while (size > 12 && TextMeasurer.MeasureSize(text, new TextOptions(f)).Width > width) f = Font(--size);
        var measured = TextMeasurer.MeasureSize(text, new TextOptions(f));
        ctx.DrawText(text, f, Ink(colour), new PointF(centerX - measured.Width / 2, y));
    }
    private void WrappedResult(IImageProcessingContext ctx, string result, uint colour)
    {
        // Full labels are shown here even when the narrow wheel segment is shortened.
        var options = new RichTextOptions(Font(44)) { Origin = new PointF(1040, 357), WrappingLength = 324,
            HorizontalAlignment = HorizontalAlignment.Center, WordBreaking = WordBreaking.BreakWord };
        var size = 44;
        while (size > 16 && TextMeasurer.MeasureSize(result, options).Height > 112)
            options.Font = Font(--size);
        ctx.DrawText(options, result, Ink(colour));
    }
    private static void Circle(byte[] data, int cx, int cy, int radius, uint colour)
    {
        for (var y = -radius; y <= radius; y++)
        {
            var half = (int)Math.Sqrt(radius * radius - y * y);
            Rect(data, cx - half, cy + y, half * 2 + 1, 1, colour);
        }
    }
    private static void Sparkle(byte[] data, int cx, int cy, int radius, uint colour)
    {
        for (var y = -radius; y <= radius; y++)
        {
            var half = Math.Max(1, (int)(radius * Math.Pow(1 - Math.Abs(y) / (double)radius, 3)));
            Rect(data, cx - half, cy + y, half * 2 + 1, 1, colour);
        }
    }
    private static void RoundedRect(byte[] data, int x, int y, int width, int height, int radius, uint colour)
    {
        for (var row = 0; row < height; row++)
        {
            var dy = row < radius ? radius - row : row >= height - radius ? row - (height - radius - 1) : 0;
            var inset = dy == 0 ? 0 : radius - (int)Math.Sqrt(radius * radius - dy * dy);
            Rect(data, x + inset, y + row, width - inset * 2, 1, colour);
        }
    }
    private static uint Darken(uint c, double factor) => (uint)((int)(((c >> 16) & 255) * factor) << 16 |
        (int)(((c >> 8) & 255) * factor) << 8 | (int)((c & 255) * factor));
    private static void Put(byte[] data, int offset, uint c)
    { data[offset] = (byte)c; data[offset + 1] = (byte)(c >> 8); data[offset + 2] = (byte)(c >> 16); data[offset + 3] = 255; }
    private static void Rect(byte[] data, int x, int y, int width, int height, uint c)
    {
        for (var py = Math.Max(0, y); py < Math.Min(Height, y + height); py++)
        for (var px = Math.Max(0, x); px < Math.Min(Width, x + width); px++) Put(data, (py * Width + px) * 4, c);
    }
}
