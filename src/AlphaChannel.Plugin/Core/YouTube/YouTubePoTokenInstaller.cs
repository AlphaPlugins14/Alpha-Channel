using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace AlphaChannel.Plugin.Video;

// Installs a matched provider/plugin pair with a private, portable Node runtime.
// Nothing is installed into Windows and no npm/git installation is required.
internal sealed class YouTubePoTokenInstaller
{
    internal const string ProviderVersion = "2.0.0";
    private const string InstallVersion = ProviderVersion + "-1";
    private readonly HttpClient http;
    private readonly string configDirectory;
    private readonly CancellationToken shutdown;
    private readonly Func<DateTime> utcNow;
    private readonly Func<string, string, IReadOnlyList<string>, CancellationToken, Task> run;
    private readonly object sync = new();
    private Task<bool>? installation;
    private DateTime retryAfterUtc;
    private int failedAttempts;
    private DateTime installationStartedUtc;

    // A failed initial setup releases playback to Android. Background repairs
    // never interrupt that fallback playback while another attempt runs.
    internal bool PlaybackMustWait { get { lock (sync) return failedAttempts == 0 && !IsInstalled; } }
    internal bool AndroidFallbackRequired => !PlaybackMustWait && !IsInstalled;
    internal int EstimatedWaitSeconds { get { lock (sync) return Math.Max(10,
        (int)Math.Ceiling(120 - (installationStartedUtc == default ? 0 : (utcNow() - installationStartedUtc).TotalSeconds))); } }
    internal DateTime NextRetryUtc { get { lock (sync) return retryAfterUtc; } }

    internal YouTubePoTokenInstaller(HttpClient http, string configDirectory, CancellationToken shutdown,
        Func<string, string, IReadOnlyList<string>, CancellationToken, Task>? run = null,
        Func<DateTime>? utcNow = null)
    {
        this.http = http;
        this.configDirectory = Path.GetFullPath(configDirectory);
        this.shutdown = shutdown;
        this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        this.run = run ?? RunNodeAsync;
    }

    internal string RootDirectory => Path.Combine(configDirectory, "youtube-po-token");

    internal static bool HasRuntimeFiles(string root) =>
        File.Exists(Path.Combine(root, "node", "node.exe")) &&
        File.Exists(Path.Combine(root, "plugins", "bgutil-ytdlp-pot-provider.zip")) &&
        File.Exists(Path.Combine(root, "provider", "server", "build", "main.js")) &&
        File.Exists(Path.Combine(root, "provider", "server", "build", "generate_once.js")) &&
        File.Exists(Path.Combine(root, "provider", "server", "node_modules", "canvas", "package.json"));

    internal Task? PendingInstallation { get { lock (sync) return installation; } }

    internal bool IsInstalled
    {
        get
        {
            try
            {
                return HasRuntimeFiles(RootDirectory) &&
                    File.ReadAllText(Path.Combine(RootDirectory, "installed-version.txt")).Trim() == InstallVersion;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }

    internal Task<bool> EnsureInstalledAsync()
    {
        lock (sync)
        {
            if (installation is { IsCompleted: false }) return installation;
            if (IsInstalled) return Task.FromResult(true);
            if (shutdown.IsCancellationRequested || utcNow() < retryAfterUtc)
                return Task.FromResult(false);
            installationStartedUtc = utcNow();
            return installation = Task.Run(InstallAsync);
        }
    }

    private async Task<bool> InstallAsync()
    {
        var stage = Path.Combine(configDirectory, ".youtube-po-token-stage-" + Guid.NewGuid().ToString("N"));
        var backup = stage + "-previous";
        var promoted = false;
        try
        {
            Directory.CreateDirectory(configDirectory);
            // Also serialize against a previous plugin instance winding down.
            using var installLock = await AcquireInstallLockAsync().ConfigureAwait(false);
            if (IsInstalled) return true;
            Directory.CreateDirectory(stage);
            

            var sums = await http.GetStringAsync("https://nodejs.org/dist/latest-v22.x/SHASUMS256.txt", shutdown).ConfigureAwait(false);
            var nodeRelease = Regex.Match(sums, @"(?m)^([a-fA-F0-9]{64})\s+(node-v22\.[0-9]+\.[0-9]+-win-x64\.zip)\r?$");
            if (!nodeRelease.Success) throw new InvalidDataException("Portable Node release was not found in its checksum manifest.");
            var nodeZip = Path.Combine(stage, "node.zip");
            await DownloadAsync("https://nodejs.org/dist/latest-v22.x/" + nodeRelease.Groups[2].Value, nodeZip).ConfigureAwait(false);
            await using (var input = File.OpenRead(nodeZip))
            {
                var digest = Convert.ToHexString(await SHA256.HashDataAsync(input, shutdown).ConfigureAwait(false));
                if (!digest.Equals(nodeRelease.Groups[1].Value, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Portable Node checksum did not match.");
            }
            var nodeExtract = Path.Combine(stage, "node-extract");
            ZipFile.ExtractToDirectory(nodeZip, nodeExtract);
            await MoveDirectoryAsync(Path.Combine(nodeExtract, Path.GetFileNameWithoutExtension(nodeRelease.Groups[2].Value)), Path.Combine(stage, "node")).ConfigureAwait(false);
            Directory.Delete(nodeExtract, true);
            File.Delete(nodeZip);

            var sourceZip = Path.Combine(stage, "provider.zip");
            await DownloadAsync($"https://codeload.github.com/Brainicism/bgutil-ytdlp-pot-provider/zip/refs/tags/{ProviderVersion}", sourceZip).ConfigureAwait(false);
            var sourceExtract = Path.Combine(stage, "source-extract");
            ZipFile.ExtractToDirectory(sourceZip, sourceExtract);
            await MoveDirectoryAsync(Path.Combine(sourceExtract, "bgutil-ytdlp-pot-provider-" + ProviderVersion), Path.Combine(stage, "provider")).ConfigureAwait(false);
            Directory.Delete(sourceExtract, true);
            File.Delete(sourceZip);
            Directory.CreateDirectory(Path.Combine(stage, "plugins"));
            var pluginZip = Path.Combine(stage, "plugins", "bgutil-ytdlp-pot-provider.zip");
            await DownloadAsync($"https://github.com/Brainicism/bgutil-ytdlp-pot-provider/releases/download/{ProviderVersion}/bgutil-ytdlp-pot-provider.zip", pluginZip).ConfigureAwait(false);
            using (var archive = ZipFile.OpenRead(pluginZip))
                if (!archive.Entries.Any(entry => entry.FullName.Contains("yt_dlp_plugins/", StringComparison.Ordinal)))
                    throw new InvalidDataException("The yt-dlp provider plugin archive is invalid.");

            var node = Path.Combine(stage, "node", "node.exe");
            var server = Path.Combine(stage, "provider", "server");
            await run(node, server, [Path.Combine(stage, "node", "node_modules", "npm", "bin", "npm-cli.js"),
                "ci", "--no-audit", "--no-fund", "--cache", Path.Combine(stage, "npm-cache")], shutdown).ConfigureAwait(false);
            await run(node, server, [Path.Combine(server, "node_modules", "typescript", "bin", "tsc")], shutdown).ConfigureAwait(false);
            // Fail installation if native canvas or another runtime dependency cannot load.
            await run(node, server, ["--input-type=module", "-e",
                "await import('canvas'); await import('./build/session_manager.js');"], shutdown).ConfigureAwait(false);
            if (!HasRuntimeFiles(stage)) throw new InvalidDataException("The compiled token provider is incomplete.");
            if (Directory.Exists(Path.Combine(stage, "npm-cache"))) Directory.Delete(Path.Combine(stage, "npm-cache"), true);
            await File.WriteAllTextAsync(Path.Combine(stage, "installed-version.txt"), InstallVersion, shutdown).ConfigureAwait(false);
            shutdown.ThrowIfCancellationRequested();
            if (Directory.Exists(RootDirectory)) await MoveDirectoryAsync(RootDirectory, backup).ConfigureAwait(false);
            try { await MoveDirectoryAsync(stage, RootDirectory).ConfigureAwait(false); }
            catch
            {
                if (Directory.Exists(backup) && !Directory.Exists(RootDirectory)) Directory.Move(backup, RootDirectory);
                throw;
            }
            promoted = true;
            
            return true;
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { return false; }
        catch (Exception error)
        {
            AepLog.Warning($"[YouTube] PO-token setup failed: {error.GetType().Name} (0x{error.HResult:X8})");
            lock (sync)
            {
                failedAttempts++;
                retryAfterUtc = utcNow() + (failedAttempts == 1 ? TimeSpan.FromMinutes(5) : TimeSpan.FromHours(1));
            }
            {}
            return false;
        }
        finally
        {
            DeleteOwnedStage(stage);
            if (promoted) DeleteOwnedStage(backup);
        }
    }

    private void DeleteOwnedStage(string path)
    {
        // Only delete this operation's named staging directories inside configDirectory.
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), configDirectory, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(path).StartsWith(".youtube-po-token-stage-", StringComparison.Ordinal)) return;
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch (Exception) {  }
    }

    private async Task<FileStream> AcquireInstallLockAsync()
    {
        while (true)
        {
            shutdown.ThrowIfCancellationRequested();
            try { return new FileStream(Path.Combine(configDirectory, ".youtube-po-token.install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { await Task.Delay(250, shutdown).ConfigureAwait(false); }
        }
    }

    private async Task MoveDirectoryAsync(string source, string destination)
    {
        // Windows scanners can temporarily hold freshly extracted executables.
        for (var attempt = 0; ; attempt++)
        {
            shutdown.ThrowIfCancellationRequested();
            try { Directory.Move(source, destination); return; }
            catch (IOException) when (attempt < 15) { }
            catch (UnauthorizedAccessException) when (attempt < 15) { }
            await Task.Delay(1000, shutdown).ConfigureAwait(false);
        }
    }

    private async Task DownloadAsync(string url, string destination)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, shutdown).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var output = File.Create(destination);
        await response.Content.CopyToAsync(output, shutdown).ConfigureAwait(false);
    }

    private static async Task RunNodeAsync(string node, string directory, IReadOnlyList<string> arguments, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        using var process = new Process { StartInfo = new ProcessStartInfo(node)
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.Environment["PATH"] = Path.GetDirectoryName(node) + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        if (!process.Start()) throw new InvalidOperationException("Portable Node could not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            throw;
        }
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var detail = (await stderr.ConfigureAwait(false)).Trim();
            if (detail.Length > 1500) detail = detail[^1500..];
            throw new InvalidOperationException($"Token-provider setup exited with code {process.ExitCode}: {detail}");
        }
    }
}
