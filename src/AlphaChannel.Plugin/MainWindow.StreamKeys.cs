using AlphaChannel.Plugin.Auth;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private int streamKeyRequestGeneration;
    private bool automaticStreamKeyChecked;
    private bool streamKeyExistsOnServer;
    private DateTime nextAutomaticStreamKeyAttemptUtc;

    private void ResetStreamKeyRequests()
    {
        streamKeyRequestGeneration++;
        automaticStreamKeyChecked = streamKeyExistsOnServer = false;
        keyRotating = keyRegenerateConfirmPending = streamKeyRevealed = false;
        keyError = null;
        nextAutomaticStreamKeyAttemptUtc = DateTime.MinValue;
    }

    // Runs on the framework thread, including while the main window is closed.
    internal void UpdateAutomaticStreamKey()
    {
        if (AccountBroadcastRestricted) return;
        if (Volatile.Read(ref disposeStarted) != 0 || SessionValidationInProgress ||
            CurrentSession is not { } session || Plugin.ReadLocalContentId() == 0 ||
            automaticStreamKeyChecked || keyRotating ||
            DateTime.UtcNow < nextAutomaticStreamKeyAttemptUtc ||
            !string.IsNullOrWhiteSpace(Plugin.Cfg.StreamKeys.GetValueOrDefault(session.AccountId))) return;

        _ = EnsureStreamKeyAsync(session);
    }

    private async Task EnsureStreamKeyAsync(CharacterSession session)
    {
        if (keyRotating) return;
        var account = CaptureAccountRequest(session);
        var generation = ++streamKeyRequestGeneration;
        bool Current() => IsCurrentAccountRequest(account) && generation == streamKeyRequestGeneration;
        keyRotating = true;
        keyError = null;
        nextAutomaticStreamKeyAttemptUtc = DateTime.UtcNow.AddMinutes(1);
        try
        {
            var result = await liveClient.EnsureKeyAsync(session.Token).ConfigureAwait(false);
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (!Current()) return;
                if (result is null)
                {
                    keyError = liveClient.LastFailure?.UserMessage ?? "Couldn't prepare a stream key. Will retry automatically.";
                    return;
                }
                automaticStreamKeyChecked = true;
                streamKeyExistsOnServer = true;
                if (!string.IsNullOrWhiteSpace(result.StreamKey))
                {
                    Plugin.Cfg.StreamKeys[session.AccountId] = result.StreamKey;
                    Plugin.Cfg.Save();
                    liveStatusDirty = true;
                }
                // Existing server key without a local copy needs explicit regeneration.
            }).ConfigureAwait(false);
        }
        catch (Exception)
        {
            AepLog.Warning("[GoLive] Automatic stream key setup failed.");
        }
        finally
        {
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (Current()) keyRotating = false;
            }).ConfigureAwait(false);
        }
    }
}
