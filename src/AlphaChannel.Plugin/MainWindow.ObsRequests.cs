using AlphaChannel.Plugin.Auth;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private int obsRequestGeneration;
    private int liveStatusRequestGeneration;

    private void ResetObsRequests()
    {
        obsRequestGeneration++;
        liveStatusRequestGeneration++;
        obsConnectionChecking = obsConnectionOnline = false;
        obsConnectionError = null;
        liveStatusDirty = true;
        liveStatusLoading = false;
        liveStatus = null;
    }

    private async Task CheckObsConnectionAsync(CharacterSession session)
    {
        var account = CaptureAccountRequest(session);
        var generation = ++obsRequestGeneration;
        obsConnectionChecking = true;
        obsConnectionOnline = false;
        obsConnectionError = null;
        bool Current() => IsCurrentAccountRequest(account) && generation == obsRequestGeneration;
        try
        {
            var hlsUrl = BuildMyHlsUrl(session);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var http = Net.PluginHttpClients.CreateProbeClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, hlsUrl);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (!Current()) return;
                obsConnectionOnline = response.IsSuccessStatusCode;
                obsConnectionError = response.IsSuccessStatusCode ? null
                    : "No stream detected yet. Start streaming in your app, then check again.";
            }).ConfigureAwait(false);
        }
        catch (Exception)
        {
            
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (!Current()) return;
                obsConnectionOnline = false;
                obsConnectionError = "No stream detected yet. Start streaming in your app, then check again.";
            }).ConfigureAwait(false);
        }
        finally
        {
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (Current()) obsConnectionChecking = false;
            }).ConfigureAwait(false);
        }
    }

    private async Task RefreshLiveStatusAsync(CharacterSession session)
    {
        var account = CaptureAccountRequest(session);
        var generation = ++liveStatusRequestGeneration;
        liveStatusDirty = false;
        liveStatusLoading = true;
        bool Current() => IsCurrentAccountRequest(account) && generation == liveStatusRequestGeneration;
        try
        {
            var status = await liveClient.GetMyStatusAsync(session.Token).ConfigureAwait(false);
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (Current()) liveStatus = status;
            }).ConfigureAwait(false);
        }
        catch (Exception)
        {
            AepLog.Warning("[GoLive] Live status refresh failed.");
        }
        finally
        {
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (Current()) liveStatusLoading = false;
            }).ConfigureAwait(false);
        }
    }
}
