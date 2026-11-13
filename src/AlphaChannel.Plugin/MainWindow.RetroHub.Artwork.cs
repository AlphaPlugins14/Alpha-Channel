using Dalamud.Bindings.ImGui;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    // All artwork uses draw-list primitives. No ROM artwork, texture downloads,
    // or per-title image lookup is required for an imported game.
    private static void RetroHubRect(ImDrawListPtr draw, Vector2 origin, float pixel,
        float x, float y, float width, float height, uint color) =>
        draw.AddRectFilled(origin + new Vector2(x, y) * pixel,
            origin + new Vector2(x + width, y + height) * pixel, color);

    private static void DrawRetroHubCartridge(ImDrawListPtr draw, Vector2 origin, float pixel)
    {
        void R(float x, float y, float w, float h, int color) => RetroHubRect(draw, origin, pixel, x, y, w, h, ImGui.GetColorU32(Hex(color)));
        R(0, 4, 58, 46, 0x29374D); R(4, 0, 50, 52, 0x77869E);
        R(8, 4, 42, 44, 0x9EABC0); R(14, 5, 30, 33, 0x172943);
        R(17, 8, 24, 24, 0x347DF0); R(17, 8, 24, 4, 0x62B3FF);
        R(14, 39, 30, 13, 0x1A2536);
        for (var x = 17; x < 43; x += 6) R(x, 41, 3, 9, 0xE7BE53);
    }

    private static void DrawRetroHubConsole(ImDrawListPtr draw, Vector2 origin, float pixel, GameSystem system)
    {
        void R(float x, float y, float w, float h, int color) => RetroHubRect(draw, origin, pixel, x, y, w, h, ImGui.GetColorU32(Hex(color)));
        void Dot(float x, float y, int color) => draw.AddCircleFilled(origin + new Vector2(x, y) * pixel,
            3 * pixel, ImGui.GetColorU32(Hex(color)), 8);
        void Pad(float x, float y)
        { R(x + 4, y, 5, 16, 0x24334A); R(x, y + 5, 13, 5, 0x24334A); }
        switch (system)
        {
            case GameSystem.Snes:
                R(5, 8, 62, 28, 0x8A98B2); R(0, 13, 72, 18, 0x9DABC2);
                R(5, 12, 62, 20, 0xB5C0D3); Pad(10, 14);
                R(30, 23, 6, 3, 0x586780); R(39, 23, 6, 3, 0x586780);
                Dot(57, 15, 0x4BA5F3); Dot(64, 22, 0xE5B94D); Dot(57, 29, 0x58C390); Dot(50, 22, 0xE96B8D);
                break;
            case GameSystem.GameBoy:
                R(19, 0, 34, 48, 0x8394AB); R(21, 2, 30, 44, 0xBCC7D6);
                R(24, 5, 24, 22, 0x344653); R(28, 8, 16, 15, 0x9DB95E);
                Pad(25, 30); Dot(45, 33, 0x944A77); Dot(40, 38, 0x944A77);
                R(39, 43, 10, 2, 0x6D7C92);
                break;
            case GameSystem.MasterSystem:
                R(1, 9, 70, 31, 0x26354C); R(5, 12, 62, 25, 0x111C2E);
                R(5, 13, 62, 6, 0xCF4D59); R(29, 22, 31, 3, 0x60718A);
                R(9, 29, 9, 4, 0xA9B6C9); R(23, 29, 9, 4, 0x64758C); Dot(62, 32, 0xE45F64);
                break;
            case GameSystem.Nes:
                R(2, 10, 68, 31, 0xBBC3D1); R(6, 14, 60, 23, 0x29364B);
                Pad(13, 18); R(32, 17, 12, 10, 0x606B7E);
                R(32, 30, 5, 3, 0xA6AEC0); R(40, 30, 5, 3, 0xA6AEC0);
                Dot(53, 29, 0xD34A55); Dot(62, 29, 0xD34A55);
                break;
            case GameSystem.GameBoyAdvance:
                R(8, 7, 55, 34, 0x7055BC); R(1, 13, 69, 22, 0x8C6ED5);
                R(21, 11, 30, 27, 0x33456D); R(25, 15, 22, 18, 0x4BA3E9);
                Pad(5, 16); Dot(59, 20, 0xC3ACEB); Dot(65, 27, 0xC3ACEB);
                break;
            case GameSystem.GameGear:
                R(8, 3, 56, 42, 0x303F55); R(1, 11, 70, 27, 0x44546C);
                R(22, 7, 28, 32, 0x152539); R(26, 12, 20, 22, 0x3188CE);
                Pad(5, 16); Dot(59, 19, 0xA3B5C9); Dot(65, 27, 0xA3B5C9);
                R(53, 37, 10, 2, 0x1D2B40);
                break;
        }
    }

    private static void DrawRetroHubSetup(ImDrawListPtr draw, Vector2 origin, float pixel)
    {
        void R(float x, float y, float w, float h, int color) => RetroHubRect(draw, origin, pixel, x, y, w, h, ImGui.GetColorU32(Hex(color)));
        R(38, 7, 111, 81, 0x111D31); R(31, 0, 111, 87, 0x3C506E);
        R(35, 4, 103, 79, 0x6B7E9B); R(40, 9, 93, 65, 0x14263F);
        R(44, 13, 84, 56, 0x1C6DB4); R(48, 17, 76, 48, 0x2588CC);
        R(48, 17, 76, 8, 0x329ADE); R(148, 20, 8, 56, 0x1B2A40);
        for (var y = 29; y < 71; y += 8) R(145, y, 8, 3, 0x0C182B);
        R(73, 87, 30, 9, 0x415775); R(62, 94, 52, 5, 0x657C9C);
        R(53, 103, 118, 29, 0x485B77); R(57, 100, 110, 25, 0xA0ADC2);
        R(62, 105, 99, 16, 0x7C8DA7); R(74, 110, 47, 4, 0x334964);
        R(148, 111, 5, 5, 0x55C29B); R(133, 111, 9, 4, 0xD5B454);
        R(27, 133, 153, 4, 0x12243C);
        // Generic square stars on the screen, deliberately not a game screenshot.
        R(58, 28, 5, 5, 0xD8F0FF); R(103, 31, 6, 4, 0xD8F0FF);
        R(81, 43, 3, 3, 0xB8E5FF); R(62, 55, 3, 3, 0xB8E5FF); R(112, 54, 4, 4, 0xD8F0FF);
        draw.AddLine(origin + new Vector2(74, 115) * pixel, origin + new Vector2(55, 126) * pixel,
            ImGui.GetColorU32(Hex(0x758AA6)), pixel * 2);
        draw.AddLine(origin + new Vector2(55, 126) * pixel, origin + new Vector2(20, 122) * pixel,
            ImGui.GetColorU32(Hex(0x758AA6)), pixel * 2);
        DrawRetroHubConsole(draw, origin + new Vector2(-3, 100) * pixel, pixel * .7f, GameSystem.Snes);
        DrawRetroHubCartridge(draw, origin + new Vector2(173, 109) * pixel, pixel * .42f);
        foreach (var point in new[] { new Vector2(13, 29), new Vector2(174, 43), new Vector2(187, 75) })
        {
            R(point.X - 1, point.Y - 4, 3, 9, 0xE9C158);
            R(point.X - 4, point.Y - 1, 9, 3, 0xE9C158);
        }
    }

    private static void DrawRetroHubPixelLabel(ImDrawListPtr draw, Vector2 origin)
    {
        var pixel = Ui(1.4f);
        var ink = ImGui.GetColorU32(Gold);
        foreach (var letter in "PRESS START")
        {
            var glyph = letter switch
            {
                'P' => "11110100011000111110100001000010000",
                'R' => "11110100011000111110101001001010001",
                'E' => "11111100001000011110100001000011111",
                'S' => "01111100001000001110000010000111110",
                'T' => "11111001000010000100001000010000100",
                'A' => "01110100011000111111100011000110001",
                _ => "00000000000000000000000000000000000"
            };
            for (var i = 0; i < glyph.Length; i++)
                if (glyph[i] == '1') RetroHubRect(draw, origin, pixel, i % 5, i / 5, 1, 1, ink);
            origin.X += pixel * 6;
        }
    }
}
