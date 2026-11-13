using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace AlphaChannel.Plugin.Video;

// The in-world screen's "now playing" banner. Rasterizing text and uploading a texture only costs
// anything when the title/source actually changes (a queue advance, or metadata enrichment landing
// a few seconds later) - not once per frame, so this never runs from ScreenPainter's own Present
// hook, only from SetText.
internal sealed class TitleTextureRenderer : IDisposable
{
    private const int CanvasWidth = 1024;
    private const int CanvasHeight = 160;
    private const int Padding = 24;

    private static readonly Texture2DDescription TextureDescription = new()
    {
        Width = CanvasWidth,
        Height = CanvasHeight,
        MipLevels = 1,
        ArraySize = 1,
        Format = Format.B8G8R8A8_UNorm,
        BindFlags = BindFlags.ShaderResource,
        CpuAccessFlags = CpuAccessFlags.None,
        SampleDescription = new SampleDescription(1, 0),
        Usage = ResourceUsage.Default,
        OptionFlags = ResourceOptionFlags.None,
    };

    private readonly Font? titleFont;
    private readonly Font? sourceFont;
    private Texture2D? texture;
    private ShaderResourceView? srv;
    private string lastTitle = string.Empty;
    private string lastSource = string.Empty;

    internal ShaderResourceView? Srv => srv;

    private readonly bool centered;
    internal TitleTextureRenderer(bool centered = false)
    {
        this.centered = centered;
        var fontDirectory = Path.Combine(Plugin.PluginInterface.AssemblyLocation.DirectoryName ?? string.Empty,
            "Fonts");
        var collection = new FontCollection();
        FontFamily family = collection.Add(Path.Combine(fontDirectory, "Inter-SemiBold.ttf"));
        FontFamily regularFamily = collection.Add(Path.Combine(fontDirectory, "Inter-Regular.ttf"));
        titleFont = family.CreateFont(40f, FontStyle.Regular);
        sourceFont = regularFamily.CreateFont(26f, FontStyle.Regular);
    }

    //No-op when the title/source pair hasn't actually changed since the last call. Returns whether
    //it actually rasterized something new, so ScreenPainter knows whether to reset its own
    //show-for-7-seconds-then-fade timer.
    internal bool SetText(string title, string source)
    {
        if (title == lastTitle && source == lastSource)
        {
            return false;
        }

        lastTitle = title;
        lastSource = source;

        if (titleFont == null || sourceFont == null)
        {
            return true;
        }

        var maxTextWidth = CanvasWidth - Padding * 2f;
        var fittedTitle = FitText(title, titleFont, maxTextWidth);
        var fittedSource = source.Length > 0 ? FitText(source, sourceFont, maxTextWidth) : source;

        using var image = new Image<Bgra32>(CanvasWidth, CanvasHeight);
        image.Mutate(context =>
        {
            context.DrawText(fittedTitle, titleFont, Color.White, new PointF(centered ? (CanvasWidth - TextMeasurer.MeasureSize(fittedTitle, new TextOptions(titleFont)).Width) / 2f : Padding, Padding));
            if (fittedSource.Length > 0)
            {
                context.DrawText(fittedSource, sourceFont, Color.LightGray, new PointF(centered ? (CanvasWidth - TextMeasurer.MeasureSize(fittedSource, new TextOptions(sourceFont)).Width) / 2f : Padding, Padding + 56f));
            }
        });

        Upload(image);
        return true;
    }

    //Trims from the end and appends an ellipsis until the text measures within maxWidth - the banner
    //is a fixed-size canvas, so an untruncated long video title would just run off the edge instead
    //of wrapping (there is no second line to wrap into).
    private static string FitText(string text, Font font, float maxWidth)
    {
        var options = new TextOptions(font);
        if (TextMeasurer.MeasureSize(text, options).Width <= maxWidth)
        {
            return text;
        }

        const string ellipsis = "...";
        for (var length = text.Length - 1; length > 0; length--)
        {
            var candidate = text[..length].TrimEnd() + ellipsis;
            if (TextMeasurer.MeasureSize(candidate, options).Width <= maxWidth)
            {
                return candidate;
            }
        }

        return ellipsis;
    }

    private unsafe void Upload(Image<Bgra32> image)
    {
        texture ??= new Texture2D(DxHandler.Device, TextureDescription);
        var pixels = new Bgra32[CanvasWidth * CanvasHeight];
        image.CopyPixelDataTo(pixels);
        fixed (Bgra32* pixelPtr = pixels)
        {
            DxHandler.Device?.ImmediateContext.UpdateSubresource(texture, 0, null, (nint)pixelPtr,
                CanvasWidth * 4, 0);
        }

        if (srv == null)
        {
            srv = new ShaderResourceView(DxHandler.Device, texture, new ShaderResourceViewDescription
            {
                Format = TextureDescription.Format,
                Dimension = ShaderResourceViewDimension.Texture2D,
                Texture2D = { MipLevels = 1 },
            });
        }
    }

    public void Dispose()
    {
        srv?.Dispose();
        texture?.Dispose();
    }
}
