using Dalamud.Interface;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace AlphaChannel.Plugin.Video;

internal enum ReactionGlyph
{
    Like,
    Laugh,
    Love,
    Surprised,
    Hype,
    Fire,
    Dislike,
    Angry,
    Crying,
    Dead,
    MindBlown,
    RocketHype,
    Victory,
    Spooky,
    Royal,
    MusicMoment,
    Warning,
    MaximumHype,
    Fabulous,
    Embarrassed,
    Awkward,
    Dizzy,
    Watching,
    Amazing,
    Exhausted,
    Cheeky,
    Celebration,
    Powerful,
    FoodMoment,
    Salute,
}

internal static class ReactionGlyphs
{
    internal const int Count = 30;

    private static readonly string[] Glyphs =
    [
        FontAwesomeIcon.ThumbsUp.ToIconString(),
        FontAwesomeIcon.Laugh.ToIconString(),
        FontAwesomeIcon.Heart.ToIconString(),
        FontAwesomeIcon.Surprise.ToIconString(),
        FontAwesomeIcon.Star.ToIconString(),
        FontAwesomeIcon.Fire.ToIconString(),
        FontAwesomeIcon.ThumbsDown.ToIconString(),
        FontAwesomeIcon.Angry.ToIconString(),
        FontAwesomeIcon.SadTear.ToIconString(),
        FontAwesomeIcon.Skull.ToIconString(),
        FontAwesomeIcon.Bomb.ToIconString(),
        FontAwesomeIcon.Rocket.ToIconString(),
        FontAwesomeIcon.Trophy.ToIconString(),
        FontAwesomeIcon.Ghost.ToIconString(),
        FontAwesomeIcon.Crown.ToIconString(),
        FontAwesomeIcon.Music.ToIconString(),
        FontAwesomeIcon.ExclamationTriangle.ToIconString(),
        FontAwesomeIcon.Bolt.ToIconString(),
        FontAwesomeIcon.Rainbow.ToIconString(),
        FontAwesomeIcon.Flushed.ToIconString(),
        FontAwesomeIcon.Grimace.ToIconString(),
        FontAwesomeIcon.Dizzy.ToIconString(),
        FontAwesomeIcon.Eye.ToIconString(),
        FontAwesomeIcon.Gem.ToIconString(),
        FontAwesomeIcon.Tired.ToIconString(),
        FontAwesomeIcon.GrinTongueWink.ToIconString(),
        FontAwesomeIcon.GlassCheers.ToIconString(),
        FontAwesomeIcon.FistRaised.ToIconString(),
        FontAwesomeIcon.PizzaSlice.ToIconString(),
        FontAwesomeIcon.Flag.ToIconString(),
    ];

    internal static string Get(ReactionGlyph glyph) => Glyphs[(int)glyph];

    // Font Awesome glyph bounds are mathematically centred by the font but a few symbols have an
    // uneven visual weight. These small atlas-space offsets centre their visible shape inside the
    // circular TV badge without changing their placement in the ImGui hotbar.
    internal static PointF GetAtlasOffset(ReactionGlyph glyph) => glyph switch
    {
        ReactionGlyph.Like => new PointF(-4f, 0f),
        ReactionGlyph.RocketHype => new PointF(-5f, 0f),
        ReactionGlyph.MusicMoment => new PointF(-5f, -1f),
        ReactionGlyph.Warning => new PointF(0f, -5f),
        _ => PointF.Empty,
    };

    internal static ReactionGlyph FromGlyph(string glyph)
    {
        for (var index = 0; index < Glyphs.Length; index++)
        {
            if (string.Equals(Glyphs[index], glyph, StringComparison.Ordinal))
            {
                return (ReactionGlyph)index;
            }
        }

        return ReactionGlyph.Like;
    }
}

// The reaction glyphs are rasterized once into a horizontal texture atlas. The TV shader then
// selects the correct atlas cell for every active particle, keeping per-frame work on the GPU.
internal sealed class ReactionTextureRenderer : IDisposable
{
    private const int CellSize = 128;
    private const int CanvasWidth = CellSize * ReactionGlyphs.Count;
    private const int CanvasHeight = CellSize;

    private Texture2D? texture;
    private ShaderResourceView? srv;

    internal ShaderResourceView? Srv => srv;

    internal ReactionTextureRenderer()
    {
        try
        {
        var fontPath = Path.Combine(
            Plugin.PluginInterface.DalamudAssetDirectory.FullName,
            "UIRes",
            "FontAwesomeFreeSolid.otf");

        if (!File.Exists(fontPath))
        {
            return;
        }

        var collection = new FontCollection();
        var font = collection.Add(fontPath).CreateFont(76f, FontStyle.Regular);

        using var atlas = new Image<Bgra32>(CanvasWidth, CanvasHeight);
        atlas.Mutate(context =>
        {
            for (var index = 0; index < ReactionGlyphs.Count; index++)
            {
                var glyph = (ReactionGlyph)index;
                var offset = ReactionGlyphs.GetAtlasOffset(glyph);
                var options = new RichTextOptions(font)
                {
                    Origin = new PointF(
                        index * CellSize + CellSize / 2f + offset.X,
                        CellSize / 2f + offset.Y),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };

                context.DrawText(
                    options,
                    ReactionGlyphs.Get(glyph),
                    Color.White);
            }
        });

        Upload(atlas);
        }
        catch { Dispose(); throw; }
    }

    private unsafe void Upload(Image<Bgra32> image)
    {
        texture = new Texture2D(DxHandler.Device, new Texture2DDescription
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
                texture,
                0,
                null,
                (nint)pointer,
                CanvasWidth * 4,
                0);
        }

        srv = new ShaderResourceView(DxHandler.Device, texture);
    }

    public void Dispose()
    {
        ResourceCleanup.DisposeAll(srv, texture);
    }
}
