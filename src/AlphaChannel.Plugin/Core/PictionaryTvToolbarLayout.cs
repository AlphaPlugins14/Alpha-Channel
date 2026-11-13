namespace AlphaChannel.Plugin;

// Render and hit testing use the same pixel rectangles at every TV scale.
internal static class PictionaryTvToolbarLayout
{
    internal readonly record struct Rect(int X, int Y, int Width, int Height)
    {
        internal bool Contains(float x, float y) => x >= X && x <= X + Width && y >= Y && y <= Y + Height;
    }

    internal static Rect Colour(int index) => new(102 + index * 42, 630, 32, 32);
    internal static Rect Brush(int index) => new(890 + index * 66, 620, 44, 50);
}
