using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Path = System.IO.Path;

namespace AlphaChannel.Plugin.Video;

internal sealed class ScreensaverTextureRenderer : IDisposable
{
    private const int CanvasWidth = 512;
    private const int CanvasHeight = 256;
    private Texture2D? texture;
    private ShaderResourceView? srv;
    private string lastStatus = string.Empty;

    internal ShaderResourceView? Srv => srv;
    private FontCollection? countdownFonts;
    private FontFamily? countdownFamily;

    internal void SetCountdown(AlphaChannel.Plugin.Core.PartyCountdownView countdown)
    {
        var key = $"countdown:{countdown.State.Id}:{countdown.Seconds}";
        if (lastStatus == key && srv is not null) return;
        lastStatus = key;
        using var canvas = new Image<Bgra32>(CanvasWidth, CanvasHeight, new Bgra32(12, 20, 39));
        var halloween = countdown.Halloween;
        var center = new PointF(256, 140);
        canvas.Mutate(context => {
            if (halloween) context.Fill(Color.FromRgb(20, 7, 31));
            for (var x = 16; x < CanvasWidth; x += 24) context.DrawLine(halloween ? Color.FromRgba(167, 73, 240, 22) : Color.FromRgba(67, 132, 238, 22), 1, new PointF(x, 0), new PointF(x, CanvasHeight));
            for (var y = 16; y < CanvasHeight; y += 24) context.DrawLine(halloween ? Color.FromRgba(167, 73, 240, 22) : Color.FromRgba(67, 132, 238, 22), 1, new PointF(0, y), new PointF(CanvasWidth, y));
            context.Draw(halloween ? Color.FromRgb(74, 28, 88) : Color.FromRgb(29, 49, 80), 4, new EllipsePolygon(center, 82));
            var points = Enumerable.Range(0, 97).Select(i => {
                var angle = -MathF.PI / 2 + MathF.Tau * MathF.Max(.001f, countdown.Progress) * i / 96;
                return new PointF(center.X + 82 * MathF.Cos(angle), center.Y + 82 * MathF.Sin(angle));
            }).ToArray();
            context.DrawLine(halloween ? Color.FromRgb(255, 132, 35) : Color.FromRgb(70, 156, 255), 4, points);
        });
        if (countdownFamily is null)
        {
            var path = Path.Combine(Plugin.PluginInterface.AssemblyLocation.DirectoryName ?? "", "Fonts", "Inter-SemiBold.ttf");
            if (File.Exists(path)) { countdownFonts = new FontCollection(); countdownFamily = countdownFonts.Add(path); }
        }
        if (countdownFamily is { } family)
        {
            void Text(string text, float size, float y, Color colour) => canvas.Mutate(c => c.DrawText(new RichTextOptions(family.CreateFont(size)) {
                Origin = new PointF(256, y), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            }, text, colour));
            // The public label is bounded by the service. Keep it on one line.
            Text(countdown.State.Label.Length > 46 ? countdown.State.Label[..43] + "…" : countdown.State.Label, 18, 25, halloween ? Color.FromRgb(255, 154, 62) : Color.FromRgb(167, 198, 238));
            Text(countdown.Clock, 48, 140, Color.White);
            Text(halloween ? "THE HAUNTING BEGINS SOON" : "STARTING SOON", 12, 240,
                halloween ? Color.FromRgb(192, 126, 244) : Color.FromRgb(110, 172, 244));
        }
        Upload(canvas);
    }

    internal void SetStatus(string status)
    {
        if (status == lastStatus && srv is not null)
            return;

        lastStatus = status;
        var pluginDirectory = Plugin.PluginInterface.AssemblyLocation.DirectoryName ?? string.Empty;
        var logoPath = Path.Combine(pluginDirectory, "Assets", "alphaicon.png");
        var fontPath = Path.Combine(pluginDirectory, "Fonts", "Inter-SemiBold.ttf");

        using var canvas = new Image<Bgra32>(CanvasWidth, CanvasHeight);
        if (File.Exists(logoPath))
        {
            using var logo = Image.Load<Bgra32>(logoPath);
            logo.Mutate(x => x.Resize(new ResizeOptions
            {
                Size = new Size(136, 136),
                Mode = ResizeMode.Max,
            }));
            canvas.Mutate(x => x.DrawImage(logo, new Point((CanvasWidth - logo.Width) / 2, 10), 1f));
        }

        if (File.Exists(fontPath))
        {
            var collection = new FontCollection();
            var font = collection.Add(fontPath).CreateFont(30f, FontStyle.Regular);
            var options = new RichTextOptions(font)
            {
                Origin = new PointF(CanvasWidth / 2f, 174f),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            canvas.Mutate(x => x.DrawText(options, "ALPHA CHANNEL", Color.White));

            var statusFont = collection.Get(font.Family.Name).CreateFont(21f, FontStyle.Regular);
            var statusOptions = new RichTextOptions(statusFont)
            {
                Origin = new PointF(CanvasWidth / 2f, 218f),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            canvas.Mutate(x => x.DrawText(statusOptions, status, Color.FromRgb(190, 178, 220)));
        }

        Upload(canvas);
    }

    private unsafe void Upload(Image<Bgra32> image)
    {
        texture ??= new Texture2D(DxHandler.Device, new Texture2DDescription
        {
            Width = CanvasWidth,
            Height = CanvasHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            BindFlags = BindFlags.ShaderResource,
            Usage = ResourceUsage.Default,
            SampleDescription = new SampleDescription(1, 0),
        });

        var pixels = new Bgra32[CanvasWidth * CanvasHeight];
        image.CopyPixelDataTo(pixels);
        fixed (Bgra32* pointer = pixels)
        {
            DxHandler.Device?.ImmediateContext.UpdateSubresource(
                texture, 0, null, (nint)pointer, CanvasWidth * 4, 0);
        }

        srv ??= new ShaderResourceView(DxHandler.Device, texture);
    }

    public void Dispose()
    {
        srv?.Dispose();
        texture?.Dispose();
    }
}
