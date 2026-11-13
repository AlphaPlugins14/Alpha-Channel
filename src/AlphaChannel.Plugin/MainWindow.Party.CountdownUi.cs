using AlphaChannel.Plugin.Core;
using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private int countdownSeconds = 30;
    private bool countdownPlayNext;
    private string countdownLabel = "Starting soon";
    private bool countdownOverlayOpen;

    private void DrawPartyCountdownControls(Vector2? size = null)
    {
        if (stream.Mode != StreamMode.Hosting) return;
        var active = VisiblePartyCountdown;
        using (UiDisabled.Scope(!CanStartPartyCountdown))
            if (PartyLabeledButton("partyCountdown", active is null ? "Countdown" : active.Clock,
                    FontAwesomeIcon.HourglassHalf, size ?? UiVec(116, 30), true)) countdownOverlayOpen = true;
        if (!CanStartPartyCountdown && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            UiFonts.SetTooltip(countdownBusy ? "Updating countdown…" : "End the current activity before starting a countdown.");
        if (countdownMessage is not null && ImGui.IsItemHovered()) UiFonts.SetTooltip(countdownMessage);
    }

    private void DrawPartyCountdownOverlay()
    {
        if (stream.Mode != StreamMode.Hosting) { countdownOverlayOpen = false; return; }
        if (!countdownOverlayOpen) return;
        var active = VisiblePartyCountdown;
        var parent = ImGui.GetWindowPos(); var parentSize = ImGui.GetWindowSize();
        var size = Vector2.Min(UiVec(580, active is null ? 608 : 354), Vector2.Max(Vector2.One, parentSize - UiVec(36, 36)));
        ImGui.SetNextWindowPos(parent); ImGui.SetNextWindowSize(parentSize); ImGui.SetNextWindowBgAlpha(0);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoBackground;
        if (!BeginWindowOverlay("##partyCountdownOverlay", flags)) { ImGui.End(); return; }
        ImGui.GetWindowDrawList().AddRectFilled(parent, parent + parentSize, ImGui.GetColorU32(new Vector4(0, 0, 0, .62f)));
        ImGui.SetCursorScreenPos(parent + (parentSize - size) * .5f);
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(14)).Push(ImGuiStyleVar.ChildBorderSize, Ui(1))
                   .Push(ImGuiStyleVar.WindowPadding, UiVec(22, 18)).Push(ImGuiStyleVar.ItemSpacing, UiVec(8, 8)))
        using (ImRaii.PushColor(ImGuiCol.ChildBg, CardBg).Push(ImGuiCol.Border, Accent)
                   .Push(ImGuiCol.Button, FrameBg).Push(ImGuiCol.ButtonHovered, AccentFieldBg).Push(ImGuiCol.ButtonActive, AccentActive))
        using (var card = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##partyCountdownCard", size, true, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)))
        {
            if (card)
            {
                var origin = ImGui.GetCursorScreenPos(); var width = ImGui.GetContentRegionAvail().X;
                DrawSectionTitle(FontAwesomeIcon.HourglassHalf, active is null ? "Start a countdown" : "Party countdown");
                ImGui.SetCursorScreenPos(origin + new Vector2(width - Ui(28), -Ui(3)));
                if (PartyIconButton("closePartyCountdown", FontAwesomeIcon.Times, UiVec(28, 28), true)) countdownOverlayOpen = false;
                ImGui.SetCursorScreenPos(origin + UiVec(0, 34));
                ImGui.Separator();
                using (var body = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##partyCountdownBody", new(width, size.Y - Ui(132)), false)))
                {
                    if (body)
                    {
                        DrawPartyCountdownPreview(active);
                        if (active is null) DrawPartyCountdownForm();
                        else
                        {
                            ImGui.TextWrapped(countdownStart?.Target is null ? "Start your content manually when the timer ends." : "The chosen queued video starts automatically when the timer ends.");
                        }
                        if (countdownMessage is not null) ImGui.TextWrapped(countdownMessage);
                    }
                }
                ImGui.SetCursorScreenPos(origin + new Vector2(0, size.Y - Ui(78)));
                ImGui.Separator();
                var buttonWidth = (width - Ui(8)) * .5f;
                if (ActivePartyButton(active is null ? "Close##countdown" : "Cancel countdown", new(buttonWidth, Ui(34))))
                { if (active is not null) CancelPartyCountdown(); countdownOverlayOpen = false; }
                ImGui.SameLine();
                using (UiDisabled.Scope(!CanStartPartyCountdown || active is null && countdownPlayNext && partyQueueState.Upcoming.Count == 0))
                    if (ActivePartyButton(countdownBusy ? "Updating…" : active is null ? "Start countdown" : "Start now", new(buttonWidth, Ui(34)), primary: true))
                    {
                        if (active is null) BeginPartyCountdown(countdownSeconds, countdownLabel, countdownPlayNext);
                        else StartPartyCountdownNow();
                        // Keep errors and the active timer visible in this same dialog.
                    }
            }
        }
        if (ImGui.IsKeyPressed(ImGuiKey.Escape)) countdownOverlayOpen = false;
        ImGui.End();
    }

    private void DrawPartyCountdownPreview(PartyCountdownView? active)
    {
        var origin = ImGui.GetCursorScreenPos(); var width = ImGui.GetContentRegionAvail().X;
        var previewSize = new Vector2(MathF.Min(Ui(180), width * .4f), Ui(102));
        var preview = active ?? new PartyCountdownView(new(Guid.Empty, "", Guid.Empty, countdownLabel, countdownSeconds, 1),
            countdownSeconds, System.Diagnostics.Stopwatch.GetTimestamp());
        DrawCountdownThumbnail(preview, origin, previewSize);
        var textX = previewSize.X + Ui(18); var textWidth = width - textX;
        ImGui.SetCursorScreenPos(origin + new Vector2(textX, Ui(8)));
        using (UiFonts.PushRelative(1f, emphasis: true)) ImGui.TextUnformatted(active is null ? "TV preview" : "Counting down live");
        ImGui.SetCursorScreenPos(origin + new Vector2(textX, Ui(34)));
        ImGui.TextUnformatted(FitPartyTextToWidth(active?.State.Label ?? (string.IsNullOrWhiteSpace(countdownLabel) ? "Starting soon" : countdownLabel), textWidth));
        ImGui.SetCursorScreenPos(origin + new Vector2(textX, Ui(60)));
        using (ImRaii.PushColor(ImGuiCol.Text, MutedText))
        {
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + textWidth);
            ImGui.TextUnformatted("Shown on the TV and in the party directory.");
            ImGui.PopTextWrapPos();
        }
        ImGui.SetCursorScreenPos(origin + new Vector2(0, previewSize.Y + Ui(14)));
    }

    private void DrawPartyCountdownForm()
    {
        ImGui.TextUnformatted("Duration");
        foreach (var (label, seconds) in new[] { ("10 sec", 10), ("30 sec", 30), ("1 min", 60) })
        {
            if (ActivePartyButton(label, UiVec(66, 32), primary: countdownSeconds == seconds)) countdownSeconds = seconds;
            ImGui.SameLine();
        }
        ImGui.TextUnformatted("Custom"); ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputInt("##countdownSeconds", ref countdownSeconds, 0, 0);
        if (ImGui.IsItemHovered()) UiFonts.SetTooltip("Custom duration in seconds (1–3600)");
        countdownSeconds = Math.Clamp(countdownSeconds, 1, 3600);
        ImGui.TextUnformatted("Public label");
        ImGui.SetNextItemWidth(-1); ImGui.InputTextWithHint("##countdownLabel", "Starting soon", ref countdownLabel, 96);
        using (ImRaii.PushColor(ImGuiCol.Text, MutedText)) ImGui.TextWrapped("Optional · locked and private parties use ‘Starting soon’.");
        ImGui.Dummy(UiVec(0, 2)); ImGui.TextUnformatted("When the countdown ends");
        if (DrawCountdownMode("only", "Countdown only", "Start your content manually when ready.", !countdownPlayNext)) countdownPlayNext = false;
        var next = partyQueueState.Upcoming.FirstOrDefault();
        if (DrawCountdownMode("next", "Play next queued video", next?.Title ?? "Add a video to the party queue first.", countdownPlayNext)) countdownPlayNext = true;
    }

    private bool DrawCountdownMode(string id, string title, string caption, bool selected)
    {
        var width = ImGui.GetContentRegionAvail().X;
        using var style = ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, Ui(1)).Push(ImGuiStyleVar.FrameRounding, Ui(8));
        using var colours = ImRaii.PushColor(ImGuiCol.Border, selected ? Accent : BorderSubtle)
            .Push(ImGuiCol.Button, selected ? AccentFieldBg : FrameBg);
        var clicked = ImGui.Button("##countdownMode" + id, new(width, Ui(54)));
        var origin = ImGui.GetItemRectMin(); var draw = ImGui.GetWindowDrawList();
        UiFonts.DrawIcon(draw, ImGui.GetFontSize(), origin + UiVec(14, 19), ImGui.GetColorU32(selected ? AccentHover : MutedText),
            (selected ? FontAwesomeIcon.CheckCircle : FontAwesomeIcon.Circle).ToIconString());
        UiFonts.DrawText(draw, ImGui.GetFontSize(), origin + UiVec(40, 7), ImGui.GetColorU32(ImGuiCol.Text), FitPartyTextToWidth(title, width - Ui(52)));
        UiFonts.DrawText(draw, ImGui.GetFontSize() * .9f, origin + UiVec(40, 30), ImGui.GetColorU32(MutedText), FitPartyTextToWidth(caption, width - Ui(52)));
        return clicked;
    }

    private void DrawCountdownThumbnail(PartyCountdownView countdown, Vector2 origin, Vector2 size,
        Vector4? accent = null, Vector4? background = null)
    {
        DrawPartyThumbnailFallback(new VideoQueueEntry("", "", "Waiting", null, null), origin, size,
            usePlaybackState: false, accentTint: accent, backgroundTint: background, countdown: countdown);
    }
}
