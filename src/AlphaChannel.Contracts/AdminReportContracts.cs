namespace AlphaChannel.Contracts;

public sealed record AdminReportDto(
    string Id,
    string ReporterAccountId,
    string ReporterHandle,
    string TargetAccountId,
    string TargetHandle,
    string Reason,
    string? Details,
    string? RevealedBody,
    bool? FrankingVerified,
    string Status,
    long CreatedAtUnix);

public enum AdminReportAction
{
    Dismiss,
    Warn,
    Suspend,
    Ban,
}

public sealed record ResolveReportRequest(AdminReportAction Action, string? Note, long? SuspendUntilUnix);

public sealed record BanAccountRequest(string Reason, long? UntilUnix);
