using System.Security.Cryptography;
using AlphaChannel.Plugin.Auth;

namespace AlphaChannel.Plugin.Crypto;

// Owns the local DM identity keypair: generates one on first use, persists the private half in
// Configuration.DmPrivateKeys (DPAPI-protected on Windows), registers the public half with the server,
// and caches other accounts' public keys fetched during a session (their public keys aren't
// sensitive, no need to persist them locally - refetching is cheap and always current).
//
// Key material is handled by BouncyCastle (see DmCipher) so Wine/CNG's broken P-521 path isn't used.
internal sealed class KeyVault(Configuration configuration, KeysClient keysClient)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DmIdentity> identityCache = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DmPublicKey> otherPartyCache = new();
    private readonly SemaphoreSlim identityGate = new(1, 1);
    private readonly SemaphoreSlim registrationGate = new(1, 1);
    private readonly HashSet<string> registeredAccounts = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> registrationErrors = new();

    internal string? GetRegistrationError(string accountId) => registrationErrors.GetValueOrDefault(accountId);
    private readonly Dictionary<string, DateTime> nextRegistrationAttemptUtc = new();

    internal async Task<DmIdentity> EnsureIdentityAsync(string accountId, string bearerToken)
    {
        await identityGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (identityCache.TryGetValue(accountId, out var cached))
                return cached;

            DmIdentity key;
            var stored = configuration.GetDmPrivateKey(accountId);
            if (stored is not null && TryImportStoredIdentity(stored, out var imported))
                key = imported!;
            else
            {
                var (checkedSuccessfully, registered) = await keysClient.GetOwnPublicKeyAsync(bearerToken).ConfigureAwait(false);
                if (!checkedSuccessfully)
                {
                    registrationErrors[accountId] = "The encryption service could not be reached. No new key was created. Click to retry.";
                    throw new CryptographicException("Could not verify registered DM identity.");
                }
                var recovered = registered is not null ? RecoverMatchingIdentity(accountId, registered) : null;
                if (recovered is not null)
                    key = recovered;
                else if (registered is not null || stored is not null)
                {
                    registrationErrors[accountId] = "Your original private key is missing or cannot be opened, and no matching local backup was found. Restore the original plugin configuration on the device where chat previously worked.";
                    throw new CryptographicException("Original DM private key unavailable.");
                }
                else
                {
                    key = DmCipher.GenerateIdentity();
                    configuration.StoreDmPrivateKey(accountId,
                        Convert.ToBase64String(Protect(DmCipher.ExportPrivateKey(key))));
                }
            }

            identityCache[accountId] = key;
            return key;
        }
        finally
        {
            identityGate.Release();
        }
    }

    // Confirm that the server has this device's public key. Never replace a different server key:
    // its matching private key may hold the only readable copy of existing message history.
    internal async Task<bool> EnsureIdentityRegisteredAsync(string accountId, string bearerToken,
        bool forceCheck = false)
    {
        await registrationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!forceCheck)
            {
                if (registeredAccounts.Contains(accountId))
                    return true;
                if (nextRegistrationAttemptUtc.TryGetValue(accountId, out var nextAttempt) &&
                    DateTime.UtcNow < nextAttempt)
                    return false;
            }

            bool verified;
            try
            {
                verified = await CheckAndRegisterIdentityAsync(accountId, bearerToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                AepLog.Warning("[Messages] Public key registration failed.");
                registrationErrors.TryAdd(accountId, "Your local encryption key could not be opened. Restore the original plugin configuration on this device.");
                verified = false;
            }
            if (verified)
            {
                registrationErrors.TryRemove(accountId, out _);
                registeredAccounts.Add(accountId);
                nextRegistrationAttemptUtc.Remove(accountId);
            }
            else
            {
                registeredAccounts.Remove(accountId);
                nextRegistrationAttemptUtc[accountId] = DateTime.UtcNow.AddMinutes(5);
            }
            return verified;
        }
        finally
        {
            registrationGate.Release();
        }
    }

    private async Task<bool> CheckAndRegisterIdentityAsync(string accountId, string bearerToken)
    {
        var identity = await EnsureIdentityAsync(accountId, bearerToken).ConfigureAwait(false);
        var expected = DmCipher.ExportPublicKey(identity);
        var (checkedSuccessfully, existing) = await keysClient.GetOwnPublicKeyAsync(bearerToken)
            .ConfigureAwait(false);
        if (!checkedSuccessfully)
        {
            registrationErrors[accountId] = "The encryption service could not be reached. Click to retry.";
            return false;
        }
        if (existing is not null)
        {
            var matches = PublicKeyMatches(existing, expected, accountId);
            if (!matches && RecoverMatchingIdentity(accountId, existing) is { } recovered)
            {
                identityCache[accountId] = recovered;
                return true;
            }
            if (!matches)
                registrationErrors[accountId] = "Your local key differs from your registered key. Restore the original plugin configuration; retrying will not replace either key.";
            return matches;
        }

        if (!await keysClient.UploadPublicKeyAsync(bearerToken, Convert.ToBase64String(expected))
                .ConfigureAwait(false))
        {
            registrationErrors[accountId] = "Your encryption key could not be registered. Click to retry.";
            return false;
        }

        var (confirmed, uploaded) = await keysClient.GetOwnPublicKeyAsync(bearerToken)
            .ConfigureAwait(false);
        var verified = confirmed && uploaded is not null && PublicKeyMatches(uploaded, expected, accountId);
        if (!verified)
            registrationErrors[accountId] = "Your registered encryption key could not be confirmed. Click to retry.";
        return verified;
    }

    private static bool TryImportStoredIdentity(string stored, out DmIdentity? identity)
    {
        try
        {
            identity = DmCipher.ImportPrivateKey(Unprotect(Convert.FromBase64String(stored)));
            return true;
        }
        catch (Exception) { identity = null; return false; }
    }

    private DmIdentity? RecoverMatchingIdentity(string accountId, string registeredKey)
    {
        DmPublicKey registered;
        try { registered = DmCipher.ImportPublicKey(Convert.FromBase64String(registeredKey)); }
        catch (Exception) { return null; }
        foreach (var candidate in configuration.GetDmPrivateKeyRecoveryCandidates(accountId))
        {
            if (!TryImportStoredIdentity(candidate, out var identity) ||
                !identity!.PublicKey.Q.Normalize().Equals(registered.PublicKey.Q.Normalize())) continue;
            // StoreDmPrivateKey retains the previous protected key before restoring this one.
            configuration.StoreDmPrivateKey(accountId, candidate);
            return identity;
        }
        return null;
    }

    private static bool PublicKeyMatches(string publicKeyBase64, byte[] expected, string accountId)
    {
        try
        {
            // Equivalent P-521 public keys can use different SPKI curve encodings.
            var registered = DmCipher.ImportPublicKey(Convert.FromBase64String(publicKeyBase64));
            var local = DmCipher.ImportPublicKey(expected);
            if (registered.PublicKey.Q.Normalize().Equals(local.PublicKey.Q.Normalize()))
                return true;
        }
        catch (Exception)
        {
            // Treat malformed server data like a mismatch; never overwrite it blindly.
        }

        return false;
    }

    internal async Task<DmPublicKey?> GetOtherPartyKeyAsync(string otherAccountId, string bearerToken)
    {
        if (otherPartyCache.TryGetValue(otherAccountId, out var cached))
        {
            return cached;
        }

        var available = await CheckOtherPartyKeyAvailabilityAsync(otherAccountId, bearerToken).ConfigureAwait(false);
        return available == true && otherPartyCache.TryGetValue(otherAccountId, out var key) ? key : null;
    }

    // true = usable public key, false = absent/invalid key, null = temporary lookup failure.
    internal async Task<bool?> CheckOtherPartyKeyAvailabilityAsync(string otherAccountId, string bearerToken)
    {
        var (checkedSuccessfully, publicKeyBase64) = await keysClient
            .CheckUserPublicKeyAsync(bearerToken, otherAccountId).ConfigureAwait(false);
        if (!checkedSuccessfully)
            return null;
        if (publicKeyBase64 is null)
        {
            otherPartyCache.TryRemove(otherAccountId, out _);
            return false;
        }

        try
        {
            otherPartyCache[otherAccountId] = DmCipher.ImportPublicKey(Convert.FromBase64String(publicKeyBase64));
            return true;
        }
        catch (Exception)
        {
            otherPartyCache.TryRemove(otherAccountId, out _);
            AepLog.Warning("[Messages] Invalid public key for account [redacted].");
            return false;
        }
    }

    // DPAPI (Windows-only) as defense-in-depth against other local processes reading the plugin's
    // config file - not a network protection, and not the whole story: a real recovery-code-backed
    // scheme (so a private key isn't silently unrecoverable if this file is ever lost, and so
    // non-Windows/Wine users get equivalent protection instead of a plain fallback) is real future
    // work, deliberately out of scope here - see the coordinator's notes on this tradeoff.
    private static byte[] Protect(byte[] data)
    {
        try
        {
            return ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
        }
        catch (PlatformNotSupportedException)
        {
            return data;
        }
        catch (CryptographicException)
        {
            // Wine often rejects DPAPI even when the API surface exists - store raw PKCS8 rather
            // than failing key creation entirely.
            return data;
        }
    }

    private static byte[] Unprotect(byte[] data)
    {
        try
        {
            return ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
        }
        catch (Exception exception) when (exception is PlatformNotSupportedException or CryptographicException)
        {
            // Either DPAPI isn't available on this platform, or the bytes were never protected in
            // the first place (the PlatformNotSupportedException fallback path above) - either way,
            // treat as already-raw PKCS8.
            return data;
        }
    }
}
