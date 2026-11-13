using AlphaChannel.Plugin.Video;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private bool chocoRaceSettingsOpen;
    private bool chocoRacePageVolumeDragging;
    private bool chocoRaceTvVolumeDragging;

    private void ResetChocoRaceSettingsUi()
    {
        chocoRaceSettingsOpen = false;
        chocoRacePageVolumeDragging = false;
        chocoRaceTvVolumeDragging = false;
    }

    private void HandleChocoRaceSettingsTarget(int target, Vector2 uv)
    {
        switch (target)
        {
            case 700:
                chocoRaceSettingsOpen = !chocoRaceSettingsOpen;
                break;
            case 701:
                chocoRaceSettingsOpen = false;
                break;
            case 710:
                Plugin.Cfg.ChocoRaceMusicEnabled = !Plugin.Cfg.ChocoRaceMusicEnabled;
                Plugin.Cfg.Save();
                break;
            case 711:
                Plugin.Cfg.ChocoRaceSoundEffectsEnabled = !Plugin.Cfg.ChocoRaceSoundEffectsEnabled;
                Plugin.Cfg.Save();
                break;
            case 712:
                Plugin.Cfg.ChocoRaceEmotesEnabled = !Plugin.Cfg.ChocoRaceEmotesEnabled;
                Plugin.Cfg.Save();
                break;
            case 720:
                SetChocoRaceVolumeFromX(uv.X * WhiteboardRenderer.Width, persist: true);
                break;
        }
        chocoRaceDirty = true;
    }

    private bool TryGetChocoRaceSettingsTarget(float x, float y, out int target)
    {
        target = -1;
        if (x is >= 1109 and < 1251 && y is >= 73 and < 105)
        {
            target = 700;
            return true;
        }
        if (!chocoRaceSettingsOpen) return false;
        if (x is >= 1190 and < 1233 && y is >= 135 and < 173)
        {
            target = 701;
            return true;
        }
        if (x is >= 1125 and < 1229)
        {
            if (y is >= 186 and < 228) target = 710;
            else if (y is >= 239 and < 281) target = 711;
            else if (y is >= 292 and < 334) target = 712;
            if (target >= 0) return true;
        }
        if (x is >= 907 and <= 1225 && y is >= 367 and < 409)
        {
            target = 720;
            return true;
        }
        return false;
    }

    private void SetChocoRaceVolumeFromX(float x, bool persist)
    {
        var volume = Math.Clamp((int)Math.Round((x - 911f) / 306f * 130f), 0, 130);
        if (Plugin.Cfg.Volume != volume || Plugin.Cfg.Muted && volume > 0)
        {
            Plugin.Cfg.Volume = volume;
            if (volume > 0) Plugin.Cfg.Muted = false;
            ApplyListeningVolume();
            chocoRaceDirty = true;
        }
        if (persist) Plugin.Cfg.Save();
    }

    private void UpdateChocoRaceVolumePointer(Vector2? uv, bool mouseDown, bool mousePressed,
        ref bool dragging)
    {
        if (mousePressed && uv is { } start &&
            TryGetChocoRaceSettingsTarget(start.X * WhiteboardRenderer.Width,
                start.Y * WhiteboardRenderer.Height, out var target) && target == 720)
            dragging = true;
        if (!mouseDown)
        {
            if (dragging) Plugin.Cfg.Save();
            dragging = false;
            return;
        }
        if (dragging && uv is { } point)
            SetChocoRaceVolumeFromX(point.X * WhiteboardRenderer.Width, persist: false);
    }

    private void UpdateChocoRacePageVolumePointer(Vector2? uv, bool mouseDown, bool mousePressed)
    {
        if (ChocoRacePageIntroBlocksInput)
        {
            chocoRacePageVolumeDragging = false;
            return;
        }
        UpdateChocoRaceVolumePointer(uv, mouseDown, mousePressed, ref chocoRacePageVolumeDragging);
    }

    internal void UpdateChocoRaceTvVolumePointer(Vector2? uv, bool mouseDown, bool mousePressed)
    {
        if (ChocoRaceTvIntroBlocksInput)
        {
            chocoRaceTvVolumeDragging = false;
            return;
        }
        UpdateChocoRaceVolumePointer(uv, mouseDown, mousePressed, ref chocoRaceTvVolumeDragging);
    }

    private void DrawChocoRaceSettingsButton(byte[] pixels)
    {
        const int width = WhiteboardRenderer.Width, height = WhiteboardRenderer.Height;
        const uint gold = 0xF6BC4E, purple = 0x8959ED;
        var hovered = chocoRaceHoveredControlTarget == 700;
        if (hovered)
            FillQuizTvPanel(pixels, width, height, 1106, 70, 148, 38, gold, 0xF5F3FB);
        FillQuizTvPanel(pixels, width, height, 1109, 73, 142, 32,
            hovered || chocoRaceSettingsOpen ? 0x64418Eu : 0x21162Fu,
            hovered ? gold : purple);
        DrawPictionaryPixelTextCentered(pixels, width, height, 1180, 81, "SETTINGS", 2, 0xF5F3FB);
    }

    private void DrawChocoRaceSettingsOverlay(byte[] pixels)
    {
        if (!chocoRaceSettingsOpen) return;
        const int width = WhiteboardRenderer.Width, height = WhiteboardRenderer.Height;
        const uint gold = 0xF6BC4E, white = 0xF5F3FB, muted = 0xB9A9CF;
        FillQuizTvPanel(pixels, width, height, 876, 123, 376, 330, 0x17131E, 0x8959ED);
        DrawPictionaryPixelText(pixels, width, height, 897, 141, "RACE SETTINGS", 3, gold);
        DrawChocoRaceSettingsControl(pixels, 1190, 135, 43, 38, "X", 701, true, "");
        DrawChocoRaceSettingsControl(pixels, 897, 183, 332, 45, "MUSIC", 710,
            Plugin.Cfg.ChocoRaceMusicEnabled, "BACKGROUND THEME");
        DrawChocoRaceSettingsControl(pixels, 897, 236, 332, 45, "SOUND FX", 711,
            Plugin.Cfg.ChocoRaceSoundEffectsEnabled, "ALERT / METEOR / FANFARE");
        DrawChocoRaceSettingsControl(pixels, 897, 289, 332, 45, "EMOTES", 712,
            Plugin.Cfg.ChocoRaceEmotesEnabled, "WIN / LOSE MOTION");

        var volume = Plugin.Cfg.Volume;
        var volumeColour = volume > 100 ? gold : white;
        DrawPictionaryPixelText(pixels, width, height, 904, 348, "VOLUME", 2, muted);
        DrawPictionaryPixelText(pixels, width, height, 1130, 348, $"{volume}%", 2, volumeColour);
        FillQuizTvPanel(pixels, width, height, 907, 375, 318, 25, 0x261B33,
            chocoRaceHoveredControlTarget == 720 ? gold : 0x6E5370);
        FillPictionaryPixels(pixels, width, height, 911, 383, 306, 9, 0x463853);
        var fill = (int)Math.Round(306d * volume / 130d);
        if (fill > 0)
            FillPictionaryPixels(pixels, width, height, 911, 383, fill, 9,
                volume > 100 ? gold : 0x8959EDu);
        RaceCircle(pixels, width, height, 911 + fill, 387, 10,
            chocoRaceHoveredControlTarget == 720 ? white : gold);
        DrawPictionaryPixelText(pixels, width, height, 904, 422,
            "YOUR SETTINGS ONLY", 2, muted);
    }

    private void DrawChocoRaceSettingsControl(byte[] pixels, int x, int y, int w, int h,
        string title, int target, bool enabled, string description)
    {
        const int width = WhiteboardRenderer.Width, height = WhiteboardRenderer.Height;
        const uint gold = 0xF6BC4E, white = 0xF5F3FB, muted = 0xB9A9CF;
        var hovered = chocoRaceHoveredControlTarget == target;
        FillQuizTvPanel(pixels, width, height, x, y, w, h,
            hovered ? 0x463059u : 0x21162Fu, hovered ? gold : 0x6E5370u);
        if (target == 701)
        {
            DrawPictionaryPixelTextCentered(pixels, width, height, x + w / 2, y + 11,
                title, 2, white);
            return;
        }
        DrawPictionaryPixelText(pixels, width, height, x + 12, y + 7, title, 2, white);
        DrawPictionaryPixelText(pixels, width, height, x + 12, y + 26, description, 1, muted);
        FillQuizTvPanel(pixels, width, height, x + 228, y + 7, 91, 31,
            enabled ? 0x1E6849u : 0x3D3B48u,
            hovered ? gold : enabled ? 0x65D795u : 0x77717Fu);
        DrawPictionaryPixelTextCentered(pixels, width, height, x + 273, y + 15,
            enabled ? "ON" : "OFF", 2, enabled ? 0xA7F5BEu : muted);
    }
}
