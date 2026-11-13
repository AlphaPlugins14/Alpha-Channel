namespace AlphaChannel.Plugin.Video;

// Keep an owned drag alive outside a detached window's current bounds.
// The release frame must still reach ImGui so its active resize/drag can end.
internal sealed class UiPointerCapture
{
    private bool captured;

    internal bool Update(bool foregroundOwned, bool pointerOwned, bool buttonDown)
    {
        if (!foregroundOwned)
        {
            captured = false;
            return false;
        }

        var allow = pointerOwned || captured;
        captured = allow && buttonDown;
        return allow;
    }
}
