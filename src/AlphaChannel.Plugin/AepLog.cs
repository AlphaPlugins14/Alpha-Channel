namespace AlphaChannel.Plugin;

internal static class AepLog
{
    private static readonly ReleaseLogPolicy Policy = new();

    public static void CleanupFailure(ShutdownOwner owner,ShutdownResource resource,Exception error) =>
        Error(ShutdownDiagnostic.Format(owner,resource,error.HResult));

    public static void GraphicsFailure(Video.GraphicsDeviceFailure.Source source, int result, int? removedReason) =>
        Warning(Video.GraphicsDeviceFailure.Diagnostic(source, result, removedReason));

    public static void DirectXPointerHooksReady()
    {
        const string message = "[DxHandler] DirectX Present and ResizeBuffers pointer hooks initialized.";
        if (Policy.TryAccept(message, out var safeMessage)) Plugin.Log.Information(safeMessage);
    }

    public static void AllowanceStorage(BroadcastAllowance.AllowanceStorageOperation operation,
        BroadcastAllowance.AllowanceStorageFailure failure, int hresult)
    {
        var message = $"[Allowance] Storage operation={operation} failure={failure} HRESULT=0x{unchecked((uint)hresult):X8}.";
        if (Policy.TryAccept(message, out var safeMessage)) Plugin.Log.Warning(safeMessage);
    }

    public static void PlaybackAudio(Video.MpvAudioDiagnostic diagnostic)
    {
        if (diagnostic.Reason is Video.AudioDiagnosticReason.Baseline or Video.AudioDiagnosticReason.StateChanged)
        {
            if (Policy.TryAccept(diagnostic.Format(), out var safeMessage)) Plugin.Log.Debug(safeMessage);
        }
        else Warning(diagnostic.Format());
    }
    public static void PlaybackAttempt(Video.PlaybackAttemptDiagnostic diagnostic)
    {
        if (Policy.TryAccept(diagnostic.Format(), out var safeMessage)) Plugin.Log.Information(safeMessage);
    }
    public static void MpvWarning(Video.MpvWarningDiagnostic diagnostic) => Warning(diagnostic.Format());
    public static void PlaybackLive(Video.MpvLiveDiagnostic diagnostic)
    {
        if (Policy.TryAccept(diagnostic.Format(), out var safeMessage)) Plugin.Log.Information(safeMessage);
    }
    public static void YouTubeFetchFailure(YouTube.YouTubeFetchDiagnostic diagnostic) => Warning(diagnostic.Format());
    public static void YouTubeGuestMetadataConfirmed()
    {
        const string message = "[YouTube/Metadata] Guest metadata fetched successfully via YoutubeExplodeNoCookie.";
        if (Policy.TryAccept(message, out var safeMessage)) Plugin.Log.Information(safeMessage);
    }

    public static void Warning(string message)
    {
        if (Policy.TryAccept(message, out var safeMessage))
            Plugin.Log.Warning(safeMessage);
    }

    public static void Error(string message)
    {
        if (Policy.TryAccept(message, out var safeMessage))
            Plugin.Log.Error(safeMessage);
    }
}
