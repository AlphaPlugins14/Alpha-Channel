using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private readonly ActivityBlockedFlash endActivityFlash = new();
    internal void NotifyActivityStartBlocked() => endActivityFlash.Trigger(Environment.TickCount64);
    private bool HeaderCanStopVideo=>stream.Mode!=StreamMode.Viewing&&
        video.State is VideoPlaybackState.Playing or VideoPlaybackState.Paused or VideoPlaybackState.Loading;
    private bool ShowHeaderEndActivity=>HeaderActivity!=HeaderActivityKind.None||HeaderCanStopVideo;
    private string HeaderStopLabel=>HeaderActivity!=HeaderActivityKind.None?"End Activity":"Stop Video";
    private float HeaderEndActivityWidth => HeaderBarSegmentWidth(HeaderStopLabel);
    private void DrawHeaderEndActivity()
    {
        if(!ShowHeaderEndActivity)return;
        var flashing = endActivityFlash.IsOn(Environment.TickCount64);
        var clicked = DrawHeaderBarSegment("stop", HeaderStopLabel, FontAwesomeIcon.Stop, HeaderEndActivityWidth,
            new Vector4(1f,.57f,.62f,1f), HeaderActivity!=HeaderActivityKind.None
                ? $"End current activity: {HeaderActivityTitle}" : "Stop video and despawn TV", danger: true, flashing: flashing);
        if (clicked)
        {
            endActivityFlash.Clear();
            if(HeaderActivity!=HeaderActivityKind.None)EndHeaderActivity();
            else if(HeaderCanStopVideo)EndPlaybackAndDespawnTv();
        }
    }
}
