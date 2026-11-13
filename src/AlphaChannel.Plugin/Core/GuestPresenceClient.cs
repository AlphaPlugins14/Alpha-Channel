using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AlphaChannel.Plugin.Net;

namespace AlphaChannel.Plugin;

// Tick runs on the framework thread even while every plugin window is closed.
// Anonymous sessions renew while logged into the game; network work never blocks it.
internal sealed class GuestPresenceClient : IDisposable
{
    internal const string DefaultUrl = "https://guests.example.invalid";
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(40);
    private readonly Configuration configuration;
    private readonly HttpClient client;
    private readonly CancellationTokenSource lifetime = new();
    private Task<Reply?>? pending;
    private string? pendingMethod;
    private DateTime nextCountUtc;
    private DateTime launchCheckUtc;
    private bool pollingStarted;
    private int? signedInCount;
    private DateTime signedInCountUpdatedUtc;
    private DateTime nextAttemptUtc;
    private DateTime countReceivedUtc;
    private bool? eligible;
    private bool removeRequired;
    private int guestCount;
    private sealed record Reply(int GuestCount, int? RetryAfterSeconds = null,
        int? SignedInCount = null, long? SignedInUpdatedAt = null);

    internal GuestPresenceClient(Configuration configuration, HttpClient? client = null)
    {
        this.configuration = configuration;
        this.client = client ?? PluginHttpClients.CreateMetadataClient();
        this.client.Timeout = TimeSpan.FromSeconds(10);
    }

    internal int GuestCount(DateTime now) => now - countReceivedUtc <= TimeSpan.FromHours(1) ? guestCount : 0;


    internal int SignedInCount(DateTime now) => now - signedInCountUpdatedUtc < TimeSpan.FromHours(1)
        ? Math.Max(0, signedInCount ?? 0) : 0;

    internal void Tick(DateTime now, bool gameLoggedIn, bool signedIn, int? realtimeSignedInCount = null)
    {
        if (!pollingStarted)
        {
            pollingStarted = true;
            nextCountUtc = now;
            launchCheckUtc = now.AddMinutes(1);
        }
        var canRegister = gameLoggedIn && !signedIn;
        if (eligible != canRegister)
        {
            eligible = canRegister;
            // The identifier persists forever; it alone does not mean a session exists.
            // Older configs only stored the 40-minute renewal deadline: its remaining
            // session lifetime is at most another 20 minutes beyond that deadline.
            var expires = configuration.GuestPresenceSessionExpiresUtc;
            if (expires == DateTime.MinValue && configuration.GuestPresenceNextUpdateUtc != DateTime.MinValue)
                expires = configuration.GuestPresenceNextUpdateUtc.AddMinutes(20);
            if (!canRegister && !string.IsNullOrEmpty(configuration.GuestPresenceInstallationId) &&
                (pendingMethod == "POST" || expires > now))
                removeRequired = true;
        }
        if (pending is { IsCompleted: true })
        {
            var reply = pending.GetAwaiter().GetResult();
            if (reply is not null)
            {
                guestCount = Math.Max(0, reply.GuestCount);
                countReceivedUtc = now;
                signedInCount = reply.SignedInCount;
                signedInCountUpdatedUtc = reply.SignedInUpdatedAt is >= 0 and <= 253402300799
                    ? DateTimeOffset.FromUnixTimeSeconds(reply.SignedInUpdatedAt.Value).UtcDateTime
                    : DateTime.MinValue;
                if (pendingMethod == "POST")
                {
                    configuration.GuestPresenceSessionExpiresUtc = now.AddHours(1);
                    configuration.GuestPresenceNextUpdateUtc = now.AddSeconds(
                        Math.Clamp(reply.RetryAfterSeconds ?? (int)Cooldown.TotalSeconds, 1, 2400));
                    configuration.Save();
                }
                else if (pendingMethod == "DELETE")
                {
                    removeRequired = false;
                    configuration.GuestPresenceNextUpdateUtc = DateTime.MinValue;
                    configuration.GuestPresenceSessionExpiresUtc = DateTime.MinValue;
                    configuration.Save();
                }
            }
            else nextAttemptUtc = now.AddMinutes(1);
            pending = null;
            pendingMethod = null;
        }
        if (pending is not null || now < nextAttemptUtc)
            return;
        if (removeRequired)
        {
            Start("DELETE");
            return;
        }
        if (canRegister && now >= configuration.GuestPresenceNextUpdateUtc)
        {
            if (string.IsNullOrEmpty(configuration.GuestPresenceInstallationId))
            {
                configuration.GuestPresenceInstallationId = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
                configuration.Save();
            }
            // Persist before sending: a timed-out request may still create a record.
            configuration.GuestPresenceSessionExpiresUtc = now.AddHours(1);
            configuration.Save();
            Start("POST");
        }
        else if (gameLoggedIn && (now >= nextCountUtc || now >= launchCheckUtc))
        {
            if (now >= nextCountUtc) nextCountUtc = now.AddMinutes(20);
            if (now >= launchCheckUtc) launchCheckUtc = DateTime.MaxValue;
            Start("GET", signedIn ? realtimeSignedInCount : null);
        }
    }

    private void Start(string method, int? reportedSignedInCount = null)
    {
        pendingMethod = method;
        var url = (string.IsNullOrWhiteSpace(configuration.GuestPresenceWorkerUrl) ? DefaultUrl : configuration.GuestPresenceWorkerUrl).TrimEnd('/') + (method == "GET" ? "/count" : "/session");
        if (method == "GET" && reportedSignedInCount is >= 0)
            url += "?signedInCount=" + reportedSignedInCount.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var id = configuration.GuestPresenceInstallationId;
        pending = Task.Run(async () =>
        {
            try
            {
                using var request = new HttpRequestMessage(new HttpMethod(method), url);
                if (method != "GET") request.Content = JsonContent.Create(new { installationId = id });
                // Never attach an XIVAuth token, character identity or account identifier.
                using var response = await client.SendAsync(request, lifetime.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<Reply>(
                    new JsonSerializerOptions(JsonSerializerDefaults.Web), lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return null; }
            catch (Exception)
            {
                
                return null;
            }
        });
    }

    public void Dispose()
    {
        lifetime.Cancel();
        client.Dispose();
        // Disposal cancels requests; existing sessions naturally expire after an hour.
    }
}
