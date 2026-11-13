namespace AlphaChannel.Plugin.Video;

internal static class TvUiRendererMethods
{
    // ID3D11DeviceContext method order, including inherited IUnknown/DeviceChild slots.
    internal static readonly int[] Slots = [12, 13, 20, 21, 39, 40, 38, 58];
    internal static readonly string[] Names = ["DrawIndexed", "Draw", "DrawIndexedInstanced", "DrawInstanced", "DrawIndexedInstancedIndirect", "DrawInstancedIndirect", "DrawAuto", "ExecuteCommandList"];
}
