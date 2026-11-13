namespace AlphaChannel.Plugin.Video;

// Only one boundary action per frame. UI setting changes invalidate the action
// so a capture or early draw from the previous mode cannot suppress fallback.
internal sealed class TvUiRendererPolicy
{
    internal bool Enabled;
    internal bool KeepUiVisible { get; private set; } = true;
    internal bool CoverUi => !KeepUiVisible;
    internal bool Drawn { get; private set; }
    internal bool Attempted { get; private set; }
    internal bool SetUiVisibility(bool visible)
    {
        if (KeepUiVisible == visible) return false;
        KeepUiVisible = visible; EndFrame(); return true;
    }
    internal bool ShouldAttempt()
    {
        if (!Enabled || Attempted) return false;
        Attempted = true; return true;
    }
    internal void CompleteAttempt(bool rendered) => Drawn = rendered;
    internal void EndFrame() { Drawn = false; Attempted = false; }
}
