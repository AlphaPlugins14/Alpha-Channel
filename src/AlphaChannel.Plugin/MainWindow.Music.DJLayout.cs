using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private string? djCredentialsAccountId;
    private string? musicDjAutoRequestAccount;
    private DateTime musicDjNextCredentialAttemptUtc;

    private void EnsureDjCredentialsForMusic()
    {
        var account = CurrentSession?.AccountId;
        if (musicDjAutoRequestAccount != account)
        {
            musicDjAutoRequestAccount = account;
            musicDjNextCredentialAttemptUtc = DateTime.MinValue;
            if (djCredentialsAccountId != account)
            {
                radioCredentials = null;
                djIsOnAir = false;
                djConnectionMonitoringEnabled = false;
                djConnectionSearchActive = false;
                djStreamUrl = "";
                radioError = null;
            }
        }
        if (account is null || radioCredentials is not null || djCredentialsLoading || DateTime.UtcNow < musicDjNextCredentialAttemptUtc) return;
        // Failed requests are visible in the panel and retried at a bounded rate.
        musicDjNextCredentialAttemptUtc = DateTime.UtcNow.AddSeconds(30);
        RequestDjConnectionDetails();
    }
    private void DrawMusicDjHeading(string title, FontAwesomeIcon icon, string? status = null, Vector4? statusColour = null)
    {
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        using var uiFontScale1556=UiFonts.PushRelative(1.1f);
        var headingWidth = ImGui.CalcTextSize(title).X + Ui(32);
        using (UiFonts.PushIcon()) ImGui.TextColored(Accent, icon.ToIconString());
        ImGui.SameLine(0, Ui(10));
        using (ImRaii.PushColor(ImGuiCol.Text, Hex(0xE2E7EF)))
        using (ImRaii.TextWrapPos(0)) ImGui.TextWrapped(title);
        uiFontScale1556.Dispose();
        if (status is null) return;
        var colour = statusColour ?? MutedText;
        var size = ImGui.CalcTextSize(status);
        var pillSize = size + UiVec(22, 12);
        var inline = width >= headingWidth + pillSize.X + Ui(12);
        var min = inline ? origin + new Vector2(width - pillSize.X, -Ui(2)) : ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(min, min + pillSize, ImGui.GetColorU32(new Vector4(colour.X, colour.Y, colour.Z, .12f)), Ui(12));
        draw.AddText(min + UiVec(11, 6), ImGui.GetColorU32(colour), status);
        if (!inline) ImGui.Dummy(pillSize);
    }

    private void DrawDjStagePreview(float width, float height)
    {
        var origin = ImGui.GetCursorScreenPos();
        var size = new Vector2(width, height);
        ImGui.Dummy(size);
        var draw = ImGui.GetWindowDrawList();
        var scene = GetMusicEffects();
        uint Colour(uint rgb, float alpha = 1) => ImGui.GetColorU32(new Vector4(WheelRgb(rgb), alpha));
        draw.PushClipRect(origin, origin + size, true);
        try
        {
            draw.AddRectFilled(origin, origin + size, ImGui.GetColorU32(FrameBg), Ui(8));
            var fullStage = (scene.Mask & (64 | 128 | 256 | 2048 | 8192)) != 0;
            var tvHeight = height * (fullStage ? .42f : .76f);
            var tvWidth = Math.Min(width * .46f, tvHeight * 16 / 9);
            var min = origin + (size - new Vector2(tvWidth, tvHeight)) / 2;
            var max = min + new Vector2(tvWidth, tvHeight);
            var centre = (min + max) / 2;
            DrawMusicPreviewStageEffects(draw, scene, min, max);
            if ((scene.Mask & 2) != 0)
                for (var i = 4; i > 0; i--) draw.AddRect(min - new Vector2(i * Ui(2)), max + new Vector2(i * Ui(2)), Colour(scene.Halo, .15f), Ui(3), ImDrawFlags.None, Ui(3));
            for (var side = 0; side < 2; side++)
            {
                var left = side == 0;
                var cabinetWidth = tvWidth * .19f;
                var cabinetMin = new Vector2(left ? min.X - cabinetWidth - Ui(9) : max.X + Ui(9), min.Y + Ui(2));
                var cabinetMax = cabinetMin + new Vector2(cabinetWidth, tvHeight - Ui(4));
                var cabinetCentre = (cabinetMin + cabinetMax) / 2;
                if ((scene.Mask & 1) != 0)
                {
                    draw.AddRectFilled(cabinetMin, cabinetMax, 0xFF0B0B12, Ui(3));
                    draw.AddRect(cabinetMin, cabinetMax, ImGui.GetColorU32(BorderSubtle), Ui(3));
                    for (var cone = 0; cone < 2; cone++)
                    {
                        var point = new Vector2(cabinetCentre.X, cabinetMin.Y + (cabinetMax.Y - cabinetMin.Y) * (cone == 0 ? .28f : .72f));
                        draw.AddCircle(point, cabinetWidth * .32f, Colour(scene.Speakers, .15f), 24, Ui(5));
                        draw.AddCircle(point, cabinetWidth * .32f, Colour(scene.Speakers), 24, Ui(1.5f));
                    }
                }
                if ((scene.Mask & 32) != 0)
                {
                    var emitter = new Vector2(left ? cabinetMin.X - Ui(3) : cabinetMax.X + Ui(3), cabinetMax.Y);
                    for (var beam = 0; beam < 3; beam++)
                    {
                        var end = origin + new Vector2(width * (left ? .04f + beam * .025f : .96f - beam * .025f), height * (.12f + beam * .15f));
                        draw.AddLine(emitter, end, Colour(scene.Lasers, .12f), Ui(5));
                        draw.AddLine(emitter, end, Colour(scene.Lasers, .65f), Ui(1));
                    }
                }
                if ((scene.Mask & 4) != 0)
                    for (var i = 0; i < 8; i++)
                    {
                        var y = cabinetMin.Y + i * (cabinetMax.Y - cabinetMin.Y) / 8;
                        draw.AddRectFilled(new Vector2(cabinetMin.X - Ui(2), y), new Vector2(cabinetMin.X, y + Ui(4)),
                            Colour(scene.RainbowLeds ? MusicPreviewRainbow(i / 8f) : scene.Leds));
                    }
                if ((scene.Mask & 8) != 0)
                    draw.AddCircle(cabinetCentre, cabinetWidth * .7f, Colour(scene.Rings, .3f), 32, Ui(1));
            }
            if ((scene.Mask & 16) != 0)
                for (var i = 0; i < 12; i++) draw.AddCircleFilled(origin + new Vector2(width * ((i + 1f) / 13), height * (.12f + ((i * 7) % 9) * .08f)), Ui(1), Colour(scene.Sparkles, .6f), 8);
            DrawMusicPreviewOuterEffects(draw, scene, min, max);
            draw.AddRectFilled(min - new Vector2(Ui(3)), max + new Vector2(Ui(3)), 0xFF06060B, Ui(3));
            draw.AddRectFilled(min, max, Colour(scene.Background));
            DrawMusicPreviewNeonFrame(draw, scene, min, max);
            // A recognisable stage thumbnail, rather than shrinking the whole
            // Scene canvas into a tiny square. Bars stay legible at this size.
            for (var i = 0; i < 26; i++)
            {
                var t = (i + .5f) / 26;
                var half = tvHeight * (.1f + .22f * Math.Abs(MathF.Sin(i * .9f)));
                var x = min.X + t * tvWidth;
                draw.AddRectFilled(new Vector2(x, centre.Y - half), new Vector2(x + tvWidth / 42, centre.Y + half),
                    Colour(MusicPreviewTheme(partyVisualizerTheme, t)), Ui(.5f));
            }
            draw.AddRect(origin, origin + size, ImGui.GetColorU32(BorderSubtle), Ui(8));
        }
        finally { draw.PopClipRect(); }
    }
}
