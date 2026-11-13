using System.Globalization;
using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private DateTime musicTextNextUpdateUtc, musicTextLiveSinceUtc;

    private AudioScreenTextSelection ResolveMusicScreenText(AudioScreenTextSelection template)
    {
        // Viewers use the host's resolved text, never their own queue/decks.
        if (stream.Mode == StreamMode.Viewing) return template;
        var state = StudioState;
        var active = state.Crossfader < .5f ? state.DeckA : state.DeckB;
        var studio = studioSessionActive;
        var playing = studio ? active.Playing || active.Position > 0 : video.IsAudioOnly && queue.Current != null;
        var current = playing ? studio ? active.Track?.Name : queue.Current?.Title : null;
        if (studio && StudioUsesApplication && state.ApplicationCapturing)
            current = state.Applications.FirstOrDefault(a => a.ProcessId == studioSettings.ApplicationProcessId)?.Name ?? "Application audio";
        var progress = video.GetProgress();
        var position = studio ? active.Position : progress.Position;
        var duration = studio ? StudioUsesApplication ? 0 : active.Duration : progress.Duration;
        var spotify = spotifyOwnsStudio && spotifyClient is not null ? new SpotifyPlayback(spotifyClient.Snapshot) : null;
        if (spotify is not null)
        {
            current = SpotifyBroadcastTitle;
            playing = spotify.Playing;
            position = spotify.Position(DateTime.UtcNow);
            duration = spotify.Duration;
        }
        var viewers = stream.Mode == StreamMode.Hosting
            ? stream.Roster.Where(p => p.UserId != stream.LocalUserId).Select(p => p.UserId).Distinct().Count() : 0;
        var live = studioPublished || djBroadcastingToWatchParty;
        var status = studioBroadcastRequested && !studioPublished ? "Connecting" : live ? "Live" :
            studio ? "Ready" : video.State == VideoPlaybackState.Paused ? "Paused" : playing ? "Playing" : "Ready";
        var next = spotify is not null ? null : studio ? state.NextTrack?.Name : queue.Entries.FirstOrDefault(t => t.Id != queue.Current?.Id)?.Title;
        var liveSeconds = studio ? state.BroadcastSeconds : musicTextLiveSinceUtc == default ? 0 : (DateTime.UtcNow - musicTextLiveSinceUtc).TotalSeconds;
        var values = new Dictionary<string, string>
        {
            ["nowplaying"] = current ?? "Nothing playing",
            ["nextsong"] = next ?? "No song queued",
            ["listeners"] = (stream.Mode == StreamMode.Hosting ? viewers + 1 : 0).ToString(CultureInfo.InvariantCulture),
            ["viewers"] = viewers.ToString(CultureInfo.InvariantCulture),
            ["broadcastname"] = DjBroadcastContentTitle,
            ["status"] = status,
            ["decka"] = state.DeckA.Track?.Name ?? "Empty",
            ["deckb"] = state.DeckB.Track?.Name ?? "Empty",
            ["elapsed"] = playing ? StudioTime(Math.Max(0, position)) : "0:00",
            ["duration"] = duration > 0 ? StudioTime(duration) : "--:--",
            ["remaining"] = duration > 0 ? StudioTime(Math.Max(0, duration - position)) : "--:--",
            ["queuecount"] = (spotify is not null ? 0 : studio ? Plugin.Cfg.StudioQueue.Count : queue.Entries.Count(t => t.Id != queue.Current?.Id)).ToString(CultureInfo.InvariantCulture),
            ["livetime"] = StudioTime(live ? Math.Max(0, liveSeconds) : 0)
        };
        return WithSpotifyCover(template with
        {
            TopText = AudioTextVariables.Resolve(template.TopText, values),
            MiddleText = AudioTextVariables.Resolve(template.MiddleText, values),
            BottomText = AudioTextVariables.Resolve(template.BottomText, values)
        });
    }

    internal void UpdateMusicTextVariables()
    {
        UpdateSpotifyCover();
        var live = studioBroadcastRequested || djBroadcastingToWatchParty;
        if (!live) musicTextLiveSinceUtc = default;
        else if (musicTextLiveSinceUtc == default) musicTextLiveSinceUtc = DateTime.UtcNow;
        if (stream.Mode == StreamMode.Viewing || !video.IsAudioOnly || DateTime.UtcNow < musicTextNextUpdateUtc) return;
        musicTextNextUpdateUtc = DateTime.UtcNow.AddSeconds(1);
        EnsureMusicScene();
        screenController.Engine.SetAudioScreenText(ResolveMusicScreenText(partyAudioScreenText));
    }

    private void DrawMusicTextVariables()
    {
        ImGui.TextWrapped("Add these placeholders to any screen text field. They update automatically; viewers see the host's values.");
        ImGui.Spacing();
        using (var list = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##musicTextVariableList", new Vector2(-1, Ui(360)))))
        {
            if (list)
            if (ImGui.BeginTable("##musicTextVariables", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("Variable", ImGuiTableColumnFlags.WidthFixed, Ui(150));
                ImGui.TableSetupColumn("Shows");
                ImGui.TableHeadersRow();
                foreach (var (name, description) in AudioTextVariables.Definitions)
                {
                    ImGui.TableNextRow(); ImGui.TableNextColumn();
                    if (ImGui.Selectable("{" + name + "}")) ImGui.SetClipboardText("{" + name + "}");
                    if (ImGui.IsItemHovered()) UiFonts.SetTooltip("Click to copy");
                    ImGui.TableNextColumn(); ImGui.TextWrapped(description);
                }
                ImGui.EndTable();
            }
        }
        ImGui.Spacing();
        ImGui.TextColored(MutedText, "Click a variable to copy it, then paste it into a text field.");
        ImGui.TextWrapped("Example: Now playing: {nowplaying}");
        if (GameLayoutButton("Close", FontAwesomeIcon.Check, Ui(120))) CloseMusicDialog();
    }
}
