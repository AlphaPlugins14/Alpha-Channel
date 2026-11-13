namespace AlphaChannel.Plugin.Video;

// A removed game-owned device cannot be repaired by creating another plugin
// texture. Latch the failure until reinitialization instead of submitting more
// frames to it. Retain the HRESULT for diagnosis, not just the outer CLR code.
internal sealed class GraphicsDeviceFailure
{
    internal enum Source { HealthCheck, RenderThreadWork, RenderThreadCallback, OnPresentSubscriber, OriginalPresentHookChain, PresentResult, ResizeResult }

    // Only fixed operation labels and numeric codes enter the diagnostic. Never
    // accept callback keys, exception messages, media URLs or filesystem paths.
    internal static string Diagnostic(Source source, int result, int? removedReason)
    {
        var operation = source switch
        {
            Source.HealthCheck => "device health check",
            Source.RenderThreadWork => "render-thread work",
            Source.RenderThreadCallback => "render-thread callback",
            Source.OnPresentSubscriber => "OnPresent subscriber",
            Source.OriginalPresentHookChain => "original Present/hook chain",
            Source.PresentResult => "Present result",
            Source.ResizeResult => "ResizeBuffers result",
            _ => "unknown operation"
        };
        var reason = removedReason is { } code ? $"0x{unchecked((uint)code):X8} ({CodeName(code)})" : "unavailable";
        return $"[DxHandler] Graphics device failure in {operation}: HRESULT=0x{unchecked((uint)result):X8} ({CodeName(result)}), " +
            $"removedReason={reason}. " +
            "Alpha Channel drawing/uploads stopped; render-thread cleanup remains available. The game owns device recovery.";
    }

    private static string CodeName(int result) => unchecked((uint)result) switch
    {
        0 => "S_OK",
        0x887A0005 => "DXGI_ERROR_DEVICE_REMOVED",
        0x887A0006 => "DXGI_ERROR_DEVICE_HUNG",
        0x887A0007 => "DXGI_ERROR_DEVICE_RESET",
        0x887A0020 => "DXGI_ERROR_DRIVER_INTERNAL_ERROR",
        _ => "unclassified"
    };

    private int code;
    internal bool Failed => Volatile.Read(ref code) != 0;
    internal bool TryMark(int result) => result < 0 && Interlocked.CompareExchange(ref code, result, 0) == 0;
    internal void Reset() => Volatile.Write(ref code, 0);
    internal static bool IsDeviceError(int result) => unchecked((uint)result) is
        0x887A0005 or 0x887A0006 or 0x887A0007 or 0x887A0020;
}
