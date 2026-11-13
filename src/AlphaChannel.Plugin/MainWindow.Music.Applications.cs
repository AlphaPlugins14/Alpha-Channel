using AlphaChannel.BroadcastStudio;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private bool StudioUsesApplication => studioSettings.MusicSource == StudioMusicSource.Application;
    private StudioApplication? StudioSelectedApplication => StudioState.Applications.FirstOrDefault(a =>
        a.ProcessId == studioSettings.ApplicationProcessId && a.StartedUtcTicks == studioSettings.ApplicationStartedUtcTicks);

    private void DrawStudioSourceSwitch(Vector2 origin, float width)
    {
        StudioHeading("Music source", FontAwesomeIcon.Music);
        StudioAt(origin, 0, 30);
        var half = (width - Ui(6)) / 2;
        if (GameLayoutButton("Song queue", FontAwesomeIcon.Music, half, !StudioUsesApplication, height: 36))
            SetStudioMusicSource(StudioMusicSource.SongQueue);
        ImGui.SameLine(0, Ui(6));
        using (UiDisabled.Scope(!StudioState.ApplicationCaptureSupported))
            if (GameLayoutButton("Application audio", FontAwesomeIcon.Desktop, half, StudioUsesApplication, height: 36))
                SetStudioMusicSource(StudioMusicSource.Application);
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && !StudioState.ApplicationCaptureSupported)
            UiFonts.SetTooltip("Application capture requires Windows build 20348 or later.");
    }
    private string StudioApplicationStatus(StudioSnapshot state) => state.ApplicationError != null ? "Capture unavailable" :
        !studioSettings.ApplicationCaptureEnabled ? "Capture off" : !state.ApplicationCapturing ? "Starting capture…" :
        state.MusicLevel > .002f ? "Receiving audio" : "Waiting for audio";

    private void DrawStudioCaptureToggle(string id)
    {
        var on = studioSettings.ApplicationCaptureEnabled;
        using var disabled = UiDisabled.Scope(!StudioState.ApplicationCaptureSupported || (!on && StudioSelectedApplication == null));
        if (DrawSettingsToggle("##" + id, "", ref on))
            SetStudioApplicationCapture(on);
        ImGui.SameLine(0, Ui(7)); ImGui.TextColored(on ? AccentHover : MutedText, on ? "Capture on" : "Capture off");
    }
    private void StudioApplicationTile(Vector2 at, Vector2 size)
    {
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(at, at + size, ImGui.GetColorU32(FrameBg), Ui(7));
        draw.AddRect(at, at + size, ImGui.GetColorU32(BorderSubtle), Ui(7));
        using var font = UiFonts.PushIcon();
        var icon = FontAwesomeIcon.Desktop.ToIconString();
        var fontSize = Ui(27);
        var textSize = ImGui.CalcTextSize(icon) * (fontSize / ImGui.GetFontSize());
        UiFonts.DrawText(draw, fontSize, at + (size - textSize) / 2, ImGui.GetColorU32(AccentHover), icon);
    }
    private void DrawStudioApplicationSource(Vector2 origin, float width, float height)
    {
        var state = StudioState;
        var selected = StudioSelectedApplication;
        StudioAt(origin, 0, 82); ImGui.TextColored(MutedText, "Capture audio from");
        StudioAt(origin, 0, 106);
        ImGui.SetNextItemWidth(Math.Max(Ui(140), width - Ui(102)));
        if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginCombo("##studioApplication", selected?.Name ?? "Choose an application")))
        {
            foreach (var app in state.Applications)
            {
                using var id = ImRaii.PushId(app.ProcessId);
                if (ImGui.Selectable(app.Name, selected?.ProcessId == app.ProcessId))
                {
                    studioSettings = studioSettings with { ApplicationProcessId = app.ProcessId,
                        ApplicationStartedUtcTicks = app.StartedUtcTicks, ApplicationCaptureEnabled = false };
                    StudioSettingsChanged();
                }
            }
            if (state.Applications.Length == 0) ImGui.TextDisabled("Open your music application, then refresh.");
            ImGui.EndCombo();
        }
        ImGui.SameLine(0, Ui(8));
        if (GameLayoutButton("Refresh", FontAwesomeIcon.Sync, Ui(94), height: 28)) StudioAction("applications");
        StudioAt(origin, 0, 154);
        ImGui.TextColored(state.ApplicationCapturing && state.MusicLevel > .002f ? new Vector4(.2f, .9f, .32f, 1) : MutedText,
            StudioApplicationStatus(state));
        ImGui.SetCursorScreenPos(origin + new Vector2(width - Ui(125), Ui(150)));
        DrawStudioCaptureToggle("studioApplicationCapture");
        StudioAt(origin, 0, 191); ImGui.TextColored(MutedText, "Audio level");
        StudioSegments(origin + UiVec(0, 216), width, state.MusicLevel, 14);
        StudioAt(origin, 0, 254);
        StudioPanel("##studioApplicationInfo", width, Ui(104), () =>
        {
            var at = ImGui.GetCursorScreenPos();
            StudioApplicationTile(at, UiVec(44, 44));
            StudioAt(at, 56, 0);
            StudioClippedText(ImGui.GetCursorScreenPos(), width - Ui(80), selected?.Name ?? "Application audio");
            StudioAt(at, 56, 26);
            using var helperFont = UiFonts.PushRelative(0.90f);
            using var helperGroup = ImRaii.Group();
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width - Ui(80));
            ImGui.TextColored(MutedText, "Control your music in this application.");
            ImGui.TextColored(MutedText, "Your microphone is mixed in separately.");
            ImGui.PopTextWrapPos();
        });
        StudioAt(origin, 0, 375);
        using (UiFonts.PushRelative(0.90f))
        {
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
        if (state.ApplicationError is { } error) ImGui.TextColored(Danger, error);
        else if (selected == null && studioSettings.ApplicationProcessId != 0)
            ImGui.TextColored(MutedText, "Application unavailable. Refresh and choose it again.");
        else ImGui.TextColored(MutedText, "Browser capture may include audio from multiple tabs.");
        ImGui.PopTextWrapPos();
        }
        StudioAt(origin, 0, 412); var monitorApplication = studioSettings.MonitorApplication;
        if (ImGui.Checkbox("Hear captured application", ref monitorApplication))
        { studioSettings = studioSettings with { MonitorApplication = monitorApplication }; StudioSettingsChanged(); }
        if (ImGui.IsItemHovered()) UiFonts.SetTooltip("Hear the captured audio locally. The shared volume and mute controls adjust this monitor; the application's own audio is controlled separately.");
        StudioAt(origin, 0, height / Ui(1) - 48);
        using (UiFonts.PushRelative(0.90f))
            ImGui.TextColored(MutedText, "Your song queue is saved when switching sources.");
    }
}
