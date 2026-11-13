using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Asn1.Sec;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;

namespace AlphaChannel.Plugin.Crypto;

// Long-term ECDH identity (NIST P-521). Managed via BouncyCastle so keygen/derive works under
// Wine, where Windows CNG's ECDiffieHellman.Create(nistP521) throws CryptographicException
// 0x80090029. Wire format stays SPKI / PKCS#8 so existing keys and the server stay compatible.
//
// Shared AES key matches .NET's ECDiffieHellman.DeriveKeyFromHash(..., SHA512) with empty
// prepend/append: SHA-512(Z) truncated to 32 bytes, where Z is the ECDH x-coordinate zero-padded
// to the P-521 field size (see ECDiffieHellmanDerivation.DeriveKeyFromHash in dotnet/runtime).
internal sealed class DmIdentity
{
    internal DmIdentity(ECPrivateKeyParameters privateKey, ECPublicKeyParameters publicKey)
    {
        PrivateKey = privateKey;
        PublicKey = publicKey;
    }

    internal ECPrivateKeyParameters PrivateKey { get; }
    internal ECPublicKeyParameters PublicKey { get; }
}

internal sealed class DmPublicKey(ECPublicKeyParameters publicKey)
{
    internal ECPublicKeyParameters PublicKey { get; } = publicKey;
}

internal static class DmCipher
{
    // CommitmentTag is HMAC-SHA512 output, independent of the 32-byte franking key.
    internal const int CommitmentTagLength = 64;
    private const int FrankingKeyLength = 32;
    private const int AesKeyLength = 32;
    private const int NonceLength = 12;
    private const int TagLength = 16;

    private static readonly SecureRandom SecureRandomSource = new();
    private static readonly ECNamedDomainParameters P521 = CreateP521Domain();

    internal static DmIdentity GenerateIdentity()
    {
        var generator = new ECKeyPairGenerator();
        generator.Init(new ECKeyGenerationParameters(P521, SecureRandomSource));
        var pair = generator.GenerateKeyPair();
        return new DmIdentity((ECPrivateKeyParameters)pair.Private, (ECPublicKeyParameters)pair.Public);
    }

    internal static byte[] ExportPublicKey(DmIdentity key) =>
        SubjectPublicKeyInfoFactory.CreateSubjectPublicKeyInfo(key.PublicKey).GetDerEncoded();

    internal static byte[] ExportPrivateKey(DmIdentity key) =>
        PrivateKeyInfoFactory.CreatePrivateKeyInfo(key.PrivateKey).GetDerEncoded();

    internal static DmPublicKey ImportPublicKey(byte[] spki)
    {
        var parsed = PublicKeyFactory.CreateKey(spki);
        if (parsed is not ECPublicKeyParameters publicKey || !IsP521(publicKey.Parameters))
        {
            throw new CryptographicException("DM public key is not a P-521 ECDH key.");
        }

        return new DmPublicKey(publicKey);
    }

    internal static DmIdentity ImportPrivateKey(byte[] pkcs8)
    {
        var parsed = PrivateKeyFactory.CreateKey(pkcs8);
        if (parsed is not ECPrivateKeyParameters privateKey || !IsP521(privateKey.Parameters))
        {
            throw new CryptographicException("DM private key is not a P-521 ECDH key.");
        }

        var publicPoint = privateKey.Parameters.G.Multiply(privateKey.D).Normalize();
        var publicKey = new ECPublicKeyParameters("ECDH", publicPoint, privateKey.Parameters);
        return new DmIdentity(privateKey, publicKey);
    }

    private static byte[] DeriveSharedKey(DmIdentity myPrivate, DmPublicKey otherPublic)
    {
        var agreement = new ECDHBasicAgreement();
        agreement.Init(myPrivate.PrivateKey);
        var raw = agreement.CalculateAgreement(otherPublic.PublicKey).ToByteArrayUnsigned();
        var fieldSize = agreement.GetFieldSize();
        var z = new byte[fieldSize];
        if (raw.Length > fieldSize)
        {
            throw new CryptographicException("ECDH agreement exceeded the P-521 field size.");
        }

        raw.CopyTo(z, fieldSize - raw.Length);
        return SHA512.HashData(z)[..AesKeyLength];
    }

    internal sealed record Sealed(byte[] Ciphertext, byte[] Nonce, byte[] Tag, byte[] CommitmentTag);

    internal static Sealed Encrypt(DmIdentity myPrivate, DmPublicKey otherPublic, string plaintext)
    {
        var sharedKey = DeriveSharedKey(myPrivate, otherPublic);
        var frankingKey = RandomNumberGenerator.GetBytes(FrankingKeyLength);
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);

        var payload = new byte[FrankingKeyLength + plaintextBytes.Length];
        frankingKey.CopyTo(payload, 0);
        plaintextBytes.CopyTo(payload, FrankingKeyLength);

        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var ciphertext = new byte[payload.Length];
        var tag = new byte[TagLength];
        using (var gcm = new AesGcm(sharedKey, TagLength))
        {
            gcm.Encrypt(nonce, payload, ciphertext, tag);
        }

        var commitmentTag = HMACSHA512.HashData(frankingKey, plaintextBytes);
        return new Sealed(ciphertext, nonce, tag, commitmentTag);
    }

    internal sealed record Opened(string Plaintext, byte[] FrankingKey);

    internal static Opened? Decrypt(DmIdentity myPrivate, DmPublicKey otherPublic, byte[] ciphertext, byte[] nonce, byte[] tag)
    {
        try
        {
            var sharedKey = DeriveSharedKey(myPrivate, otherPublic);
            var payload = new byte[ciphertext.Length];
            using (var gcm = new AesGcm(sharedKey, TagLength))
            {
                gcm.Decrypt(nonce, ciphertext, tag, payload);
            }

            var frankingKey = payload[..FrankingKeyLength];
            var plaintext = Encoding.UTF8.GetString(payload[FrankingKeyLength..]);
            return new Opened(plaintext, frankingKey);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static ECNamedDomainParameters CreateP521Domain()
    {
        var parameters = SecNamedCurves.GetByOid(SecObjectIdentifiers.SecP521r1);
        return new ECNamedDomainParameters(SecObjectIdentifiers.SecP521r1, parameters.Curve, parameters.G,
            parameters.N, parameters.H, parameters.GetSeed());
    }

    private static bool IsP521(ECDomainParameters? parameters) =>
        parameters is not null
        && parameters.Curve.FieldSize == P521.Curve.FieldSize
        && parameters.N.Equals(P521.N)
        && parameters.H.Equals(P521.H)
        && parameters.G.Normalize().Equals(P521.G.Normalize());
}
