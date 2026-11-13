using System;
using System.Runtime.CompilerServices;

namespace AlphaChannel.Plugin;

internal static class YouTubeDependencyBootstrap
{
    // YoutubeExplode's bundled region guard supports this opt-out. Run before
    // plugin code can load YouTube types; never change the user's Windows region.
    [ModuleInitializer]
    internal static void Initialize()
    {
        Environment.SetEnvironmentVariable("SLAVA_UKRAINI", "1", EnvironmentVariableTarget.Process);
    }
}
