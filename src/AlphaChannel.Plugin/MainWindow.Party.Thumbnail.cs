using AlphaChannel.Plugin.Video;
using AlphaChannel.Plugin.Core;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private FontAwesomeIcon CompactMediaPlaceholderIcon(VideoQueueEntry? entry, FontAwesomeIcon fallback)
    {
        if (entry is not null && (entry.Source.Equals("Arcade", StringComparison.OrdinalIgnoreCase) ||
            entry.Source.Equals("Gameplay", StringComparison.OrdinalIgnoreCase) ||
            PartyEntryCanvasActivity(entry.Url, entry.Title) is not (ActiveActivityKind.None or ActiveActivityKind.Whiteboard)))
            return FontAwesomeIcon.Gamepad;
        if (video.IsAudioOnly || entry?.Source.Equals("Radio", StringComparison.OrdinalIgnoreCase) == true)
            return FontAwesomeIcon.Music;
        return fallback;
    }

    private void DrawPartyThumbnailFallback(VideoQueueEntry entry, Vector2 origin, Vector2 size,
        bool usePlaybackState = true, FontAwesomeIcon? compactIcon = null, float? compactIconFontSize = null,
        Vector4? accentTint = null, Vector4? backgroundTint = null, PartyCountdownView? countdown = null)
    {
        var draw = ImGui.GetWindowDrawList();
        Vector4 Ink(float alpha) => accentTint is { } tint ? tint with { W = alpha } : AccentInk(alpha);
        var highlight = accentTint ?? AccentHover;
        draw.AddRectFilled(origin, origin + size, ImGui.GetColorU32(backgroundTint ?? FrameBg), Ui(9f));
        var center = origin + size * 0.5f;
        var compact = size.Y < Ui(100f);
        if (usePlaybackState && screenController.Engine.AudioOnlyMode) compactIcon = FontAwesomeIcon.Headphones;
        var radius = MathF.Min(size.X * 0.45f, size.Y * 0.46f);
        draw.PushClipRect(origin, origin + size, true);
        // Layered translucent circles give the card depth without a raster asset.
        for (var layer = 24; layer > 0; layer--)
            draw.AddCircleFilled(center, radius * layer / 24f,
                ImGui.GetColorU32(Ink(0.018f)), 48);

        if (!compact || compactIcon is not null)
        {
            var grid = Ui(compact ? 12f : 18f);
            var gridColor = ImGui.GetColorU32(Ink(0.035f));
            for (var x = origin.X + Ui(9f); x < origin.X + size.X - Ui(9f); x += grid)
                draw.AddLine(new Vector2(x, origin.Y + Ui(9f)), new Vector2(x, origin.Y + size.Y - Ui(9f)), gridColor);
            for (var y = origin.Y + Ui(9f); y < origin.Y + size.Y - Ui(9f); y += grid)
                draw.AddLine(new Vector2(origin.X + Ui(9f), y), new Vector2(origin.X + size.X - Ui(9f), y), gridColor);
            var pixel = MathF.Min(size.Y * 0.04f, Ui(10f));
            var pixelColor = ImGui.GetColorU32(Ink(0.16f));
            var corner = origin + size * 0.08f;
            draw.AddRectFilled(corner + new Vector2(pixel, 0f), corner + new Vector2(pixel * 2f, pixel), pixelColor);
            draw.AddRectFilled(corner + new Vector2(0f, pixel), corner + new Vector2(pixel, pixel * 2f), pixelColor);
            corner = origin + size * 0.88f;
            for (var i = 0; i < 3; i++)
            {
                var p = corner + new Vector2((i - 1) * pixel, -i * pixel);
                draw.AddRectFilled(p, p + new Vector2(pixel, pixel), pixelColor);
            }
        }

        if (countdown is not null)
        {
            var r = MathF.Min(size.Y * .35f, size.X * .3f);
            draw.AddCircle(center, r, ImGui.GetColorU32(Ink(.18f)), 64, MathF.Max(2, size.Y * .015f));
            draw.PathArcTo(center, r, -MathF.PI / 2, -MathF.PI / 2 + MathF.Tau * MathF.Max(.001f, countdown.Progress), 64);
            draw.PathStroke(ImGui.GetColorU32(highlight), ImDrawFlags.None, MathF.Max(2, size.Y * .015f));
            var clock = countdown.Clock; var fontSize = size.Y * .24f;
            var measure = ImGui.CalcTextSize(clock) * (fontSize / ImGui.GetFontSize());
            UiFonts.DrawText(draw, fontSize, center - measure * .5f, ImGui.GetColorU32(Vector4.One), clock);
            var countdownCaption = "STARTING SOON";
            var labelSize = ImGui.CalcTextSize(countdownCaption);
            if (size.Y > Ui(95)) draw.AddText(new(center.X - labelSize.X / 2, origin.Y + size.Y * .87f), ImGui.GetColorU32(highlight), countdownCaption);
            draw.AddRect(origin, origin + size, ImGui.GetColorU32(Ink(.4f)), Ui(9));
            draw.PopClipRect(); return;
        }
        if (compactIcon is { } icon)
        {
            var glyph = icon.ToIconString();
            using var uiIconFont3279=UiFonts.PushIcon();
            var measured = ImGui.CalcTextSize(glyph);
            var measuredFontSize = ImGui.GetFontSize();
            uiIconFont3279.Dispose();
            var fontSize = compactIconFontSize ?? MathF.Min(size.X, size.Y) * 0.42f;
            var glyphSize = measured * (fontSize / measuredFontSize);
            UiFonts.DrawIcon(draw, fontSize, center - glyphSize * 0.5f, ImGui.GetColorU32(highlight), glyph);
            draw.AddRect(origin, origin + size,
                ImGui.GetColorU32(Ink(0.28f)), Ui(9f));
            draw.PopClipRect();
            return;
        }

        var source = entry.Source ?? string.Empty;
        var isCurrent = usePlaybackState && (entry.Id == queue.Current?.Id ||
            AudioVisualizerSelection.IsSameMedia(entry.Url, queue.Current?.Url ?? string.Empty));
        var waiting = source.Equals("Waiting", StringComparison.OrdinalIgnoreCase);
        var arcade = source.Equals("Arcade", StringComparison.OrdinalIgnoreCase) ||
            PartyEntryCanvasActivity(entry.Url, entry.Title) is not (ActiveActivityKind.None or ActiveActivityKind.Whiteboard);
        var game = arcade || source.Equals("Gameplay", StringComparison.OrdinalIgnoreCase);
        var browser = source.Equals("Browser", StringComparison.OrdinalIgnoreCase) || (isCurrent && video.IsPlayingBrowser);
        var audio = source.Equals("Radio", StringComparison.OrdinalIgnoreCase) || (isCurrent && video.IsAudioOnly);
        var broadcast = !game && !browser && !audio && IsBroadcastPartyMedia(entry);
        var label = waiting ? "WAITING FOR CONTENT" : arcade ? "ARCADE" : game ? PartyFallbackGameLabel(entry.Title) : browser ? "BROWSER"
            : audio ? "LIVE RADIO" : broadcast ? "LIVE STREAM" : "VIDEO";
        var iconWidth = MathF.Min(size.X * 0.36f, size.Y * (compact ? 1.05f : 0.65f));
        var iconSize = new Vector2(iconWidth, iconWidth * 0.52f);
        var iconOrigin = center - iconSize * 0.5f - new Vector2(0f, compact ? 0f : size.Y * 0.065f);
        var color = ImGui.GetColorU32(highlight);
        var stroke = MathF.Max(Ui(1.2f), iconWidth * 0.022f);
        Vector2 P(float x, float y) => iconOrigin + iconSize * new Vector2(x, y);
        if (waiting)
        {
            var c = P(0.5f, 0.5f);
            var r = iconSize.Y * 0.45f;
            draw.AddCircle(c, r, color, 40, stroke);
            draw.AddLine(c, c - new Vector2(0f, r * 0.6f), color, stroke);
            draw.AddLine(c, c + new Vector2(r * 0.42f, r * 0.22f), color, stroke);
            draw.AddCircleFilled(c, stroke, color);
        }
        else if (game)
        {
            // Controller outline and controls use geometry, so resizing stays crisp.
            draw.AddLine(P(0.27f, 0.08f), P(0.73f, 0.08f), color, stroke);
            draw.AddBezierCubic(P(0.73f, 0.08f), P(1.04f, 0.08f), P(1.12f, 0.96f), P(0.77f, 0.96f), color, stroke);
            draw.AddBezierCubic(P(0.77f, 0.96f), P(0.68f, 0.98f), P(0.64f, 0.89f), P(0.60f, 0.86f), color, stroke);
            draw.AddLine(P(0.60f, 0.86f), P(0.40f, 0.86f), color, stroke);
            draw.AddBezierCubic(P(0.40f, 0.86f), P(0.36f, 0.89f), P(0.32f, 0.98f), P(0.23f, 0.96f), color, stroke);
            draw.AddBezierCubic(P(0.23f, 0.96f), P(-0.12f, 0.96f), P(-0.04f, 0.08f), P(0.27f, 0.08f), color, stroke);
            var dpad = P(0.27f, 0.49f);
            var arm = iconWidth * 0.09f;
            var half = arm * 0.32f;
            Vector2[] cross = [new(-half, -arm), new(half, -arm), new(half, -half),
                new(arm, -half), new(arm, half), new(half, half), new(half, arm),
                new(-half, arm), new(-half, half), new(-arm, half), new(-arm, -half), new(-half, -half)];
            for (var i = 0; i < cross.Length; i++)
                draw.AddLine(dpad + cross[i], dpad + cross[(i + 1) % cross.Length], color, stroke);
            foreach (var button in new[] { P(0.74f, 0.30f), P(0.64f, 0.49f), P(0.84f, 0.49f), P(0.74f, 0.68f) })
                draw.AddCircle(button, iconWidth * 0.035f, color, 20, stroke);
        }
        else if (audio)
        {
            for (var i = 0; i < 13; i++)
            {
                var h = iconSize.Y * (0.20f + 0.65f * MathF.Abs(MathF.Sin(i * 0.8f)));
                var p = P(0.05f + i * 0.075f, 0.5f);
                draw.AddLine(p - new Vector2(0f, h * 0.5f), p + new Vector2(0f, h * 0.5f), color, stroke);
            }
        }
        else if (broadcast)
        {
            var c = P(0.5f, 0.5f);
            draw.AddCircle(c, iconSize.Y * 0.43f, color, 40, stroke);
            draw.AddCircle(c, iconSize.Y * 0.24f, color, 32, stroke);
            draw.AddCircleFilled(c, stroke * 1.5f, color);
        }
        else
        {
            draw.AddRect(P(0.05f, 0.04f), P(0.95f, 0.96f), color, stroke * 2f, ImDrawFlags.None, stroke);
            if (browser)
            {
                draw.AddLine(P(0.05f, 0.24f), P(0.95f, 0.24f), color, stroke);
                for (var i = 0; i < 3; i++) draw.AddCircleFilled(P(0.14f + i * 0.08f, 0.14f), stroke * 0.75f, color);
            }
            else
            {
                draw.AddLine(P(0.43f, 0.30f), P(0.66f, 0.50f), color, stroke);
                draw.AddLine(P(0.66f, 0.50f), P(0.43f, 0.70f), color, stroke);
                draw.AddLine(P(0.43f, 0.70f), P(0.43f, 0.30f), color, stroke);
            }
        }
        if (!compact)
        {
            var textSize = ImGui.CalcTextSize(label);
            draw.AddText(new Vector2(center.X - textSize.X * 0.5f, center.Y + size.Y * 0.19f),
                ImGui.GetColorU32(Vector4.Lerp(MutedText, highlight, 0.45f)), label);
        }
        draw.AddRect(origin, origin + size, ImGui.GetColorU32(Ink(0.28f)), Ui(9f));
        draw.PopClipRect();
    }

    private static string PartyFallbackGameLabel(string title)
    {
        // Use this entry's metadata, never the viewer's locally selected emulator.
        foreach (var (name, label) in new[] { ("SNES", "SNES"), ("Super Nintendo", "SNES"),
            ("Game Boy Advance", "GAME BOY ADVANCE"), ("GBA", "GAME BOY ADVANCE"),
            ("Game Boy", "GAME BOY"), ("GameBoy", "GAME BOY"), ("Master System", "MASTER SYSTEM"),
            ("Game Gear", "GAME GEAR"), ("NES", "NES") })
            if (title.Contains(name, StringComparison.OrdinalIgnoreCase)) return label;
        return "GAMEPLAY";
    }

    private static ActiveActivityKind PartyEntryCanvasActivity(string url, string title)
    {
        var activity = ActivityFromCanvasUrl(url);
        if (activity != ActiveActivityKind.Whiteboard) return activity;
        // Older canvas URLs have no activity suffix; their published title
        // still identifies the activity for viewers and thumbnail fallbacks.
        foreach (var candidate in Enum.GetValues<ActiveActivityKind>())
            if (candidate is not (ActiveActivityKind.None or ActiveActivityKind.Whiteboard) &&
                title.Equals(ActivityTitle(candidate), StringComparison.OrdinalIgnoreCase))
                return candidate;
        return activity;
    }
}
