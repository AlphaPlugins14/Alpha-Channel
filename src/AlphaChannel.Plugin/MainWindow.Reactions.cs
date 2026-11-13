using AlphaChannel.Contracts;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

// Just the send buttons - the actual reactions now render on the in-world screen itself
// (Plugin.cs's UpdateReactions/VideoEngine.SetReactions/ScreenPainter's ReactionsPS), not floating
// in this GUI window. Only one place can drain stream.IncomingReactions (it's a ConcurrentQueue,
// not a broadcast), and Plugin.cs is it.
internal sealed partial class MainWindow
{
    private sealed record ReactionDefinition(
        FontAwesomeIcon Icon,
        string Name,
        Vector4 BadgeColor,
        bool RequiresPatreon = false);

    private static readonly ReactionDefinition[] Reactions =
    [
        new(
        FontAwesomeIcon.ThumbsUp,
        "Like",
        new Vector4(0.18f, 0.45f, 1.00f, 1f)),

    new(
        FontAwesomeIcon.Laugh,
        "Laugh",
        new Vector4(1.00f, 0.72f, 0.16f, 1f)),

    new(
        FontAwesomeIcon.Heart,
        "Love",
        new Vector4(1.00f, 0.20f, 0.42f, 1f)),

    new(
        FontAwesomeIcon.Surprise,
        "Surprised",
        new Vector4(0.12f, 0.76f, 0.96f, 1f)),

    new(
        FontAwesomeIcon.Star,
        "Hype",
        new Vector4(0.56f, 0.20f, 0.96f, 1f)),

    new(
        FontAwesomeIcon.Fire,
        "Fire",
        new Vector4(1.00f, 0.31f, 0.06f, 1f),
        true),

    new(
        FontAwesomeIcon.ThumbsDown,
        "Dislike",
        new Vector4(0.80f, 0.18f, 0.27f, 1f),
        true),

    new(
        FontAwesomeIcon.Angry,
        "Angry",
        new Vector4(0.96f, 0.10f, 0.08f, 1f),
        true),

    new(
        FontAwesomeIcon.SadTear,
        "Crying",
        new Vector4(0.16f, 0.55f, 1.00f, 1f),
        true),

    new(
        FontAwesomeIcon.Skull,
        "Dead",
        new Vector4(0.42f, 0.48f, 0.58f, 1f),
        true),

    new(
        FontAwesomeIcon.Bomb,
        "Mind Blown",
        new Vector4(0.96f, 0.39f, 0.08f, 1f),
        true),

    new(
        FontAwesomeIcon.Rocket,
        "Hype",
        new Vector4(0.08f, 0.78f, 0.70f, 1f),
        true),

    new(
        FontAwesomeIcon.Trophy,
        "Victory",
        new Vector4(0.96f, 0.69f, 0.10f, 1f),
        true),

    new(
        FontAwesomeIcon.Ghost,
        "Spooky",
        new Vector4(0.56f, 0.39f, 0.84f, 1f),
        true),

    new(
        FontAwesomeIcon.Crown,
        "Royal",
        new Vector4(0.88f, 0.53f, 0.07f, 1f),
        true),

    new(
        FontAwesomeIcon.Music,
        "Music Moment",
        new Vector4(0.86f, 0.24f, 0.78f, 1f),
        true),

    new(
        FontAwesomeIcon.ExclamationTriangle,
        "Warning",
        new Vector4(1.00f, 0.59f, 0.06f, 1f),
        true),

    new(
        FontAwesomeIcon.Bolt,
        "Maximum Hype",
        new Vector4(0.92f, 0.82f, 0.08f, 1f),
        true),

    new(
        FontAwesomeIcon.Rainbow,
        "Fabulous",
        new Vector4(0.96f, 0.21f, 0.70f, 1f),
        true),

    new(
        FontAwesomeIcon.Flushed,
        "Embarrassed",
        new Vector4(0.97f, 0.43f, 0.53f, 1f),
        true),

    new(
        FontAwesomeIcon.Grimace,
        "Awkward",
        new Vector4(0.74f, 0.55f, 0.17f, 1f),
        true),

    new(
        FontAwesomeIcon.Dizzy,
        "Dizzy",
        new Vector4(0.45f, 0.30f, 0.84f, 1f),
        true),

    new(
        FontAwesomeIcon.Eye,
        "Watching",
        new Vector4(0.10f, 0.67f, 0.79f, 1f),
        true),

    new(
        FontAwesomeIcon.Gem,
        "Amazing",
        new Vector4(0.08f, 0.81f, 0.95f, 1f),
        true),

    new(
        FontAwesomeIcon.Tired,
        "Exhausted",
        new Vector4(0.31f, 0.43f, 0.66f, 1f),
        true),

    new(
        FontAwesomeIcon.GrinTongueWink,
        "Cheeky",
        new Vector4(0.55f, 0.76f, 0.12f, 1f),
        true),

    new(
        FontAwesomeIcon.GlassCheers,
        "Celebration",
        new Vector4(0.95f, 0.34f, 0.57f, 1f),
        true),

    new(
        FontAwesomeIcon.FistRaised,
        "Powerful",
        new Vector4(0.77f, 0.18f, 0.12f, 1f),
        true),

    new(
        FontAwesomeIcon.PizzaSlice,
        "Food Moment",
        new Vector4(0.96f, 0.32f, 0.10f, 1f),
        true),

    new(
        FontAwesomeIcon.Flag,
        "Salute",
        new Vector4(0.12f, 0.70f, 0.38f, 1f),
        true),
];

    private const int ReactionHotbarSlotCount = 30;
    private const int ReactionHotbarColumns = 5;
    private static readonly TimeSpan ReactionCooldown = TimeSpan.FromSeconds(1.5);

    // Slots are displayed left-to-right, then top-to-bottom in the hotbar.
    private static int RequiredReactionTier(int slot) => slot switch
    {
        < 5 => 0,
        < 13 => 1,
        < 21 => 2,
        _ => 3,
    };

    private DateTime reactionCooldownUntilUtc;
    private int lastReactionSlot = -1;

    private static Vector4 GetReactionDisplayColor(
        string glyph)
    {
        foreach (var reaction in Reactions)
        {
            if (string.Equals(
                    reaction.Icon.ToIconString(),
                    glyph,
                    StringComparison.Ordinal))
            {
                return reaction.BadgeColor;
            }
        }

        return new Vector4(
            0.56f,
            0.40f,
            1.00f,
            1f);
    }

    private void DrawSectionTitle(
    FontAwesomeIcon icon,
    string title)
    {
        using (UiFonts.PushIcon())
        {
            ImGui.TextColored(
                currentPage == HomePage.WatchAlong ? Hex(0xE2E7EF) : Vector4.One,
                icon.ToIconString());
        }

        ImGui.SameLine(0, 8);

        ImGui.TextColored(
            currentPage == HomePage.WatchAlong ? Hex(0xE2E7EF) : Vector4.One,
            title);
    }

    private void DrawReactionButton(
     ReactionDefinition reaction,
     Vector2 size)
    {
        var drawList =
            ImGui.GetWindowDrawList();

        var buttonOrigin =
            ImGui.GetCursorScreenPos();

        var radius =
            MathF.Min(size.X, size.Y) *
            0.5f;

        var center =
            buttonOrigin +
            size *
            0.5f;

        drawList.AddCircleFilled(
            center + UiVec(1.5f, 2.5f),
            radius,
            ImGui.GetColorU32(
                new Vector4(0f, 0f, 0f, 0.42f)));

        var normalColor =
            reaction.BadgeColor with
            {
                W = 0.50f,
            };

        var hoveredColor =
            reaction.BadgeColor with
            {
                W = 0.70f,
            };

        var activeColor =
            reaction.BadgeColor with
            {
                W = 0.85f,
            };

        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameRounding,
                   radius))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameBorderSize,
                   Ui(2f)))
        using (ImRaii.PushColor(
                   ImGuiCol.Button,
                   normalColor))
        using (ImRaii.PushColor(
                   ImGuiCol.ButtonHovered,
                   hoveredColor))
        using (ImRaii.PushColor(
                   ImGuiCol.ButtonActive,
                   activeColor))
        using (ImRaii.PushColor(
                   ImGuiCol.Border,
                   new Vector4(
                       0.018f,
                       0.025f,
                       0.055f,
                       1f)))
        {
            if (ImGui.Button(
                    $"##reaction_{reaction.Name}",
                    size))
            {
                _ = stream.SendReactionAsync(
                    reaction.Icon.ToIconString(),
                    CurrentSession?.DisplayName,
                    CurrentCharacterName,
                    CurrentCharacterHomeWorldName,
                    Plugin.Cfg.ShowReactionsInPartyChat);
            }
        }

        var min =
            ImGui.GetItemRectMin();

        var iconText =
            reaction.Icon.ToIconString();

        Vector2 textSize;

        using (UiFonts.PushIcon())
        {
            textSize =
                ImGui.CalcTextSize(
                    iconText);

            var pos =
                new Vector2(
                    min.X +
                    ((size.X - textSize.X) * 0.5f),

                    min.Y +
                    ((size.Y - textSize.Y) * 0.5f));

            UiFonts.DrawText(drawList, ImGui.GetFontSize(), pos, ImGui.GetColorU32(
                    Vector4.One), iconText);
        }

        if (ImGui.IsItemHovered())
        {
            UiFonts.SetTooltip(reaction.Name);
        }
    }

    private void DrawCompactReactions(
      float width, float? height = null, bool openBelow = false)
    {
        if (stream.Mode == StreamMode.None)
        {
            return;
        }

        var buttonSize =
            new Vector2(
                MathF.Max(Ui(150f), width),
                height ?? Ui(40f));

        var normalFont = ImGui.GetFont();
        var fontSize = ImGui.GetFontSize();

        using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(9f))
            .Push(ImGuiStyleVar.FrameBorderSize, openBelow ? Ui(1) : 0f))
        using (ImRaii.PushColor(ImGuiCol.Button, openBelow ? FrameBg : Accent)
                   .Push(ImGuiCol.ButtonHovered, openBelow ? FrameBgHover : AccentHover)
                   .Push(ImGuiCol.ButtonActive, openBelow ? AccentFieldActive : AccentActive)
                   .Push(ImGuiCol.Border,new Vector4(Accent.X,Accent.Y,Accent.Z,0.65f)))
        {
            if (ImGui.Button(
                    "##reactionHotbarToggle",
                    buttonSize))
            {
                ImGui.OpenPopup("##reactionHotbarPopup");
            }
        }

        var buttonMin = ImGui.GetItemRectMin();
        var buttonMax = ImGui.GetItemRectMax();
        DrawReactionHotbarButtonLabel(
            buttonMin,
            buttonMax,
            normalFont,
            fontSize);

        var popupWidth = Ui(304f);
        ImGui.SetNextWindowPos(
            new Vector2(
                buttonMax.X - popupWidth,
                openBelow ? buttonMax.Y + Ui(8f) : buttonMin.Y - Ui(8f)),
            ImGuiCond.Always,
            new Vector2(0f, openBelow ? 0f : 1f));
        ImGui.SetNextWindowSize(
            new Vector2(popupWidth, 0f),
            ImGuiCond.Always);

        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, UiVec(12f, 11f))
                   .Push(ImGuiStyleVar.PopupRounding, Ui(12f))
                   .Push(ImGuiStyleVar.PopupBorderSize, Ui(2f)))
        using (ImRaii.PushColor(ImGuiCol.PopupBg, new Vector4(0.025f, 0.030f, 0.060f, 0.98f))
                   .Push(ImGuiCol.Border, Accent))
        {
            if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginPopup(
                    "##reactionHotbarPopup",
                    ImGuiWindowFlags.AlwaysAutoResize |
                    ImGuiWindowFlags.NoMove |
                    ImGuiWindowFlags.NoResize |
                    ImGuiWindowFlags.NoScrollbar |
                    ImGuiWindowFlags.NoScrollWithMouse)))
            {
                DrawReactionHotbar();
                ImGui.EndPopup();
            }
        }
    }

    private static void DrawReactionHotbarButtonLabel(
        Vector2 buttonMin,
        Vector2 buttonMax,
        ImFontPtr normalFont,
        float fontSize)
    {
        const string label = "React live";
        var icon = FontAwesomeIcon.Bolt.ToIconString();
        var drawList = ImGui.GetWindowDrawList();
        var labelSize = ImGui.CalcTextSize(label);
        Vector2 iconSize;

        using (UiFonts.PushIcon())
        {
            iconSize = ImGui.CalcTextSize(icon);
        }

        var gap = Ui(8f);
        var totalWidth = iconSize.X + gap + labelSize.X;
        var start = new Vector2(
            buttonMin.X + ((buttonMax.X - buttonMin.X - totalWidth) * 0.5f),
            buttonMin.Y + ((buttonMax.Y - buttonMin.Y - labelSize.Y) * 0.5f));

        UiFonts.DrawIcon(drawList, fontSize, new Vector2(start.X, start.Y), ImGui.GetColorU32(Vector4.One), icon);
        drawList.AddText(
            normalFont,
            fontSize,
            new Vector2(start.X + iconSize.X + gap, start.Y),
            ImGui.GetColorU32(Vector4.One),
            label);
    }

    private void DrawReactionHotbar()
    {
        var remaining =
            Math.Max(
                0d,
                (reactionCooldownUntilUtc - DateTime.UtcNow).TotalSeconds);

        using (UiFonts.PushIcon())
        {
            ImGui.TextColored(
                Accent,
                FontAwesomeIcon.Bolt.ToIconString());
        }

        ImGui.SameLine(0f, Ui(7f));
        ImGui.TextColored(AccentHover, "REACT LIVE");

        var status = remaining > 0d
            ? $"Cooldown {remaining:0.0}s"
            : "Ready";
        var statusSize = ImGui.CalcTextSize(status);
        ImGui.SameLine(
            MathF.Max(
                ImGui.GetCursorPosX() + Ui(8f),
                ImGui.GetWindowContentRegionMax().X - statusSize.X));
        ImGui.TextColored(
            remaining > 0d ? MutedText : Good,
            status);

        using (UiFonts.PushRegularRelative(0.85f))
        {
            ImGui.TextColored(MutedText, "Reactions show on the tv");
        }

        ImGui.Dummy(UiVec(0f, 6f));

        var gap = Ui(7f);
        var slotSize =
            (ImGui.GetContentRegionAvail().X -
             gap * (ReactionHotbarColumns - 1)) /
            ReactionHotbarColumns;

        using (ImRaii.PushStyle(
                   ImGuiStyleVar.ItemSpacing,
                   new Vector2(gap, gap)))
        {
            for (var slot = 0; slot < ReactionHotbarSlotCount; slot++)
            {
                if (slot > 0 && slot % ReactionHotbarColumns != 0)
                {
                    ImGui.SameLine(0f, gap);
                }

                DrawReactionHotbarSlot(
                    slot,
                    new Vector2(slotSize, slotSize),
                    remaining);
            }
        }
    }

    private void DrawReactionHotbarSlot(
        int slot,
        Vector2 size,
        double remainingCooldownSeconds)
    {
        var reaction = Reactions[slot];
        var requiredTier = RequiredReactionTier(slot);
        var isLocked = ConfiguredPatreonTier() < requiredTier;
        var onCooldown = remainingCooldownSeconds > 0d;
        var baseColor = reaction.BadgeColor;

        using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(7f))
                   .Push(ImGuiStyleVar.FrameBorderSize, Ui(1.5f)))
        using (ImRaii.PushColor(ImGuiCol.Button, baseColor with { W = isLocked ? 0.28f : 0.42f })
                   .Push(ImGuiCol.ButtonHovered, baseColor with { W = isLocked ? 0.38f : 0.62f })
                   .Push(ImGuiCol.ButtonActive, baseColor with { W = 0.78f })
                   .Push(ImGuiCol.Border, new Vector4(0.30f, 0.32f, 0.45f, 1f)))
        using (UiDisabled.Scope(onCooldown && !isLocked))
        {
            if (ImGui.Button($"##reactionHotbarSlot{slot}", size))
            {
                if (isLocked)
                {
                    patreonPopupOpen = true;
                }
                else
                {
                    _ = stream.SendReactionAsync(
                        reaction.Icon.ToIconString(),
                        CurrentSession?.DisplayName,
                        CurrentCharacterName,
                        CurrentCharacterHomeWorldName,
                        Plugin.Cfg.ShowReactionsInPartyChat);
                    reactionCooldownUntilUtc = DateTime.UtcNow + ReactionCooldown;
                    lastReactionSlot = slot;
                }
            }
        }

        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var drawList = ImGui.GetWindowDrawList();
        var glyph = reaction.Icon.ToIconString();

        Vector2 glyphSize;
        using (UiFonts.PushIcon())
        {
            glyphSize = ImGui.CalcTextSize(glyph);
        }

        UiFonts.DrawIcon(drawList, ImGui.GetFontSize() * 1.18f, new Vector2(
                min.X + (size.X - glyphSize.X * 1.18f) * 0.5f,
                min.Y + (size.Y - glyphSize.Y * 1.18f) * 0.5f), ImGui.GetColorU32(
                isLocked
                    ? new Vector4(1f, 1f, 1f, 0.46f)
                    : Vector4.One), glyph);

        if (onCooldown)
        {
            var fraction =
                (float)Math.Clamp(
                    remainingCooldownSeconds / ReactionCooldown.TotalSeconds,
                    0d,
                    1d);
            drawList.AddRectFilled(
                min,
                new Vector2(max.X, min.Y + size.Y * fraction),
                ImGui.GetColorU32(new Vector4(0.01f, 0.012f, 0.025f, 0.72f)),
                Ui(7f));

            if (slot == lastReactionSlot)
            {
                var countdown = remainingCooldownSeconds.ToString("0.0");
                var countdownSize = ImGui.CalcTextSize(countdown);
                drawList.AddText(
                    new Vector2(
                        min.X + (size.X - countdownSize.X) * 0.5f,
                        min.Y + (size.Y - countdownSize.Y) * 0.5f),
                    ImGui.GetColorU32(Vector4.One),
                    countdown);
            }
        }

        if (isLocked)
        {
            var lockGlyph = FontAwesomeIcon.Lock.ToIconString();
            Vector2 lockSize;
            using (UiFonts.PushIcon())
            {
                lockSize = ImGui.CalcTextSize(lockGlyph);
            }

            UiFonts.DrawIcon(drawList, ImGui.GetFontSize() * 0.70f, new Vector2(
                    max.X - lockSize.X * 0.70f - Ui(5f),
                    min.Y + Ui(4f)), ImGui.GetColorU32(PatreonOrange), lockGlyph);
        }

        if (ImGui.IsItemHovered())
        {
            UiFonts.SetTooltip(
                isLocked
                    ? $"{reaction.Name} — requires Patreon Tier {requiredTier} ({PatreonTierName(requiredTier)})"
                    : onCooldown
                        ? $"{reaction.Name} — ready in {remainingCooldownSeconds:0.0}s"
                        : reaction.Name);
        }
    }

}
