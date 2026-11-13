using System.Security.Cryptography;
using System.Text.Json;
using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private readonly LuckyWheelPreview luckyWheelPreview = new();
    private DateTime luckyWheelPreviewLastFrameUtc = DateTime.MinValue;
    private readonly RouletteRollSoundPlayer luckyWheelPreviewRoll = new("lucky-wheel-spin.wav");
    private readonly RouletteChipSoundPlayer luckyWheelPreviewFinish = new("roulette-chips.wav");
    private int luckyWheelPreviewSoundFinished = -1;
    private void ResetLuckyWheelPreview()
    {
        luckyWheelPreviewRoll.Dispose(); luckyWheelPreviewFinish.Dispose();
        luckyWheelPreview.Clear();
        luckyWheelPreviewKey = null;
        luckyWheelPreviewLastFrameUtc = DateTime.MinValue;
        luckyWheelPreviewSoundFinished = -1;
    }
    private void DrawLuckyWheelDesignPreview()
    {
        var design = luckyWheelDraft.Copy();
        if (string.IsNullOrWhiteSpace(design.Title)) design.Title = "Lucky Wheel";
        design.Segments = design.Segments.Select(s => string.IsNullOrWhiteSpace(s.Text) ? s with { Text = "Option" } : s).ToArray();
        if (luckyWheelPreview.SetDesign(design))
        {
            luckyWheelPreviewKey = null;
            luckyWheelPreviewSoundFinished = -1;
        }
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        luckyWheelPreview.Advance(now);
        var state = luckyWheelPreview.State;
        if (state is null) return;
        UpdateLuckyWheelSound(state, now, true);
        var key = JsonSerializer.Serialize(state);
        var animated = state.Spinning || state.Design.Confetti && state.WinnerId is not null && now - state.StartedUnixMs - state.DurationMs < LuckyWheelScreenRenderer.ConfettiDurationMs;
        if (key != luckyWheelPreviewKey || luckyWheelPageRenderer is null || animated && DateTime.UtcNow - luckyWheelPreviewLastFrameUtc >= TimeSpan.FromMilliseconds(50))
        {
            UploadLuckyWheelPageFrame(BuildLuckyWheelFrame(state, now, preview: true));
            luckyWheelPreviewKey = key;
            luckyWheelPreviewLastFrameUtc = DateTime.UtcNow;
        }
        var contentWidth = ImGui.GetContentRegionAvail().X;
        var availableHeight = Math.Max(Ui(260), ImGui.GetContentRegionAvail().Y - Ui(150));
        var imageWidth = Math.Max(1, Math.Min(contentWidth - Ui(32), (availableHeight - Ui(64)) * 16 / 9));
        GameLayoutPanel("##wheelPreviewPanel", contentWidth, imageWidth * 9 / 16 + Ui(76), () =>
        {
            GameLayoutHeading("Screen preview", FontAwesomeIcon.Tv, "Preview only");
            if (luckyWheelPageRenderer?.PreviewHandle is { } handle && handle != nint.Zero)
            {
                var offset = (ImGui.GetContentRegionAvail().X - imageWidth) / 2;
                if (offset > 0) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offset);
                ImGui.Image(new ImTextureID(handle), new Vector2(imageWidth, imageWidth * 9 / 16));
            }
        });
        SetGameLayoutRowGap(Ui(14));
        GameLayoutPanel("##wheelPreviewActions", contentWidth, Ui(92), () =>
        {
            var width = ImGui.GetContentRegionAvail().X;
            var gap = Ui(8);
            var buttonWidth = (width - gap * 2) / 3;
            if (GameLayoutButton("Back to design", FontAwesomeIcon.ArrowLeft, buttonWidth)) luckyWheelEditorTab = WheelEditorTab.Design;
            ImGui.SameLine(0, gap);
            if (GameLayoutButton("Reset preview", FontAwesomeIcon.Undo, buttonWidth))
            {
                luckyWheelPreview.Reset(); luckyWheelPreviewKey = null;
                luckyWheelPreviewSoundFinished = -1;
            }
            ImGui.SameLine(0, gap);
            using (UiDisabled.Scope(state.Spinning || state.RemainingIds.Length == 0))
                if (GameLayoutButton("Test spin", FontAwesomeIcon.Play, buttonWidth, true))
                    luckyWheelPreview.Spin(RandomNumberGenerator.GetInt32(state.RemainingIds.Length), RandomNumberGenerator.GetInt32(5, 9), now);
            using (UiFonts.PushRelative(.90f)) ImGui.TextColored(MutedText, state.RemainingIds.Length == 0 ? "All options have been drawn. Reset the preview to spin again." : "Test spins stay here and do not affect your Watch Party.");
        }, 12);
    }
}
