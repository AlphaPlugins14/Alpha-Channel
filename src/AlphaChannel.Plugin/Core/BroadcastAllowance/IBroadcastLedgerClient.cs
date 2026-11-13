namespace AlphaChannel.Plugin.BroadcastAllowance;

internal sealed record BroadcastLedgerStatus(
    bool Allowed, string? Reason, string? SessionId, long? LeaseExpiresAt,
    long UsedMinimumUnits = 0, long LimitUnits = 0, long WeekStartAt = 0);

internal interface IBroadcastLedgerClient
{
    Task<BroadcastLedgerStatus> StartAsync(string token);
    Task<BroadcastLedgerStatus> RenewAsync(string token, string sessionId);
    Task StopAsync(string token, string sessionId);
    Task<BroadcastLedgerStatus> StatusAsync(string token, string hostId);
}
