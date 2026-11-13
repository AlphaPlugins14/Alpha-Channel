namespace AlphaChannel.Plugin.Video;

internal static class TvUiRendererReentry
{
    [ThreadStatic] private static int depth;
    internal static Scope Enter() => new(++depth == 1);
    internal readonly struct Scope(bool outermost) : IDisposable
    {
        internal bool Outermost => outermost;
        public void Dispose() => depth--;
    }
}
