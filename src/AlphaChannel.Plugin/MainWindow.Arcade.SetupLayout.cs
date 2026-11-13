using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{


    private static void DrawArcadeSetupParagraph(string text)
    {
        using var font = UiFonts.PushRelative(.90f);
        using var colour = ImRaii.PushColor(ImGuiCol.Text, Hex(0xE2E7EF));
        ImGui.TextWrapped(text);
    }



    private void DrawArcadeReviewHero(string game, string title, string heading, string description,
        string badge, bool cumulative, ActivityPreviewKind? preview)
    {
        var width = ImGui.GetContentRegionAvail().X;
        var artWidth = MathF.Min(Ui(220f), width * .30f);
        var textWidth = MathF.Max(Ui(120f), width - artWidth - Ui(60f));
        float titleHeight;
        using (UiFonts.PushRelative(1.5f, emphasis: true))
            titleHeight = ImGui.CalcTextSize(heading, false, textWidth).Y;
        float descriptionHeight;
        using (UiFonts.PushRelative(.90f))
            descriptionHeight = ImGui.CalcTextSize(description, false, textWidth).Y;
        var height = MathF.Max(Ui(190f), Ui(92f) + titleHeight + descriptionHeight);
        DrawQuizCardBegin("##arcadeSetupHero_" + game, width, height);
        var origin = ImGui.GetWindowPos();
        ImGui.SetCursorScreenPos(origin + UiVec(20f, 18f));
        using (UiFonts.PushRelative(1.5f, emphasis: true))
        using (ImRaii.TextWrapPos(ImGui.GetCursorPosX() + textWidth))
            ImGui.TextColored(Hex(0xE2E7EF), heading);
        ImGui.SetCursorScreenPos(origin + new Vector2(Ui(20f), Ui(30f) + titleHeight));
        using (UiFonts.PushRelative(.90f))
        using (ImRaii.TextWrapPos(ImGui.GetCursorPosX() + textWidth))
            ImGui.TextColored(MutedText, description);
        ImGui.SetCursorScreenPos(origin + new Vector2(Ui(20f), height - Ui(48f)));
        DrawPictionarySetupBadge(badge, true);
        var artOrigin = origin + new Vector2(width - artWidth - Ui(20f), Ui(16f));
        if (preview is { } kind)
        {
            ImGui.SetCursorScreenPos(artOrigin);
            DrawActivityPreview("arcadeHeroArt_" + game, kind, artWidth, 108f, 14f, 8f);
        }
        else
            DrawPictionarySampleCanvas(ImGui.GetWindowDrawList(), artOrigin, artOrigin + new Vector2(artWidth, Ui(108f)));
        ImGui.SetCursorScreenPos(origin + new Vector2(width - artWidth - Ui(20f), height - Ui(48f)));
        if (GameLayoutButton("Scores", FontAwesomeIcon.Trophy, artWidth, true, height: 32f))
            OpenArcadeScoreboard(game, title, cumulative);
        ImGui.EndChild();
    }

    private void DrawArcadeSetupSteps(string id, string[] labels)
    {
        var width = ImGui.GetContentRegionAvail().X;
        var columns = width < Ui(900f) ? 2 : labels.Length;
        var rows = (labels.Length + columns - 1) / columns;
        DrawQuizCardBegin(id, width, Ui(58f + rows * 48f));
        using (UiFonts.PushRelative(1f, emphasis: true))
            ImGui.TextColored(Hex(0xE2E7EF), "How it works");
        var origin = ImGui.GetWindowPos() + UiVec(18f, 48f);
        var cellWidth = (width - Ui(36f)) / columns;
        var draw = ImGui.GetWindowDrawList();
        for (var index = 0; index < labels.Length; index++)
        {
            var cell = origin + new Vector2(index % columns * cellWidth, Ui(48f) * (index / columns));
            var center = cell + UiVec(17f, 17f);
            draw.AddCircleFilled(center, Ui(17f), ImGui.GetColorU32(Accent));
            var number = (index + 1).ToString();
            UiFonts.DrawText(draw, ImGui.GetFontSize(), center - ImGui.CalcTextSize(number) * .5f,
                ImGui.GetColorU32(Vector4.One), number);
            DrawWrappedLines(draw, cell + UiVec(45f, 7f), cellWidth - Ui(55f), ImGui.GetTextLineHeight(), 2,
                ImGui.GetColorU32(Hex(0xE2E7EF)), labels[index]);
        }
        ImGui.EndChild();
    }
}
