using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private Vector2 sharedHeaderOrigin;
    private Core.SharedHeaderLayout sharedHeaderLayout;
    private float HeaderTextRight => sharedHeaderOrigin.X + Ui(sharedHeaderLayout.TitleRight) - ImGui.GetWindowPos().X;

    private void DrawSplashCloseButton()
    {
        // A separate overlay keeps both drawing and hit testing above the
        // splash's full-window drag shield and the sign-in veil.
        var origin = ImGui.GetWindowPos() + new Vector2(ImGui.GetWindowSize().X - Ui(40), Ui(12));
        ImGui.SetNextWindowPos(origin, ImGuiCond.Always);
        ImGui.SetNextWindowSize(UiVec(28, 28), ImGuiCond.Always);
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        var visible = BeginWindowOverlay("##splashCloseButton",
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoBackground);
        try
        {
            if (!visible) return;
            ImGui.SetCursorScreenPos(origin);
            if (DrawSharedUtility("splashClose", FontAwesomeIcon.Times, "Close Alpha Channel")) CloseUi();
        }
        finally { ImGui.End(); }
    }

    private void DrawSharedHeaderChrome()
    {
        var mainSize = ImGui.GetWindowSize();
        var cursor = ImGui.GetCursorScreenPos();
        var layout = sharedHeaderLayout;
        // The avatar grows slightly outside its original slot. Give the chrome
        // child a gutter so its clip rectangle includes the complete circle.
        var origin = sharedHeaderOrigin + UiVec(layout.Left - 4, layout.ProfileY - 4);
        var size = UiVec(layout.IdentityRight - layout.Left, 34f) + UiVec(4, 8);
        var lightingOrigin = Vector2.Zero;
        var lightingClicked = false;
        var menuClicked = false;
        var menuAnchor = sharedHeaderOrigin + UiVec(layout.Width - 12f, Core.SharedHeaderLayout.UtilityTop + Core.SharedHeaderLayout.UtilityRowGap + 33f);
        ImGui.SetCursorScreenPos(origin);
        ImGui.SetNextWindowPos(origin, ImGuiCond.Always);
        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero))
        {
            var visible = BeginWindowChrome("##alphaWindowControls", size,
                ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoSavedSettings |
                ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoDocking);
            try
            {
                if (visible)
                {
                    ImGui.SetScrollX(0); ImGui.SetScrollY(0);
                    DrawSharedHeaderIdentity(sharedHeaderOrigin + UiVec(layout.Left, layout.ProfileY));
                }
            }
            finally { ImGui.EndChild(); }
        }
        // Give the activity strip its own exact hit-test region, allowing it
        // to move left of the identity without blocking Home's search field.
        if (HeaderHasActionBar)
        {
            var actionOrigin = sharedHeaderOrigin + UiVec(HeaderActivityLeft, layout.ActionsY);
            ImGui.SetCursorScreenPos(actionOrigin);
            ImGui.SetNextWindowPos(actionOrigin, ImGuiCond.Always);
            using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            var visible = BeginWindowChrome("##alphaHeaderActivity", new Vector2(HeaderActivityWidth + Ui(2), Ui(32)),
                ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoSavedSettings |
                ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoDocking);
            try
            {
                if (visible)
                {
                    ImGui.SetScrollX(0); ImGui.SetScrollY(0);
                    ImGui.SetCursorScreenPos(actionOrigin);
                    DrawHeaderActivityBar();
                }
            }
            finally { ImGui.EndChild(); }
        }
        for (var row = 0; row < 2; row++)
        {
            var rowLeft = row == 0 ? layout.CountsLeft : layout.FriendsLeft;
            var rowY = Core.SharedHeaderLayout.UtilityTop + row * Core.SharedHeaderLayout.UtilityRowGap;
            var rowOrigin = sharedHeaderOrigin + UiVec(rowLeft, rowY);
            ImGui.SetCursorScreenPos(rowOrigin);
            ImGui.SetNextWindowPos(rowOrigin, ImGuiCond.Always);
            using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            var visible = BeginWindowChrome(row == 0 ? "##alphaHeaderTopUtilities" : "##alphaHeaderLowerUtilities",
                UiVec(layout.Width - 6 - rowLeft, 28), ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoMove |
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoSavedSettings |
                ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoDocking);
            try
            {
                if (!visible) continue;
                ImGui.SetScrollX(0); ImGui.SetScrollY(0);
                ImGui.SetCursorScreenPos(rowOrigin);
                if (row == 0)
                {
                    DrawSharedHeaderOnlineCount();
                    ImGui.SetCursorScreenPos(sharedHeaderOrigin + UiVec(layout.UtilitiesX + 33, rowY));
                    if (DrawSharedUtility("mini", FontAwesomeIcon.Compress, "Mini Mode")) SetMinimized(true, mainSize);
                    ImGui.SetCursorScreenPos(sharedHeaderOrigin + UiVec(layout.UtilitiesX + 66, rowY));
                    if (DrawSharedUtility("close", FontAwesomeIcon.Times, "Close Alpha Channel")) CloseUi();
                }
                else
                {
                    DrawSharedHeaderSocialCounts();
                    var volumeOrigin = sharedHeaderOrigin + UiVec(layout.UtilitiesX, rowY);
                    ImGui.SetCursorScreenPos(volumeOrigin);
                    if (DrawSharedUtility("volume", Plugin.Cfg.Muted || Plugin.Cfg.Volume == 0
                            ? FontAwesomeIcon.VolumeMute : FontAwesomeIcon.VolumeUp, "Volume"))
                        quickVolumeToggleRequested = true;
                    lightingOrigin = volumeOrigin + UiVec(33, 0);
                    ImGui.SetCursorScreenPos(lightingOrigin);
                    lightingClicked = DrawSharedUtility("lighting", FontAwesomeIcon.Lightbulb, "Lighting");
                    if (lightingClicked) quickVolumeCloseRequested = true;
                    ImGui.SetCursorScreenPos(volumeOrigin + UiVec(66, 0));
                    menuClicked = DrawSharedUtility("menu", FontAwesomeIcon.EllipsisV, "Menu");
                    DrawHeaderLightingPanel?.Invoke(lightingOrigin, lightingClicked);
                }
            }
            finally { ImGui.EndChild(); }
        }
        ImGui.SetCursorScreenPos(cursor);
        if (menuClicked) { quickVolumeCloseRequested = true; ImGui.OpenPopup("##headerMenu"); }
        ImGui.SetNextWindowPos(menuAnchor, ImGuiCond.Always, new Vector2(1, 0));
        using (ImRaii.PushColor(ImGuiCol.PopupBg, SidebarBg).Push(ImGuiCol.Border, new Vector4(Accent.X, Accent.Y, Accent.Z, .65f)))
        using (ImRaii.PushStyle(ImGuiStyleVar.PopupRounding, Ui(10)).Push(ImGuiStyleVar.PopupBorderSize, Ui(1))
                   .Push(ImGuiStyleVar.WindowPadding, UiVec(10, 8)).Push(ImGuiStyleVar.ItemSpacing, UiVec(0, 1)))
        using (var menu = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Popup("##headerMenu")))
        {
            if (menu)
            {
                CheckModeratorAccess();
                if (ModeratorApproved && DrawSharedHeaderMenuRow("Moderator Panel", FontAwesomeIcon.UserShield))
                { OpenModeratorPanel(); ImGui.CloseCurrentPopup(); }
#if DEBUG
#endif
                if (DrawSharedHeaderMenuRow("Settings", FontAwesomeIcon.Cog))
                { currentPage = HomePage.Settings; ImGui.CloseCurrentPopup(); }
                if (DrawSharedHeaderMenuRow("Screen", FontAwesomeIcon.Desktop))
                { currentPage = HomePage.Screen; ImGui.CloseCurrentPopup(); }
                if (DrawSharedHeaderMenuRow("Help", FontAwesomeIcon.QuestionCircle, last: true))
                { if (currentPage != HomePage.Help) helpReturnPage = currentPage; currentPage = HomePage.Help; ImGui.CloseCurrentPopup(); }
            }
        }
        DrawQuickVolumeOverlay(sharedHeaderOrigin + UiVec(layout.Width - 326f, 86f));
    }

    private bool DrawSharedUtility(string id, FontAwesomeIcon icon, string tooltip)
    {
        var origin = ImGui.GetCursorScreenPos();
        var size = Ui(28f);
        var clicked = ImGui.InvisibleButton("##headerUtility" + id, new Vector2(size));
        var hovered = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();
        if (hovered) draw.AddRectFilled(origin, origin + new Vector2(size), ImGui.GetColorU32(CardBgHover), Ui(6));
        UiFonts.DrawCenteredIcon(draw, ImGui.GetFontSize() * 1.15f, origin + new Vector2(size * .5f),
            ImGui.GetColorU32(hovered ? Vector4.One : Hex(0xBDC9E5)), icon.ToIconString());
        if (hovered) { ImGui.SetMouseCursor(ImGuiMouseCursor.Hand); UiFonts.SetTooltip(tooltip); }
        return clicked;
    }

    private bool DrawSharedHeaderMenuRow(string label, FontAwesomeIcon icon, bool debug = false, bool last = false)
    {
        var origin = ImGui.GetCursorScreenPos();
        var size = UiVec(184, 36);
        var clicked = ImGui.InvisibleButton("##headerMenu" + label, size);
        var draw = ImGui.GetWindowDrawList();
        if (ImGui.IsItemHovered()) draw.AddRectFilled(origin, origin + size, ImGui.GetColorU32(CardBgHover), Ui(5));
        UiFonts.DrawCenteredIcon(draw, ImGui.GetFontSize() * 1.08f, origin + UiVec(12, 18),
            ImGui.GetColorU32(Hex(0xBDC9E5)), icon.ToIconString());
        draw.AddText(origin + new Vector2(Ui(32), (size.Y - ImGui.GetTextLineHeight()) * .5f), ImGui.GetColorU32(Vector4.One), label);
        if (debug)
        {
            using var font = UiFonts.PushRelative(.6f);
            var badge = ImGui.CalcTextSize("DEBUG ONLY");
            var pos = origin + new Vector2(size.X - badge.X - Ui(9), (size.Y - badge.Y) * .5f);
            draw.AddRectFilled(pos - UiVec(4, 3), pos + badge + UiVec(4, 3), ImGui.GetColorU32(new Vector4(.32f,.06f,.12f,1)), Ui(5));
            draw.AddText(pos, ImGui.GetColorU32(new Vector4(1,.7f,.76f,1)), "DEBUG ONLY");
        }
        if (!last) draw.AddLine(origin + new Vector2(0, size.Y), origin + size, ImGui.GetColorU32(new Vector4(Accent.X,Accent.Y,Accent.Z,.35f)));
        return clicked;
    }
}
