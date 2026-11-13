using AlphaChannel.Plugin.Video;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private bool rouletteSettingsOpen;
    private bool roulettePageVolumeDragging;
    private bool rouletteTvVolumeDragging;

    private int RouletteMusicVolume =>
        Plugin.Cfg.RouletteMusicEnabled && !Plugin.Cfg.Muted ? Plugin.Cfg.Volume : 0;

    private void ResetRouletteSettingsUi()
    {
        rouletteSettingsOpen = false;
        roulettePageVolumeDragging = false;
        rouletteTvVolumeDragging = false;
    }

    private void HandleRouletteSettingsTarget(int target, Vector2 uv)
    {
        switch (target)
        {
            case 108:
                rouletteSettingsOpen = !rouletteSettingsOpen;
                if (rouletteSettingsOpen) { rouletteHelpOpen = false; rouletteLedgerOverlayOpen = false; }
                break;
            case 109:
                rouletteSettingsOpen = false;
                break;
            case 110:
                Plugin.Cfg.RouletteMusicEnabled = !Plugin.Cfg.RouletteMusicEnabled;
                Plugin.Cfg.Save();
                rouletteMusicPlayer.SetVolume(RouletteMusicVolume);
                break;
            case 111:
                Plugin.Cfg.RouletteSoundEffectsEnabled = !Plugin.Cfg.RouletteSoundEffectsEnabled;
                Plugin.Cfg.Save();
                break;
            case 112:
                Plugin.Cfg.RouletteEmotesEnabled = !Plugin.Cfg.RouletteEmotesEnabled;
                Plugin.Cfg.Save();
                break;
            case 113:
                SetRouletteVolumeFromX(uv.X * WhiteboardRenderer.Width, persist: true);
                break;
        }
        whiteboardTvDirty = true;
    }

    private bool TryGetRouletteSettingsTarget(float x, float y, out int target)
    {
        target = -1;
        if (!rouletteSettingsOpen) return false;
        if (x is >= 1201 and < 1243 && y is >= 130 and < 166)
        {
            target = 109;
            return true;
        }
        if (x is >= 870 and < 1240)
        {
            if (y is >= 186 and < 232) target = 110;
            else if (y is >= 242 and < 288) target = 111;
            else if (y is >= 298 and < 344) target = 112;
            if (target >= 0) return true;
        }
        if (x is >= 880 and <= 1240 && y is >= 376 and < 412)
        {
            target = 113;
            return true;
        }
        return false;
    }

    private void SetRouletteVolumeFromX(float x, bool persist)
    {
        var volume = Math.Clamp((int)Math.Round((x - 886f) / 348f * 130f), 0, 130);
        if (Plugin.Cfg.Volume != volume || Plugin.Cfg.Muted && volume > 0)
        {
            Plugin.Cfg.Volume = volume;
            if (volume > 0) Plugin.Cfg.Muted = false;
            ApplyListeningVolume();
            rouletteMusicPlayer.SetVolume(RouletteMusicVolume);
            whiteboardTvDirty = true;
        }
        if (persist) Plugin.Cfg.Save();
    }

    private void UpdateRouletteVolumePointer(Vector2? uv, bool mouseDown, bool mousePressed,
        ref bool dragging)
    {
        if (mousePressed && uv is { } start &&
            TryGetRouletteSettingsTarget(start.X * WhiteboardRenderer.Width,
                start.Y * WhiteboardRenderer.Height, out var target) && target == 113)
            dragging = true;
        if (!mouseDown)
        {
            if (dragging) Plugin.Cfg.Save();
            dragging = false;
            return;
        }
        if (dragging && uv is { } point)
            SetRouletteVolumeFromX(point.X * WhiteboardRenderer.Width, persist: false);
    }

    private void UpdateRoulettePageVolumePointer(Vector2? uv, bool mouseDown, bool mousePressed) =>
        UpdateRouletteVolumePointer(uv, mouseDown, mousePressed, ref roulettePageVolumeDragging);

    internal void UpdateRouletteTvVolumePointer(Vector2? uv, bool mouseDown, bool mousePressed) =>
        UpdateRouletteVolumePointer(uv, mouseDown, mousePressed, ref rouletteTvVolumeDragging);

    private void DrawRouletteSettings(byte[] pixels, int width, int height)
    {
        const uint gold = 0xD6B456, ivory = 0xF8F5FF;
        const int buttonX = 1162, buttonY = 10, buttonSize = 48;
        var hovered = rouletteHoveredTarget == 108 || roulettePageHoveredTarget == 108;
        FillPictionaryPixels(pixels, width, height, buttonX, buttonY, buttonSize, buttonSize,
            hovered || rouletteSettingsOpen ? 0x4A2B75u : 0x251A38u);
        DrawRoulettePrototypeRectOutline(pixels, width, height, buttonX, buttonY, buttonSize, buttonSize,
            rouletteSettingsOpen ? 0xBCA0FFu : gold, 2);
        var cogX = buttonX + buttonSize / 2;
        var cogY = buttonY + buttonSize / 2;
        for (var tooth = 0; tooth < 8; tooth++)
        {
            var angle = tooth * Math.PI / 4d;
            FillPictionaryPixels(pixels, width, height,
                cogX + (int)Math.Round(Math.Cos(angle) * 13d) - 2,
                cogY + (int)Math.Round(Math.Sin(angle) * 13d) - 2, 5, 5, ivory);
        }
        DrawRoulettePrototypeRing(pixels, width, height, cogX, cogY, 5, 11, ivory);

        if (!rouletteSettingsOpen) return;

        const int panelX = 850, panelY = 116, panelWidth = 408, panelHeight = 352;
        FillPictionaryPixels(pixels, width, height, panelX, panelY, panelWidth, panelHeight, 0x073321);
        DrawRoulettePrototypeRectOutline(pixels, width, height, panelX, panelY, panelWidth, panelHeight, gold, 3);
        FillPictionaryPixels(pixels, width, height, panelX + 3, panelY + 3, panelWidth - 6, 54, 0x181123);
        DrawPictionaryPixelText(pixels, width, height, 872, 135, "ROULETTE SETTINGS", 3, gold);
        DrawRouletteSettingsControl(pixels, width, height, 1201, 130, 42, 36, "X", 109, true, "");
        DrawRouletteSettingsControl(pixels, width, height, 870, 186, 370, 46, "MUSIC", 110,
            Plugin.Cfg.RouletteMusicEnabled, "BACKGROUND TRACKS");
        DrawRouletteSettingsControl(pixels, width, height, 870, 242, 370, 46, "SOUND FX", 111,
            Plugin.Cfg.RouletteSoundEffectsEnabled, "CHIPS / WHEEL");
        DrawRouletteSettingsControl(pixels, width, height, 870, 298, 370, 46, "EMOTES", 112,
            Plugin.Cfg.RouletteEmotesEnabled, "RESULT MOTION");

        var volume = Plugin.Cfg.Volume;
        DrawPictionaryPixelText(pixels, width, height, 880, 352, "VOLUME", 2, 0xBCA0FF);
        DrawPictionaryPixelText(pixels, width, height, 1150, 352, $"{volume}%", 2,
            volume > 100 ? gold : ivory);
        var sliderHovered = rouletteHoveredTarget == 113 || roulettePageHoveredTarget == 113;
        FillPictionaryPixels(pixels, width, height, 880, 376, 360, 32, 0x181123);
        DrawRoulettePrototypeRectOutline(pixels, width, height, 880, 376, 360, 32,
            sliderHovered ? gold : 0x8B55F6u, 2);
        FillPictionaryPixels(pixels, width, height, 886, 389, 348, 7, 0x4A3852);
        var fill = (int)Math.Round(348d * volume / 130d);
        if (fill > 0)
            FillPictionaryPixels(pixels, width, height, 886, 389, fill, 7,
                volume > 100 ? gold : 0x8B55F6u);
        FillRoulettePrototypeCircle(pixels, width, height, 886 + fill, 392, 10,
            sliderHovered ? ivory : gold);
        DrawPictionaryPixelText(pixels, width, height, 880, 433, "YOUR SETTINGS ONLY", 2, 0xBCA0FF);
    }

    private void DrawRouletteSettingsControl(byte[] pixels, int width, int height,
        int x, int y, int w, int h, string title, int target, bool enabled, string description)
    {
        const uint gold = 0xD6B456, ivory = 0xF8F5FF, muted = 0xBCA0FF;
        var hovered = rouletteHoveredTarget == target || roulettePageHoveredTarget == target;
        FillPictionaryPixels(pixels, width, height, x, y, w, h,
            hovered ? 0x174C37u : 0x052E20u);
        DrawRoulettePrototypeRectOutline(pixels, width, height, x, y, w, h,
            hovered ? gold : 0x47705Du, 2);
        if (target == 109)
        {
            DrawPictionaryPixelTextCentered(pixels, width, height, x + w / 2, y + 9, title, 2, ivory);
            return;
        }
        DrawPictionaryPixelText(pixels, width, height, x + 12, y + 7, title, 2, ivory);
        DrawPictionaryPixelText(pixels, width, height, x + 12, y + 27, description, 1, muted);
        FillPictionaryPixels(pixels, width, height, x + w - 99, y + 7, 87, 32,
            enabled ? 0x07543Au : 0x3D3B48u);
        DrawRoulettePrototypeRectOutline(pixels, width, height, x + w - 99, y + 7, 87, 32,
            hovered ? gold : enabled ? 0x24D991u : 0x77717Fu, 2);
        DrawPictionaryPixelTextCentered(pixels, width, height, x + w - 56, y + 15,
            enabled ? "ON" : "OFF", 2, enabled ? 0xA7F5BEu : muted);
    }
}
