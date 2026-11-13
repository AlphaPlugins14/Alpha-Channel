using AlphaChannel.Plugin.Video;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private bool ShouldRenderArcadeTv => whiteboardActive &&
        (screenController.Engine.IsActive || stream.Mode == StreamMode.Viewing && ViewerTvEnabled ||
         IsMiniPlayerOpen?.Invoke() == true) &&
        (stream.Mode != StreamMode.Viewing || ViewerTvEnabled || IsMiniPlayerOpen?.Invoke() == true);

    private readonly List<WhiteboardRenderer> retiredArcadePageRenderers = [];

    private void RetireArcadePageRenderer(ref WhiteboardRenderer? renderer)
    {
        if (renderer is null) return;
        renderer.CancelPendingFrame();
        lock (retiredArcadePageRenderers) retiredArcadePageRenderers.Add(renderer);
        renderer = null;
        // The End button may follow AddImage in the same ImGui draw callback.
        // Release after those draw commands have consumed the preview texture.
        DxHandler.RunOnRenderThread("AlphaChannel.ArcadePages.Dispose",
            DrainRetiredArcadePageRenderers, allowAfterDeviceLoss: true);
    }

    private void DrainRetiredArcadePageRenderers()
    {
        WhiteboardRenderer[] retired;
        lock (retiredArcadePageRenderers)
        {
            retired = retiredArcadePageRenderers.ToArray();
            retiredArcadePageRenderers.Clear();
        }
        foreach (var renderer in retired) renderer.Dispose();
    }
}
