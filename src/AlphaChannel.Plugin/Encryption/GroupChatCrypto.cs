using System.Security.Cryptography;
using System.Text;
using AlphaChannel.Plugin.Auth;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace AlphaChannel.Plugin.Crypto;

// Managed C# primitives only; no new OS-specific/native crypto dependency. Local-vault
// wrapping and group-message encryption have distinct keys and authenticated contexts.
internal static class GroupChatCrypto
{
    internal sealed record Sealed(string Ciphertext, string Nonce, string Tag);
    internal sealed record Payload(string Text, string FrankingKey, GroupImageContent? Image = null);
    internal static byte[] RandomKey() => RandomNumberGenerator.GetBytes(32);
    internal static Sealed Seal(byte[] key, byte[] plaintext, string context)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new GcmBlockCipher(new AesEngine());
        cipher.Init(true, new AeadParameters(new KeyParameter(key), 128, nonce, Encoding.UTF8.GetBytes(context)));
        var output = new byte[cipher.GetOutputSize(plaintext.Length)];
        var used = cipher.ProcessBytes(plaintext, 0, plaintext.Length, output, 0);
        used += cipher.DoFinal(output, used);
        return new(Convert.ToBase64String(output, 0, used - 16), Convert.ToBase64String(nonce), Convert.ToBase64String(output, used - 16, 16));
    }
    internal static byte[] Open(byte[] key, Sealed packet, string context)
    {
        var cipher = new GcmBlockCipher(new AesEngine());
        cipher.Init(false, new AeadParameters(new KeyParameter(key), 128, Convert.FromBase64String(packet.Nonce), Encoding.UTF8.GetBytes(context)));
        var input = Convert.FromBase64String(packet.Ciphertext).Concat(Convert.FromBase64String(packet.Tag)).ToArray();
        var output = new byte[cipher.GetOutputSize(input.Length)];
        var used = cipher.ProcessBytes(input, 0, input.Length, output, 0);
        used += cipher.DoFinal(output, used);
        return output[..used];
    }
    private static byte[] SharedKey(DmIdentity mine, DmPublicKey other)
    {
        var agreement = new ECDHBasicAgreement(); agreement.Init(mine.PrivateKey);
        var raw = agreement.CalculateAgreement(other.PublicKey).ToByteArrayUnsigned();
        var padded = new byte[agreement.GetFieldSize()]; raw.CopyTo(padded, padded.Length - raw.Length);
        // Domain separated from legacy DM encryption, with fixed-width ECDH encoding.
        var key = SHA512.HashData(Encoding.UTF8.GetBytes("AC-GROUP-KEY-1\n").Concat(padded).ToArray())[..32];
        CryptographicOperations.ZeroMemory(padded); CryptographicOperations.ZeroMemory(raw); return key;
    }
    internal static GroupKeyPackage Wrap(string group, DmIdentity mine, GroupDevice recipient, byte[] key)
    {
        var wrapping = SharedKey(mine, DmCipher.ImportPublicKey(Convert.FromBase64String(recipient.EncryptionKey)));
        try {
            var packet = Seal(wrapping, key, $"AC-GROUP-PACKAGE-1\n{group}\n{recipient.AccountId}\n{recipient.DeviceId}");
            return new(recipient.AccountId, recipient.DeviceId, packet.Ciphertext, packet.Nonce, packet.Tag);
        } finally { CryptographicOperations.ZeroMemory(wrapping); }
    }
    internal static byte[] Unwrap(string group, DmIdentity mine, GroupKeyPackage packet)
    {
        var wrapping = SharedKey(mine, DmCipher.ImportPublicKey(Convert.FromBase64String(packet.SenderEncryptionKey!)));
        try { return Open(wrapping, new(packet.Ciphertext, packet.Nonce, packet.Tag), $"AC-GROUP-PACKAGE-1\n{group}\n{packet.AccountId}\n{packet.DeviceId}"); }
        finally { CryptographicOperations.ZeroMemory(wrapping); }
    }
    internal static byte[] SigningPrivateKey() => new Ed25519PrivateKeyParameters(new SecureRandom()).GetEncoded();
    internal static string SigningPublicKey(byte[] key) => Convert.ToBase64String(new Ed25519PrivateKeyParameters(key, 0).GeneratePublicKey().GetEncoded());
    internal static string SignedText(string group, GroupMessage m) => string.Join("\n", "AC-GROUP-1", group, m.Id,
        m.SenderAccountId, m.DeviceId, m.Ciphertext, m.Nonce, m.Tag, m.Commitment) +
        (m.MediaId is null ? "" : $"\nAC-MEDIA-1\n{m.MediaId}\n{m.MediaDigest}");
    internal static string Sign(byte[] key, string text)
    {
        var signer = new Ed25519Signer(); signer.Init(true, new Ed25519PrivateKeyParameters(key, 0));
        var data = Encoding.UTF8.GetBytes(text); signer.BlockUpdate(data, 0, data.Length); return Convert.ToBase64String(signer.GenerateSignature());
    }
    internal static bool Verify(string publicKey, string text, string signature)
    {
        try {
            var signer = new Ed25519Signer(); signer.Init(false, new Ed25519PublicKeyParameters(Convert.FromBase64String(publicKey), 0));
            var data = Encoding.UTF8.GetBytes(text); signer.BlockUpdate(data, 0, data.Length); return signer.VerifySignature(Convert.FromBase64String(signature));
        } catch { return false; }
    }
}
