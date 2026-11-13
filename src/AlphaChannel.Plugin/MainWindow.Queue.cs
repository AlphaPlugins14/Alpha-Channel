using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{








    private static void DrawQueueProfileIcon(
        string? iconKey,
        Vector2 origin,
        Vector2 size,
        Vector4 color)
    {
        var icon =
      iconKey switch
      {
          "Film" => FontAwesomeIcon.Film,
          "Music" => FontAwesomeIcon.Music,
          "Gamepad" => FontAwesomeIcon.Gamepad,
          "List" => FontAwesomeIcon.List,
          "Clapperboard" => FontAwesomeIcon.Clapperboard,

          "BookOpen" => FontAwesomeIcon.BookOpen,
          "Globe" => FontAwesomeIcon.Globe,
          "Bolt" => FontAwesomeIcon.Bolt,
          "Lightbulb" => FontAwesomeIcon.Lightbulb,
          "BroadcastTower" => FontAwesomeIcon.BroadcastTower,
          "Fire" => FontAwesomeIcon.Fire,

          "Heart" => FontAwesomeIcon.Heart,
          "Smile" => FontAwesomeIcon.Smile,
          "Images" => FontAwesomeIcon.Images,
          "Headphones" => FontAwesomeIcon.Headphones,
          "Video" => FontAwesomeIcon.Video,
          "Crown" => FontAwesomeIcon.Crown,

          _ => FontAwesomeIcon.Tv
      };

        using (UiFonts.PushIcon())
        {
            var glyph =
                icon.ToIconString();

            var glyphSize =
                ImGui.CalcTextSize(
                    glyph);

            UiFonts.DrawText(ImGui.GetWindowDrawList(), ImGui.GetFontSize(), origin +
                    new Vector2(
                        (
                            size.X -
                            glyphSize.X
                        ) *
                        0.5f,
                        (
                            size.Y -
                            glyphSize.Y
                        ) *
                        0.5f), ImGui.GetColorU32(
                        color), glyph);
        }
    }

    private void OpenQueueEditor(
        int slotIndex,
        SavedQueueProfile? profile)
    {
        creatingQueueIndex =
            slotIndex;

        editingQueueProfile =
            profile is not null;

        newQueueName =
            profile?.Name ??
            string.Empty;

        newQueueIcon =
            NormalizeQueueIconKey(
                profile?.Icon);

        queueEditorError =
            null;

        createQueuePopupOpen =
            true;
    }

    private static string NormalizeQueueIconKey(
     string? iconKey) =>
     iconKey switch
     {
         "Film" => "Film",
         "Music" => "Music",
         "Gamepad" => "Gamepad",
         "List" => "List",
         "Clapperboard" => "Clapperboard",

         "BookOpen" => "BookOpen",
         "Globe" => "Globe",
         "Bolt" => "Bolt",
         "Lightbulb" => "Lightbulb",
         "BroadcastTower" => "BroadcastTower",
         "Fire" => "Fire",

         "Heart" => "Heart",
         "Smile" => "Smile",
         "Images" => "Images",
         "Headphones" => "Headphones",
         "Video" => "Video",
         "Crown" => "Crown",

         _ => "Tv"
     };



    private void DrawCreateQueuePopup()
    {
        if (!createQueuePopupOpen)
        {
            return;
        }

        var popupWidth = Ui(470f);
        var popupHeight = Ui(390f);
        var padding = NativeUi(20f);

        var parentPos =
            ImGui.GetWindowPos();

        var parentSize =
            ImGui.GetWindowSize();

        var popupPos =
            parentPos +
            new Vector2(
                (
                    parentSize.X -
                    popupWidth
                ) *
                0.5f,
                (
                    parentSize.Y -
                    popupHeight
                ) *
                0.5f);

        var popupMax =
            popupPos +
            new Vector2(
                popupWidth,
                popupHeight);

        ImGui.SetNextWindowPos(
            parentPos,
            ImGuiCond.Always);

        ImGui.SetNextWindowSize(
            parentSize,
            ImGuiCond.Always);

        ImGui.SetNextWindowBgAlpha(0f);

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
                "##queueEditorOverlay",
                overlayFlags))
        {
            ImGui.End();
            return;
        }

        var drawList =
            ImGui.GetWindowDrawList();

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

        var contentWidth =
            popupWidth -
            padding *
            2f;

        var headerIconOrigin =
            popupPos +
            new Vector2(
                padding,
                Ui(13f));

        DrawQueueProfileIcon(
            newQueueIcon,
            headerIconOrigin,
            UiVec(38f, 38f),
            Accent);

        ImGui.SetCursorScreenPos(
            popupPos +
            new Vector2(
                padding +
                Ui(48f),
                Ui(20f)));

        using var uiFontScale37113=UiFonts.PushRelative(1.15f);

        ImGui.TextColored(
            Hex(0xE2E7EF),
            editingQueueProfile
                ? "Edit queue"
                : "Create a new queue");

        uiFontScale37113.Dispose();

        ImGui.SetCursorScreenPos(
            popupPos +
            new Vector2(
                padding,
                Ui(67f)));

        DrawSocialHelper("Queue name");

        ImGui.SetCursorScreenPos(
            popupPos +
            new Vector2(
                padding,
                Ui(89f)));

        ImGui.SetNextItemWidth(
            contentWidth);

        using (ImRaii.PushColor(
                   ImGuiCol.FrameBg,
                   FrameBg)
                   .Push(
                       ImGuiCol.Border,
                       new Vector4(
                           Accent.X,
                           Accent.Y,
                           Accent.Z,
                           0.65f)))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameBorderSize,
                   1f)
                   .Push(
                       ImGuiStyleVar.FrameRounding,
                       5f))
        {
            ImGui.InputTextWithHint(
                "##queueName",
                "For example: Movie Night",
                ref newQueueName,
                64);
        }

        ImGui.SetCursorScreenPos(
            popupPos +
            new Vector2(
                padding,
                Ui(135f)));

        DrawSocialHelper("Queue icon");

        var iconChoices =
     new (string Key, FontAwesomeIcon Icon)[]
     {
                // General media
                ("Tv", FontAwesomeIcon.Tv),
                ("Film", FontAwesomeIcon.Film),
                ("Music", FontAwesomeIcon.Music),
                ("Gamepad", FontAwesomeIcon.Gamepad),
                ("List", FontAwesomeIcon.List),
                ("Clapperboard", FontAwesomeIcon.Clapperboard),

                // Fantasy, science fiction, technology, and action
                ("BookOpen", FontAwesomeIcon.BookOpen),
                ("Globe", FontAwesomeIcon.Globe),
                ("Bolt", FontAwesomeIcon.Bolt),
                ("Lightbulb", FontAwesomeIcon.Lightbulb),
                ("BroadcastTower", FontAwesomeIcon.BroadcastTower),
                ("Fire", FontAwesomeIcon.Fire),

                // Romance, comedy, art, audio, video, and royalty
                ("Heart", FontAwesomeIcon.Heart),
                ("Smile", FontAwesomeIcon.Smile),
                ("Images", FontAwesomeIcon.Images),
                ("Headphones", FontAwesomeIcon.Headphones),
                ("Video", FontAwesomeIcon.Video),
                ("Crown", FontAwesomeIcon.Crown)
     };

        ImGui.SetCursorScreenPos(
            popupPos +
            new Vector2(
                padding,
                Ui(159f)));

        for (var i = 0;
      i < iconChoices.Length;
      i++)
        {
            if (i > 0)
            {
                if (i % 6 == 0)
                {
                    ImGui.SetCursorScreenPos(
                        popupPos +
                        new Vector2(
                            padding,
                            Ui(159f) +
                            (
                                i /
                                6
                            ) *
                            Ui(50f)));
                }
                else
                {
                    ImGui.SameLine(
                        0f,
                        8f);
                }
            }

            var choice =
                iconChoices[i];

            var selected =
                newQueueIcon ==
                choice.Key;

            using (UiFonts.PushIcon())
            using (ImRaii.PushColor(
                       ImGuiCol.Button,
                       selected
                           ? Accent
                           : FrameBg)
                       .Push(
                           ImGuiCol.ButtonHovered,
                           selected
                               ? AccentHover
                               : FrameBgHover)
                       .Push(
                           ImGuiCol.ButtonActive,
                           selected
                               ? AccentActive
                               : AccentFieldActive))
            {
                if (ImGui.Button(
                        $"{choice.Icon.ToIconString()}##queueIcon_{choice.Key}",
                        UiVec(52f, 42f)))
                {
                    newQueueIcon =
                        choice.Key;
                }
            }
        }

        if (queueEditorError is { } error)
        {
            ImGui.SetCursorScreenPos(
                popupPos +
                new Vector2(
                    padding,
                    Ui(314f)));

            ImGui.TextColored(
                Danger,
                error);
        }

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
                BorderSubtle));

        var buttonGap = Ui(10f);

        var buttonWidth =
            (
                contentWidth -
                buttonGap
            ) /
            2f;

        ImGui.SetCursorScreenPos(
            new Vector2(
                popupPos.X +
                padding,
                popupMax.Y -
                Ui(50f)));

        var cancelRequested =
            ImGui.Button(
                "Cancel",
                new Vector2(
                    buttonWidth,
                    Ui(36f)));

        ImGui.SameLine(
            0f,
            buttonGap);

        var saveRequested =
            false;

        using (ImRaii.PushColor(
                   ImGuiCol.Button,
                   Accent)
                   .Push(
                       ImGuiCol.ButtonHovered,
                       AccentHover)
                   .Push(
                       ImGuiCol.ButtonActive,
                       AccentActive))
        {
            saveRequested =
                ImGui.Button(
                    editingQueueProfile
                        ? "Save changes"
                        : "Create queue",
                    new Vector2(
                        buttonWidth,
                        Ui(36f)));
        }

        if (cancelRequested)
        {
            createQueuePopupOpen = false;
            creatingQueueIndex = -1;
            editingQueueProfile = false;
            newQueueName = string.Empty;
            newQueueIcon = "Tv";
            queueEditorError = null;
        }
        else if (saveRequested)
        {
            if (string.IsNullOrWhiteSpace(
                    newQueueName))
            {
                queueEditorError =
                    "Please enter a queue name.";
            }
            else
            {
                var succeeded =
                    editingQueueProfile
                        ? queueManager.UpdateDetails(
                            creatingQueueIndex,
                            newQueueName,
                            newQueueIcon)
                        : queueManager.Create(
                            creatingQueueIndex,
                            newQueueName,
                            newQueueIcon);

                if (succeeded)
                {
                    createQueuePopupOpen = false;
                    creatingQueueIndex = -1;
                    editingQueueProfile = false;
                    newQueueName = string.Empty;
                    newQueueIcon = "Tv";
                    queueEditorError = null;
                }
                else
                {
                    queueEditorError =
                        "That queue slot could not be updated.";
                }
            }
        }

        ImGui.End();
    }

    private void DrawDeleteQueuePopup()
    {
        if (!deleteQueuePopupOpen)
        {
            return;
        }

        var popupWidth = Ui(470f);
        var popupHeight = Ui(255f);
        var padding = NativeUi(20f);

        var parentPos =
            ImGui.GetWindowPos();

        var parentSize =
            ImGui.GetWindowSize();

        var popupPos =
            parentPos +
            new Vector2(
                (
                    parentSize.X -
                    popupWidth
                ) *
                0.5f,
                (
                    parentSize.Y -
                    popupHeight
                ) *
                0.5f);

        var popupMax =
            popupPos +
            new Vector2(
                popupWidth,
                popupHeight);

        ImGui.SetNextWindowPos(
            parentPos,
            ImGuiCond.Always);

        ImGui.SetNextWindowSize(
            parentSize,
            ImGuiCond.Always);

        ImGui.SetNextWindowBgAlpha(0f);

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
                "##deleteQueueOverlay",
                overlayFlags))
        {
            ImGui.End();
            return;
        }

        var drawList =
            ImGui.GetWindowDrawList();

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
                    Danger.X,
                    Danger.Y,
                    Danger.Z,
                    0.55f)),
            10f,
            ImDrawFlags.RoundCornersAll,
            1f);

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
                    0.18f)),
            24);

        using (UiFonts.PushIcon())
        {
            var trashGlyph =
                FontAwesomeIcon.Trash
                    .ToIconString();

            var glyphSize =
                ImGui.CalcTextSize(
                    trashGlyph);

            UiFonts.DrawText(drawList, ImGui.GetFontSize(), iconCenter -
                glyphSize *
                0.5f, ImGui.GetColorU32(
                    Danger), trashGlyph);
        }

        ImGui.SetCursorScreenPos(
            popupPos +
            new Vector2(
                padding +
                Ui(48f),
                Ui(20f)));

        using var uiFontScale49365=UiFonts.PushRelative(1.15f);

        DrawSocialHeading("Delete queue?",1.15f);

        uiFontScale49365.Dispose();

        var contentWidth =
            popupWidth -
            padding *
            2f;

        ImGui.SetCursorScreenPos(
            popupPos +
            new Vector2(
                padding,
                Ui(72f)));

        ImGui.PushTextWrapPos(
            ImGui.GetCursorPosX() +
            contentWidth);

        DrawSocialHelper($"Deleting \"{deletingQueueName}\" will permanently remove all videos saved in that queue.");

        ImGui.PopTextWrapPos();

        ImGui.SetCursorScreenPos(
            popupPos +
            new Vector2(
                padding,
                Ui(123f)));

        ImGui.PushTextWrapPos(
            ImGui.GetCursorPosX() +
            contentWidth);

        DrawSocialHelper("A video already playing will continue. If this is the active queue, another available queue will become active.");

        ImGui.PopTextWrapPos();

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
                BorderSubtle));

        var buttonGap = Ui(10f);

        var buttonWidth =
            (
                contentWidth -
                buttonGap
            ) /
            2f;

        ImGui.SetCursorScreenPos(
            new Vector2(
                popupPos.X +
                padding,
                popupMax.Y -
                Ui(50f)));

        var cancelRequested =
            false;

        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameRounding,
                   8f))
        using (ImRaii.PushColor(
                   ImGuiCol.Button,
                   FrameBg)
                   .Push(
                       ImGuiCol.ButtonHovered,
                       FrameBgHover)
                   .Push(
                       ImGuiCol.ButtonActive,
                       AccentFieldActive))
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

        var deleteRequested =
            false;

        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameRounding,
                   8f))
        using (ImRaii.PushColor(
                   ImGuiCol.Button,
                   new Vector4(
                       Danger.X * 0.65f,
                       Danger.Y * 0.65f,
                       Danger.Z * 0.65f,
                       1f))
                   .Push(
                       ImGuiCol.ButtonHovered,
                       Danger)
                   .Push(
                       ImGuiCol.ButtonActive,
                       new Vector4(
                           Danger.X * 0.8f,
                           Danger.Y * 0.8f,
                           Danger.Z * 0.8f,
                           1f)))
        {
            deleteRequested =
                ImGui.Button(
                    "Delete queue",
                    new Vector2(
                        buttonWidth,
                        Ui(36f)));
        }

        if (cancelRequested)
        {
            deleteQueuePopupOpen = false;
            deletingQueueIndex = -1;
            deletingQueueName = string.Empty;
        }
        else if (deleteRequested)
        {
            queueManager.Delete(
                deletingQueueIndex);

            deleteQueuePopupOpen = false;
            deletingQueueIndex = -1;
            deletingQueueName = string.Empty;
        }

        ImGui.End();
    }

    private void DrawClearQueuePopup()
    {
        if (!clearQueuePopupOpen)
        {
            return;
        }

        var popupWidth = Ui(470f);
        var popupHeight = Ui(245f);
        var padding = NativeUi(20f);

        var parentPos =
            ImGui.GetWindowPos();

        var parentSize =
            ImGui.GetWindowSize();

        var popupPos =
            parentPos +
            new Vector2(
                (
                    parentSize.X -
                    popupWidth
                ) *
                0.5f,
                (
                    parentSize.Y -
                    popupHeight
                ) *
                0.5f);

        var popupMax =
            popupPos +
            new Vector2(
                popupWidth,
                popupHeight);

        var contentWidth =
            popupWidth -
            padding *
            2f;

        ImGui.SetNextWindowPos(
            parentPos,
            ImGuiCond.Always);

        ImGui.SetNextWindowSize(
            parentSize,
            ImGuiCond.Always);

        ImGui.SetNextWindowBgAlpha(0f);

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
                "##clearQueueOverlay",
                overlayFlags))
        {
            ImGui.End();
            return;
        }

        var drawList =
            ImGui.GetWindowDrawList();

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
                    Danger.X,
                    Danger.Y,
                    Danger.Z,
                    0.55f)),
            10f,
            ImDrawFlags.RoundCornersAll,
            1f);

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
                    0.18f)),
            24);

        using (UiFonts.PushIcon())
        {
            var trashGlyph =
                FontAwesomeIcon.Trash
                    .ToIconString();

            var glyphSize =
                ImGui.CalcTextSize(
                    trashGlyph);

            UiFonts.DrawText(drawList, ImGui.GetFontSize(), iconCenter -
                glyphSize *
                0.5f, ImGui.GetColorU32(
                    Danger), trashGlyph);
        }

        ImGui.SetCursorScreenPos(
            popupPos +
            new Vector2(
                padding +
                Ui(48f),
                Ui(20f)));

        using var uiFontScale57366=UiFonts.PushRelative(1.15f);

        ImGui.TextColored(
            Vector4.One,
            "Clear active queue?");

        uiFontScale57366.Dispose();

        ImGui.SetCursorScreenPos(
            popupPos +
            new Vector2(
                padding,
                Ui(75f)));

        ImGui.PushTextWrapPos(
            ImGui.GetCursorPosX() +
            contentWidth);

        DrawSocialHelper($"This will remove all {clearingQueueVideoCount} videos from \"{clearingQueueName}\".");

        ImGui.PopTextWrapPos();

        ImGui.SetCursorScreenPos(
            popupPos +
            new Vector2(
                padding,
                Ui(118f)));

        ImGui.PushTextWrapPos(
            ImGui.GetCursorPosX() +
            contentWidth);

        DrawSocialHelper("A video already playing will continue, but every upcoming video in this queue will be removed.");

        ImGui.PopTextWrapPos();

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
                BorderSubtle));

        var buttonGap = Ui(10f);

        var buttonWidth =
            (
                contentWidth -
                buttonGap
            ) /
            2f;

        ImGui.SetCursorScreenPos(
            new Vector2(
                popupPos.X +
                padding,
                popupMax.Y -
                Ui(50f)));

        var cancelRequested =
            ImGui.Button(
                "Cancel",
                new Vector2(
                    buttonWidth,
                    Ui(36f)));

        ImGui.SameLine(
            0f,
            buttonGap);

        var clearRequested =
            false;

        using (ImRaii.PushColor(
                   ImGuiCol.Button,
                   new Vector4(
                       Danger.X * 0.65f,
                       Danger.Y * 0.65f,
                       Danger.Z * 0.65f,
                       1f))
                   .Push(
                       ImGuiCol.ButtonHovered,
                       Danger)
                   .Push(
                       ImGuiCol.ButtonActive,
                       new Vector4(
                           Danger.X * 0.8f,
                           Danger.Y * 0.8f,
                           Danger.Z * 0.8f,
                           1f)))
        {
            clearRequested =
                ImGui.Button(
                    "Clear queue",
                    new Vector2(
                        buttonWidth,
                        Ui(36f)));
        }

        if (cancelRequested)
        {
            clearQueuePopupOpen = false;
            clearingQueueName = string.Empty;
            clearingQueueVideoCount = 0;
        }
        else if (clearRequested)
        {
            //
            // Remove upcoming entries without stopping the video
            // that is currently playing.
            //
            foreach (var entry in
                     queue.Entries.ToList())
            {
                queue.Remove(entry);
            }

            clearQueuePopupOpen = false;
            clearingQueueName = string.Empty;
            clearingQueueVideoCount = 0;
        }

        ImGui.End();
    }

    private static bool DrawQueueGhostIcon(
        string id,
        FontAwesomeIcon icon)
    {
        var size = NativeUi(22f);

        var origin = ImGui.GetCursorScreenPos();

        var clicked = ImGui.InvisibleButton(
            id,
            new Vector2(size, size));

        var hovered = ImGui.IsItemHovered();

        using (UiFonts.PushIcon())
        {
            var glyph = icon.ToIconString();
            var glyphSize = ImGui.CalcTextSize(glyph);

            UiFonts.DrawText(ImGui.GetWindowDrawList(), ImGui.GetFontSize(), origin + new Vector2(
                    (size - glyphSize.X) * 0.5f,
                    (size - glyphSize.Y) * 0.5f), ImGui.GetColorU32(
                    hovered
                        ? Vector4.One
                        : MutedText), glyph);
        }

        return clicked;
    }
}
