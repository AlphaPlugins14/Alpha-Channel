using Dalamud.Bindings.ImGui;

namespace AlphaChannel.Plugin;

// One instance per feed: the full and mini chat keep independent scroll positions.
internal sealed class ChatFeedScroll
{
    private int settlingFrames;
    private bool followThisFrame, cancelledThisFrame;
    internal bool FollowingLatest { get; private set; }

    internal void Begin(float bottomTolerance)
    {
        cancelledThisFrame = false;
        var io = ImGui.GetIO();
        var window = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        var hovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
        var usingScrollbar = io.MouseDown[0] && ImGui.GetScrollMaxY() > 0 &&
            io.MousePos.X >= window.X + size.X - ImGui.GetStyle().ScrollbarSize &&
            io.MousePos.X <= window.X + size.X;
        var scrollingBack = hovered && (io.MouseWheel > 0 || usingScrollbar);
        if (scrollingBack) settlingFrames = 0;
        followThisFrame = !scrollingBack && (settlingFrames > 0 ||
            ImGui.GetScrollMaxY() - ImGui.GetScrollY() <= bottomTolerance);
    }

    // Call immediately after the trailing item, before drawing floating controls.
    internal void Finish(bool requested)
    {
        if (requested && !cancelledThisFrame)
        {
            followThisFrame = true;
            settlingFrames = 2;
        }
        FollowingLatest = followThisFrame && !cancelledThisFrame;
        if (FollowingLatest) ImGui.SetScrollHereY(1f);
        if (settlingFrames > 0) settlingFrames--;
    }

    internal void Cancel()
    {
        settlingFrames = 0;
        cancelledThisFrame = true;
        followThisFrame = FollowingLatest = false;
    }
}
