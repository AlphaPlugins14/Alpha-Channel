using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace AlphaChannel.Plugin.Video;

// Text is rasterized only when a host changes it, then composited over the
// audio visualizer on the same world-space TV quad.
internal sealed class AudioScreenTextRenderer : IDisposable
{
    private const int Width = 1024;
    private const int Height = 576;
    private const float Padding = 42f;
    private readonly Dictionary<AudioTextFont, Font> fonts;
    private Texture2D? texture;
    private ShaderResourceView? srv;
    private AudioScreenTextSelection selection = AudioScreenTextSelection.Empty;

    internal ShaderResourceView? Srv => selection.HasText ? srv : null;
    internal AudioScreenTextSelection Selection => selection;

    internal AudioScreenTextRenderer()
    {
        var fontDirectory = Path.Combine(
            Plugin.PluginInterface.AssemblyLocation.DirectoryName ?? string.Empty, "Fonts");

        static Font LoadBundled(string directory, string fileName)
        {
            var collection = new FontCollection();
            return collection.Add(Path.Combine(directory, fileName))
                .CreateFont(47f, FontStyle.Regular);
        }

        var defaultFont = LoadBundled(fontDirectory, "Inter-SemiBold.ttf");
        Font LoadSystem(string name) => SystemFonts.TryGet(name, out var family)
            ? family.CreateFont(47f, FontStyle.Regular) : defaultFont;
        fonts = new Dictionary<AudioTextFont, Font>
        {
            [AudioTextFont.InterSemiBold] = defaultFont,
            [AudioTextFont.InterRegular] = LoadBundled(fontDirectory, "Inter-Regular.ttf"),
            [AudioTextFont.InterBold] = LoadBundled(fontDirectory, "Inter-Bold.ttf"),
            [AudioTextFont.Consolas] = SystemFonts.TryGet("Consolas", out var consolas)
                ? consolas.CreateFont(47f, FontStyle.Regular) : defaultFont,
            [AudioTextFont.Georgia] = SystemFonts.TryGet("Georgia", out var georgia)
                ? georgia.CreateFont(47f, FontStyle.Regular) : defaultFont,
            [AudioTextFont.Impact] = LoadSystem("Impact"),
            [AudioTextFont.TrebuchetMs] = LoadSystem("Trebuchet MS"),
            [AudioTextFont.ComicSansMs] = LoadSystem("Comic Sans MS"),
            [AudioTextFont.CourierNew] = LoadSystem("Courier New"),
            [AudioTextFont.Gabriola] = LoadSystem("Gabriola"),
        };
    }

    internal void SetText(AudioScreenTextSelection next)
    {
        if (selection == next) return;
        selection = next;
        if (!selection.HasText) return;

        using var image = new Image<Bgra32>(Width, Height);
        Image<Bgra32>? cover = null;
        try
        {
            if (next.CoverBase64 is { Length: > 0 and <= 700000 } encoded)
            {
                var bytes = Convert.FromBase64String(encoded);
                var info = Image.Identify(bytes);
                if (info.Width <= 1024 && info.Height <= 1024)
                {
                    cover = Image.Load<Bgra32>(new SixLabors.ImageSharp.Formats.DecoderOptions { MaxFrames = 1 }, bytes);
                    cover.Mutate(c => c.Resize(new ResizeOptions { Size = new Size(192, 192), Mode = ResizeMode.Pad }));
                }
            }
        }
        catch { cover?.Dispose(); cover = null; }
        image.Mutate(context =>
        {
            DrawBand(context, next.TopText, next.TopAlignment, next.TopColor,
                next.TopFont, next.TopGradient, next.TopFontSize, 42f);
            if (cover is null)
                DrawBand(context, next.MiddleText, next.MiddleAlignment, next.MiddleColor,
                    next.MiddleFont, next.MiddleGradient, next.MiddleFontSize, 264f);
            else context.DrawImage(cover, new Point((Width - 192) / 2, (Height - 192) / 2), 1f);
            DrawBand(context, next.BottomText, next.BottomAlignment, next.BottomColor,
                next.BottomFont, next.BottomGradient, next.BottomFontSize, 500f);
        });
        Upload(image);
        cover?.Dispose();
    }

    private void DrawBand(
        IImageProcessingContext context, string value,
        AudioTextAlignment alignment, uint color, AudioTextFont selectedFont,
        AudioTextGradient gradient, int fontSize, float y)
    {
        if (string.IsNullOrWhiteSpace(value)) return;

        var font = fonts.GetValueOrDefault(selectedFont, fonts[AudioTextFont.InterSemiBold]);
        var size = AudioScreenTextSelection.ClampFontSize(fontSize);
        if (size != AudioScreenTextSelection.DefaultFontSize) font = new Font(font, size);
        // Keep each line centred in its original band as its size changes.
        y += (AudioScreenTextSelection.DefaultFontSize - size) * .5f;
        var text = value.Trim();
        var options = new TextOptions(font);
        while (text.Length > 1 && TextMeasurer.MeasureSize(text, options).Width > Width - Padding * 2f)
            text = text[..^1];
        if (text.Length < value.Trim().Length) text = text.TrimEnd() + "…";

        var width = TextMeasurer.MeasureSize(text, options).Width;
        var x = alignment switch
        {
            AudioTextAlignment.Left => Padding,
            AudioTextAlignment.Right => Width - Padding - width,
            _ => (Width - width) * 0.5f,
        };
        var point = new PointF(x, y);
        var shadow = new PointF(x + 2f, y + 3f);
        context.DrawText(text, font, Color.FromRgba(0, 0, 0, 180), shadow);
        var ink = gradient == AudioTextGradient.Solid
            ? Color.FromRgb((byte)(color >> 16), (byte)(color >> 8), (byte)color)
            : Color.White;
        context.DrawText(text, font, ink, point);
    }

    private unsafe void Upload(Image<Bgra32> image)
    {
        texture ??= new Texture2D(DxHandler.Device, new Texture2DDescription
        {
            Width = Width,
            Height = Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            BindFlags = BindFlags.ShaderResource,
            Usage = ResourceUsage.Default,
            SampleDescription = new SampleDescription(1, 0),
        });
        var pixels = new Bgra32[Width * Height];
        image.CopyPixelDataTo(pixels);
        fixed (Bgra32* pixelPtr = pixels)
            DxHandler.Device?.ImmediateContext.UpdateSubresource(
                texture, 0, null, (nint)pixelPtr, Width * 4, 0);
        srv ??= new ShaderResourceView(DxHandler.Device, texture);
    }

    public void Dispose()
    {
        srv?.Dispose();
        texture?.Dispose();
    }
}
