using System;
using System.IO;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace AlphaChannel.Media;

internal static class MediaTlsTrust
{
    private static string? installationDirectory;

    internal static void ConfigureDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
            throw new ArgumentException("Media certificates require an absolute installation directory.", nameof(directory));
        System.Threading.Volatile.Write(ref installationDirectory, Path.GetFullPath(directory));
    }

    internal static string BundlePath
    {
        get
        {
            // The plugin host may load assemblies from memory. Its supplied on-disk
            // installation path is authoritative; regular helper processes use theirs.
            var directory = System.Threading.Volatile.Read(ref installationDirectory);
            if (directory is null)
            {
                var location = typeof(MediaTlsTrust).Assembly.Location;
                if (string.IsNullOrEmpty(location))
                    throw new InvalidOperationException("Configure the media certificate installation directory before playback.");
                directory = Path.GetDirectoryName(location)!;
            }
            return Path.Combine(directory, "cacert.pem");
        }
    }

    internal static SslClientAuthenticationOptions ClientOptions(string host)
    {
        var roots = new X509Certificate2Collection();
        roots.ImportFromPemFile(BundlePath);
        if (roots.Count == 0) throw new InvalidDataException("Media certificate bundle is empty.");
        var policy = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust,
            RevocationMode = X509RevocationMode.NoCheck
        };
        policy.CustomTrustStore.AddRange(roots);
        return new SslClientAuthenticationOptions { TargetHost = host, CertificateChainPolicy = policy };
    }
}
