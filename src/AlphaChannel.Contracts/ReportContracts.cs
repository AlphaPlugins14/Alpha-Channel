namespace AlphaChannel.Contracts;

// RevealedBody/FrankingKeyBase64 are only set for a DM-message report - see AlphaChannel.Server's
// DmMessage.CommitmentTag doc comment for the franking scheme this feeds into. The reporter's
// client decrypts locally and chooses to reveal both; the server verifies the reveal against the
// commitment tag it already stored at send time, so it can trust it without ever having decrypted
// the message on its own.
public sealed record SubmitReportRequest(
    string Category,
    string? Note,
    string? TargetAccountId,
    string? TargetMessageId,
    string? RevealedBody,
    string? FrankingKeyBase64);
