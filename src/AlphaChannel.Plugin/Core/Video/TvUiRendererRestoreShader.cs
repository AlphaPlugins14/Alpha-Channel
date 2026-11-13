namespace AlphaChannel.Plugin.Video;

internal static class TvUiRendererRestoreShader
{
    // Geometry uses the regular curved TV vertex shader. Deliberately no depth
    // test: restore the scene even where a foreground character hides the TV.
    internal const string Source = """
        Texture2D<float4> cleanScene : register(t0);
        float4 PS(float4 position : SV_POSITION) : SV_TARGET {
            return cleanScene.Load(int3(int2(position.xy), 0));
        }
        """;
}
