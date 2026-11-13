using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.Game;
using TerritoryType = Lumina.Excel.Sheets.TerritoryType;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    //
    // Resolve the player's location only when explicitly requested.
    // The Location field otherwise remains completely user-controlled.
    //
    private unsafe string GetCurrentWatchPartyLocation()
    {
        if (!Plugin.ClientState.IsLoggedIn)
        {
            return string.Empty;
        }

        try
        {
            var territoryId =
                Plugin.ClientState.TerritoryType;

            if (territoryId == 0)
            {
                return string.Empty;
            }

            var housingManager =
                HousingManager.Instance();

            var isHousingTerritory =
                housingManager is not null &&
                housingManager->CurrentTerritory is not null;

            //
            // When inside an estate, the current client territory can
            // be an interior territory. Resolve its original housing
            // district so the form says "Mist" rather than an internal
            // estate-interior name.
            //
            if (isHousingTerritory)
            {
                var originalTerritoryId =
                    HousingManager
                        .GetOriginalHouseTerritoryTypeId();

                if (originalTerritoryId != 0)
                {
                    territoryId =
                        originalTerritoryId;
                }
            }

            var territory =
                Plugin.DataManager
                    .GetExcelSheet<TerritoryType>()
                    .GetRow(territoryId);

            var territoryName =
                territory
                    .PlaceName
                    .Value
                    .Name
                    .ToString()
                    .Trim();

            if (string.IsNullOrWhiteSpace(
                    territoryName))
            {
                return string.Empty;
            }

            if (!isHousingTerritory)
            {
                var instance =
                    Plugin.ClientState.Instance;

                return instance > 0
                    ? $"{territoryName} ({instance})"
                    : territoryName;
            }

            var wardIndex =
                housingManager->GetCurrentWard();

            if (wardIndex < 0)
            {
                return territoryName;
            }

            var wardNumber =
                wardIndex + 1;

            var plotIndex =
                housingManager->GetCurrentPlot();

            var roomNumber =
                housingManager->GetCurrentRoom();

            //
            // HousingManager uses -128 and -127 for apartments in the
            // main division and subdivision respectively.
            //
            var isApartment =
                plotIndex is -128 or -127;

            if (isApartment)
            {
                return roomNumber > 0
                    ? $"{territoryName}, Ward {wardNumber}, Apt {roomNumber}"
                    : $"{territoryName}, Ward {wardNumber}, Apartments";
            }

            if (plotIndex >= 0)
            {
                var plotNumber =
                    plotIndex + 1;

                return
                    $"{territoryName}, Ward {wardNumber}, Plot {plotNumber}";
            }

            return
                $"{territoryName}, Ward {wardNumber}";
        }
        catch (Exception)
        {
            //
            // Location detection is convenience-only. A sheet lookup
            // or transient territory-loading failure must never stop
            // the Watch Party page from drawing.
            //
            

            return string.Empty;
        }
    }

    private void DrawWatchPartyPage()
    {
        if (stream.Mode == StreamMode.Hosting ||
            stream.Mode == StreamMode.Viewing)
        {
            DrawConnectedEventLink();
            DrawWatchPartyDrawer();
        }
        else
        {
            DrawWatchPartyLandingContent();
        }
    }

    private void DrawWatchPartyHero(float heroHeight)
    {
        using var hero =
    UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(
        "##watchPartyHero",
        new Vector2(0, heroHeight),
        false,
        ImGuiWindowFlags.NoScrollbar |
        ImGuiWindowFlags.NoScrollWithMouse));

        if (!hero)
            return;

        var heroMin = ImGui.GetCursorScreenPos();

        var heroMax = new Vector2(
            heroMin.X + ImGui.GetContentRegionAvail().X,
            heroMin.Y + heroHeight);

        var drawList = ImGui.GetWindowDrawList();

        drawList.AddRectFilled(
      heroMin,
      heroMax,
      ImGui.GetColorU32(
          CardBg),
      18f);

        drawList.AddRect(
            heroMin,
            heroMax,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.55f)),
            18f,
            ImDrawFlags.RoundCornersAll,
            1.5f);


        var pad =
                Ui(18f);

        var innerH =
            MathF.Max(
                Ui(120f),
                heroHeight -
                pad * 2f);

        //
        // Center the complete text/image row vertically inside the hero.
        //
        var rowY =
            heroMin.Y +
            (heroHeight -
             innerH) *
            0.5f;

        ImGui.SetCursorScreenPos(
            new Vector2(
                heroMin.X +
                pad,
                rowY));

        var width =
            ImGui.GetContentRegionAvail().X;

        var previewWidth =
            Ui(620f);

        var textWidth =
            width -
            previewWidth -
            Ui(28f);


        using (UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(
            "##watchPartyHeroText",
            new Vector2(
                textWidth,
                innerH),
            false,
            ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse)))
        {

            using (ImRaii.PushFont(UiBuilder.DefaultFont))
            {
                using var uiFontScale19627=UiFonts.PushRelative(1.65f);

                ImGui.TextColored(
                    Vector4.One,
                    "Watch together,");

                ImGui.TextColored(
                    Accent,
                    "anywhere in Eorzea.");

                uiFontScale19627.Dispose();
            }

            ImGui.Spacing();
            ImGui.Spacing();

            ImGui.PushTextWrapPos(
                ImGui.GetCursorPosX() +
                textWidth -
                Ui(30f));

            using (ImRaii.PushFont(UiBuilder.DefaultFont))
            {
                using var uiFontScale20220=UiFonts.PushRelative(1.25f);

                ImGui.TextColored(
                    MutedText,
                    "Create a room, invite friends, and enjoy videos with synced playback, chat, and live reactions.");

                uiFontScale20220.Dispose();
            }

            ImGui.PopTextWrapPos();
        }


        ImGui.SameLine();


        using (UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(
            "##watchPartyPreview",
            new Vector2(
    previewWidth,
    innerH),
                    false,
            ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse)))
        {
            var innerWidth =
                ImGui.GetContentRegionAvail().X;

            var previewMin = ImGui.GetCursorScreenPos();

            var previewMax = new Vector2(
                previewMin.X + innerWidth,
                previewMin.Y + innerH);

            drawList.AddRectFilled(
                previewMin,
                previewMax,
                ImGui.GetColorU32(
                    CardBg),
                14f);

            drawList.AddRect(
                previewMin,
                previewMax,
                ImGui.GetColorU32(
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.35f)),
                14f,
                ImDrawFlags.RoundCornersAll,
                1f);

            var panelHeight = innerH;

            var panelMin = ImGui.GetCursorScreenPos();

            var panelMax = new Vector2(
                panelMin.X + innerWidth,
                panelMin.Y + panelHeight);

            drawList.AddRectFilled(
                panelMin,
                panelMax,
                ImGui.GetColorU32(
                    CardBg),
                14f);

            drawList.AddRect(
                panelMin,
                panelMax,
                ImGui.GetColorU32(
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.35f)),
                14f,
                ImDrawFlags.RoundCornersAll,
                1f);


            using (UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(
                "##watchPreviewPanel",
                new Vector2(
                    innerWidth,
                    panelHeight),
                false,
                ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse)))
            {
                ImGui.SetCursorPos(
    Vector2.Zero);
                var panelWidth =
                    ImGui.GetContentRegionAvail().X;

                var imageWidth =
                    panelWidth * 0.60f;

                var chatWidth =
                    panelWidth - imageWidth - 12f;


                if (watchPartyHeaderImage is not null)
                {
                    var texture =
                        watchPartyHeaderImage.GetWrapOrEmpty();

                    ImGui.Image(
                        texture.Handle,
                        new Vector2(
                            innerWidth,
                            panelHeight));
                }
                drawList.AddRect(
        panelMin,
        panelMax,
        ImGui.GetColorU32(
            new Vector4(
                Accent.X,
                Accent.Y,
                Accent.Z,
                0.45f)),
        12f,
        ImDrawFlags.RoundCornersAll,
        1.5f);
            }
        }
    }

    private void DrawWatchPartyActions(
        float cardHeight)
    {
        var width =
            ImGui.GetContentRegionAvail().X;

        var gap =
            Ui(12f);

        //
        // Stack the forms vertically at narrower window sizes.
        //
        var stacked =
            width < Ui(760f);

        var cardWidth =
            stacked
                ? width
                : (width - gap) / 2f;

        var startHeight =
           Math.Max(
               cardHeight,
               Ui(390f));

        var joinHeight =
            Math.Max(
                cardHeight,
                Ui(390f));

        DrawStartWatchPartyPanel(
            cardWidth,
            startHeight);

        if (stacked)
        {
            ImGui.Dummy(
                new Vector2(
                    0f,
                    gap));
        }
        else
        {
            ImGui.SameLine(
                0f,
                gap);
        }

        DrawJoinWatchPartyPanel(
            cardWidth,
            joinHeight);
    }

    private void DrawStartWatchPartyPanel(
        float width,
        float height)
    {
        using (ImRaii.PushColor(
                   ImGuiCol.ChildBg,
                   CardBg))
        using (ImRaii.PushColor(
                   ImGuiCol.Border,
                   new Vector4(
                       Accent.X,
                       Accent.Y,
                       Accent.Z,
                       0.48f)))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.ChildRounding,
                   Ui(16f)))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.ChildBorderSize,
                   Ui(1.5f)))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.WindowPadding,
                   UiVec(20f, 18f)))
        using (var panel =
               UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(
                   "##startParty",
                   new Vector2(
                       width,
                       height),
                   true,
                   ImGuiWindowFlags.NoScrollbar |
                   ImGuiWindowFlags.NoScrollWithMouse)))
        {
            if (!panel)
            {
                return;
            }

            DrawWatchPartyFormHeader(
                   FontAwesomeIcon.Clapperboard,
                   "Start a Watch Party",
                   "Create a room now and add content whenever you're ready.",
                   showDetailsTooltip: true);

            ImGui.Dummy(UiVec(0f, 10f));

            DrawCreateRoomFields(
                ImGui.GetContentRegionAvail().X);

            ImGui.Dummy(
                UiVec(
                    0f,
                    5f));

            using (ImRaii.PushStyle(
                       ImGuiStyleVar.FrameRounding,
                       Ui(7f)))
            using (ImRaii.PushStyle(
                       ImGuiStyleVar.FramePadding,
                       UiVec(12f, 9f)))
            using (ImRaii.PushColor(
                       ImGuiCol.Button,
                       Accent))
            using (ImRaii.PushColor(
                       ImGuiCol.ButtonHovered,
                       AccentHover))
            using (ImRaii.PushColor(
                       ImGuiCol.ButtonActive,
                       new Vector4(
                           Accent.X * 0.82f,
                           Accent.Y * 0.82f,
                           Accent.Z * 0.82f,
                           1f)))
            {
                if (ImGui.Button(
                        "Create Room",
                        new Vector2(
                            ImGui.GetContentRegionAvail().X,
                            Ui(38f))))
                {
                    CreateEmptyWatchParty();
                }
            }
        }
    }

    private void DrawJoinWatchPartyPanel(
        float width,
        float height)
    {
        using (ImRaii.PushColor(
                   ImGuiCol.ChildBg,
                   CardBg))
        using (ImRaii.PushColor(
                   ImGuiCol.Border,
                   new Vector4(
                       Accent.X,
                       Accent.Y,
                       Accent.Z,
                       0.48f)))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.ChildRounding,
                   Ui(16f)))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.ChildBorderSize,
                   Ui(1.5f)))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.WindowPadding,
                   UiVec(20f, 18f)))
        using (var panel =
               UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(
                   "##joinParty",
                   new Vector2(
                       width,
                       height),
                   true,
                   ImGuiWindowFlags.NoScrollbar |
                   ImGuiWindowFlags.NoScrollWithMouse)))
        {
            if (!panel)
            {
                return;
            }

            DrawWatchPartyFormHeader(
                FontAwesomeIcon.Users,
                "Join a Watch Party",
                "Join a friend's room or discover public Watch Parties.",
                showDetailsTooltip: false);

            ImGui.Dummy(
                 UiVec(
                     0f,
                     5f));

            using (ImRaii.PushStyle(
                        ImGuiStyleVar.FrameRounding,
                        Ui(7f)))
            using (ImRaii.PushStyle(
                       ImGuiStyleVar.FramePadding,
                       UiVec(9f, 5f)))
            using (ImRaii.PushColor(
                       ImGuiCol.FrameBg,
                       FrameBg))
            using (ImRaii.PushColor(
                       ImGuiCol.FrameBgHovered,
                       FrameBgHover))
            using (ImRaii.PushColor(
                       ImGuiCol.FrameBgActive,
                       AccentFieldActive))
            {
                var fieldGap =
                   Ui(10f);

                var availableWidth =
                    ImGui.GetContentRegionAvail().X;

                var hostWidth =
                    availableWidth *
                    0.58f;

                var passwordWidth =
                    availableWidth -
                    hostWidth -
                    fieldGap;

                var fieldsOrigin =
                    ImGui.GetCursorScreenPos();

                //
                // Host username column
                //
                ImGui.SetCursorScreenPos(
                    fieldsOrigin);

                ImGui.BeginGroup();

                ImGui.TextColored(
                    MutedText,
                    "Host username");

                ImGui.SetNextItemWidth(
                    hostWidth);

                ImGui.InputTextWithHint(
                    "##hostName",
                    "Alpha Channel username",
                    ref joinHostNameInput,
                    32);

                ImGui.EndGroup();

                var hostBottomY =
                    ImGui.GetItemRectMax().Y;

                //
                // Password column
                //
                ImGui.SetCursorScreenPos(
                    new Vector2(
                        fieldsOrigin.X +
                        hostWidth +
                        fieldGap,
                        fieldsOrigin.Y));

                ImGui.BeginGroup();

                ImGui.TextColored(
                    MutedText,
                    "Password");

                ImGui.SetNextItemWidth(
                    passwordWidth);

                ImGui.InputTextWithHint(
                    "##joinRoomPassword",
                    "Optional",
                    ref joinPasswordInput,
                    64,
                    ImGuiInputTextFlags.Password);

                var passwordHovered =
                    ImGui.IsItemHovered();

                ImGui.EndGroup();

                var passwordBottomY =
                    ImGui.GetItemRectMax().Y;

                if (passwordHovered)
                {
                    UiFonts.SetTooltip(
                        "For locked rooms only");
                }

                //
                // Continue beneath the taller of the two columns.
                //
                ImGui.SetCursorScreenPos(
                    new Vector2(
                        fieldsOrigin.X,
                        MathF.Max(
                            hostBottomY,
                            passwordBottomY)));

                ImGui.Dummy(
                    Vector2.Zero);
            }

            ImGui.Dummy(
                          UiVec(
                              0f,
                              6f));

            using (ImRaii.PushStyle(
                       ImGuiStyleVar.FrameRounding,
                       Ui(7f)))
            using (ImRaii.PushStyle(
                       ImGuiStyleVar.FramePadding,
                       UiVec(12f, 9f)))
            using (ImRaii.PushColor(
                       ImGuiCol.Button,
                       Accent))
            using (ImRaii.PushColor(
                       ImGuiCol.ButtonHovered,
                       AccentHover))
            {
                if (ImGui.Button(
                        "Join Room",
                        new Vector2(
                            ImGui.GetContentRegionAvail().X,
                            Ui(38f))))
                {
                    DoJoin(
                        joinHostNameInput,
                        joinPasswordInput);
                }
            }

            if (joinError is { } error)
            {
                ImGui.Dummy(
                    UiVec(
                        0f,
                        5f));

                ImGui.TextColored(
                    Danger,
                    error);
            }

            ImGui.Dummy(
                 UiVec(
                     0f,
                     9f));

            DrawWatchPartyOrDivider();

            ImGui.Dummy(
                UiVec(
                    0f,
                    8f));

            DrawWatchPartyBrowseButton(
              ImGui.GetContentRegionAvail().X,
              FontAwesomeIcon.Globe,
              "Browse public Watch Parties and venues",
              () =>
              {
                  currentPage =
                      HomePage.PartyDirectory;
              });
        }
    }

    private void DrawWatchPartyFormHeader(
        FontAwesomeIcon icon,
        string title,
        string subtitle,
        bool showDetailsTooltip)
    {
        var drawList =
            ImGui.GetWindowDrawList();

        var headerStart =
            ImGui.GetCursorScreenPos();

        var iconSize =
            Ui(44f);

        var iconCenter =
            headerStart +
            new Vector2(
                iconSize * 0.5f,
                iconSize * 0.5f);

        drawList.AddCircleFilled(
            iconCenter,
            iconSize * 0.5f,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.24f)),
            24);

        using (UiFonts.PushIcon())
        {
            var glyph =
                icon.ToIconString();

            var glyphSize =
                ImGui.CalcTextSize(
                    glyph);

            UiFonts.DrawText(drawList, ImGui.GetFontSize(), iconCenter -
                glyphSize * 0.5f, ImGui.GetColorU32(
                    Accent), glyph);
        }

        var textX =
            headerStart.X +
            iconSize +
            Ui(12f);

        UiFonts.DrawText(drawList, ImGui.GetFontSize(), new Vector2(
                textX,
                headerStart.Y +
                Ui(2f)), ImGui.GetColorU32(
                Vector4.One), title);

        UiFonts.DrawText(drawList, ImGui.GetFontSize(), new Vector2(
                textX,
                headerStart.Y +
                Ui(24f)), ImGui.GetColorU32(
                MutedText), subtitle);

        if (showDetailsTooltip)
        {
            var infoGlyph =
                FontAwesomeIcon.InfoCircle
                    .ToIconString();

            Vector2 infoSize;
            Vector2 infoPos;

            using (UiFonts.PushIcon())
            {
                infoSize =
                    ImGui.CalcTextSize(
                        infoGlyph);

                infoPos =
                    new Vector2(
                        ImGui.GetWindowPos().X +
                        ImGui.GetWindowSize().X -
                        Ui(20f) -
                        infoSize.X,
                        headerStart.Y +
                        Ui(4f));

                UiFonts.DrawText(drawList, ImGui.GetFontSize(), infoPos, ImGui.GetColorU32(
                        MutedText), infoGlyph);
            }

            //
            // Evaluate and draw the tooltip after restoring the normal font.
            //
            if (ImGui.IsMouseHoveringRect(
                    infoPos -
                    UiVec(4f, 4f),
                    infoPos +
                    infoSize +
                    UiVec(4f, 4f)))
            {
                UiFonts.SetTooltip(
                    "Room details can be changed later while hosting.");
            }
        }

        ImGui.Dummy(
            new Vector2(
                0f,
                iconSize));
    }

    private void DrawWatchPartyOrDivider()
    {
        var width =
            ImGui.GetContentRegionAvail().X;

        const string text =
            "OR";

        var textSize =
            ImGui.CalcTextSize(
                text);

        var gap =
            Ui(10f);

        var lineWidth =
            MathF.Max(
                0f,
                (
                    width -
                    textSize.X -
                    gap * 2f
                ) *
                0.5f);

        var origin =
            ImGui.GetCursorScreenPos();

        var lineY =
            origin.Y +
            textSize.Y *
            0.5f;

        var drawList =
            ImGui.GetWindowDrawList();

        var lineColor =
            ImGui.GetColorU32(
                new Vector4(
                    MutedText.X,
                    MutedText.Y,
                    MutedText.Z,
                    0.30f));

        drawList.AddLine(
            new Vector2(
                origin.X,
                lineY),
            new Vector2(
                origin.X +
                lineWidth,
                lineY),
            lineColor,
            Ui(1f));

        UiFonts.DrawText(drawList, ImGui.GetFontSize(), new Vector2(
                origin.X +
                lineWidth +
                gap,
                origin.Y), ImGui.GetColorU32(
                MutedText), text);

        drawList.AddLine(
            new Vector2(
                origin.X +
                lineWidth +
                gap * 2f +
                textSize.X,
                lineY),
            new Vector2(
                origin.X +
                width,
                lineY),
            lineColor,
            Ui(1f));

        ImGui.Dummy(
            new Vector2(
                width,
                textSize.Y));
    }

    private void DrawWatchPartyBrowseButton(
        float width,
        FontAwesomeIcon icon,
        string label,
        Action onClick)
    {
        var origin =
            ImGui.GetCursorScreenPos();

        var size =
            new Vector2(
                width,
                Ui(66f));

        var max =
            origin +
            size;

        var hovered =
            ImGui.IsMouseHoveringRect(
                origin,
                max);

        var drawList =
            ImGui.GetWindowDrawList();

        drawList.AddRectFilled(
            origin,
            max,
            ImGui.GetColorU32(
                hovered
                    ? CardBgHover
                    : CardBg),
            Ui(8f));

        drawList.AddRect(
            origin,
            max,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    hovered
                        ? 0.80f
                        : 0.38f)),
            Ui(8f));

        var glyph =
            icon.ToIconString();

        Vector2 glyphSize;

        using (UiFonts.PushIcon())
        {
            glyphSize =
                ImGui.CalcTextSize(
                    glyph);

            UiFonts.DrawText(drawList, ImGui.GetFontSize(), new Vector2(
                    origin.X +
                    (width -
                     glyphSize.X) *
                    0.5f,
                    origin.Y +
                    Ui(10f)), ImGui.GetColorU32(
                    Accent), glyph);
        }

        var labelSize =
            ImGui.CalcTextSize(
                label);

        UiFonts.DrawText(drawList, ImGui.GetFontSize(), new Vector2(
                origin.X +
                (width -
                 labelSize.X) *
                0.5f,
                origin.Y +
                Ui(37f)), ImGui.GetColorU32(
                Vector4.One), label);

        ImGui.SetCursorScreenPos(
            origin);

        if (ImGui.InvisibleButton(
                $"##watchPartyBrowse_{label}",
                size))
        {
            onClick();
        }

        if (hovered)
        {
            ImGui.SetMouseCursor(
                ImGuiMouseCursor.Hand);
        }
    }
    private void CreateEmptyWatchParty()
    {
        //
        // Public and venue rooms can be created immediately.
        //
        if (createRoomKindIndex != 1)
        {
            createRoomPassword =
                string.Empty;

            createLockedRoomPasswordError =
                null;

            StartWatchParty(
                goToPlayer: false);

            return;
        }

        //
        // Let the existing hosting validation report a sign-in error
        // before asking an unsigned-in user to choose a password.
        //
        if (CurrentSession is null)
        {
            StartWatchParty(
                goToPlayer: false);

            return;
        }

        //
        // Locked rooms collect their password in a separate confirmation
        // popup after the rest of the form has been completed.
        //
        createRoomPassword =
               string.Empty;

        createLockedRoomPasswordError =
            null;

        createLockedRoomPasswordForRoomEdit =
            false;

        createLockedRoomPasswordPopupRequested =
            true;
    }

    private void DrawCreateLockedRoomPasswordPopup()
    {
        if (!createLockedRoomPasswordPopupRequested)
        {
            return;
        }

        var popupWidth = Ui(470f);

        var popupHeight = Ui(285f);

        //
        // Cover only the Alpha Channel window. This follows the same
        // overlay pattern as the username and controller prompts.
        //
        var parentPos =
            ImGui.GetWindowPos();

        var parentSize =
            ImGui.GetWindowSize();

        var popupPos =
            new Vector2(
                parentPos.X +
                (parentSize.X -
                 popupWidth) *
                0.5f,

                parentPos.Y +
                (parentSize.Y -
                 popupHeight) *
                0.5f);

        ImGui.SetNextWindowPos(
            parentPos,
            ImGuiCond.Always);

        ImGui.SetNextWindowSize(
            parentSize,
            ImGuiCond.Always);

        ImGui.SetNextWindowBgAlpha(
            0f);

        const ImGuiWindowFlags overlayFlags =
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoDocking |
            ImGuiWindowFlags.NoBackground;

        if (!BeginWindowOverlay(
                "##createLockedRoomPasswordOverlay",
                overlayFlags))
        {
            ImGui.End();
            return;
        }

        var drawList =
            ImGui.GetWindowDrawList();

        //
        // Darken the Alpha Channel window behind the prompt.
        //
        drawList.AddRectFilled(
            parentPos,
            parentPos +
            parentSize,
            ImGui.GetColorU32(
                new Vector4(
                    0f,
                    0f,
                    0f,
                    0.48f)));

        var popupMax =
            popupPos +
            new Vector2(
                popupWidth,
                popupHeight);

        //
        // Popup background and border.
        //
        drawList.AddRectFilled(
            popupPos,
            popupMax,
            ImGui.GetColorU32(
                CardBg),
            10f);

        drawList.AddRect(
            popupPos,
            popupMax,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.45f)),
            10f,
            ImDrawFlags.RoundCornersAll,
            1f);

        var padding = NativeUi(20f);

        var contentWidth =
            popupWidth -
            padding *
            2f;

        //
        // Header icon.
        //
        var iconCenter =
            popupPos +
            new Vector2(
                padding +
                Ui(18f),
                Ui(31f));

        drawList.AddCircleFilled(
            iconCenter,
            18f,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.24f)),
            24);

        using (UiFonts.PushIcon())
        {
            var lockGlyph =
                FontAwesomeIcon.Lock
                    .ToIconString();

            var lockGlyphSize =
                ImGui.CalcTextSize(
                    lockGlyph);

            UiFonts.DrawText(drawList, ImGui.GetFontSize(), iconCenter -
                lockGlyphSize *
                0.5f, ImGui.GetColorU32(
                    Accent), lockGlyph);
        }

        //
        // Title.
        //
        ImGui.SetCursorScreenPos(
            popupPos +
            new Vector2(
                padding +
                Ui(48f),
                Ui(20f)));

        using var uiFontScale51619=UiFonts.PushRelative(1.15f);

        ImGui.TextColored(
            Vector4.One,
            "Set a room password");

        uiFontScale51619.Dispose();

        //
        // Explanation.
        //
        ImGui.SetCursorScreenPos(
            popupPos +
            new Vector2(
                padding,
                Ui(63f)));

        ImGui.PushTextWrapPos(
            ImGui.GetCursorPosX() +
            contentWidth);

        ImGui.TextColored(
            MutedText,
            "Users will require the password to join your room and locked rooms do not publish what you're watching to the party directory.");

        ImGui.PopTextWrapPos();

        //
        // Password field.
        //
        ImGui.SetCursorScreenPos(
            popupPos +
            new Vector2(
                padding,
                Ui(119f)));

        ImGui.TextColored(
            MutedText,
            "Room password");

        ImGui.SetCursorScreenPos(
            popupPos +
            new Vector2(
                padding,
                Ui(143f)));

        ImGui.SetNextItemWidth(
            contentWidth);

        if (ImGui.IsWindowAppearing())
        {
            ImGui.SetKeyboardFocusHere();
        }

        var submitRequested =
            false;

        using (ImRaii.PushColor(
                   ImGuiCol.FrameBg,
                   FrameBg))
        using (ImRaii.PushColor(
                   ImGuiCol.Border,
                   new Vector4(
                       Accent.X,
                       Accent.Y,
                       Accent.Z,
                       0.75f)))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameBorderSize,
                   1f))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameRounding,
                   5f))
        {
            submitRequested =
                ImGui.InputTextWithHint(
                    "##createLockedRoomPassword",
                    "Enter a password",
                    ref createRoomPassword,
                    64,
                    ImGuiInputTextFlags.Password |
                    ImGuiInputTextFlags.EnterReturnsTrue);
        }

        if (createLockedRoomPasswordError is { } passwordError)
        {
            ImGui.SetCursorScreenPos(
                popupPos +
                new Vector2(
                    padding,
                    Ui(181f)));

            ImGui.TextColored(
                Danger,
                passwordError);
        }

        //
        // Footer divider.
        //
        var dividerY =
            popupMax.Y -
            65f;

        drawList.AddLine(
            new Vector2(
                popupPos.X +
                padding,
                dividerY),
            new Vector2(
                popupMax.X -
                padding,
                dividerY),
            ImGui.GetColorU32(
                BorderSubtle),
            1f);

        var buttonGap = Ui(10f);

        var buttonWidth =
            (
                contentWidth -
                buttonGap
            ) /
            2f;

        var cancelRequested =
            false;

        ImGui.SetCursorScreenPos(
            new Vector2(
                popupPos.X +
                padding,
                popupMax.Y -
                Ui(50f)));

        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameRounding,
                   8f))
        using (ImRaii.PushColor(
                   ImGuiCol.Button,
                   FrameBg))
        using (ImRaii.PushColor(
                   ImGuiCol.ButtonHovered,
                   FrameBgHover))
        using (ImRaii.PushColor(
                   ImGuiCol.ButtonActive,
                   AccentFieldActive))
        using (ImRaii.PushColor(
                   ImGuiCol.Border,
                   new Vector4(
                       Accent.X,
                       Accent.Y,
                       Accent.Z,
                       0.55f)))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameBorderSize,
                   1f))
        {
            cancelRequested =
                ImGui.Button(
                    "Cancel",
                    new Vector2(
                        buttonWidth,
                        Ui(36f)));
        }

        ImGui.SameLine(
            0f,
            buttonGap);

        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameRounding,
                   8f))
        using (ImRaii.PushColor(
                   ImGuiCol.Button,
                   Accent))
        using (ImRaii.PushColor(
                   ImGuiCol.ButtonHovered,
                   AccentHover))
        using (ImRaii.PushColor(
                   ImGuiCol.ButtonActive,
                   AccentActive))
        {
            if (ImGui.Button(
                 createLockedRoomPasswordForRoomEdit
                     ? "Save Changes"
                     : "Create Room",
                 new Vector2(
                     buttonWidth,
                     Ui(36f))))
            {
                submitRequested =
                    true;
            }
        }

        var createConfirmed =
            false;

        if (cancelRequested)
        {
            createLockedRoomPasswordPopupRequested =
                false;

            createLockedRoomPasswordError =
                null;

            createRoomPassword =
                string.Empty;
        }
        else if (submitRequested)
        {
            createRoomPassword =
                createRoomPassword.Trim();

            if (string.IsNullOrWhiteSpace(
                    createRoomPassword))
            {
                createLockedRoomPasswordError =
                    "Please enter a password.";
            }
            else
            {
                createLockedRoomPasswordPopupRequested =
                    false;

                createLockedRoomPasswordError =
                    null;

                createConfirmed =
                    true;
            }
        }

        ImGui.End();

        //
        // Complete the requested action only after closing the
        // overlay's ImGui window.
        //
        if (createConfirmed)
        {
            if (createLockedRoomPasswordForRoomEdit)
            {
                createLockedRoomPasswordForRoomEdit =
                    false;

                SavePartyRoomDetails();
            }
            else
            {
                StartWatchParty(
                    goToPlayer: false);
            }
        }
    }

    private void StartWatchParty(bool goToPlayer)
    {
        if (CurrentSession is null)
        {
            joinError =
                "Sign in to host a watch party.";

            Plugin.ChatGui.Print(
                "[AlphaChannel] Sign in before hosting a watch party.");

            return;
        }

        if (stream.IsClosingPreviousRoomConnection)
        {
            ShowRoomClosingNotice();
            return;
        }

        if (!stream.IsConnected || stream.ConnectionState != RealtimeConnectionState.Connected)
        {
            joinError = "Connect to the Alpha Channel server before creating a watch party.";
            joinErrorExpiresAt = DateTime.UtcNow.AddSeconds(4);
            Plugin.ChatGui.Print("[AlphaChannel] Cannot create a watch party while disconnected from the server.");
            return;
        }

        if (createRoomKindIndex == 1 &&
            string.IsNullOrWhiteSpace(createRoomPassword))
        {
            joinError =
                "Locked rooms need a password.";

            return;
        }

        var creationPage = currentPage;

        ApplyCreateRoomToStream();

        gameplayStreamOfferDismissed =
            false;

        browserStreamOfferDismissed =
            false;

        var engine =
            screenController.Engine;

        var current =
            queue.Current;

        //
        // Keep every existing playback surface intact. Only show the empty
        // room screen when there is genuinely no active media to inherit.
        //
        if (current is null &&
            !engine.IsPlayingGame &&
            !engine.IsPlayingBrowser &&
            !engine.IsPlayingWhiteboard &&
            !video.IsPlayingLocalVideo)
        {
            engine.ShowWaitingScreen();
        }

        //
        // Publish the current queue item immediately. This also changes the
        // StreamClient to Hosting before an exclusive source is armed below.
        // Games, browsers and local files remain local until the host
        // confirms their broadcast settings.
        //
        var (position, _, paused) =
            current is null
                ? (0d, 0d, true)
                : video.GetProgress();

        var mediaTitle =
            current is not null &&
            !string.IsNullOrWhiteSpace(current.Title) &&
            !string.Equals(
                current.Title,
                current.Url,
                StringComparison.OrdinalIgnoreCase)
                ? current.Title
                : null;

        var publishedUrl =
            current?.Url;

        if (!string.IsNullOrWhiteSpace(publishedUrl) &&
            engine.IsAudioOnly)
        {
            publishedUrl =
                BuildPartyAudioPresentationUrl(publishedUrl);
        }

        _ = stream.PublishStateAsync(
            publishedUrl,
            position,
            paused,
            engine.IsActive
                ? engine.ScreenPosition
                : null,
            engine.IsActive
                ? engine.ScreenYaw
                : null,
            engine.IsActive
                ? engine.ScreenScale
                : null,
            engine.IsActive
                ? engine.DisableFixedScreenScaleRatio
                : null,
            engine.IsActive
                ? engine.ScreenWidthScale
                : null,
            engine.IsActive
                ? engine.ScreenHeightScale
                : null,
            mediaTitle,
            current?.ThumbnailUrl);


        if (goToPlayer)
        {
            currentPage =
                HomePage.Player;

            playerSourceTab =
                0;
        }

        CompletePendingWatchPartyMedia();
        if (creationPage != HomePage.WatchAlong && !goToPlayer)
            currentPage = creationPage;
    }


    // =========================================================
    // Gameplay Watch Party
    // =========================================================
    //
    // Publishes the public HLS viewer URL to the Watch Party
    // without changing the host's local playback queue.
    //
    // The host continues rendering the emulator locally.
    // Viewers receive the HLS stream through normal Watch Party
    // synchronization.
    //
    // IMPORTANT:
    // This must only ever receive the public HLS URL.
    // Never pass the RTMP publish URL or stream key here.
    //
    private async Task PublishGameplayWatchPartyAsync(
     string? hlsUrl)
    {
        var romPath = gameBroadcastSystem switch
        {
            GameSystem.Snes => snesSelectedRomPath,
            GameSystem.Nes => nesSelectedRomPath,
            GameSystem.GameBoyAdvance => gameBoyAdvanceSelectedRomPath,
            _ => gameBoySelectedRomPath
        };

        var gameName =
            string.IsNullOrWhiteSpace(
                romPath)
                ? string.Empty
                : LibraryGameName(romPath);

        var title =
            string.IsNullOrWhiteSpace(
                gameName)
                ? gameBroadcastSystem switch
                {
                    GameSystem.Snes => "SNES Gameplay",
                    GameSystem.Nes => "NES Gameplay",
                    GameSystem.GameBoyAdvance => "Game Boy Advance Gameplay",
                    _ => "Game Boy Gameplay"
                }
                : $"Playing: {gameName}";

        await stream.PublishStateAsync(
            hlsUrl,
            0d,
            false,
            screenController.Engine.ScreenPosition,
            screenController.Engine.ScreenYaw,
            screenController.Engine.ScreenScale,
            title,
            null);
    }

    private void DrawHostLeaveConfirmationPopup()
    {
        if (!hostLeaveConfirmationRequested)
        {
            return;
        }

        var parentPos =
            ImGui.GetWindowPos();

        var parentSize =
            ImGui.GetWindowSize();

        var popupWidth = MathF.Min(Ui(510f), parentSize.X - Ui(24f));

        var popupHeight = MathF.Min(Ui(285f), parentSize.Y - Ui(24f));

        var popupPos =
            new Vector2(
                parentPos.X +
                (parentSize.X -
                 popupWidth) *
                0.5f,

                parentPos.Y +
                (parentSize.Y -
                 popupHeight) *
                0.5f);

        ImGui.SetNextWindowPos(
            parentPos,
            ImGuiCond.Always);

        ImGui.SetNextWindowSize(
            parentSize,
            ImGuiCond.Always);

        ImGui.SetNextWindowBgAlpha(
            0f);


        const ImGuiWindowFlags overlayFlags =
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoDocking |
            ImGuiWindowFlags.NoBackground;

        if (!BeginWindowOverlay(
                "##hostLeaveWatchPartyOverlay",
                overlayFlags))
        {
            ImGui.End();
            return;
        }

        var drawList =
            ImGui.GetWindowDrawList();

        //
        // Darken the Alpha Channel window behind the prompt.
        //
        drawList.AddRectFilled(
            parentPos,
            parentPos +
            parentSize,
            ImGui.GetColorU32(
                new Vector4(
                    0f,
                    0f,
                    0f,
                    0.48f)));

        var popupMax =
            popupPos +
            new Vector2(
                popupWidth,
                popupHeight);

        //
        // Popup card.
        //
        drawList.AddRectFilled(
            popupPos,
            popupMax,
            ImGui.GetColorU32(
                CardBg),
            10f);

        drawList.AddRect(
            popupPos,
            popupMax,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.45f)),
            10f,
            ImDrawFlags.RoundCornersAll,
            1f);

        var padding = NativeUi(20f);

        var contentWidth =
            popupWidth -
            padding *
            2f;

        //
        // Warning icon.
        //
        var iconCenter =
            popupPos +
            new Vector2(
                padding +
                Ui(18f),
                Ui(31f));

        drawList.AddCircleFilled(
            iconCenter,
            18f,
            ImGui.GetColorU32(
                new Vector4(
                    Danger.X,
                    Danger.Y,
                    Danger.Z,
                    0.22f)),
            24);

        using (UiFonts.PushIcon())
        {
            var warningGlyph =
                FontAwesomeIcon.ExclamationTriangle
                    .ToIconString();

            var warningGlyphSize =
                ImGui.CalcTextSize(
                    warningGlyph);

            UiFonts.DrawText(drawList, ImGui.GetFontSize(), iconCenter -
                warningGlyphSize *
                0.5f, ImGui.GetColorU32(
                    Danger), warningGlyph);
        }

        //
        // Title.
        //
        ImGui.SetCursorScreenPos(
            popupPos +
            new Vector2(
                padding +
                Ui(48f),
                Ui(20f)));

        using var uiFontScale68502=UiFonts.PushRelative(1.15f);

        ImGui.TextColored(
            Hex(0xE2E7EF),
            "Close this Watch Party?");

        uiFontScale68502.Dispose();

        //
        // Explanation.
        //
        ImGui.SetCursorScreenPos(
            popupPos +
            new Vector2(
                padding,
                Ui(67f)));

        ImGui.PushTextWrapPos(
            ImGui.GetCursorPosX() +
            contentWidth);

        using (UiFonts.PushRelative(.90f))
        ImGui.TextColored(
            Hex(0xE2E7EF),
            "As the host, leaving will close the room and kick all viewers from your current Watch Party.");

        ImGui.PopTextWrapPos();

        ImGui.SetCursorScreenPos(
            popupPos +
            new Vector2(
                padding,
                Ui(123f)));

        ImGui.PushTextWrapPos(
            ImGui.GetCursorPosX() +
            contentWidth);

        using (UiFonts.PushRelative(.90f))
        ImGui.TextColored(
            MutedText,
            "Alternatively, you can transfer host to another viewer before leaving if you'd like the room to remain open.");

        ImGui.PopTextWrapPos();

        //
        // Offer a personal copy before closing this temporary queue.
        ImGui.SetCursorScreenPos(new Vector2(popupPos.X + padding, popupMax.Y - Ui(105f)));
        if (ImGui.Button("Save party queue before closing…", new Vector2(contentWidth, Ui(30f))))
        {
            partyQueueEndAfterSave = true;
            ImGui.OpenPopup("##partyQueueSave");
        }
        DrawPartyQueueSavePopup();

        // Footer divider.
        //
        var dividerY =
            popupMax.Y -
            65f;

        drawList.AddLine(
            new Vector2(
                popupPos.X +
                padding,
                dividerY),
            new Vector2(
                popupMax.X -
                padding,
                dividerY),
            ImGui.GetColorU32(
                BorderSubtle),
            1f);

        var buttonGap = Ui(10f);

        var buttonWidth =
            (
                contentWidth -
                buttonGap
            ) /
            2f;

        var cancelRequested =
            false;

        var closeRequested =
            false;

        //
        // Cancel.
        //
        ImGui.SetCursorScreenPos(
            new Vector2(
                popupPos.X +
                padding,
                popupMax.Y -
                Ui(50f)));

        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameRounding,
                   8f))
        using (ImRaii.PushColor(
                   ImGuiCol.Button,
                   FrameBg))
        using (ImRaii.PushColor(
                   ImGuiCol.ButtonHovered,
                   FrameBgHover))
        using (ImRaii.PushColor(
                   ImGuiCol.ButtonActive,
                   AccentFieldActive))
        using (ImRaii.PushColor(
                   ImGuiCol.Border,
                   new Vector4(
                       Accent.X,
                       Accent.Y,
                       Accent.Z,
                       0.55f)))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameBorderSize,
                   1f))
        {
            cancelRequested =
                ImGui.Button(
                    "Cancel",
                    new Vector2(
                        buttonWidth,
                        Ui(36f)));
        }

        ImGui.SameLine(
            0f,
            buttonGap);

        //
        // Destructive confirmation.
        //
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameRounding,
                   8f))
        using (ImRaii.PushColor(
                   ImGuiCol.Button,
                   new Vector4(
                       Danger.X,
                       Danger.Y,
                       Danger.Z,
                       0.72f)))
        using (ImRaii.PushColor(
                   ImGuiCol.ButtonHovered,
                   Danger))
        using (ImRaii.PushColor(
                   ImGuiCol.ButtonActive,
                   new Vector4(
                       Danger.X * 0.82f,
                       Danger.Y * 0.82f,
                       Danger.Z * 0.82f,
                       1f)))
        {
            closeRequested =
                ImGui.Button(
                    "Close room",
                    new Vector2(
                        buttonWidth,
                        Ui(36f)));
        }

        if (cancelRequested ||
            closeRequested)
        {
            hostLeaveConfirmationRequested =
                false;
        }

        ImGui.End();

        //
        // Perform the network and playback changes after closing the
        // overlay window.
        //
        if (closeRequested)
        {
            LeaveStream();
        }
    }

    private void DrawViewerTvSpawnPrompt()
    {
        if (!viewerTvSpawnPromptRequested)
        {
            return;
        }

        var popupWidth = Ui(470f);

        var popupHeight = Ui(245f);

        //
        // Cover only the Alpha Channel window, matching the username,
        // controller configuration, and room-password prompts.
        //
        var parentPos =
            ImGui.GetWindowPos();

        var parentSize =
            ImGui.GetWindowSize();

        var popupPos =
            new Vector2(
                parentPos.X +
                (parentSize.X -
                 popupWidth) *
                0.5f,

                parentPos.Y +
                (parentSize.Y -
                 popupHeight) *
                0.5f);

        ImGui.SetNextWindowPos(
            parentPos,
            ImGuiCond.Always);

        ImGui.SetNextWindowSize(
            parentSize,
            ImGuiCond.Always);

        ImGui.SetNextWindowBgAlpha(
            0f);

        const ImGuiWindowFlags overlayFlags =
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoDocking |
            ImGuiWindowFlags.NoBackground;

        if (!BeginWindowOverlay(
                "##viewerTvSpawnOverlay",
                overlayFlags))
        {
            ImGui.End();
            return;
        }

        var drawList =
            ImGui.GetWindowDrawList();

        //
        // Darkened window background.
        //
        drawList.AddRectFilled(
            parentPos,
            parentPos +
            parentSize,
            ImGui.GetColorU32(
                new Vector4(
                    0f,
                    0f,
                    0f,
                    0.48f)));

        var popupMax =
            popupPos +
            new Vector2(
                popupWidth,
                popupHeight);

        //
        // Popup card and accent border.
        //
        drawList.AddRectFilled(
            popupPos,
            popupMax,
            ImGui.GetColorU32(
                CardBg),
            10f);

        drawList.AddRect(
            popupPos,
            popupMax,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.45f)),
            10f,
            ImDrawFlags.RoundCornersAll,
            1f);

        var padding = NativeUi(20f);

        var contentWidth =
            popupWidth -
            padding *
            2f;

        //
        // Header icon.
        //
        var iconCenter =
            popupPos +
            new Vector2(
                padding +
                Ui(18f),
                Ui(31f));

        drawList.AddCircleFilled(
            iconCenter,
            18f,
            ImGui.GetColorU32(
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.24f)),
            24);

        using (UiFonts.PushIcon())
        {
            var tvGlyph =
                FontAwesomeIcon.Tv
                    .ToIconString();

            var tvGlyphSize =
                ImGui.CalcTextSize(
                    tvGlyph);

            UiFonts.DrawText(drawList, ImGui.GetFontSize(), iconCenter -
                tvGlyphSize *
                0.5f, ImGui.GetColorU32(
                    Accent), tvGlyph);
        }

        //
        // Title.
        //
        ImGui.SetCursorScreenPos(
            popupPos +
            new Vector2(
                padding +
                Ui(48f),
                Ui(20f)));

        using var uiFontScale77262=UiFonts.PushRelative(1.15f);

        ImGui.TextColored(
            Vector4.One,
            viewerTvSpawnPromptForQuiz
                ? "Quiz has started"
                : viewerTvSpawnPromptForRoulette
                    ? "Roulette has started"
                : viewerTvSpawnPromptForPictionary
                    ? "Pictionary has started"
                : "Watch Party content available");

        uiFontScale77262.Dispose();

        //
        // Main question.
        //
        ImGui.SetCursorScreenPos(
            popupPos +
            new Vector2(
                padding,
                Ui(67f)));

        ImGui.PushTextWrapPos(
            ImGui.GetCursorPosX() +
            contentWidth);

        ImGui.TextColored(
            Vector4.One,
            viewerTvSpawnPromptForQuiz
                ? "The Watch Party host has started a quiz. Spawn the TV to take part?"
                : viewerTvSpawnPromptForRoulette
                    ? "The Watch Party host has started roulette. Spawn the TV to place your bets?"
                : viewerTvSpawnPromptForPictionary
                    ? "The Watch Party host has started Pictionary. Spawn the TV to draw and guess?"
                : "Watch party host is currently sharing content. Spawn virtual screen?");

        ImGui.PopTextWrapPos();

        //
        // Supporting note.
        //
        ImGui.SetCursorScreenPos(
            popupPos +
            new Vector2(
                padding,
                Ui(112f)));

        ImGui.PushTextWrapPos(
            ImGui.GetCursorPosX() +
            contentWidth);

        ImGui.TextColored(
            MutedText,
            viewerTvSpawnPromptForQuiz
                ? "Questions and answers appear directly on the TV."
                : viewerTvSpawnPromptForRoulette
                    ? "Choose a chip and place bets directly on the shared roulette table."
                : viewerTvSpawnPromptForPictionary
                    ? "The game starts in 15 seconds. Word choices, drawing, and guesses appear on the TV."
                : "You can also spawn/despawn the TV via the Watch Party 'Now Playing' tab.");

        ImGui.PopTextWrapPos();

        //
        // Footer divider.
        //
        var dividerY =
            popupMax.Y -
            65f;

        drawList.AddLine(
            new Vector2(
                popupPos.X +
                padding,
                dividerY),
            new Vector2(
                popupMax.X -
                padding,
                dividerY),
            ImGui.GetColorU32(
                BorderSubtle),
            1f);

        var buttonGap = Ui(10f);

        var buttonWidth =
            (
                contentWidth -
                buttonGap
            ) /
            2f;

        var noRequested =
            false;

        var yesRequested =
            false;

        //
        // Secondary "No" button.
        //
        ImGui.SetCursorScreenPos(
            new Vector2(
                popupPos.X +
                padding,
                popupMax.Y -
                Ui(50f)));

        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameRounding,
                   8f))
        using (ImRaii.PushColor(
                   ImGuiCol.Button,
                   FrameBg))
        using (ImRaii.PushColor(
                   ImGuiCol.ButtonHovered,
                   FrameBgHover))
        using (ImRaii.PushColor(
                   ImGuiCol.ButtonActive,
                   AccentFieldActive))
        using (ImRaii.PushColor(
                   ImGuiCol.Border,
                   new Vector4(
                       Accent.X,
                       Accent.Y,
                       Accent.Z,
                       0.55f)))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameBorderSize,
                   1f))
        {
            noRequested =
                ImGui.Button(
                    "No",
                    new Vector2(
                        buttonWidth,
                        Ui(36f)));
        }

        ImGui.SameLine(
            0f,
            buttonGap);

        //
        // Primary "Yes" button.
        //
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameRounding,
                   8f))
        using (ImRaii.PushColor(
                   ImGuiCol.Button,
                   Accent))
        using (ImRaii.PushColor(
                   ImGuiCol.ButtonHovered,
                   AccentHover))
        using (ImRaii.PushColor(
                   ImGuiCol.ButtonActive,
                   AccentActive))
        {
            yesRequested =
                ImGui.Button(
                    "Yes",
                    new Vector2(
                        buttonWidth,
                        Ui(36f)));
        }

        if (noRequested)
        {
            ViewerTvEnabled =
                false;

            viewerTvSpawnPromptRequested =
                false;

            viewerTvSpawnPromptForQuiz =
                false;
            viewerTvSpawnPromptForPictionary = false;
            viewerTvSpawnPromptForRoulette = false;
        }

        if (yesRequested)
        {
            viewerTvSpawnPromptRequested =
                false;

            viewerTvSpawnPromptForQuiz =
                false;
            viewerTvSpawnPromptForPictionary = false;
            viewerTvSpawnPromptForRoulette = false;
        }

        ImGui.End();

        //
        // Spawn only after closing the overlay window, matching the
        // deferred-action pattern used by the password prompt.
        //
        if (yesRequested)
        {
            RequestViewerTvSpawn();
        }
    }

    private void DrawWatchPartyFeatures(
      float rowHeight)
    {
        var width =
            ImGui.GetContentRegionAvail().X;

        var gap =
            Ui(8f);

        var cardWidth =
            (width - gap * 2f) /
            3f;

        DrawWatchPartyFeatureCard(
            FontAwesomeIcon.CommentDots.ToIconString(),
            "Live Chat",
            cardWidth,
            rowHeight,
            new Vector4(
                0.35f,
                0.75f,
                1.00f,
                1f));

        ImGui.SameLine(
            0f,
            gap);

        DrawWatchPartyFeatureCard(
            FontAwesomeIcon.Heart.ToIconString(),
            "Reactions",
            cardWidth,
            rowHeight,
            new Vector4(
                1.00f,
                0.55f,
                0.75f,
                1f));

        ImGui.SameLine(
            0f,
            gap);

        DrawWatchPartyFeatureCard(
            FontAwesomeIcon.Sync.ToIconString(),
            "Sync Playback",
            cardWidth,
            rowHeight,
            new Vector4(
                0.45f,
                0.90f,
                0.60f,
                1f));
    }

    private void DrawWatchPartyFeatureCard(
        string icon,
        string title,
        float width,
        float height,
        Vector4 featureColor)
    {
        var origin =
            ImGui.GetCursorScreenPos();

        var size =
            new Vector2(
                width,
                height);

        var drawList =
            ImGui.GetWindowDrawList();

        drawList.AddRectFilled(
            origin,
            origin +
            size,
            ImGui.GetColorU32(
                new Vector4(
                    featureColor.X,
                    featureColor.Y,
                    featureColor.Z,
                    0.07f)),
            Ui(9f));

        drawList.AddRect(
            origin,
            origin +
            size,
            ImGui.GetColorU32(
                new Vector4(
                    featureColor.X,
                    featureColor.Y,
                    featureColor.Z,
                    0.24f)),
            Ui(9f));

        var iconGlyphSize =
            Vector2.Zero;

        using (UiFonts.PushIcon())
        {
            iconGlyphSize =
                ImGui.CalcTextSize(
                    icon);
        }

        var titleSize =
            ImGui.CalcTextSize(
                title);

        var contentWidth =
            iconGlyphSize.X +
            Ui(9f) +
            titleSize.X;

        var contentX =
            origin.X +
            (width -
             contentWidth) *
            0.5f;

        var contentY =
            origin.Y +
            (height -
             MathF.Max(
                 iconGlyphSize.Y,
                 titleSize.Y)) *
            0.5f;

        using (UiFonts.PushIcon())
        {
            UiFonts.DrawText(drawList, ImGui.GetFontSize(), new Vector2(
                    contentX,
                    contentY), ImGui.GetColorU32(
                    featureColor), icon);
        }

        UiFonts.DrawText(drawList, ImGui.GetFontSize(), new Vector2(
                contentX +
                iconGlyphSize.X +
                Ui(9f),
                contentY +
                (
                    iconGlyphSize.Y -
                    titleSize.Y
                ) *
                0.5f), ImGui.GetColorU32(
                featureColor), title);

        ImGui.Dummy(
            size);
    }

    private void ApplyCreateRoomToStream()
    {
        stream.IsPrivate = createRoomPrivate;
        var category =
            WatchPartyCategoryOptions[
                Math.Clamp(
                    createRoomCategoryIndex,
                    0,
                    WatchPartyCategoryOptions.Length - 1)];

        var rating =
            createRoomAdultOnly
                ? 1
                : 0;

        var currentWorld =
            string.IsNullOrWhiteSpace(
                CurrentWorldName)
                ? string.Empty
                : CurrentWorldName.Trim();

        var metadata =
            PartyVibes.Marker(category, rating, currentWorld, createRoomPartyVibes);

        var description =
            string.IsNullOrWhiteSpace(
                createRoomDescription)
                ? string.Empty
                : createRoomDescription.Trim();

        stream.RoomDescription =
            string.IsNullOrEmpty(
                description)
                ? metadata
                : $"{metadata} {description}";

        stream.RoomLocation =
            string.IsNullOrWhiteSpace(
                createRoomLocation)
                ? string.Empty
                : createRoomLocation.Trim();

        stream.RoomKind =
            createRoomKindIndex switch
            {
                1 =>
                    RoomKind.Locked,

                2 =>
                    RoomKind.Venue,

                _ =>
                    RoomKind.Public,
            };

        stream.RoomPassword =
            stream.RoomKind ==
            RoomKind.Locked
                ? createRoomPassword.Trim()
                : string.Empty;
    }

    private void DrawCreateRoomFields(
     float width, bool showPrivacyOptions = true)
    {
        // Compact spacing is local to this form so its fields and both vibe
        // rows fit above the footer in Standard and Native sizing.
        using var fieldSpacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, UiVec(8f, 5f));
        const int descriptionLimit =
            60;

        const int locationLimit =
            50;

        //
        // Preserve the caller-provided form margin. ImGui resets new
        // lines to the child window's default cursor start, so each
        // major field section restores this position explicitly.
        //
        var formLeftX =
            ImGui.GetCursorPosX();

        if (createRoomDescription.Length >
            descriptionLimit)
        {
            createRoomDescription =
                createRoomDescription[
                    ..descriptionLimit];
        }

        if (createRoomLocation.Length >
            locationLimit)
        {
            createRoomLocation =
                createRoomLocation[
                    ..locationLimit];
        }

        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameRounding,
                   Ui(7f)))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FramePadding,
                   UiVec(9f, 6f)))
        using (ImRaii.PushColor(
                   ImGuiCol.FrameBg,
                   FrameBg))
        using (ImRaii.PushColor(
                   ImGuiCol.FrameBgHovered,
                   FrameBgHover))
        using (ImRaii.PushColor(
                   ImGuiCol.FrameBgActive,
                   AccentFieldActive))
        using (ImRaii.PushColor(
ImGuiCol.Border,
new Vector4(
Accent.X,
Accent.Y,
Accent.Z,
0.32f)))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameBorderSize,
                   Ui(1f)))
        {
            ImGui.SetCursorPosX(
                formLeftX);

            DrawWatchPartyFieldLabel(
                "Description",
                $"{createRoomDescription.Length}/{descriptionLimit}",
                width);

            ImGui.SetCursorPosX(
                formLeftX);

            ImGui.SetNextItemWidth(
                width);

            ImGui.InputTextWithHint(
                "##createRoomDescription",
                "What are you watching?",
                ref createRoomDescription,
                descriptionLimit);

            ImGui.Dummy(
                           UiVec(
                               0f,
                               1f));

            ImGui.SetCursorPosX(
      formLeftX);

            DrawWatchPartyFieldLabel(
                "Location",
                $"{createRoomLocation.Length}/{locationLimit}",
                width);

            ImGui.SetCursorPosX(
                formLeftX);

            //
            // Keep the Location field editable, with a compact button
            // that fetches the player's location only when clicked.
            //
            var locationButtonGap =
                Ui(6f);

            var locationButtonSize =
                ImGui.GetFrameHeight();

            var locationInputWidth =
                Math.Max(
                    Ui(80f),
                    width -
                    locationButtonSize -
                    locationButtonGap);

            ImGui.SetNextItemWidth(
                locationInputWidth);

            ImGui.InputTextWithHint(
                "##createRoomLocation",
                "House, venue or meeting place",
                ref createRoomLocation,
                locationLimit);

            ImGui.SameLine(
                0f,
                locationButtonGap);

            var locationButtonClicked =
                false;

            using (ImRaii.PushStyle(
                       ImGuiStyleVar.FrameRounding,
                       Ui(7f)))
            using (ImRaii.PushStyle(
                       ImGuiStyleVar.FramePadding,
                       Vector2.Zero))
            using (ImRaii.PushColor(
                       ImGuiCol.Button,
                       new Vector4(
                           Accent.X,
                           Accent.Y,
                           Accent.Z,
                           0.18f)))
            using (ImRaii.PushColor(
                       ImGuiCol.ButtonHovered,
                       new Vector4(
                           Accent.X,
                           Accent.Y,
                           Accent.Z,
                           0.34f)))
            using (ImRaii.PushColor(
                       ImGuiCol.ButtonActive,
                       new Vector4(
                           Accent.X,
                           Accent.Y,
                           Accent.Z,
                           0.48f)))
            using (ImRaii.PushColor(
                       ImGuiCol.Border,
                       new Vector4(
                           Accent.X,
                           Accent.Y,
                           Accent.Z,
                           0.72f)))
            using (ImRaii.PushStyle(
                       ImGuiStyleVar.FrameBorderSize,
                       Ui(1f)))
            using (UiFonts.PushIcon())
            {
                locationButtonClicked =
                    ImGui.Button(
                        FontAwesomeIcon
                            .MapMarkerAlt
                            .ToIconString() +
                        "##useCurrentWatchPartyLocation",
                        new Vector2(
                            locationButtonSize,
                            locationButtonSize));
            }

            if (ImGui.IsItemHovered())
            {
                UiFonts.SetTooltip(
                    "Use your current FFXIV location");
            }

            if (locationButtonClicked)
            {
                var detectedLocation =
                    GetCurrentWatchPartyLocation();

                if (!string.IsNullOrWhiteSpace(
                        detectedLocation))
                {
                    createRoomLocation =
                        detectedLocation.Length <=
                        locationLimit
                            ? detectedLocation
                            : detectedLocation[
                                ..locationLimit];
                }
                else
                {
                    Plugin.ChatGui.Print(
                        "[AlphaChannel] Your current location could not be detected.");
                }
            }

            ImGui.Dummy(
                UiVec(
                    0f,
                    1f));

            ImGui.SetCursorPosX(
                formLeftX);

            var fieldGap =
                Ui(10f);

            var halfWidth =
                (width - fieldGap) /
                2f;

            var fieldsOrigin =
                ImGui.GetCursorScreenPos();

            //
            // Category column
            //
            ImGui.SetCursorScreenPos(
                fieldsOrigin);

            ImGui.BeginGroup();

            ImGui.TextColored(
                MutedText,
                "Category");

            ImGui.SetNextItemWidth(
                halfWidth);

            DrawWatchPartyCategoryCombo("##createRoomCategory", ref createRoomCategoryIndex);

            ImGui.EndGroup();

            var categoryBottomY =
                ImGui.GetItemRectMax().Y;

            //
            // Room type column
            //
            ImGui.SetCursorScreenPos(
                new Vector2(
                    fieldsOrigin.X +
                    halfWidth +
                    fieldGap,
                    fieldsOrigin.Y));

            ImGui.BeginGroup();

            ImGui.TextColored(
                MutedText,
                "Type");

            ImGui.SetNextItemWidth(
                halfWidth);

            ImGui.Combo(
                "##createRoomKind",
                ref createRoomKindIndex,
                ["Public", "Locked", "Venue"],
                3);

            ImGui.EndGroup();

            var visibilityBottomY =
                ImGui.GetItemRectMax().Y;

            //
            // Continue beneath the taller of the two columns.
            //
            ImGui.SetCursorScreenPos(
                new Vector2(
                    fieldsOrigin.X,
                    MathF.Max(
                        categoryBottomY,
                        visibilityBottomY)));

            ImGui.Dummy(
                Vector2.Zero);
            DrawPartyVibeChoices(width);
            if (showPrivacyOptions)
            {
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + Ui(8f));
                ImGui.SetCursorPosX(formLeftX);
                DrawWatchPartyPrivacyOptions(width);
            }
        }
    }

    private void DrawWatchPartyFieldLabel(
       string label,
       string counter,
       float width)
    {
        var rowStartX =
            ImGui.GetCursorPosX();

        ImGui.TextColored(
            MutedText,
            label);

        using var counterFont = UiFonts.PushRelative(.90f);
        var counterSize =
            ImGui.CalcTextSize(
                counter);

        ImGui.SameLine();

        ImGui.SetCursorPosX(
            rowStartX +
            width -
            counterSize.X);

        ImGui.TextColored(
            new Vector4(
                MutedText.X,
                MutedText.Y,
                MutedText.Z,
                0.72f),
            counter);

        //
        // The right-aligned counter changes the active cursor column.
        // Restore the form's original left edge before the following
        // input control is drawn.
        //
        ImGui.SetCursorPosX(
            rowStartX);
    }
    private void DrawWatchPartyAdultToggle()
    {
        var switchSize =
            UiVec(
                38f,
                20f);

        var switchOrigin =
            ImGui.GetCursorScreenPos();

        if (ImGui.InvisibleButton(
                "##createRoomAdultOnly",
                switchSize))
        {
            createRoomAdultOnly =
                !createRoomAdultOnly;
        }

        var hovered =
            ImGui.IsItemHovered();

        var drawList =
            ImGui.GetWindowDrawList();

        drawList.AddRectFilled(
            switchOrigin,
            switchOrigin +
            switchSize,
            ImGui.GetColorU32(
                createRoomAdultOnly
                    ? Accent
                    : hovered
                        ? Vector4.Lerp(CardBg, MutedText, 0.45f)
                        : Vector4.Lerp(CardBg, MutedText, 0.3f)),
            switchSize.Y *
            0.5f);

        drawList.AddRect(
            switchOrigin,
            switchOrigin + switchSize,
            ImGui.GetColorU32(hovered || createRoomAdultOnly ? AccentHover : MutedText),
            switchSize.Y * 0.5f,
            ImDrawFlags.None,
            Ui(1f));

        var knobRadius =
            Ui(7f);

        var knobCenter =
            new Vector2(
                createRoomAdultOnly
                    ? switchOrigin.X +
                      switchSize.X -
                      switchSize.Y *
                      0.5f
                    : switchOrigin.X +
                      switchSize.Y *
                      0.5f,
                switchOrigin.Y +
                switchSize.Y *
                0.5f);

        drawList.AddCircleFilled(
            knobCenter,
            knobRadius,
            ImGui.GetColorU32(
                Vector4.One),
            18);

        ImGui.SameLine(
            0f,
            Ui(7f));

        ImGui.TextUnformatted(
            "18+");

        if (hovered)
        {
            ImGui.SetMouseCursor(
                ImGuiMouseCursor.Hand);

            UiFonts.SetTooltip(
                "Mark room as an 18+ adult only room");
        }
    }
    private void DrawWatchPartyDrawer()
    {
        DrawPartyPanel();
    }
}
