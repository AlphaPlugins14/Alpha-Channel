using AlphaChannel.Plugin.Auth;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private bool alphaChatSettingsOpen;
    private string? alphaChatSettingsAccount;
    private bool alphaChatCustomUsernameColour;
    private Vector3 alphaChatUsernameColourDraft;
    private DateTime alphaChatNextColourSaveUtc;
    private readonly Dictionary<string, string?> alphaChatUsernameColourOverrides = new();
    private readonly Dictionary<string, (string Effect, long Until)> alphaChatUsernameEffectOverrides = new();
    private static readonly string[] AlphaChatEffectValues = AlphaChatNameEffects.Presets.Select(p => p.Id).ToArray();
    private static readonly string[] AlphaChatEffectLabels = AlphaChatNameEffects.Presets.Select(p => p.Label).ToArray();
    private int alphaChatUsernameEffectDraft;

    private string EligibleAlphaChatUsernameEffect(string accountId)
    {
        return CurrentVerifiedPatreonTier() < 1 ? "solid" : SavedAlphaChatUsernameEffect(accountId);
    }

    // The selected preference is independent of its temporary approval. The
    // worker verifies membership; refreshing local entitlement must not publish
    // a replacement "solid" preference on the user's behalf.
    private string SavedAlphaChatUsernameEffect(string accountId)
    {
        if (Plugin.Cfg.AlphaChatUsernameEffects is null ||
            !Plugin.Cfg.AlphaChatUsernameEffects.TryGetValue(Guid.Parse(accountId).ToString("D"), out var effect)) return "solid";
        try { return AlphaChatNameEffects.Normalize(effect); }
        catch (ArgumentException) { return "solid"; }
    }

    private void ObserveAlphaChatUsernameStyle(AlphaChatUser user)
    {
        RegisterUsername(user.AccountId,user.DisplayName);
        var style=new UsernameStyle(user.AccountId,user.NameColourHex,user.NameEffect,user.NameEffectUntil);
        usernameStyles[user.AccountId]=(style,UsernameStyleRefreshAt(style,DateTime.UtcNow));
        if(user.AccountId!=CurrentSession?.AccountId || usernameOwnLoaded) usernameStylePending.Remove(user.AccountId);
        alphaChatUsernameColourOverrides[user.AccountId] = user.NameColourHex;
        alphaChatUsernameEffectOverrides[user.AccountId] = (user.NameEffect, user.NameEffectUntil);
    }

    private void DrawAlphaChatUsername(AlphaChatUser user, string text, Vector4 fallback)
    {
        DrawUsername(user.AccountId,text,fallback);
    }

    private void DrawAlphaChatStyledUsername(ImDrawListPtr draw, Vector2 position, string text, Vector4 colour, string effect)
    {
        var size = ImGui.CalcTextSize(text);
        if (!ImGui.IsRectVisible(position, position + size)) return;
        if (effect == "solid" || size.X <= 0)
        { draw.AddText(position, ImGui.GetColorU32(colour), text); return; }
        var slices = Math.Clamp((int)MathF.Ceiling(size.X / Ui(6f)), 2, 24);
        for (var i = 0; i < slices; i++)
        {
            var sample = AlphaChatNameEffects.Sample(effect, colour, (float)i / (slices - 1), ImGui.GetTime());
            draw.PushClipRect(position + new Vector2(size.X * i / slices, -Ui(3f)),
                position + new Vector2(size.X * (i + 1) / slices, size.Y + Ui(3f)), true);
            if (sample.Glow > 0)
            {
                var halo = sample.Colour; halo.W = .22f * sample.Glow;
                for (var ring = 0; ring < 4; ring++)
                {
                    var angle = ring * MathF.PI / 2;
                    draw.AddText(position + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * Ui(1.5f), ImGui.GetColorU32(halo), text);
                }
            }
            draw.AddText(position, ImGui.GetColorU32(sample.Colour), text);
            draw.PopClipRect();
        }
    }

    private bool DrawAlphaChatEffectPicker()
    {
        using var style = ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, Ui(1))
            .Push(ImGuiStyleVar.PopupBorderSize, Ui(1)).Push(ImGuiStyleVar.PopupRounding, Ui(8))
            .Push(ImGuiStyleVar.WindowPadding, UiVec(8, 8)).Push(ImGuiStyleVar.ItemSpacing, UiVec(8, 4));
        using var colours = ImRaii.PushColor(ImGuiCol.Border, AccentInk(.7f)).Push(ImGuiCol.PopupBg, CardBg);
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        var maxHeight = MathF.Max(Ui(100), MathF.Min(Ui(340), ImGui.GetMainViewport().WorkSize.Y * .6f));
        ImGui.SetNextWindowSizeConstraints(Vector2.Zero, new Vector2(float.MaxValue, maxHeight));
        if (!ImGui.BeginCombo("##alphaChatUsernameEffect", AlphaChatEffectLabels[alphaChatUsernameEffectDraft], ImGuiComboFlags.HeightLarge)) return false;
        var changed = false;
        var colour = new Vector4(alphaChatUsernameColourDraft, 1f);
        var rowHeight = MathF.Max(Ui(28), ImGui.GetTextLineHeight() + Ui(8));
        for (var i = 0; i < AlphaChatNameEffects.Presets.Length; i++)
        {
            var preset = AlphaChatNameEffects.Presets[i];
            var selected = i == alphaChatUsernameEffectDraft;
            var rowStart = ImGui.GetCursorScreenPos();
            // Keep native selection, keyboard navigation and focus; paint the label
            // with the same renderer used for actual usernames and the main preview.
            using (ImRaii.PushColor(ImGuiCol.Text, Vector4.Zero))
                if (ImGui.Selectable(preset.Label, selected, ImGuiSelectableFlags.None, new Vector2(0, rowHeight)))
                { alphaChatUsernameEffectDraft = i; changed = true; }
            DrawAlphaChatStyledUsername(ImGui.GetWindowDrawList(), rowStart + new Vector2(Ui(6),
                (rowHeight - ImGui.GetTextLineHeight()) * .5f), preset.Label, colour, preset.Id);
            if (selected) ImGui.SetItemDefaultFocus();
            if (ImGui.IsItemHovered()) UiFonts.SetTooltip(preset.Description);
        }
        ImGui.EndCombo();
        return changed;
    }

    private string? SavedAlphaChatUsernameColour(string accountId)
    {
        var id = Guid.Parse(accountId).ToString("D");
        if (Plugin.Cfg.AlphaChatUsernameColours is null || !Plugin.Cfg.AlphaChatUsernameColours.TryGetValue(id, out var colour)) return null;
        try { return AlphaChatClient.NormalizeUsernameColour(colour); }
        catch (ArgumentException) { return null; }
    }

    private void OpenAlphaChatSettings()
    {
        if (CurrentSession is not { } session) return;
        alphaChatSettingsAccount = session.AccountId;
        var colour = SavedAlphaChatUsernameColour(session.AccountId);
        alphaChatCustomUsernameColour = colour is not null;
        var rgba = colour is null ? AccentHover : ParseAvatarColor(colour);
        alphaChatUsernameColourDraft = new Vector3(rgba.X, rgba.Y, rgba.Z);
        alphaChatUsernameEffectDraft = Array.IndexOf(AlphaChatEffectValues, EligibleAlphaChatUsernameEffect(session.AccountId));
        alphaChatCustomUsernameColour |= alphaChatUsernameEffectDraft > 0;
        alphaChatSettingsOpen = true;
    }

    private void SaveAlphaChatSettings()
    {
        if (CurrentSession is not { } session || session.AccountId != alphaChatSettingsAccount) return;
        var id = Guid.Parse(session.AccountId).ToString("D");
        var rgb = Vector3.Clamp(alphaChatUsernameColourDraft, Vector3.Zero, Vector3.One);
        string? colour = alphaChatCustomUsernameColour
            ? $"#{(int)MathF.Round(rgb.X * 255):X2}{(int)MathF.Round(rgb.Y * 255):X2}{(int)MathF.Round(rgb.Z * 255):X2}" : null;
        Plugin.Cfg.AlphaChatUsernameColours ??= new();
        if (colour is null) Plugin.Cfg.AlphaChatUsernameColours.Remove(id);
        else Plugin.Cfg.AlphaChatUsernameColours[id] = colour;
        Plugin.Cfg.AlphaChatUsernameEffects ??= new();
        var effect = CurrentVerifiedPatreonTier() > 0 && alphaChatCustomUsernameColour ? AlphaChatEffectValues[alphaChatUsernameEffectDraft] : "solid";
        if (effect == "solid") Plugin.Cfg.AlphaChatUsernameEffects.Remove(id);
        else Plugin.Cfg.AlphaChatUsernameEffects[id] = effect;
        Plugin.Cfg.UsernameStylePreferencesSaved ??= new();
        Plugin.Cfg.UsernameStylePreferencesSaved.Add(id);
        // Invalidate a read already in flight, even if a publish is still busy.
        usernameSaveRevision++;
        usernameNextOwnRefresh = DateTime.MinValue;
        Plugin.Cfg.Save(); alphaChat.SetUsernameStyle(colour, effect, publish: false);
        usernameOwnLoaded=true;
        _=PublishUsernameStyleAsync(session);
        alphaChatNextColourSaveUtc = DateTime.UtcNow.AddSeconds(1.1);
        alphaChatSettingsOpen = false;
    }

    private void DrawAlphaChatSettingsOverlay()
    {
        if (!alphaChatSettingsOpen) return;
        if (currentPage is not (HomePage.AlphaChat or HomePage.Settings) || CurrentSession?.AccountId != alphaChatSettingsAccount)
        { alphaChatSettingsOpen = false; return; }
        var parent = ImGui.GetWindowPos(); var parentSize = ImGui.GetWindowSize();
        var size = new Vector2(MathF.Min(Ui(500f), parentSize.X - Ui(32f)), MathF.Min(Ui(480f), parentSize.Y - Ui(32f)));
        var position = parent + (parentSize - size) * .5f;
        ImGui.SetNextWindowPos(parent, ImGuiCond.Always); ImGui.SetNextWindowSize(parentSize, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoBackground;
        if (!BeginWindowOverlay("##alphaChatSettingsOverlay", flags)) { ImGui.End(); return; }
        ImGui.GetWindowDrawList().AddRectFilled(parent, parent + parentSize, ImGui.GetColorU32(new Vector4(0f, 0f, 0f, .54f)));
        ImGui.SetCursorScreenPos(position);
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(14f)).Push(ImGuiStyleVar.ChildBorderSize, Ui(1f))
            .Push(ImGuiStyleVar.WindowPadding, UiVec(20f, 18f)))
        using (ImRaii.PushColor(ImGuiCol.ChildBg, CardBg)
            .Push(ImGuiCol.Border, new Vector4(Accent.X, Accent.Y, Accent.Z, .55f)))
        using (var panel = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##alphaChatSettingsPanel", size, true, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)))
        {
            if (panel)
            {
                using (UiFonts.PushIcon()) ImGui.TextColored(AccentHover, FontAwesomeIcon.Cog.ToIconString());
                ImGui.SameLine(); DrawSocialHeading("Username colour");
                ImGui.SetCursorScreenPos(position + new Vector2(size.X - Ui(48f), Ui(14f)));
                if (AlphaChatButton("closeAlphaChatSettings", "", FontAwesomeIcon.Times, Ui(28f), Ui(28f))) alphaChatSettingsOpen = false;
                ImGui.SetCursorScreenPos(position + UiVec(20f, 60f));
                using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero))
                using (var body = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##alphaChatSettingsBody", new Vector2(size.X - Ui(40f), size.Y - Ui(124f)), false, ImGuiWindowFlags.NoScrollbar)))
                    if (body)
                    {
                        DrawSocialHelperWrapped("Choose how your username appears throughout Alpha Channel."); ImGui.Spacing();
                        if(usernameStyleError is { } styleError) ImGui.TextWrapped(styleError);
                        if (ImGui.Checkbox("Enable custom colour", ref alphaChatCustomUsernameColour) && !alphaChatCustomUsernameColour)
                            alphaChatUsernameEffectDraft = 0;
                        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
                        var colourAdjustable = AlphaChatNameEffects.UsesCustomColour(AlphaChatEffectValues[alphaChatUsernameEffectDraft]);
                        using (UiDisabled.Scope(!colourAdjustable))
                            if (ImGui.ColorEdit3("##alphaChatUsernameColour", ref alphaChatUsernameColourDraft, ImGuiColorEditFlags.NoLabel))
                                alphaChatCustomUsernameColour = true;
                        if (!colourAdjustable) DrawSocialHelper("This preset uses its own colour palette.");
                        ImGui.Spacing(); ImGui.TextUnformatted("Colour effect");
                        var eligible = CurrentVerifiedPatreonTier() > 0;
                        if (!eligible) alphaChatUsernameEffectDraft = 0;
                        using (UiDisabled.Scope(!eligible))
                            if (DrawAlphaChatEffectPicker())
                                alphaChatCustomUsernameColour = true;
                        DrawSocialHelperWrapped(AlphaChatNameEffects.Presets[alphaChatUsernameEffectDraft].Description);
                        if (!eligible) DrawSocialHelper("Name effects require an active Patreon subscription.");
                        ImGui.Spacing(); DrawSocialHelper("Preview");
                        var preview = CurrentDisplayName ?? CurrentSession?.DisplayName ?? "Your name";
                        ImGui.TextColored(Vector4.Zero, preview);
                        DrawAlphaChatStyledUsername(ImGui.GetWindowDrawList(), ImGui.GetItemRectMin(), preview,
                            alphaChatCustomUsernameColour ? new Vector4(alphaChatUsernameColourDraft, 1f) : AccentHover,
                            eligible && alphaChatCustomUsernameColour ? AlphaChatEffectValues[alphaChatUsernameEffectDraft] : "solid");
                    }
                ImGui.SetCursorScreenPos(position + new Vector2(MathF.Max(Ui(20f), size.X - Ui(268f)), size.Y - Ui(54f)));
                if (ActivePartyButton("Cancel##alphaChatSettings", UiVec(120f, 36f))) alphaChatSettingsOpen = false;
                ImGui.SameLine();
                using (UiDisabled.Scope(usernameStyleSaving || DateTime.UtcNow < alphaChatNextColourSaveUtc))
                    if (ActivePartyButton("Save##alphaChatSettings", UiVec(120f, 36f), primary: true)) SaveAlphaChatSettings();
            }
        }
        if (ImGui.IsKeyPressed(ImGuiKey.Escape)) alphaChatSettingsOpen = false;
        ImGui.End();
    }
}
