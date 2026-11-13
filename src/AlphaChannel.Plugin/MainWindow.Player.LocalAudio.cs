using System.Globalization;
using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private static readonly Lazy<Dictionary<string, string>> LocalAudioLanguages = new(() =>
        CultureInfo.GetCultures(CultureTypes.NeutralCultures).Where(c => c.Name.Length > 0)
            .GroupBy(c => c.ThreeLetterISOLanguageName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().EnglishName, StringComparer.OrdinalIgnoreCase));

    private static string LocalAudioTrackLabel(AudioTrack track)
    {
        var language = track.Language;
        if (!string.IsNullOrWhiteSpace(language))
        {
            if (LocalAudioLanguages.Value.TryGetValue(language, out var name)) language = name;
            else try { language = CultureInfo.GetCultureInfo(language).EnglishName; } catch (CultureNotFoundException) { }
        }
        if (string.IsNullOrWhiteSpace(language)) return string.IsNullOrWhiteSpace(track.Name) ? $"Audio track {track.Id}" : track.Name;
        return string.IsNullOrWhiteSpace(track.Name) || string.Equals(language, track.Name, StringComparison.OrdinalIgnoreCase)
            ? language : $"{language} · {track.Name}";
    }

    private void DrawLocalVideoAudioTracks(float width)
    {
        var tracks = video.GetAudioTracks();
        var selectedId = video.GetSelectedAudioTrackId();
        var selected = tracks.FirstOrDefault(track => track.Id == selectedId);
        var rowX = ImGui.GetCursorPosX();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(MutedText, "Audio track");
        ImGui.SameLine(0, Ui(12));
        ImGui.SetNextItemWidth(MathF.Max(Ui(100), width - (ImGui.GetCursorPosX() - rowX)));
        using (UiDisabled.Scope(tracks.Count == 0 || stream.Mode == StreamMode.Viewing))
        {
            var preview = selected is not null ? LocalAudioTrackLabel(selected) : tracks.Count == 0
                ? screenController.Engine.MediaLoadPending ? "Detecting audio tracks…" : "No audio tracks" : "Automatic";
            if (ImGui.BeginCombo("##localVideoAudioTrack", preview))
            {
                foreach (var track in tracks)
                {
                    using (UiDisabled.Scope(stream.Mode == StreamMode.Hosting && track.FfmpegStreamIndex is null))
                        if (ImGui.Selectable($"{LocalAudioTrackLabel(track)}##audio{track.Id}", track.Id == selectedId))
                        {
                            if (!video.SelectAudioTrack(track.Id)) localVideoError = "Could not change the audio track. Try again.";
                            else localVideoError = null;
                        }
                    if (track.Id == selectedId) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
        }
    }
}
