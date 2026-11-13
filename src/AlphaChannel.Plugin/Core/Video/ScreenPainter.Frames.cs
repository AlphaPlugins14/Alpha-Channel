using SharpDX.Direct3D11;
using SharpDX.DXGI;

namespace AlphaChannel.Plugin.Video;

internal sealed unsafe partial class ScreenPainter
{
    private Texture2D? premiumFrameTexture;
    private ShaderResourceView? premiumFrameView;
    private TvFrameSelection? premiumFrameLoaded;
    private DateTime premiumFrameNextUpdate;

    // Called under the painter's frame lifetime lock, on the render thread.
    private bool PreparePremiumFrame(TvFrameSelection frame)
    {
        var style = frame.Style;
        if (!PremiumTvFrameArt.IsPremium(style)) return true;
        var key = TvFrameAppearance.TextureKey(frame);
        if (premiumFrameLoaded == key && premiumFrameView is not null) return true;
        var previousReady = premiumFrameLoaded?.Style == style && premiumFrameView is not null;
        if (previousReady && DateTime.UtcNow < premiumFrameNextUpdate) return true;
        var artwork = PremiumTvFrameArt.GetPixels(key);
        if (!artwork.IsCompletedSuccessfully)
        {
            premiumFrameNextUpdate = DateTime.UtcNow.AddMilliseconds(50);
            return previousReady;
        }
        var device = DxHandler.Device;
        if (device is null) return false;
        premiumFrameTexture ??= new Texture2D(device, new Texture2DDescription
        {
            Width = PremiumTvFrameArt.Width, Height = PremiumTvFrameArt.Height,
            MipLevels = 1, ArraySize = 1, Format = Format.R8G8B8A8_UNorm,
            BindFlags = BindFlags.ShaderResource, Usage = ResourceUsage.Default,
            SampleDescription = new SampleDescription(1, 0),
        });
        fixed (byte* pixels = artwork.Result)
            device.ImmediateContext.UpdateSubresource(premiumFrameTexture, 0, null,
                (nint)pixels, PremiumTvFrameArt.Width * 4, 0);
        premiumFrameView ??= new ShaderResourceView(device, premiumFrameTexture);
        premiumFrameLoaded = key;
        premiumFrameNextUpdate = DateTime.UtcNow.AddMilliseconds(200);
        return true;
    }
}
