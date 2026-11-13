using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;

namespace AlphaChannel.Plugin.Video;

/// <summary>Installs an exact, driver-compatible FFmpeg build, including downgrades.</summary>
internal sealed class FFmpegPackageInstaller
{
    // Pin the archive as well as FFmpeg's version: a rolling build of an older
    // FFmpeg branch can still acquire newer NVENC headers and driver requirements.
    internal const string BuildId = "btbn-8.1.2-20260630";
    internal const string Version = "n8.1.2-21-gce3c09c101-20260630";
    internal const string DownloadUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-06-30-13-34/ffmpeg-n8.1.2-21-gce3c09c101-win64-lgpl-8.1.zip";
    private const string ArchiveSha256 = "3B9ECEB438016B647E0755A51CE3A388CD4ED5679E2427CB83A01E1AE2CD0EBA";
    private const string ExecutableSha256 = "C623359F35E7DB4820694B77D0F032F075E783A9C7207B81D908C28F148E8A68";
    private const string ArchiveRoot = "ffmpeg-n8.1.2-21-gce3c09c101-win64-lgpl-8.1";
    private readonly HttpClient client;
    private readonly string directory;
    private readonly CancellationToken cancellation;
    private readonly SemaphoreSlim installationGate = new(1, 1);
    private string? selectedPath;
    private string? lastError;

    internal string? LastError => Volatile.Read(ref lastError);

    internal FFmpegPackageInstaller(HttpClient client, string configDirectory, CancellationToken cancellation)
    {
        this.client = client;
        this.cancellation = cancellation;
        directory = Path.Combine(configDirectory, "ffmpeg");
        // New sessions receive a path only after checksum and version validation.
        // Existing encoder processes retain their executable independently.
    }

    internal string? Location
    {
        get
        {
            var selected = Volatile.Read(ref selectedPath);
            return selected is not null && File.Exists(selected) ? selected : null;
        }
    }

    internal async Task<bool> EnsureInstalledAsync()
    {
        await installationGate.WaitAsync(cancellation).ConfigureAwait(false);
        string? staging = null;
        try
        {
            Volatile.Write(ref selectedPath, null);
            Volatile.Write(ref lastError, null);
            var targetDirectory = Path.Combine(directory, BuildId);
            var target = Path.Combine(targetDirectory, "ffmpeg.exe");
            // The hash checks the actual binary, not a marker that could survive
            // someone overwriting it with the incompatible newer build.
            if (await IsRequiredBuildAsync(target).ConfigureAwait(false))
            {
                Volatile.Write(ref selectedPath, target);
                
                return true;
            }

            Directory.CreateDirectory(directory);
            staging = Path.Combine(directory, $".install-{Guid.NewGuid():N}");
            Directory.CreateDirectory(staging);
            var zip = Path.Combine(staging, "download.zip");
            
            using (var response = await client.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var output = File.Create(zip);
                await response.Content.CopyToAsync(output, cancellation).ConfigureAwait(false);
            }
            if (!await HasHashAsync(zip, ArchiveSha256).ConfigureAwait(false))
                throw new InvalidDataException("FFmpeg archive checksum does not match the pinned release.");

            var candidate = Path.Combine(staging, "ffmpeg.exe");
            using (var archive = ZipFile.OpenRead(zip))
            {
                // Extract only known entries, so no archive path can escape staging.
                var executable = archive.GetEntry(ArchiveRoot + "/bin/ffmpeg.exe")
                    ?? throw new InvalidDataException("FFmpeg executable was not found in the pinned archive.");
                await using (var input = executable.Open())
                await using (var output = File.Create(candidate))
                    await input.CopyToAsync(output, cancellation).ConfigureAwait(false);
                var license = archive.GetEntry(ArchiveRoot + "/LICENSE.txt");
                if (license is not null) license.ExtractToFile(Path.Combine(staging, "LICENSE.txt"));
            }
            if (!await IsRequiredBuildAsync(candidate).ConfigureAwait(false))
                throw new InvalidDataException("Downloaded FFmpeg executable failed its checksum or version check.");

            cancellation.ThrowIfCancellationRequested();
            Directory.CreateDirectory(targetDirectory);
            // A versioned path avoids replacing an executable that an existing
            // broadcast is using. New sessions switch only after full validation;
            // existing processes finish normally on their original executable.
            File.Move(candidate, target, overwrite: true);
            var licensePath = Path.Combine(staging, "LICENSE.txt");
            if (File.Exists(licensePath)) File.Move(licensePath, Path.Combine(targetDirectory, "LICENSE.txt"), overwrite: true);
            Volatile.Write(ref selectedPath, target);
            
            return true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            Volatile.Write(ref lastError, "The required FFmpeg build could not be verified or downloaded. Check your connection and try again in two minutes.");
            AepLog.Warning("[FFMPEG] Required build verification/installation failed; new sessions are blocked until it is available.");
            return false;
        }
        finally
        {
            if (staging is not null)
            {
                try { Directory.Delete(staging, recursive: true); } catch { }
            }
            installationGate.Release();
        }
    }

    private async Task<bool> IsRequiredBuildAsync(string path) =>
        File.Exists(path) && await HasHashAsync(path, ExecutableSha256).ConfigureAwait(false) &&
        await ReadVersionAsync(path).ConfigureAwait(false) == "ffmpeg version " + Version;

    private async Task<bool> HasHashAsync(string path, string expected)
    {
        await using var input = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(input, cancellation).ConfigureAwait(false);
        return Convert.ToHexString(hash) == expected;
    }

    private async Task<string?> ReadVersionAsync(string path)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(path, "-version")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            },
        };
        try
        {
            if (!process.Start()) return null;
            var output = process.StandardOutput.ReadToEndAsync(cancellation);
            var errors = process.StandardError.ReadToEndAsync(cancellation);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await Task.WhenAll(output, errors).ConfigureAwait(false);
            if (process.ExitCode != 0) return null;
            // Ignore the copyright suffix, but compare the full build identity.
            var line = (await output.ConfigureAwait(false)).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return line?.Split(" Copyright", StringSplitOptions.None)[0];
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch { return null; }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        }
    }
}
