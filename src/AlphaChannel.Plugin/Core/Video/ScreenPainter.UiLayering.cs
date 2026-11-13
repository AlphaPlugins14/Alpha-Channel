using SharpDX.Direct3D11;
using SharpDX.D3DCompiler;

namespace AlphaChannel.Plugin.Video;

internal sealed unsafe partial class ScreenPainter
{
    private bool boundaryDraw;
    private long boundaryDrawSerial;
    private PixelShader? sceneRestoreShader;
    private ShaderResourceView? restoredScene;
    private nint boundaryTargetView;

    private void ReleaseUiBoundaryTargets()
    {
        lock (frameLifetime.SyncRoot)
        {
            _cachedRtv?.Dispose(); _cachedRtv = null; _cachedRtvPtr = 0;
            _cachedDsv?.Dispose(); _cachedDsv = null; _cachedDsvPtr = 0;
            _sceneDepthSrv?.Dispose(); _sceneDepthSrv = null; _sceneDepthSrvPtr = 0;
        }
    }

    private bool BoundaryAvailable() => _srv != null && !frameLifetime.IsDisposed &&
        Plugin.ClientState.IsLoggedIn && WorldScreenVisible?.Invoke() != false && VisualsSuspended?.Invoke() != true;

    private bool DrawAtUiBoundary(nint targetView, ShaderResourceView? restoreScene)
    {
        lock (frameLifetime.SyncRoot)
        {
            if (frameLifetime.IsDisposed) return false;
            if (restoreScene != null && sceneRestoreShader == null)
            {
                using var code = ShaderBytecode.Compile(TvUiRendererRestoreShader.Source, "PS", "ps_4_0");
                sceneRestoreShader = new PixelShader(DxHandler.Device, code);
            }
            var before = boundaryDrawSerial;
            boundaryDraw = true; boundaryTargetView = targetView; restoredScene = restoreScene;
            try { DrawTvFrame(); return boundaryDrawSerial != before; }
            finally { boundaryDraw = false; boundaryTargetView = 0; restoredScene = null; }
        }
    }
}
