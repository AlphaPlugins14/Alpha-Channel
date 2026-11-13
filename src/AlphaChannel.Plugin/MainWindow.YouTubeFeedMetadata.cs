using AlphaChannel.Plugin.Video;
using AlphaChannel.Plugin.YouTube;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private readonly CancellationTokenSource youtubeFeedLifetime = new();
    // Capture once: background continuations can use a cancelled token after
    // the source is disposed, but cannot safely read source.Token at that point.
    private readonly CancellationToken youtubeFeedToken;

}
