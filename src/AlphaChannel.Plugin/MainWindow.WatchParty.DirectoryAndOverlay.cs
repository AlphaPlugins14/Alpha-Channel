using AlphaChannel.Contracts;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private bool watchPartyCreateOverlayRequested;
    private bool watchPartyJoinOverlayRequested;
    private bool watchPartyLandingOverlayWasOpen;

    private void DrawWatchPartyDirectoryRoomCard(RoomDirectoryDto room, float width, float height, bool game)
    {
        EnsurePartyDirectoryMediaLoaded(room);
        var media = GetPartyDirectoryMedia(room);
        var origin = ImGui.GetCursorScreenPos();
        var size = new Vector2(width, height);
        var inset = Ui(8f);
        var bottomY = origin.Y + height - Ui(42f);
        var buttonWidth = Ui(72f);
        var buttonOrigin = new Vector2(origin.X + width - buttonWidth - inset, bottomY);
        ImGui.PushID("watchPartyLandingRoom" + room.HostAccountId);
        // Keep the card's click targets outside the Join button. A full-card
        // invisible button captures its input before the visible button can.
        var clicked = ImGui.InvisibleButton("##card", new Vector2(width, bottomY - origin.Y));
        var hovered = ImGui.IsItemHovered();
        ImGui.SetCursorScreenPos(new Vector2(origin.X, bottomY));
        clicked |= ImGui.InvisibleButton("##cardFooter",
            new Vector2(buttonOrigin.X - origin.X, origin.Y + height - bottomY));
        hovered |= ImGui.IsItemHovered();
        hovered |= ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(buttonOrigin, origin + size);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + size, ImGui.GetColorU32(hovered ? CardBgHover : CardBg), Ui(9f));
        draw.AddRect(origin, origin + size, ImGui.GetColorU32(hovered ? AccentHover : BorderSubtle), Ui(9f));
        var thumb = game
            ? new Vector2(MathF.Min(width * 0.38f, Ui(185f)), height - inset * 2f)
            : new Vector2(width - inset * 2f, Ui(104f));
        DrawPartyDirectoryThumbnail(draw, room, media, origin + new Vector2(inset, inset), thumb);
        var textX = game ? origin.X + thumb.X + Ui(18f) : origin.X + inset;
        var contentWidth = game ? width - thumb.X - Ui(26f) : width - inset * 2f;
        var rawDescription = WatchPartyDescription(room);
        var title = rawDescription == "No description set for this Watch Party."
            ? media.Title ?? $"{room.HostDisplayName}'s Watch Party" : rawDescription;
        var titleY = game ? origin.Y + Ui(13f) : origin.Y + Ui(122f);
        draw.AddText(new Vector2(textX, titleY), ImGui.GetColorU32(Vector4.One),
            ArcadeFitText(title, contentWidth));
        var type = room.HasMedia ? PartyDirectoryContentTypeName(GetPartyDirectoryContentType(room))
            : "Waiting for content";
        draw.AddText(new Vector2(textX, titleY + Ui(24f)), ImGui.GetColorU32(AccentHover),
            ArcadeFitText(type.ToUpperInvariant(), contentWidth));
        EnsurePartyAvatarLoaded(room.HostAccountId, room.HostDisplayName);
        partyAvatarCache.TryGetValue(room.HostAccountId, out var hostAvatar);
        DrawAvatarAt(new Vector2(textX, bottomY + Ui(6f)),
            hostAvatar?.AvatarIcon ?? FontAwesomeIcon.User.ToString(),
            hostAvatar?.AvatarColorHex, Ui(20f), hostAvatar?.AvatarImageUrl);
        var badgeWidth = PatreonBadgeTier(room.HostAccountId) > 0 ? Ui(34f) : 0f;
        var hostLabel = ArcadeFitText(room.HostDisplayName,
            contentWidth - Ui(game ? 145f : 124f) - badgeWidth);
        DrawUsernameAt(draw,new Vector2(textX + Ui(26f), bottomY + Ui(8f)),hostLabel,MutedText,room.HostAccountId);
        DrawPatreonBadgeAt(new Vector2(textX + Ui(32f) + ImGui.CalcTextSize(hostLabel).X,
            bottomY + Ui(6f)), room.HostAccountId, "featuredHost" + room.HostAccountId);
        ImGui.SetCursorScreenPos(buttonOrigin);
        var joinClicked = ImGui.Button("Join##room", new Vector2(buttonWidth, Ui(33f)));
        if (joinClicked || clicked)
            JoinPartyDirectoryRoom(room);
        var count = $"{room.ViewerCount}/{AlphaChannel.Plugin.Auth.RoomCapacityClient.Shared.Cached(room.HostAccountId)?.Capacity.ToString()??"…"}";
        if (!game)
            draw.AddText(new Vector2(textX, titleY + Ui(53f)), ImGui.GetColorU32(MutedText), count);
        else if (width >= Ui(440f))
            draw.AddText(new Vector2(buttonOrigin.X - Ui(85f), bottomY + Ui(8f)),
                ImGui.GetColorU32(MutedText), count);
        ImGui.PopID();
        ImGui.SetCursorScreenPos(origin + size);
    }

    private void DrawWatchPartyLandingOverlay()
    {
        if (!watchPartyCreateOverlayRequested && !watchPartyJoinOverlayRequested)
        {
            watchPartyLandingOverlayWasOpen = false;
            return;
        }

        if (stream.Mode is StreamMode.Hosting or StreamMode.Viewing)
        {
            var wasCreating = watchPartyCreateOverlayRequested;
            watchPartyCreateOverlayRequested = false;
            watchPartyJoinOverlayRequested = false;
            watchPartyLandingOverlayWasOpen = false;
            if (!wasCreating)
                currentPage = HomePage.WatchAlong;
            return;
        }

        var create = watchPartyCreateOverlayRequested;
        var parentPosition = ImGui.GetWindowPos();
        var parentSize = ImGui.GetWindowSize();
        var panelWidth = MathF.Min(Ui(680f), parentSize.X - Ui(32f));
        var panelHeight = MathF.Min(Ui(create ? 528f : 360f), parentSize.Y - Ui(32f));
        var panelPosition = parentPosition +
            new Vector2((parentSize.X - panelWidth) * 0.5f,
                (parentSize.Y - panelHeight) * 0.5f);

        ImGui.SetNextWindowPos(parentPosition, ImGuiCond.Always);
        ImGui.SetNextWindowSize(parentSize, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        if (!watchPartyLandingOverlayWasOpen)
        {
            
        }
        watchPartyLandingOverlayWasOpen = true;
        const ImGuiWindowFlags overlayFlags =
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoBackground;
        if (!BeginWindowOverlay("##watchPartyLandingOverlay", overlayFlags))
        {
            ImGui.End();
            return;
        }

        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(parentPosition, parentPosition + parentSize,
            ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.54f)));
        ImGui.SetCursorScreenPos(panelPosition);
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(14f)))
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildBorderSize, Ui(1f)))
        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, UiVec(24f, 20f)))
        using (ImRaii.PushColor(ImGuiCol.ChildBg,
                   CardBg))
        using (ImRaii.PushColor(ImGuiCol.Border,
                   new Vector4(Accent.X, Accent.Y, Accent.Z, 0.82f)))
        using (var panel = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##watchPartyLandingOverlayPanel",
                   new Vector2(panelWidth, panelHeight), true,
                   ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)))
        {
            if (panel)
                DrawWatchPartyLandingOverlayContent(panelPosition, panelWidth, panelHeight, create);
        }
        ImGui.End();
    }

    private void DrawWatchPartyLandingOverlayContent(Vector2 origin, float width,
        float height, bool create)
    {
        var draw = ImGui.GetWindowDrawList();
        DrawWatchPartyLandingText(draw, origin + UiVec(24f, 22f), create ? "Create a Watch Party" : "Join a Watch Party", width - Ui(90f), heading: true);
        DrawWatchPartyLandingText(draw, origin + UiVec(24f, 50f), create
                ? "Create a room to watch, listen or play with friends."
                : "Enter a friend's Alpha Channel username to join their room.",
                width - Ui(48f));

        ImGui.SetCursorScreenPos(origin + new Vector2(width - Ui(53f), Ui(17f)));
        using (UiFonts.PushIcon())
        {
            var closeIcon = FontAwesomeIcon.Times.ToIconString();
            if (ImGui.InvisibleButton("##closeWatchPartyLandingOverlay", UiVec(29f, 29f)))
            {
                watchPartyCreateOverlayRequested = false;
                watchPartyJoinOverlayRequested = false;
            }
            UiFonts.DrawText(draw, ImGui.GetFontSize(), origin + new Vector2(width - Ui(45f), Ui(23f)), ImGui.GetColorU32(Vector4.One), closeIcon);
        }

        if (create)
        {
            ImGui.SetCursorScreenPos(origin + UiVec(24f, 88f));
            using var headingFont = UiFonts.PushRelative(1f, emphasis: true);
            ImGui.TextColored(Hex(0xE2E7EF), "Room details");
        }

        var bodyTop = Ui(create ? 124f : 104f);
        var bodyHeight = MathF.Max(Ui(50f), height - bodyTop - Ui(83f));
        ImGui.SetCursorScreenPos(origin + new Vector2(Ui(24f), bodyTop));
        using (var body = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##watchPartyLandingOverlayBody",
                   new Vector2(width - Ui(48f), bodyHeight), false)))
        {
            if (body)
            {
                if (create)
                    DrawCreateRoomFields(ImGui.GetContentRegionAvail().X);
                else
                    DrawWatchPartyLandingJoinFields(ImGui.GetContentRegionAvail().X);
            }
        }

        var buttonWidth = (width - Ui(58f)) * 0.5f;
        var footerY = origin.Y + height - Ui(59f);
        ImGui.SetCursorScreenPos(new Vector2(origin.X + Ui(24f), footerY));
        DrawDjActionButton("##cancelWatchPartyLandingOverlay", FontAwesomeIcon.Times,
            "Cancel", new Vector2(buttonWidth, Ui(38f)), false, () =>
            {
                watchPartyCreateOverlayRequested = false;
                watchPartyJoinOverlayRequested = false;
            });
        ImGui.SameLine(0f, Ui(10f));
        DrawDjActionButton("##confirmWatchPartyLandingOverlay", FontAwesomeIcon.Users,
            create ? "Create Watch Party" : "Join Watch Party",
            new Vector2(buttonWidth, Ui(38f)), false, () =>
            {
                if (create)
                {
                    CreateEmptyWatchParty();
                    if (createLockedRoomPasswordPopupRequested)
                        watchPartyCreateOverlayRequested = false;
                }
                else
                {
                    DoJoin(joinHostNameInput, joinPasswordInput);
                }
            }, true);
    }

    private void DrawWatchPartyLandingJoinFields(float width)
    {
        var sideBySide = width >= Ui(390f);
        var gap = Ui(12f);
        var hostWidth = sideBySide ? width * 0.56f : width;
        var passwordWidth = sideBySide ? width - hostWidth - gap : width;
        var origin = ImGui.GetCursorScreenPos();
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(7f)))
        using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, UiVec(9f, 6f)))
        using (ImRaii.PushColor(ImGuiCol.FrameBg,
                   FrameBg))
        {
            ImGui.TextColored(MutedText, "Host username");
            ImGui.SetNextItemWidth(hostWidth);
            ImGui.InputTextWithHint("##watchPartyLandingJoinHost",
                "Alpha Channel username", ref joinHostNameInput, 32);
            ImGui.SetCursorScreenPos(sideBySide
                ? origin + new Vector2(hostWidth + gap, 0f)
                : origin + UiVec(0f, 67f));
            ImGui.TextColored(MutedText, "Password (optional)");
            ImGui.SetNextItemWidth(passwordWidth);
            ImGui.InputTextWithHint("##watchPartyLandingJoinPassword", "Optional",
                ref joinPasswordInput, 64, ImGuiInputTextFlags.Password);
        }
        if (!string.IsNullOrWhiteSpace(joinError))
        {
            ImGui.Dummy(UiVec(0f, 12f));
            ImGui.TextColored(Danger, joinError);
        }
    }
}
