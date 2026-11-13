using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private bool patreonPopupOpen;
    private const string PatreonMembershipUrl = "https://www.patreon.com/c/alphachannelplugin/membership";
    private const string PatreonUpgradeLabel = "Upgrade with Patreon";
    private const string PatreonHostIntro = "Your support gives hosts more ways to share and bring people together.";
    private const string PatreonSupporterIntro = "Get extra ways to participate, show your support, and be part of the community.";

    private static readonly (FontAwesomeIcon Icon, string Title, string Copy)[] PatreonHostBenefits =
    [
        (FontAwesomeIcon.Users, "Bring more people together", "Larger Watch Parties."),
        (FontAwesomeIcon.Star, "Share in higher quality", "Higher-quality broadcasts."),
        (FontAwesomeIcon.Clock, "Keep the party going", "A larger weekly broadcast allowance."),
        (FontAwesomeIcon.Bullhorn, "Get your parties noticed", "Promotion for your public parties."),
    ];
    private static readonly (FontAwesomeIcon Icon, string Title, string Copy)[] PatreonSupporterBenefits =
    [
        (FontAwesomeIcon.Smile, "More Live Reactions", "A wider choice of on-TV reactions."),
        (FontAwesomeIcon.Heart, "Show your support", "A Patreon badge inside the plugin."),
        (FontAwesomeIcon.Comments, "Stand out in our community", "A supporter role on Discord."),
        (FontAwesomeIcon.Bolt, "Try what's coming next", "Early access to new features."),
    ];

    private void DrawPatreonPopup()
    {
        if (!patreonPopupOpen) return;
        var parentPos = ImGui.GetWindowPos();
        var parentSize = ImGui.GetWindowSize();
        var cardSize = new Vector2(MathF.Min(Ui(820f), parentSize.X - Ui(40f)),
            MathF.Min(Ui(790f), parentSize.Y - Ui(40f)));
        ImGui.SetNextWindowPos(parentPos, ImGuiCond.Always);
        ImGui.SetNextWindowSize(parentSize, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
                    ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings |
                    ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
                    ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoBackground;
        if (!BeginWindowOverlay("##patreonOverlay", flags))
        {
            ImGui.End();
            return;
        }
        ImGui.GetWindowDrawList().AddRectFilled(parentPos, parentPos + parentSize,
            ImGui.GetColorU32(new Vector4(0f, 0.01f, 0.035f, 0.76f)));
        ImGui.SetCursorScreenPos(parentPos + (parentSize - cardSize) * 0.5f);
        using var styles = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, UiVec(22f, 18f))
            .Push(ImGuiStyleVar.ChildRounding, Ui(14f))
            .Push(ImGuiStyleVar.ChildBorderSize, Ui(1f))
            .Push(ImGuiStyleVar.ItemSpacing, UiVec(8f, 6f));
        using var colors = ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0.025f, 0.03f, 0.06f, 0.995f))
            .Push(ImGuiCol.Border, new Vector4(Accent.X, Accent.Y, Accent.Z, 0.9f));
        if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginChild("##patreonCard", cardSize, true,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)))
        {
            DrawPatreonHeader();
            const string helper = "Explore membership tiers and their benefits. Opens in your browser.";
            var width = ImGui.GetContentRegionAvail().X;
            var footerHeight = Ui(44f) + ImGui.CalcTextSize(helper, false, width).Y
                + ImGui.GetStyle().ItemSpacing.Y * 3f;
            var bodyHeight = MathF.Max(Ui(1f), ImGui.GetContentRegionAvail().Y - footerHeight);
            using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero)
                       .Push(ImGuiStyleVar.ChildBorderSize, 0f))
            using (ImRaii.PushColor(ImGuiCol.ChildBg, Vector4.Zero))
            {
                if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginChild("##patreonBody", new Vector2(0f, bodyHeight), false)))
                {
                    DrawPatreonHero();
                    ImGui.Dummy(UiVec(0f, 5f));
                    DrawPatreonBenefits();
                    ImGui.Dummy(UiVec(0f, 5f));
                    ImGui.Separator();
                    ImGui.Dummy(UiVec(0f, 5f));
                    DrawPatreonCenteredText("Help us build more ways to spend time together", Vector4.One, 1.08f);
                    DrawPatreonCenteredText("Your membership helps fund our servers, streaming infrastructure, and continued development—so Alpha Channel can grow with even more fun social features.", MutedText);
                }
                ImGui.EndChild();
            }
            DrawPatreonJoinButton();
            DrawPatreonCenteredText(helper, MutedText);
        }
        ImGui.EndChild();
        ImGui.End();
    }

    private void DrawPatreonHeader()
    {
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var draw = ImGui.GetWindowDrawList();
        var center = start + new Vector2(width * 0.5f, Ui(25f));
        draw.AddCircleFilled(center, Ui(23f), ImGui.GetColorU32(new Vector4(0.075f, 0.06f, 0.14f, 1f)), 40);
        draw.AddCircle(center, Ui(23f), ImGui.GetColorU32(Accent), 40, Ui(2.5f));
        draw.AddCircle(center, Ui(20f), ImGui.GetColorU32(PatreonOrange), 40, Ui(1.5f));
        using (UiFonts.PushIcon())
        {
            var heart = FontAwesomeIcon.Heart.ToIconString();
            UiFonts.DrawText(draw, ImGui.GetFontSize(), center - ImGui.CalcTextSize(heart) * 0.5f, ImGui.GetColorU32(PatreonOrange), heart);
        }
        ImGui.SetCursorScreenPos(start + new Vector2(width - Ui(30f), -Ui(5f)));
        using (ImRaii.PushColor(ImGuiCol.Button, Vector4.Zero)
                   .Push(ImGuiCol.ButtonHovered, new Vector4(1f, 1f, 1f, 0.08f)))
        {
            if (ImGui.Button("×##closePatreon", UiVec(30f, 28f))) patreonPopupOpen = false;
        }
        ImGui.SetCursorScreenPos(start + UiVec(0f, 56f));
        DrawPatreonCenteredText("Get more from every Watch Party", Vector4.One, 1.38f);
        DrawPatreonCenteredText("Enjoy extra perks, help your hosts, and support the future of Alpha Channel.", MutedText);
        ImGui.Dummy(UiVec(0f, 4f));
    }

    private float PatreonTextHeight(string text, float width, float scale = .90f)
    {
        using var uiFontScale6670=UiFonts.PushRelative(scale, emphasis: scale > 1f);
        var height = ImGui.CalcTextSize(text, false, width).Y;
        uiFontScale6670.Dispose();
        return height;
    }

    private void DrawPatreonTextAt(Vector2 pos, float width, string text, Vector4 color, float scale = .90f)
    {
        if(color == Vector4.One) color=Hex(0xE2E7EF);
        using var uiFontScale6943=UiFonts.PushRelative(scale, emphasis: scale > 1f);
        ImGui.SetCursorScreenPos(pos);
        using (ImRaii.TextWrapPos(ImGui.GetCursorPosX() + width)) ImGui.TextColored(color, text);
        uiFontScale6943.Dispose();
    }

    private void DrawPatreonHero()
    {
        const string title = "Your support helps the whole party";
        const string copy = "Join a Patreon host's room as a verified Patreon member to reduce their weekly broadcast allowance usage—helping them share more with everyone.";
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var textWidth = MathF.Max(Ui(1f), width - Ui(94f));
        var titleHeight = PatreonTextHeight(title, textWidth, 1.13f);
        var height = MathF.Max(Ui(94f), Ui(36f) + titleHeight + PatreonTextHeight(copy, textWidth));
        ImGui.Dummy(new Vector2(width, height));
        var after = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + new Vector2(width, height), ImGui.GetColorU32(new Vector4(0.055f, 0.065f, 0.12f, 1f)), Ui(11f));
        draw.AddRect(origin, origin + new Vector2(width, height), ImGui.GetColorU32(new Vector4(Accent.X, Accent.Y, Accent.Z, 0.65f)), Ui(11f));
        UiFonts.DrawIcon(draw, ImGui.GetFontSize() * 2f, origin + UiVec(18f, 27f), ImGui.GetColorU32(Accent), FontAwesomeIcon.Users.ToIconString());
        UiFonts.DrawIcon(draw, ImGui.GetFontSize() * 0.85f, origin + UiVec(51f, 23f), ImGui.GetColorU32(PatreonOrange), FontAwesomeIcon.Heart.ToIconString());
        DrawPatreonTextAt(origin + UiVec(76f, 15f), textWidth, title, Vector4.One, 1.13f);
        DrawPatreonTextAt(origin + new Vector2(Ui(76f), Ui(22f) + titleHeight), textWidth, copy, MutedText);
        ImGui.SetCursorScreenPos(after);
    }

    private float PatreonPanelHeight(float width, string heading, string intro,
        (FontAwesomeIcon Icon, string Title, string Copy)[] benefits)
    {
        var height = Ui(51f) + PatreonTextHeight(heading, width - Ui(61f), 1.13f)
            + PatreonTextHeight(intro, width - Ui(32f));
        foreach (var benefit in benefits)
            height += PatreonTextHeight(benefit.Title, width - Ui(70f))
                + PatreonTextHeight(benefit.Copy, width - Ui(70f)) + Ui(18f);
        return height;
    }

    private void DrawPatreonBenefits()
    {
        var width = ImGui.GetContentRegionAvail().X;
        var stacked = width < Ui(640f);
        var panelWidth = stacked ? width : (width - Ui(12f)) * 0.5f;
        var hostHeight = PatreonPanelHeight(panelWidth, "For hosts", PatreonHostIntro, PatreonHostBenefits);
        var supporterHeight = PatreonPanelHeight(panelWidth, "For every supporter", PatreonSupporterIntro, PatreonSupporterBenefits);
        var height = MathF.Max(hostHeight, supporterHeight);
        var origin = ImGui.GetCursorScreenPos();
        DrawPatreonBenefitPanel("For hosts", FontAwesomeIcon.BroadcastTower, PatreonHostIntro,
            PatreonHostBenefits, panelWidth, stacked ? hostHeight : height);
        ImGui.SetCursorScreenPos(stacked
            ? origin + new Vector2(0f, hostHeight + Ui(12f))
            : origin + new Vector2(panelWidth + Ui(12f), 0f));
        DrawPatreonBenefitPanel("For every supporter", FontAwesomeIcon.Heart, PatreonSupporterIntro,
            PatreonSupporterBenefits, panelWidth, stacked ? supporterHeight : height);
        ImGui.SetCursorScreenPos(origin + new Vector2(0f,
            (stacked ? hostHeight + Ui(12f) + supporterHeight : height) + ImGui.GetStyle().ItemSpacing.Y));
    }

    private void DrawPatreonBenefitPanel(string heading, FontAwesomeIcon headingIcon, string intro,
        (FontAwesomeIcon Icon, string Title, string Copy)[] benefits, float width, float height)
    {
        var origin = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(width, height));
        var after = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + new Vector2(width, height), ImGui.GetColorU32(new Vector4(0.045f, 0.055f, 0.095f, 1f)), Ui(10f));
        draw.AddRect(origin, origin + new Vector2(width, height), ImGui.GetColorU32(new Vector4(0.24f, 0.3f, 0.46f, 0.9f)), Ui(10f));
        UiFonts.DrawIcon(draw, ImGui.GetFontSize() * 1.2f, origin + UiVec(16f, 16f), ImGui.GetColorU32(Accent), headingIcon.ToIconString());
        DrawPatreonTextAt(origin + UiVec(45f, 16f), width - Ui(61f), heading, Vector4.One, 1.13f);
        var headingHeight = PatreonTextHeight(heading, width - Ui(61f), 1.13f);
        DrawPatreonTextAt(origin + new Vector2(Ui(16f), Ui(25f) + headingHeight), width - Ui(32f), intro, MutedText);
        var y = Ui(36f) + headingHeight + PatreonTextHeight(intro, width - Ui(32f));
        draw.AddLine(origin + new Vector2(Ui(16f), y), origin + new Vector2(width - Ui(16f), y), ImGui.GetColorU32(new Vector4(0.24f, 0.3f, 0.46f, 0.7f)));
        y += Ui(14f);
        var textWidth = width - Ui(70f);
        foreach (var benefit in benefits)
        {
            UiFonts.DrawIcon(draw, ImGui.GetFontSize() * 1.35f, origin + new Vector2(Ui(17f), y + Ui(5f)), ImGui.GetColorU32(benefit.Icon == FontAwesomeIcon.Heart ? PatreonOrange : Accent), benefit.Icon.ToIconString());
            var titleHeight = PatreonTextHeight(benefit.Title, textWidth);
            DrawPatreonTextAt(origin + new Vector2(Ui(54f), y), textWidth, benefit.Title, Vector4.One);
            DrawPatreonTextAt(origin + new Vector2(Ui(54f), y + titleHeight + Ui(5f)), textWidth, benefit.Copy, MutedText);
            y += titleHeight + PatreonTextHeight(benefit.Copy, textWidth) + Ui(18f);
        }
        ImGui.SetCursorScreenPos(after);
    }

    private static void OpenPatreonMembershipPage()
    {
        try { Dalamud.Utility.Util.OpenLink(PatreonMembershipUrl); }
        catch (Exception) { AepLog.Warning("[Support] Failed to open browser."); }
    }

    private void DrawPatreonUpgradeLink(float? rightEdge = null)
    {
        if (ConfiguredPatreonTier() > 0) return;
        var size = ImGui.CalcTextSize(PatreonUpgradeLabel);
        if (rightEdge is { } edge && ImGui.GetItemRectMax().X - ImGui.GetWindowPos().X + Ui(12f) + size.X <= edge)
        {
            ImGui.SameLine();
            ImGui.SetCursorPosX(edge - size.X);
        }
        var origin = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton("##patreonUpgradeLink", size);
        var hovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled);
        ImGui.GetWindowDrawList().AddText(origin,
            ImGui.GetColorU32(hovered ? PatreonOrangeHover : PatreonOrange), PatreonUpgradeLabel);
        if (clicked || hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) OpenPatreonMembershipPage();
        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            UiFonts.SetTooltip("Open Alpha Channel memberships in your browser");
        }
    }

    private void DrawPatreonJoinButton()
    {
        var origin = ImGui.GetCursorScreenPos();
        var size = new Vector2(ImGui.GetContentRegionAvail().X, Ui(44f));
        if (ImGui.InvisibleButton("##joinPatreon", size)) OpenPatreonMembershipPage();
        var hovered = ImGui.IsItemHovered();
        if (hovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            UiFonts.SetTooltip("Open Alpha Channel memberships in your browser");
        }
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + size, ImGui.GetColorU32(hovered ? AccentHover : Accent), Ui(8f));
        const string text = "Become a supporter on Patreon";
        var textSize = ImGui.CalcTextSize(text);
        var x = origin.X + (size.X - textSize.X - Ui(24f)) * 0.5f;
        UiFonts.DrawIcon(draw, ImGui.GetFontSize(), new Vector2(x, origin.Y + (size.Y - textSize.Y) * 0.5f), ImGui.GetColorU32(PatreonOrange), FontAwesomeIcon.Heart.ToIconString());
        UiFonts.DrawText(draw, ImGui.GetFontSize(), new Vector2(x + Ui(24f), origin.Y + (size.Y - textSize.Y) * 0.5f), ImGui.GetColorU32(Vector4.One), text);
    }

    private void DrawPatreonCenteredText(string text, Vector4 color, float scale = .90f)
    {
        if(color == Vector4.One) color=Hex(0xE2E7EF);
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        using var uiFontScale15476=UiFonts.PushRelative(scale, emphasis: scale > 1f);
        var textSize = ImGui.CalcTextSize(text);
        // A wrap boundary at the measured text width can wrap the last word
        // due to font rounding. Only wrap when the full line does not fit.
        if (textSize.X <= width)
        {
            ImGui.SetCursorScreenPos(origin + new Vector2((width - textSize.X) * 0.5f, 0f));
            using (ImRaii.TextWrapPos(-1f)) ImGui.TextColored(color, text);
        }
        else
        {
            ImGui.SetCursorScreenPos(origin);
            using (ImRaii.TextWrapPos(ImGui.GetCursorPosX() + width)) ImGui.TextColored(color, text);
        }
        var bottom = ImGui.GetItemRectMax().Y;
        uiFontScale15476.Dispose();
        ImGui.SetCursorScreenPos(new Vector2(origin.X, bottom + ImGui.GetStyle().ItemSpacing.Y));
    }

    private void DrawPatreonFeatureTag(float gap = 12f)
    {
        ImGui.SameLine(0f, Ui(gap));
        using var uiFontScale16413=UiFonts.PushRelative(0.76f);
        ImGui.TextColored(PatreonOrange, "PATREON FEATURE");
        uiFontScale16413.Dispose();
    }
}
