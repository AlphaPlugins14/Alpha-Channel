using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace AlphaChannel.Plugin.Video;

internal enum ChromiumPackageStatus { Waiting, Downloading, Ready, Failed }

// Installs into versioned config directories, never into the plugin package or
// over a running browser. Only a complete, verified staging directory is exposed.
internal sealed class ChromiumPackageInstaller
{
    private sealed record Manifest(int SchemaVersion, string Url, string Sha256);
    private sealed record Snapshot(ChromiumPackageStatus Status, string? Directory, string? Error);
    private readonly HttpClient client;
    private readonly string installRoot;
    private readonly string manifestPath;
    private readonly SemaphoreSlim installGate = new(1, 1);
    private Snapshot snapshot = new(ChromiumPackageStatus.Waiting, null, null);
    private static readonly string[] RequiredFiles =
    [
        "AlphaChannel.BrowserHost.exe", "AlphaChannel.BrowserHost.dll",
        "AlphaChannel.BrowserHost.deps.json", "AlphaChannel.BrowserHost.runtimeconfig.json",
        "CefSharp.Core.Runtime.dll", "libcef.dll", "resources.pak", "icudtl.dat",
        "locales/en-US.pak"
    ];

    internal ChromiumPackageStatus Status => Volatile.Read(ref snapshot).Status;
    internal string? RuntimeDirectory => Volatile.Read(ref snapshot).Directory;
    internal string? Error => Volatile.Read(ref snapshot).Error;

    internal bool TryQueueRetry()
    {
        var current = Volatile.Read(ref snapshot);
        return current.Status == ChromiumPackageStatus.Failed &&
            ReferenceEquals(Interlocked.CompareExchange(ref snapshot,
                new(ChromiumPackageStatus.Waiting, null, null), current), current);
    }

    internal ChromiumPackageInstaller(HttpClient client, string configDirectory, string manifestPath)
    {
        this.client = client;
        this.manifestPath = manifestPath;
        installRoot = Path.GetFullPath(Path.Combine(configDirectory, "Browsers", "Chromium"));
        try
        {
            var manifest = ReadManifest();
            var target = Path.Combine(installRoot, manifest.Sha256.ToLowerInvariant());
            if (IsComplete(target)) snapshot = new(ChromiumPackageStatus.Ready, target, null);
        }
        catch (Exception exception)
        {
            snapshot = new(ChromiumPackageStatus.Failed, null, exception.Message);
        }
    }

    private Manifest ReadManifest()
    {
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (manifest is null || manifest.SchemaVersion != 1 ||
            !Uri.TryCreate(manifest.Url, UriKind.Absolute, out var url) || url.Scheme != "https" ||
            manifest.Sha256 is not { Length: 64 } || !manifest.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("The Chromium download manifest is invalid.");
        return manifest;
    }

    private static bool IsComplete(string directory) =>
        File.Exists(Path.Combine(directory, ".installed")) &&
        File.ReadAllText(Path.Combine(directory, ".installed")).Trim()
            .Equals(Path.GetFileName(directory), StringComparison.OrdinalIgnoreCase) &&
        RequiredFiles.All(file => File.Exists(Path.Combine(directory, file)));

    internal async Task InstallAsync(CancellationToken cancellationToken)
    {
        await installGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? staging = null;
        try
        {
            if (Status == ChromiumPackageStatus.Ready) return;
            Volatile.Write(ref snapshot, new(ChromiumPackageStatus.Downloading, null, null));
            var manifest = ReadManifest();
            var target = Path.Combine(installRoot, manifest.Sha256.ToLowerInvariant());
            if (!IsComplete(target))
            {
                Directory.CreateDirectory(installRoot);
                // Coordinate hot-reload instances too: the old Resources task
                // may still be unwinding when a new plugin instance starts.
                await using var installationLock = await AcquireInstallationLockAsync(cancellationToken).ConfigureAwait(false);
                if (IsComplete(target))
                {
                    Volatile.Write(ref snapshot, new(ChromiumPackageStatus.Ready, target, null));
                    return;
                }
                // The exclusive install lock ensures these belong to interrupted
                // attempts, rather than another live downloader.
                foreach (var abandoned in Directory.EnumerateDirectories(installRoot, ".download-*"))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try { DeleteOwnedDirectory(abandoned); } catch (IOException) { }
                }
                staging = Path.Combine(installRoot, ".download-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(staging);
                var archivePath = Path.Combine(staging, "package.zip");
                using (var response = await client.GetAsync(manifest.Url,
                           HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    await using var file = File.Create(archivePath);
                    await response.Content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
                }
                await using (var file = File.OpenRead(archivePath))
                {
                    var digest = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false));
                    if (!digest.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Chromium download verification failed. Please retry the download.");
                }
                var extracted = Path.Combine(staging, "files");
                Directory.CreateDirectory(extracted);
                var prefix = Path.GetFullPath(extracted) + Path.DirectorySeparatorChar;
                using (var archive = ZipFile.OpenRead(archivePath))
                {
                    foreach (var entry in archive.Entries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var destination = Path.GetFullPath(Path.Combine(extracted,
                            entry.FullName.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));
                        if (!destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Unsafe path in Chromium archive.");
                        if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                        {
                            Directory.CreateDirectory(destination);
                            continue;
                        }
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        await using var input = entry.Open();
                        await using var output = File.Create(destination);
                        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                    }
                }
                if (!RequiredFiles.All(file => File.Exists(Path.Combine(extracted, file))))
                    throw new InvalidDataException("Chromium archive is missing required runtime files.");
                await File.WriteAllTextAsync(Path.Combine(extracted, ".installed"), manifest.Sha256, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                // An incomplete directory is never used by the renderer.
                if (Directory.Exists(target)) DeleteOwnedDirectory(target);
                Directory.Move(extracted, target);
            }
            Volatile.Write(ref snapshot, new(ChromiumPackageStatus.Ready, target, null));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Volatile.Write(ref snapshot, new(ChromiumPackageStatus.Waiting, null, null));
            throw;
        }
        catch (Exception exception)
        {
            Volatile.Write(ref snapshot, new(ChromiumPackageStatus.Failed, null, exception.Message));
        }
        finally
        {
            if (staging is not null)
            {
                try { DeleteOwnedDirectory(staging); } catch { /* Next launch remains safe after interrupted cleanup. */ }
            }
            installGate.Release();
        }
    }

    private void DeleteOwnedDirectory(string directory)
    {
        if (!Path.GetFullPath(directory).StartsWith(installRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Chromium cleanup escaped its installation directory.");
        Directory.Delete(directory, recursive: true);
    }

    private async Task<FileStream> AcquireInstallationLockAsync(CancellationToken cancellationToken)
    {
        var attempts = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(Path.Combine(installRoot, ".install.lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException exception) when ((exception.HResult & 0xffff) is 32 or 33 && attempts++ < 120)
            {
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
