using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private bool alphaChatRulesOpen;
    private static readonly string[] AlphaChatRules =
    [
        "Be respectful of each other.",
        "No hate speech, harmful language or discrimination.",
        "Whilst we aren't limiting the chat to SFW topics only, please be mindful of other members and don't go overboard.",
        "Do not share real-life personal details about yourself or others in the chat.",
    ];

    private void DrawAlphaChatRulesOverlay()
    {
        if (!alphaChatRulesOpen) return;
        if (currentPage != HomePage.AlphaChat) { alphaChatRulesOpen = false; return; }
        var parent = ImGui.GetWindowPos(); var parentSize = ImGui.GetWindowSize();
        var size = new Vector2(MathF.Min(Ui(560f), parentSize.X - Ui(32f)),
            MathF.Min(Ui(440f), parentSize.Y - Ui(32f)));
        var position = parent + (parentSize - size) * .5f;
        ImGui.SetNextWindowPos(parent, ImGuiCond.Always);
        ImGui.SetNextWindowSize(parentSize, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoBackground;
        if (!BeginWindowOverlay("##alphaChatRulesOverlay", flags)) { ImGui.End(); return; }
        ImGui.GetWindowDrawList().AddRectFilled(parent, parent + parentSize,
            ImGui.GetColorU32(new Vector4(0f, 0f, 0f, .54f)));
        ImGui.SetCursorScreenPos(position);
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(14f))
            .Push(ImGuiStyleVar.ChildBorderSize, Ui(1f))
            .Push(ImGuiStyleVar.WindowPadding, UiVec(20f, 18f))
            .Push(ImGuiStyleVar.ItemSpacing, UiVec(8f, 8f)))
        using (ImRaii.PushColor(ImGuiCol.ChildBg, CardBg)
            .Push(ImGuiCol.Border, new Vector4(Accent.X, Accent.Y, Accent.Z, .55f)))
        using (var panel = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##alphaChatRulesPanel", size, true,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)))
        {
            if (panel)
            {
                var heading = ImGui.GetCursorScreenPos();
                using (UiFonts.PushIcon()) ImGui.TextColored(AccentHover, FontAwesomeIcon.ClipboardList.ToIconString());
                ImGui.SameLine(); DrawSocialHeading("Alpha Chat rules");
                ImGui.SetCursorScreenPos(position + new Vector2(size.X - Ui(48f), Ui(14f)));
                if (AlphaChatButton("closeAlphaChatRules", "", FontAwesomeIcon.Times, Ui(28f), Ui(28f))) alphaChatRulesOpen = false;
                ImGui.SetCursorScreenPos(heading + UiVec(0f, 38f));
                using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero))
                using (var body = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##alphaChatRulesBody", new Vector2(0f, size.Y - Ui(126f)), false)))
                {
                    if (body)
                    {
                        for (var i = 0; i < AlphaChatRules.Length; i++)
                        {
                            ImGui.TextColored(AccentHover, $"{i + 1}.");
                            ImGui.SameLine(Ui(28f));
                            using (ImRaii.Group())
                            {
                                ImGui.PushTextWrapPos(ImGui.GetWindowContentRegionMax().X - Ui(8f));
                                ImGui.TextUnformatted(AlphaChatRules[i]);
                                ImGui.PopTextWrapPos();
                            }
                            ImGui.Dummy(UiVec(0f, 2f));
                        }
                        ImGui.Separator(); ImGui.Spacing();
                        using (UiFonts.PushIcon()) ImGui.TextColored(AccentHover, FontAwesomeIcon.InfoCircle.ToIconString());
                        ImGui.SameLine(); DrawSocialHeading("Managing whose messages you see");
                        ImGui.PushTextWrapPos(ImGui.GetWindowContentRegionMax().X - Ui(8f));
                        DrawSocialHelper("To hide a specific user's messages, block them via the chat roster (click the people here button).");
                        ImGui.Spacing();
                        DrawSocialHelper("You can unblock someone in Settings → Other → Blocked Accounts.");
                        ImGui.PopTextWrapPos();
                    }
                }
                ImGui.SetCursorScreenPos(position + new Vector2(size.X - Ui(140f), size.Y - Ui(54f)));
                if (ActivePartyButton("Close##alphaChatRules", UiVec(120f, 36f), primary: true)) alphaChatRulesOpen = false;
            }
        }
        if (ImGui.IsKeyPressed(ImGuiKey.Escape)) alphaChatRulesOpen = false;
        ImGui.End();
    }
}
