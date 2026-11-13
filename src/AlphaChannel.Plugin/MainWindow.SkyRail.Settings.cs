using AlphaChannel.Plugin.Video;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private bool skyRailSettingsOpen;
    private int skyRailPageVolumeDragging;
    private int skyRailTvVolumeDragging;

    private void ResetSkyRailSettingsUi()
    {
        skyRailSettingsOpen = false;
        skyRailPageVolumeDragging = 0;
        skyRailTvVolumeDragging = 0;
    }

    private void HandleSkyRailSettingsTarget(int target, Vector2 uv)
    {
        switch (target)
        {
            case 700:
                skyRailSettingsOpen = !skyRailSettingsOpen;
                break;
            case 701:
                skyRailSettingsOpen = false;
                break;
            case 710:
                Plugin.Cfg.SkyRailMusicEnabled = !Plugin.Cfg.SkyRailMusicEnabled;
                Plugin.Cfg.Save();
                break;
            case 711:
                Plugin.Cfg.SkyRailSoundEffectsEnabled = !Plugin.Cfg.SkyRailSoundEffectsEnabled;
                Plugin.Cfg.Save();
                break;
            case 720:
            case 721:
                SetSkyRailVolumeFromX(uv.X * WhiteboardRenderer.Width, target, persist: true);
                break;
        }
        whiteboardTvDirty = true;
    }

    private int SkyRailHoveredSettingsTarget
    {
        get
        {
            var uv = skyRailPageHover ?? skyRailTvHover;
            return uv is { } point && TryGetSkyRailSettingsTarget(point.X * WhiteboardRenderer.Width,
                point.Y * WhiteboardRenderer.Height, out var target) ? target : -1;
        }
    }

    private bool TryGetSkyRailSettingsTarget(float x, float y, out int target)
    {
        target = -1;
        if (x is >= 350 and < 396 && y is >= 643 and < 689) target = 700;
        else if (skyRailSettingsOpen)
        {
            if (x is >= 1190 and < 1233 && y is >= 135 and < 173) target = 701;
            else if (x is >= 1125 and < 1229 && y is >= 186 and < 228) target = 710;
            else if (x is >= 1125 and < 1229 && y is >= 239 and < 281) target = 711;
            else if (x is >= 907 and <= 1225 && y is >= 307 and < 341) target = 720;
            else if (x is >= 907 and <= 1225 && y is >= 381 and < 415) target = 721;
        }
        return target >= 0;
    }

    private void SetSkyRailVolumeFromX(float x, int target, bool persist)
    {
        var volume = Math.Clamp((int)Math.Round((x - 911f) / 306f * 130f), 0, 130);
        if (target == 720) Plugin.Cfg.SkyRailMusicVolume = volume;
        else Plugin.Cfg.SkyRailSoundEffectsVolume = volume;
        whiteboardTvDirty = true;
        if (persist) Plugin.Cfg.Save();
    }

    private void UpdateSkyRailVolumePointer(Vector2? uv, bool mouseDown, bool mousePressed, ref int dragging)
    {
        if (!skyRailSettingsOpen) { dragging = 0; return; }
        if (mousePressed && uv is { } start && TryGetSkyRailSettingsTarget(start.X * WhiteboardRenderer.Width,
            start.Y * WhiteboardRenderer.Height, out var target) && target is 720 or 721)
            dragging = target;
        if (!mouseDown)
        {
            if (dragging != 0) Plugin.Cfg.Save();
            dragging = 0;
            return;
        }
        if (dragging != 0 && uv is { } point)
            SetSkyRailVolumeFromX(point.X * WhiteboardRenderer.Width, dragging, persist: false);
    }

    internal void UpdateSkyRailTvVolumePointer(Vector2? uv, bool mouseDown, bool mousePressed) =>
        UpdateSkyRailVolumePointer(uv, mouseDown, mousePressed, ref skyRailTvVolumeDragging);

    private void DrawSkyRailSettingsButton(byte[] pixels)
    {
        const int width = WhiteboardRenderer.Width, height = WhiteboardRenderer.Height;
        const uint gold = 0xF6BC4E, purple = 0x8959ED;
        var hovered = SkyRailHoveredSettingsTarget == 700;
        if (hovered)
            FillQuizTvPanel(pixels, width, height, 347, 640, 52, 52, gold, 0xF5F3FB);
        var background = hovered || skyRailSettingsOpen ? 0x64418Eu : 0x21162Fu;
        FillQuizTvPanel(pixels, width, height, 350, 643, 46, 46, background, hovered ? gold : purple);
        const int cx = 373, cy = 666;
        const uint icon = 0xF5F3FB;
        for (var tooth = 0; tooth < 8; tooth++)
        {
            var angle = tooth * Math.PI / 4;
            RaceLine(pixels, width, height, cx + (int)(Math.Cos(angle) * 10), cy + (int)(Math.Sin(angle) * 10),
                cx + (int)(Math.Cos(angle) * 15), cy + (int)(Math.Sin(angle) * 15), icon, 5);
        }
        FillRoulettePrototypeCircle(pixels, width, height, cx, cy, 12, icon);
        FillRoulettePrototypeCircle(pixels, width, height, cx, cy, 6, background);
    }

    private void DrawSkyRailSettingsOverlay(byte[] pixels)
    {
        if (!skyRailSettingsOpen) return;
        const int width = WhiteboardRenderer.Width, height = WhiteboardRenderer.Height;
        FillQuizTvPanel(pixels, width, height, 876, 123, 376, 347, 0x17131E, 0x8959ED);
        DrawPictionaryPixelText(pixels, width, height, 897, 141, "AUDIO SETTINGS", 2, 0xF6BC4E);
        DrawSkyRailSettingsControl(pixels, 1190, 135, 43, 38, "X", 701, true, "");
        DrawSkyRailSettingsControl(pixels, 897, 183, 332, 45, "MUSIC", 710,
            Plugin.Cfg.SkyRailMusicEnabled, "BACKGROUND THEME");
        DrawSkyRailSettingsControl(pixels, 897, 236, 332, 45, "SOUND FX", 711,
            Plugin.Cfg.SkyRailSoundEffectsEnabled, "BOSS WARNING");
        DrawSkyRailVolumeSlider(pixels, 292, "MUSIC VOLUME", Plugin.Cfg.SkyRailMusicVolume, 720);
        DrawSkyRailVolumeSlider(pixels, 366, "SOUND FX VOLUME", Plugin.Cfg.SkyRailSoundEffectsVolume, 721);
        DrawPictionaryPixelText(pixels, width, height, 904, 445, "YOUR SETTINGS ONLY", 2, 0xB9A9CF);
    }

    private void DrawSkyRailVolumeSlider(byte[] pixels, int y, string label, int volume, int target)
    {
        const int width = WhiteboardRenderer.Width, height = WhiteboardRenderer.Height;
        volume = Math.Clamp(volume, 0, 130);
        var hovered = SkyRailHoveredSettingsTarget == target;
        DrawPictionaryPixelText(pixels, width, height, 904, y, label, 2, 0xB9A9CF);
        DrawPictionaryPixelText(pixels, width, height, 1130, y, $"{volume}%", 2, 0xF5F3FB);
        FillQuizTvPanel(pixels, width, height, 907, y + 23, 318, 25, 0x261B33, hovered ? 0xF6BC4Eu : 0x6E5370u);
        FillPictionaryPixels(pixels, width, height, 911, y + 31, 306, 9, 0x463853);
        var fill = (int)Math.Round(306d * volume / 130d);
        if (fill > 0) FillPictionaryPixels(pixels, width, height, 911, y + 31, fill, 9, 0x8959ED);
        RaceCircle(pixels, width, height, 911 + fill, y + 35, 10, hovered ? 0xF5F3FBu : 0xF6BC4Eu);
    }

    private void DrawSkyRailSettingsControl(byte[] pixels, int x, int y, int w, int h,
        string title, int target, bool enabled, string description)
    {
        const int width = WhiteboardRenderer.Width, height = WhiteboardRenderer.Height;
        const uint gold = 0xF6BC4E, white = 0xF5F3FB, muted = 0xB9A9CF;
        var hovered = SkyRailHoveredSettingsTarget == target;
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
