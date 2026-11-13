using AlphaChannel.Plugin.BroadcastAllowance;
using AlphaChannel.Plugin.Video;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    // Ordinary videos (including paused/loading ones) never own the TV exclusively.
    private bool PlaybackActivityOwnsTv => HalloweenActivityActive || IsArcadeGameActive ||
        screenController.Engine.HasActiveNonVideoPlayback || video.IsPlayingLocalVideo ||
        IsGameBroadcastArmed || IsBrowserBroadcastArmed || IsLocalVideoBroadcastArmed ||
        djBroadcastingToWatchParty || StudioTvTransportActive ||
        HostBroadcastPanelMethod() != BroadcastAllowanceMethod.None;

    internal bool CanStartPlaybackUrl(string url)
    {
        if (stream.Mode == StreamMode.Viewing)
        {
            if (!RemoteMediaUrlPolicy.IsWebMediaUrl(url)) return false;
            // Failed/idle playback does not grant the viewer ownership. Permit
            // only the host's current media, including its audio presentation
            // fragment, so remote loads and deliberate re-sync still work.
            var hostUrl = stream.CurrentRoomState?.Url;
            if (string.IsNullOrWhiteSpace(hostUrl)) return false;
            var fragment = hostUrl.IndexOf('#');
            var hostMediaUrl = fragment >= 0 ? hostUrl[..fragment] : hostUrl;
            return string.Equals(url, hostUrl, StringComparison.Ordinal) ||
                   string.Equals(url, hostMediaUrl, StringComparison.Ordinal);
        }
        if (HalloweenActivityActive)
        {
            if (halloweenStartingMedia || stream.Mode == StreamMode.Viewing && HalloweenTvFrame.IsVideo(url)) return true;
            NotifyActivityStartBlocked();
            Plugin.ChatGui.Print("[AlphaChannel] End the Halloween show before starting other TV content.");
            return false;
        }
        if (hostTransferPreparing || stream.IsJoiningWatchParty) return false;
        // Preserve the Studio's exact private preview/live-feed exceptions.
        if (StudioOwnsPlaybackControls) return CanStartStudioPlaybackUrl(url);
        // Remote updates and a producer reloading its own feed must still work.
        if (!IsLocalActivityHost || !PlaybackActivityOwnsTv) return true;
        if (string.Equals(url, video.CurrentMediaUrl, StringComparison.Ordinal)) return true;
        if (djBroadcastingToWatchParty && radioCredentials is { } radio &&
            Uri.TryCreate(url, UriKind.Absolute, out var source) &&
            Uri.TryCreate(radio.ListenUrl, UriKind.Absolute, out var own) &&
            string.Equals(source.GetLeftPart(UriPartial.Path), own.GetLeftPart(UriPartial.Path), StringComparison.OrdinalIgnoreCase))
            return true;
        NotifyActivityStartBlocked();
        Plugin.ChatGui.Print("[AlphaChannel] End the current activity before starting other TV content.");
        return false;
    }
}
