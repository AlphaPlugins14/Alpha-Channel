namespace AlphaChannel.Plugin.Crypto;

internal readonly record struct DmEncryptionStatus(
    bool? KeysReady, bool Checking, int UnreadableMessages, bool RepairAttempted, string? KeyError)
{
    internal bool HasKeyError => KeysReady == false;
    internal bool CanRetry => !Checking && (HasKeyError || UnreadableMessages > 0);
    internal string Title => Checking ? "Checking encryption…"
        : HasKeyError ? "Encryption key unavailable — click to repair"
        : KeysReady == true ? "End-to-end encrypted" : "Checking encryption…";

    internal string? Details => Checking ? null
        : HasKeyError ? KeyError ?? "Your encryption keys could not be verified. Click the bar to retry."
        : KeysReady != true ? null
        : UnreadableMessages > 0 ? RepairAttempted
            ? $"Keys verified, but {UnreadableMessages} saved message(s) still could not be decrypted. Retry could not recover them with the available keys. New messages can be sent securely."
            : $"{UnreadableMessages} saved message(s) could not be decrypted. Click the bar to retry them; your current encryption keys are valid."
        : RepairAttempted ? "Encryption verified. No unreadable messages remain." : null;
}
