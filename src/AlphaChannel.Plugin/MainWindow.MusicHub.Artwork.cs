using Dalamud.Bindings.ImGui;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    // Static decorative decks, independent of the real audio meter. Every
    // element is a draw-list primitive, with no textures or external assets.
    private static void DrawMusicHubDecks(ImDrawListPtr draw, Vector2 origin, float scale)
    {
        uint Ink(int rgb) => ImGui.GetColorU32(Hex(rgb));
        Vector2 P(float x, float y) => origin + new Vector2(x, y) * scale;
        void R(float x, float y, float w, float h, int rgb) => draw.AddRectFilled(P(x, y), P(x + w, y + h), Ink(rgb));
        void L(float x, float y, float a, float b, int rgb, float thickness = 1) => draw.AddLine(P(x, y), P(a, b), Ink(rgb), thickness * scale);

        // Fixed blocks give the header a music identity without pretending to
        // show incoming audio or adding continuous decorative animation.
        ReadOnlySpan<int> levels = [3, 5, 8, 6, 10, 4, 7, 5, 9, 6, 4, 7, 3, 8, 5, 6];
        for (var i = 0; i < levels.Length; i++)
            for (var j = 0; j < levels[i]; j++)
                R(6 + i * 15, 68 - j * 6, 9, 4, i % 3 == 0 ? 0x8860DB : j > 5 ? 0x54D6ED : 0x277EC1);
        L(3, 125, 245, 125, 0x23436D, 2);
        for (var deck = 0; deck < 2; deck++)
        {
            var x = deck == 0 ? 5 : 160;
            R(x + 3, 69, 81, 54, 0x10213C);
            R(x, 65, 81, 54, 0x224579);
            R(x + 3, 68, 75, 47, 0x11223C);
            draw.AddRect(P(x, 65), P(x + 81, 119), Ink(0x4D85CF), scale * 2, ImDrawFlags.None, scale);
            var centre = P(x + 37, 91);
            draw.AddCircleFilled(centre, 24 * scale, Ink(0x070F20), 48);
            draw.AddCircle(centre, 24 * scale, Ink(0x8DA2C9), 48, scale);
            draw.AddCircle(centre, 20 * scale, Ink(0x21314F), 48, scale);
            draw.AddCircle(centre, 17 * scale, Ink(0x1B2944), 48, scale);
            draw.AddCircle(centre, 13 * scale, Ink(0x1B2944), 40, scale);
            draw.AddCircleFilled(centre, 7 * scale, Ink(0x48D4E8), 24);
            draw.AddCircleFilled(centre, 2 * scale, Ink(0x11243A), 12);
            L(x + 71, 74, x + 69, 94, 0xA8B9D4, 2);
            L(x + 69, 94, x + 59, 101, 0xA8B9D4, 2);
            R(x + 55, 99, 7, 4, 0x66B3DC);
            R(x + 7, 71, 6, 3, 0x79BCED);
            R(x + 7, 108, 6, 4, 0x54CDE6);
            R(x + 68, 108, 6, 4, 0x9468DB);
        }
        R(92, 65, 60, 56, 0x25487C);
        R(95, 68, 54, 50, 0x0E1D34);
        for (var i = 0; i < 3; i++)
        {
            var x = 104 + i * 17;
            draw.AddCircleFilled(P(x, 75), scale * 2.5f, Ink(0x7C71CC), 12);
            L(x, 83, x, 105, 0x6687B0, 2);
            R(x - 3, 87 + i * 5, 7, 4, 0x60C5EB);
            R(x - 3, 110, 7, 3, i == 1 ? 0x8F66D4 : 0x49CBE6);
        }
        L(110, 115, 134, 115, 0x3C679C);
    }
}
