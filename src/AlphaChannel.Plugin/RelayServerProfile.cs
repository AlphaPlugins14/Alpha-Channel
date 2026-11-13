using AlphaChannel.Plugin.Crypto;

namespace AlphaChannel.Plugin;

internal static class RelayServerProfile
{
    // Debug and Release both use the replacement server and its separate vault.
    // To switch back, use Configuration.ProductionRelayServerUrl and a null
    // VaultProfile together; the original server's encrypted vault is preserved.
    internal const string ServerUrl = "https://" + RelayServerOrigins.CurrentHost;
    internal const string VaultProfile = "alphachannel14";

    internal static LocalSecretVault OpenVault(Configuration configuration, string configDirectory)
    {
        // Never import the original server's sessions into the replacement.
        // Preserve any legacy plaintext credentials in their original vault
        // before the next configuration save removes those plaintext fields.
        if (configuration.CharacterSessions.Count > 0 || configuration.StreamKeys.Count > 0)
        {
            var originalVault = new LocalSecretVault(configDirectory);
            if (!originalVault.Restore(configuration))
                throw new InvalidOperationException("Cannot preserve original server credentials; refusing to switch servers.");
            if (!originalVault.Save(configuration))
                throw new InvalidOperationException("Cannot save original server credentials; refusing to switch servers.");
        }
        configuration.CharacterSessions.Clear();
        configuration.StreamKeys.Clear();
        return new LocalSecretVault(configDirectory, VaultProfile);
    }
}
