namespace AlphaChannel.Plugin.Video;

internal enum RetroControlMode { Game, Ffxiv, Controller }

// Both entry and release wait for neutral input, so held buttons cannot cross ownership.
internal sealed class RetroControllerRouting
{
    private bool requested, waiting;
    internal bool Blocking => requested || waiting;
    internal void Reset() { requested = false; waiting = true; }

    internal (bool Block, bool Read) Poll(bool capture, bool neutral)
    {
        if (capture != requested) { requested = capture; waiting = true; }
        if (waiting)
        {
            if (neutral) waiting = false;
            return (true, false);
        }
        return (requested, requested);
    }

}
