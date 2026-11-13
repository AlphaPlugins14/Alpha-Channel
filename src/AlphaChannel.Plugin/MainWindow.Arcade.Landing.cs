using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private static Vector4 ArcadeSecondaryText => Vector4.Lerp(MutedText, Vector4.One, .15f);
    private static Vector4 ArcadeCardBorder => HubTint(0x254A78);

    private void DrawArcadeGameCard(ActivityLibraryEntry activity, float width, float height, bool featured)
    {
        var origin = ImGui.GetCursorScreenPos();
        var size = new Vector2(width, height);
        using var id = ImRaii.PushId((featured ? "featured" : "library") + activity.Id);
        ImGui.Dummy(size);
        var hovered = ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(origin, origin + size);
        var kind = ArcadeActivityKind(activity.Id);
        var blocked = !activity.Enabled || kind != ActiveActivityKind.None && !CanLaunchActivity(kind);
        var active = kind != ActiveActivityKind.None && ActiveActivity == kind;
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + size, ImGui.GetColorU32(hovered ? HubSurface(0x1A2440) : HubSurface(0x121A30)), Ui(10f));
        draw.AddRect(origin, origin + size, ImGui.GetColorU32(active ? Good : hovered ? Accent : ArcadeCardBorder), Ui(10f));

        var previewSize = MathF.Min(Ui(featured ? 150f : 104f), width * (featured ? .38f : .28f));
        var previewHeight = previewSize;
        ImGui.SetCursorScreenPos(origin + UiVec(10f, 10f));
        Vector4? previewBackground = featured
            ? activity.Id == "poker" ? Hex(0x103D33) : Hex(0x36204F)
            : null;
        DrawActivityPreview("preview", activity.Preview, previewSize, previewHeight / Ui(1f),
            4f, featured ? 12f : 4f, false, previewBackground, insetBorder: false, gallery: true);
        var textOrigin = origin + new Vector2(previewSize + Ui(20f), Ui(12f));
        var textWidth = width - previewSize - Ui(30f);
        DrawArcadeCardTitle(draw, textOrigin, activity.Title, textWidth - Ui(26f));
        DrawArcadeFavouriteStar(activity, origin, width);
        if (featured)
        {
            using var labelFont = UiFonts.PushRelative(.90f, emphasis: true);
            var badge = origin + UiVec(10f, 8f);
            var badgeSize = ImGui.CalcTextSize("Featured") + UiVec(32f, 10f);
            draw.AddRectFilled(badge, badge + badgeSize, ImGui.GetColorU32(Gold), Ui(4f));
            UiFonts.DrawIcon(draw, Ui(12f), badge + UiVec(6f, 5f), ImGui.GetColorU32(Hex(0x182136)), FontAwesomeIcon.Star.ToIconString());
            draw.AddText(badge + UiVec(24f, 5f), ImGui.GetColorU32(Hex(0x182136)), "Featured");
        }
        var description = activity.Id switch
        {
            "pixels" => "Create one global canvas, one pixel at a time.",
            "whiteboard" => "Draw and create together on every TV.",
            "pictionary" => "Take turns drawing. Race to guess.",
            _ => activity.Description,
        };
        ArcadeDrawDescription(draw, description, textOrigin + UiVec(0f, 27f), textWidth, featured ? 3 : 2);
        var footerOffset = featured ? 78f : 44f;
        var footer = origin + new Vector2(textOrigin.X - origin.X, height - Ui(footerOffset));
        // Keep the player count, leaderboard and launch button on separate hit targets.
        DrawArcadePlayerLabel(draw, footer + UiVec(0f, 8f), activity.PlayerLabel, ImGui.GetFontSize(), .90f);
        var playWidth = featured ? textWidth : Ui(width >= Ui(370f) ? 80f : 70f);
        DrawArcadeCardTrophy(activity, origin, featured ? width : width - playWidth - Ui(10f), height, Ui(footerOffset - 1f));
        var playHeight = Ui(34f);
        ImGui.SetCursorScreenPos(origin + new Vector2(featured ? textOrigin.X - origin.X : width - playWidth - Ui(10f),
            height - Ui(44f)));
        using (UiDisabled.Scope(blocked))
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(7f)))
        using (ImRaii.PushColor(ImGuiCol.Button, Accent)
            .Push(ImGuiCol.ButtonHovered, AccentHover).Push(ImGuiCol.ButtonActive, AccentActive))
        {
            if (ImGui.Button(active ? "Return##launch" : "Play##launch", new Vector2(playWidth, playHeight)))
                OpenArcadeActivity(activity);
            if (blocked && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                UiFonts.SetTooltip("End the current activity to launch another");
        }
        ImGui.SetCursorScreenPos(origin + new Vector2(0f, height));
    }

    private void DrawArcadeNowPlaying()
    {
        var activity = ActivityLibrary.FirstOrDefault(entry =>
            ActiveActivity != ActiveActivityKind.None && ArcadeActivityKind(entry.Id) == ActiveActivity);
        if (activity is null) return;
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var size = new Vector2(width, Ui(52f));
        ImGui.Dummy(size);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + size, ImGui.GetColorU32(HubSurface(0x121A30)), Ui(8f));
        draw.AddRect(origin, origin + size, ImGui.GetColorU32(BorderSubtle), Ui(8f));
        draw.AddCircleFilled(origin + UiVec(17f, 26f), Ui(4f), ImGui.GetColorU32(Good));
        draw.AddText(origin + UiVec(31f, 18f), ImGui.GetColorU32(Vector4.One),
            ArcadeFitText("Now playing: " + activity.Title, width - Ui(215f)));
        ImGui.SetCursorScreenPos(origin + new Vector2(width - Ui(166f), Ui(10f)));
        if (AlphaChatButton("arcadeReturn", "Return", FontAwesomeIcon.Play, Ui(78f), Ui(32f))) OpenArcadeActivity(activity);
        DrawArcadeEndButton(origin + new Vector2(width - Ui(78f), Ui(10f)), Ui(66f));
        ImGui.SetCursorScreenPos(origin + new Vector2(0f, size.Y));
        ImGui.Dummy(Vector2.Zero);
    }

    private static void DrawArcadePixelCabinet(ImDrawListPtr draw, Vector2 origin, float pixel)
    {
        // Fixed pixel shapes: no animation, textures or additional resources.
        void Rect(float x, float y, float w, float h, Vector4 colour) =>
            draw.AddRectFilled(origin + new Vector2(x, y) * pixel,
                origin + new Vector2(x + w, y + h) * pixel, ImGui.GetColorU32(colour));
        var blue = Hex(0x347FF3);
        var dark = Hex(0x0B1428);
        var gold = Hex(0xF4C75E);
        draw.AddQuadFilled(origin + new Vector2(78, 2) * pixel, origin + new Vector2(91, 12) * pixel,
            origin + new Vector2(91, 118) * pixel, origin + new Vector2(76, 118) * pixel,
            ImGui.GetColorU32(Hex(0x102B59)));
        Rect(78, 7, 6, 17, Hex(0x183C7E));
        Rect(8, 0, 70, 10, Hex(0x335BC1));
        Rect(4, 10, 78, 13, Hex(0x254595));
        Rect(8, 2, 66, 2, Hex(0x74ABFF));
        Rect(8, 20, 70, 3, dark);
        Rect(7, 9, 2, 10, gold);
        Rect(74, 9, 2, 10, gold);
        Rect(10, 23, 66, 56, dark);
        Rect(7, 23, 6, 50, Hex(0x214F99));
        Rect(73, 23, 6, 50, Hex(0x214F99));
        Rect(10, 24, 2, 48, gold);
        Rect(74, 24, 2, 48, gold);
        Rect(16, 28, 54, 41, Hex(0x162C4A));
        Rect(18, 30, 2, 35, Hex(0x28516F));
        Rect(0, 73, 86, 7, Hex(0x4E91F7));
        Rect(4, 79, 78, 14, blue);
        Rect(4, 90, 78, 3, Hex(0x153867));
        Rect(7, 78, 2, 12, gold);
        Rect(77, 78, 2, 12, gold);
        Rect(10, 93, 66, 20, Hex(0x1D4385));
        Rect(10, 111, 66, 7, Hex(0x214C96));
        Rect(13, 114, 60, 2, Hex(0x2D60AE));
        Rect(21, 73, 4, 10, gold);
        Rect(18, 71, 10, 5, Hex(0xFF657D));
        Rect(50, 84, 5, 4, Hex(0x5AE0AD));
        Rect(62, 84, 5, 4, gold);
        Rect(22, 96, 21, 14, dark);
        Rect(28, 99, 8, 2, gold);
        Rect(31, 104, 3, 3, Hex(0x5179AC));
        Rect(53, 98, 13, 2, Hex(0x153867));
        Rect(53, 103, 13, 2, Hex(0x153867));
        string[] alien = ["001000100", "000101000", "001111100", "011010110", "111111111", "101111101", "101000101", "000101000"];
        for (var y = 0; y < alien.Length; y++)
        for (var x = 0; x < alien[y].Length; x++)
            if (alien[y][x] == '1') Rect(25 + x * 4, 34 + y * 4, 4, 4, Hex(0x43C5FF));
        foreach (var (x, y, radius) in new[] { (-20f, 15f, 4f), (-30f, 33f, 6f), (-23f, 95f, 5f), (113f, 47f, 5f), (212f, 75f, 7f), (124f, 99f, 4f), (200f, 102f, 3f) })
            DrawArcadeStar(draw, origin + new Vector2(x, y) * pixel, radius * pixel, ImGui.GetColorU32(Gold));
        Rect(143, 80, 12, 18, gold);
        Rect(140, 84, 18, 10, gold);
        Rect(148, 84, 3, 10, Hex(0xFFF0AB));
    }

    private static void DrawArcadeStar(ImDrawListPtr draw, Vector2 centre, float radius, uint ink)
    {
        Vector2 Point(int index)
        {
            var angle = -MathF.PI / 2f + index * MathF.PI / 5f;
            return centre + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius * (index % 2 == 0 ? 1f : .43f);
        }
        for (var i = 0; i < 10; i++) draw.AddTriangleFilled(centre, Point(i), Point((i + 1) % 10), ink);
    }

    private static void DrawArcadeGradientPanel(ImDrawListPtr draw, Vector2 origin, Vector2 size,
        Vector4 top, Vector4 bottom, Vector4 border, float rounding)
    {
        var radius = Ui(rounding);
        var rows = Math.Max(1, (int)MathF.Ceiling(size.Y / Ui(2f)));
        for (var i = 0; i < rows; i++)
        {
            var y = size.Y * i / rows;
            var nextY = size.Y * (i + 1) / rows;
            var edgeDistance = MathF.Min(y, size.Y - nextY);
            var inset = edgeDistance < radius
                ? radius - MathF.Sqrt(MathF.Max(0f, radius * radius - MathF.Pow(radius - edgeDistance, 2f))) : 0f;
            var first = Vector4.Lerp(top, bottom, y / size.Y);
            var last = Vector4.Lerp(top, bottom, nextY / size.Y);
            draw.AddRectFilledMultiColor(origin + new Vector2(inset, y), origin + new Vector2(size.X - inset, nextY),
                ImGui.GetColorU32(first), ImGui.GetColorU32(Vector4.Lerp(first, bottom, .35f)),
                ImGui.GetColorU32(Vector4.Lerp(last, bottom, .35f)), ImGui.GetColorU32(last));
        }
        draw.AddRect(origin, origin + size, ImGui.GetColorU32(border), radius, ImDrawFlags.None, Ui(1f));
    }

    private bool DrawArcadePartyButton(float width)
    {
        var origin = ImGui.GetCursorScreenPos();
        var size = new Vector2(width, Ui(34f));
        var clicked = ActivePartyButton("##arcadeEmptyRoom", size);
        var hovered = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();
        DrawArcadeGradientPanel(draw, origin, size, hovered ? HubSelected(0x28549A) : HubSelected(0x1C3D73), HubSelected(0x10213F), HubTint(0x398CF3), 6f);
        var label = "Start a party";
        using (UiFonts.PushRegularRelative(1f))
            draw.AddText(origin + (size - ImGui.CalcTextSize(label)) * .5f, ImGui.GetColorU32(Vector4.One), label);
        return clicked;
    }

    private static void DrawArcadePixelLabel(ImDrawListPtr draw, Vector2 origin)
    {
        // A small 5x7 bitmap alphabet keeps the arcade label crisp without a font download.
        var pixel = Ui(1.5f);
        var ink = ImGui.GetColorU32(Gold);
        foreach (var letter in "SELECT YOUR GAME")
        {
            var glyph = letter switch
            {
                'S' => "01111100001000001110000010000111110",
                'E' => "11111100001000011110100001000011111",
                'L' => "10000100001000010000100001000011111",
                'C' => "01111100001000010000100001000001111",
                'T' => "11111001000010000100001000010000100",
                'Y' => "10001100010101000100001000010000100",
                'O' => "01110100011000110001100011000101110",
                'U' => "10001100011000110001100011000101110",
                'R' => "11110100011000111110101001001010001",
                'G' => "01111100001000010111100011000101111",
                'A' => "01110100011000111111100011000110001",
                'M' => "10001110111010110101100011000110001",
                _ => "",
            };
            for (var i = 0; i < glyph.Length; i++)
            {
                if (glyph[i] != '1') continue;
                var min = origin + new Vector2(i % 5, i / 5) * pixel;
                draw.AddRectFilled(min, min + new Vector2(pixel), ink);
            }
            origin.X += pixel * (letter == ' ' ? 4f : 6f);
        }
    }
}
