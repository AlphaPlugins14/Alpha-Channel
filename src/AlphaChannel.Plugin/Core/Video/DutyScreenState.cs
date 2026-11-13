namespace AlphaChannel.Plugin.Video;

// Hiding the world screen never changes the decoder or playback state.
internal sealed class DutyScreenState
{
    private volatile bool inDuty;
    private volatile bool awaitingPlacement;
    private bool notified;
    internal bool InDuty => inDuty;
    internal bool Hidden => inDuty || awaitingPlacement;

    internal bool Update(bool duty, bool hasContent)
    {
        inDuty = duty;
        if (!duty)
        {
            notified = false;
            return false;
        }
        if (!hasContent) return false;
        awaitingPlacement = true;
        if (notified) return false;
        notified = true;
        return true;
    }

    internal bool Place()
    {
        if (inDuty) return false;
        awaitingPlacement = false;
        return true;
    }

    internal void Reset()
    {
        inDuty = awaitingPlacement = notified = false;
    }
}
