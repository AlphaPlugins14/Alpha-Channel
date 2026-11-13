using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private DateTime friendsRepoCopiedUntil;

    private void DrawFriendsShareBar()
    {
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var stacked = width < Ui(580);
        var gap = Ui(12);
        var cardWidth = stacked ? width : (width - gap) * .5f;
        var height = Ui(112);
        DrawFriendsDiscoveryCard(origin, cardWidth, height, directory: false);
        DrawFriendsDiscoveryCard(origin + new Vector2(stacked ? 0 : cardWidth + gap, stacked ? height + gap : 0), cardWidth, height, directory: true);
        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(width, (stacked ? height * 2 + gap : height) + Ui(14)));
    }

    private void DrawFriendsDiscoveryCard(Vector2 origin, float width, float height, bool directory)
    {
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + new Vector2(width, height), ImGui.GetColorU32(FrameBg), Ui(10));
        draw.AddRect(origin, origin + new Vector2(width, height), ImGui.GetColorU32(AccentInk(.35f)), Ui(10));
        UiFonts.DrawIcon(draw, ImGui.GetFontSize(), origin + UiVec(14, 15), ImGui.GetColorU32(AccentHover),
            (directory ? FontAwesomeIcon.UserFriends : FontAwesomeIcon.UserPlus).ToIconString());
        using (UiFonts.PushRelative(1f, emphasis: true))
            draw.AddText(origin + UiVec(40, 14), ImGui.GetColorU32(ImGuiCol.Text), directory ? "Find Friends" : "Invite your friends");
        var description = directory ? "Meet people who share your interests." : "Bring your friends to Alpha Channel.";
        draw.AddText(origin + UiVec(14, 41), ImGui.GetColorU32(MutedText), FitPartyTextToWidth(description, width - Ui(28)));
        ImGui.SetCursorScreenPos(origin + UiVec(14, 69));
        var copied = DateTime.UtcNow < friendsRepoCopiedUntil;
        if (directory)
        {
            if (AlphaChatButton("friendsBrowseDirectory", "Browse directory", FontAwesomeIcon.ArrowRight, width - Ui(28), Ui(30), primary: true))
                OpenFriendsDirectory();
        }
        else
        {
            if (AlphaChatButton("friendsCopyRepo", copied ? "Copied!" : "Copy repo link",
                    copied ? FontAwesomeIcon.Check : FontAwesomeIcon.Copy, width - Ui(28), Ui(30)))
            {
                ImGui.SetClipboardText("https://plugin-repository.example.invalid/");
                friendsRepoCopiedUntil = DateTime.UtcNow.AddSeconds(2);
            }
            if (ImGui.IsItemHovered()) UiFonts.SetTooltip("Copy the plugin repository link to share with friends.");
        }
    }
}
