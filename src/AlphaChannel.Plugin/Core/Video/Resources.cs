using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Newtonsoft.Json;
using Dalamud.Utility;
using SharpCompress.Archives;
using SharpCompress.Common;
using Newtonsoft.Json.Linq;
using AlphaChannel.Plugin.Net;

namespace AlphaChannel.Plugin.Video;

internal sealed class Resources : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _configDir;
    private readonly object mpvPathGate = new();
    private string? sessionMpvPath;
    private readonly YouTubePoTokenService _youtubePoTokenService;
    private readonly CancellationTokenSource shutdown = new();
    private readonly CancellationToken shutdownToken;
    private int disposeStarted;
    private readonly ConcurrentDictionary<int, Task> backgroundTasks = new();
    private int nextBackgroundTaskId;
    private volatile bool disposed;
    private readonly OnDemandFeaturePreparation featurePreparation;
    internal ChromiumPackageInstaller Chromium { get; }
    internal YouTubePoTokenInstaller PoTokens { get; }
    private readonly FFmpegPackageInstaller ffmpegPackage;

    internal YouTubePlaybackPolicy YouTubePolicy
    {
        get;
    }

    internal string[] MpvCheckResult { get; private set; } = [string.Empty, string.Empty];
    internal string[] YtdlpCheckResult { get; private set; } = [string.Empty, string.Empty];
    private long _ntpTimeOffset;
    private long _sysTimeOffset;

    internal long CurrentTimeNTPNormalizedMilliseconds => _ntpTimeOffset > 0 ? _ntpTimeOffset + (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _sysTimeOffset) : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    internal string RomsDirectory => Path.Combine(_configDir, "roms");


    internal Resources()
    {
        shutdownToken = shutdown.Token;
        featurePreparation = new(shutdownToken);
        _httpClient =
            PluginHttpClients.CreateDownloadClient();

        _httpClient.DefaultRequestHeaders.Add(
            "User-Agent",
            "AlphaChannelUpdater/1.0");

        _configDir =
            Plugin.PluginInterface
                .ConfigDirectory
                .FullName;

        ffmpegPackage = new FFmpegPackageInstaller(_httpClient, _configDir, shutdownToken);

        Chromium = new ChromiumPackageInstaller(_httpClient, _configDir,
            Path.Combine(Plugin.PluginInterface.AssemblyLocation.DirectoryName!, "chromium-download.json"));

        // A native DLL cannot be replaced safely by a Dalamud hot reload. If
        // this process already has Alpha Channel's libmpv mapped, keep using
        // that exact build for the remainder of the FFXIV session. Otherwise,
        // pin the newest build that existed when this plugin instance started.
        // A background update may still be installed, but it is not selected
        // until the next full game process.
        sessionMpvPath =
            FindLoadedAlphaChannelMpv() ??
            FindNewestInstalledMpv();

        if (sessionMpvPath is not null)
        {
            
        }

        PoTokens = new YouTubePoTokenInstaller(_httpClient, _configDir, shutdownToken);
        _youtubePoTokenService = new YouTubePoTokenService(PoTokens.EnsureInstalledAsync);

        YouTubePolicy =
            new YouTubePlaybackPolicy(
                Plugin.Cfg,
                _youtubePoTokenService,
                () => PoTokens.IsInstalled,
                () => PoTokens.AndroidFallbackRequired);



        Initialize();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposeStarted, 1) != 0) return;

        disposed = true;
        shutdown.Cancel();
        _httpClient.Dispose();

        try
        {
            Task.WaitAll(
                backgroundTasks.Values.Concat(PoTokens.PendingInstallation is { } install ? new[] { install } : Array.Empty<Task>()).Distinct().ToArray(),
                TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
            
        }

        backgroundTasks.Clear();
        _youtubePoTokenService.Dispose();
        NativeLoader.Release(this);
        shutdown.Dispose();

        GC.SuppressFinalize(this);
    }

    private void Track(Task task)
    {
        if (disposed)
        {
            _ = task.ContinueWith(
                completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously |
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            return;
        }

        var id =
            Interlocked.Increment(ref nextBackgroundTaskId);

        backgroundTasks[id] = task;
        _ = task.ContinueWith(
            completedTask =>
            {
                _ = completedTask.Exception; // Observe faults even when the task finishes before shutdown.
                backgroundTasks.TryRemove(id, out _);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void Initialize()
    {
        Directory.CreateDirectory(RomsDirectory);
        // Time synchronization is independent of feature downloads.
        Track(GetNtpUtcAsync().ContinueWith(task =>
        {
            if (task.IsCompletedSuccessfully) _ntpTimeOffset = task.GetResultSafely();
            _sysTimeOffset = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }));
        // Optional packages are prepared only after their feature is requested.
    }

    internal Task PrepareMediaAsync(bool remote) => PrepareFeatureAsync(remote ? "remote-media" : "media",async () =>
    {
        if (GetLocationMPV(prepareIfMissing: false) is null) { await CheckMPVAsync(); if (!await DownloadMPVAsync()) throw new IOException("MPV preparation failed."); }
        if (remote && GetLocationYTDLP(prepareIfMissing: false) is null) { await CheckYTDLPAsync(); if (!await DownloadYTDLPAsync()) throw new IOException("Video support preparation failed."); }
    });
    private Task PrepareFeatureAsync(string feature,Func<Task> install)
    {
        if (disposed) return Task.CompletedTask;
        var task = featurePreparation.Request(feature,install); Track(task); return task;
    }
    private string? RequestCore(string directory,string filename,Func<Task<bool>> install)
    {
        var path=Path.Combine(_configDir,directory,filename);
        if (File.Exists(path)) return path;
        _=PrepareFeatureAsync(directory,async () => { if (!await install().ConfigureAwait(false)) throw new IOException("Feature preparation failed."); });
        return null;
    }
    internal void PrepareChromium() => Track(PrepareFeatureAsync("chromium",InstallChromiumAsync));

    private async Task InstallChromiumAsync()
    {
        await Chromium.InstallAsync(shutdownToken).ConfigureAwait(false);
        if (Chromium.Status == ChromiumPackageStatus.Failed)
            AepLog.Warning("[CHROMIUM] Download/install failed.");
    }

    internal void RetryChromiumDownload()
    {
        if (disposed || !Chromium.TryQueueRetry())
            return;
        featurePreparation.Retry("chromium");
        PrepareChromium();
    }
	internal string? GetLocationMPV(bool prepareIfMissing = true)
    {
        lock (mpvPathGate)
        {
            if (sessionMpvPath is not null && File.Exists(sessionMpvPath))
            {
                return sessionMpvPath;
            }

            // This is normally only reached on a first installation where no
            // MPV build existed when Resources was constructed. Re-check the
            // process first so a hot reload can never jump to a newly installed
            // build while an older native module remains mapped.
            sessionMpvPath =
                FindLoadedAlphaChannelMpv() ??
                FindNewestInstalledMpv();

            if (sessionMpvPath is not null)
            {
                
            }

            if (sessionMpvPath is null && prepareIfMissing) _ = PrepareMediaAsync(false);
            return sessionMpvPath;
        }
    }

    private string? FindNewestInstalledMpv()
    {
        const string filenameStart = "mpv-dev-lgpl-x86_64-";
        foreach (var dir in Directory.GetDirectories(_configDir, $"{filenameStart}*")
                     .OrderByDescending(d => d, StringComparer.Ordinal))
        {
            var dll = Path.Combine(dir, "libmpv-2.dll");
            if (File.Exists(dll))
                return Path.GetFullPath(dll);
        }

        return null;
    }

    private string? FindLoadedAlphaChannelMpv()
    {
        try
        {
            var configRoot =
                Path.GetFullPath(_configDir)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;

            using var process = Process.GetCurrentProcess();
            foreach (ProcessModule module in process.Modules)
            {
                if (!string.Equals(
                        module.ModuleName,
                        "libmpv-2.dll",
                        StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(module.FileName))
                {
                    continue;
                }

                var loadedPath = Path.GetFullPath(module.FileName);
                if (loadedPath.StartsWith(configRoot, StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(loadedPath))
                {
                    
                    return loadedPath;
                }
            }
        }
        catch (Exception)
        {
            AepLog.Warning(
                "[MPV] Could not inspect loaded native modules; using the startup build.");
        }

        return null;
    }

    internal string? GetLocationYTDLP(bool prepareIfMissing = true)
    {
        const string filenameStart = "yt-dlp";

        foreach (var dir in Directory
                     .GetDirectories(_configDir, $"{filenameStart}*")
                     .OrderByDescending(d => d, StringComparer.Ordinal))
        {
            var exe = Path.Combine(dir, "yt-dlp.exe");

            if (File.Exists(exe))
            {
                
                return exe;
            }
        }

        if (prepareIfMissing) _ = PrepareFeatureAsync("yt-dlp",async () =>
        {
            if (GetLocationYTDLP(prepareIfMissing: false) is not null) return;
            await CheckYTDLPAsync();
            if (!await DownloadYTDLPAsync()) throw new IOException("Video metadata preparation failed.");
        });
        return null;
    }

    internal string? GetLocationSNES9X() => RequestCore("snes9x","snes9x_libretro.dll",DownloadSNES9XAsync);

    internal string? GetLocationGambatte() => RequestCore("gambatte","gambatte_libretro.dll",DownloadGambatteAsync);

    internal string? GetLocationNestopia() => RequestCore("nestopia","nestopia_libretro.dll",DownloadNestopiaAsync);

    internal string? GetLocationMgba() => RequestCore("mgba","mgba_libretro.dll",DownloadMgbaAsync);

    internal Task StopYouTubeProviderAsync() => YouTubePolicy.StopProviderWhenIdleAsync();
    internal bool IsYouTubeProviderRunning => _youtubePoTokenService.IsRunning;
    internal Task RefreshYouTubeTokensAsync(CancellationToken token) => _youtubePoTokenService.RefreshTokensAsync(token);

    internal string? GetLocationGearsystem() => RequestCore("gearsystem","gearsystem_libretro.dll",DownloadGearsystemAsync);


    internal string? GetLocationFFmpeg(bool prepareIfMissing = true)
    {
        var path=ffmpegPackage.Location;
        if(path is null && prepareIfMissing) _=PrepareFFmpegAsync();
        return path;
    }

    internal Task PrepareFFmpegAsync() => PrepareFeatureAsync("ffmpeg", async () =>
    {
        if (!await DownloadFFmpegAsync().ConfigureAwait(false))
            throw new IOException("FFmpeg preparation failed.");
    });

    internal string FFmpegUnavailableMessage => ffmpegPackage.LastError
        ?? "Preparing and verifying the required FFmpeg build. Try again shortly.";


    internal async Task CheckMPVAsync()
    {
        string filenameStart = "mpv-dev-lgpl-x86_64-";
        string filenameEnd = ".7z";
        string url = "https://api.github.com/repos/zhongfly/mpv-winbuild/releases/latest";
        MpvCheckResult = await CheckForUpdateAsync(_configDir, filenameStart, filenameEnd, url);
    }
    internal async Task CheckYTDLPAsync()
    {
        // The 32-bit yt-dlp_x86.exe build saves ~4MB but fails to spawn as a subprocess ("Subprocess
        // failed: init" from mpv's ytdl_hook) under at least one real Wine setup this was tested on -
        // spawning a 32-bit child process from inside a Wine-hosted 64-bit game needs WoW64 support
        // that isn't guaranteed to be present. Not worth the risk for 4MB; use the 64-bit build.
        string filenameStart = "yt-dlp.exe";
        string filenameEnd = ".exe";
        string url = "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest";
        YtdlpCheckResult = await CheckForUpdateAsync(_configDir, filenameStart, filenameEnd, url);
    }
    // downloadURL is empty either because CheckForUpdateAsync already found the local folder up
    // to date, or because the check itself failed (rate limit, no network yet at plugin load) and
    // fell back to its empty-result default - either way there is nothing to fetch, and calling
    // HttpClient.GetAsync with an empty URI throws. Callers (AetherStreamApp.Settings) already
    // re-run the check first when this is empty, but this guard stays as the actual line that
    // can never hand HttpClient an invalid request.
    internal async Task<bool> DownloadMPVAsync()
    {
        string filenameStart = "mpv-dev-lgpl-x86_64-";
        string filenameEnd = ".7z";
        string downloadURL = MpvCheckResult[0];
        string folderName = MpvCheckResult[1];
        if (downloadURL.Length == 0)
        {
            return false;
        }

        var installed =
            await UpdateAsync(
                _configDir,
                filenameStart,
                filenameEnd,
                downloadURL,
                folderName);

        if (installed && sessionMpvPath is not null)
        {
            var installedPath =
                Path.Combine(
                    _configDir,
                    folderName,
                    "libmpv-2.dll");

            if (!string.Equals(
                    Path.GetFullPath(installedPath),
                    Path.GetFullPath(sessionMpvPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                
            }
        }

        return installed;
    }
    internal async Task<bool> DownloadYTDLPAsync()
    {
        string filenameStart = "yt-dlp";
        string filenameEnd = ".exe";
        string downloadURL = YtdlpCheckResult[0];
        string folderName = YtdlpCheckResult[1];
        if (downloadURL.Length == 0)
        {
            return false;
        }

        return await UpdateAsync(_configDir, filenameStart, filenameEnd, downloadURL, folderName);
    }
    private async Task<string[]> CheckForUpdateAsync(string configDir, string nameStartsWith, string nameEndsWith, string checkURL)
    {
        try {
            string json = await _httpClient.GetStringAsync(checkURL, shutdownToken);
            var doc = JObject.Parse(json);
            long remoteId = doc["id"]!.Value<long>();
            var asset = doc["assets"]!
                .First(a => a["name"]!.Value<string>()!
                    .StartsWith(nameStartsWith, StringComparison.Ordinal) &&
                    a["name"]!.Value<string>()!.EndsWith(nameEndsWith, StringComparison.Ordinal));

            string assetName = asset["name"]!.Value<string>()!;
            string folderName = assetName.Replace(nameEndsWith, "") + "_" + remoteId;

            string localFolder = Path.Combine(configDir, folderName);

            if (Directory.Exists(localFolder))
            {
                return [string.Empty, folderName]; //Already up to date
            }

            string downloadURL = asset["browser_download_url"]!.Value<string>()!;
            
            return [downloadURL, folderName];
        }
        catch (Exception)
        {
            AepLog.Warning("Failed to check for update ([redacted]).");
            return [string.Empty, string.Empty];
        }
    }

    private async Task<bool> UpdateAsync(string configDir, string nameStartsWith, string nameEndsWith, string downloadURL, string folderName)
    {
        string? tempFile = null;
        string? extractFolder = null;

        try
        {
            
            tempFile = Path.Combine(
                Path.GetTempPath(),
                $"alphachannel-update-{Guid.NewGuid():N}{nameEndsWith}");
            using var response = await _httpClient.GetAsync(
                downloadURL,
                HttpCompletionOption.ResponseHeadersRead,
                shutdownToken);
            await using (var fs = File.OpenWrite(tempFile))
            {
                await response.Content.CopyToAsync(fs, shutdownToken);
            }
            
            if (nameEndsWith == ".7z")
            {
                string targetFolder = Path.Combine(configDir, folderName);
                if (Directory.Exists(targetFolder))
                {
                    TryDeleteOldVersionFolders(configDir, nameStartsWith, keepFolder: targetFolder);
                    return true;
                }

                // Extract into a temp dir, then rename into place — never delete the currently-loaded
                // libmpv folder first (that throws Access Denied under Wine while the DLL is mapped).
                extractFolder = Path.Combine(configDir, Path.GetRandomFileName());
                Directory.CreateDirectory(extractFolder);
                using (var archive = ArchiveFactory.OpenArchive(tempFile))
                {
                    foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
                    {
                        shutdownToken.ThrowIfCancellationRequested();
                        entry.WriteToDirectory(extractFolder, new ExtractionOptions
                        {
                            ExtractFullPath = true,
                            Overwrite = true
                        });
                    }
                }

                Directory.Move(extractFolder, targetFolder);
                extractFolder = null;
                TryDeleteOldVersionFolders(configDir, nameStartsWith, keepFolder: targetFolder);
            }
            else
            {
                string localFolder = Path.Combine(configDir, folderName);
                Directory.CreateDirectory(localFolder);

                string targetPath = Path.Combine(localFolder, nameStartsWith.EndsWith(nameEndsWith, StringComparison.Ordinal) ? nameStartsWith : nameStartsWith + nameEndsWith);
                File.Copy(tempFile, targetPath, overwrite: true);
                TryDeleteOldVersionFolders(configDir, nameStartsWith, keepFolder: localFolder);
            }
            return true;
        }
        catch (Exception)
        {
            AepLog.Error("Error updating [redacted].");
            return false;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(tempFile) && File.Exists(tempFile))
            {
                try { File.Delete(tempFile); }
                catch { }
            }

            if (!string.IsNullOrWhiteSpace(extractFolder) && Directory.Exists(extractFolder))
            {
                try { Directory.Delete(extractFolder, recursive: true); }
                catch { }
            }
        }
    }

    // Best-effort cleanup. libmpv-2.dll stays mapped for the whole process lifetime, so the folder
    // that supplied the current handle often can't be removed until the next full game restart —
    // leave it and keep the session-pinned build instead of failing the whole update.
    private static void TryDeleteOldVersionFolders(string configDir, string nameStartsWith, string keepFolder)
    {
        foreach (string dir in Directory.GetDirectories(configDir, $"{nameStartsWith}*"))
        {
            if (string.Equals(Path.GetFullPath(dir), Path.GetFullPath(keepFolder), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                AepLog.Warning("Leaving old [redacted] folder in place (still in use).");
            }
        }
    }

    internal async Task<bool> DownloadSNES9XAsync()
    {
        const string downloadUrl =
            "https://buildbot.libretro.com/nightly/windows/x86_64/latest/snes9x_libretro.dll.zip";

        string directoryName = "snes9x";
        string? temp = null;

        try
        {
            

            temp = Path.Combine(
                Path.GetTempPath(),
                $"alphachannel-snes9x-{Guid.NewGuid():N}.zip");

            using var response = await _httpClient.GetAsync(
                downloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                shutdownToken);

            

            response.EnsureSuccessStatusCode();

            await using (var fs = File.Create(temp))
            {
                await response.Content.CopyToAsync(fs, shutdownToken);
            }

            

            string localFolder =
                Path.Combine(_configDir, directoryName);

            Directory.CreateDirectory(localFolder);

            using (var archive = ArchiveFactory.OpenArchive(temp))
            {
                foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
                {
                    

                    entry.WriteToDirectory(
                        localFolder,
                        new ExtractionOptions
                        {
                            ExtractFullPath = true,
                            Overwrite = true
                        });
                }
            }

            string expectedDll =
                Path.Combine(
                    localFolder,
                    "snes9x_libretro.dll");

            if (!File.Exists(expectedDll))
            {
                AepLog.Error(
                    "[SNES9X] Download completed but DLL was not found at.");

                return false;
            }

            

            return true;
        }
        catch (Exception)
        {
            AepLog.Error(
                "[SNES9X] Download/install exception.");

            return false;
        }
        finally
        {
            if (temp is not null && File.Exists(temp))
            {
                try
                {
                    File.Delete(temp);
                }
                catch
                {
                    // Temporary cleanup failure is harmless.
                }
            }
        }
    }

    internal async Task<bool> DownloadGambatteAsync()
    {
        const string downloadUrl =
            "https://buildbot.libretro.com/nightly/windows/x86_64/latest/gambatte_libretro.dll.zip";

        const string directoryName =
            "gambatte";

        string? temp =
            null;

        try
        {
            

            temp =
                Path.Combine(
                    Path.GetTempPath(),
                    $"alphachannel-gambatte-{Guid.NewGuid():N}.zip");

            using var response =
                await _httpClient.GetAsync(
                    downloadUrl,
                    HttpCompletionOption.ResponseHeadersRead,
                    shutdownToken);

            

            response.EnsureSuccessStatusCode();

            await using (var fs =
                         File.Create(temp))
            {
                await response.Content
                    .CopyToAsync(fs, shutdownToken);
            }

            var localFolder =
                Path.Combine(
                    _configDir,
                    directoryName);

            Directory.CreateDirectory(
                localFolder);

            using (var archive =
                   ArchiveFactory.OpenArchive(temp))
            {
                foreach (var entry in
                         archive.Entries.Where(
                             entry => !entry.IsDirectory))
                {
                    

                    entry.WriteToDirectory(
                        localFolder,
                        new ExtractionOptions
                        {
                            ExtractFullPath = true,
                            Overwrite = true
                        });
                }
            }

            var expectedDll =
                Path.Combine(
                    localFolder,
                    "gambatte_libretro.dll");

            if (!File.Exists(expectedDll))
            {
                AepLog.Error(
                    "[GAMBATTE] Download completed but DLL was not found at.");

                return false;
            }

            

            return true;
        }
        catch (Exception)
        {
            AepLog.Error(
                "[GAMBATTE] Core download failed.");

            return false;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(temp) &&
                File.Exists(temp))
            {
                try
                {
                    File.Delete(temp);
                }
                catch
                {
                    // Best-effort temp cleanup.
                }
            }
        }
    }

    internal async Task<bool> DownloadNestopiaAsync()
    {
        const string downloadUrl =
            "https://buildbot.libretro.com/nightly/windows/x86_64/latest/nestopia_libretro.dll.zip";
        const string directoryName = "nestopia";
        string? temp = null;

        try
        {
            
            temp = Path.Combine(Path.GetTempPath(), $"alphachannel-nestopia-{Guid.NewGuid():N}.zip");
            using var response = await _httpClient.GetAsync(
                downloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                shutdownToken);
            
            response.EnsureSuccessStatusCode();
            await using (var fs = File.Create(temp))
                await response.Content.CopyToAsync(fs, shutdownToken);

            var localFolder = Path.Combine(_configDir, directoryName);
            Directory.CreateDirectory(localFolder);
            using (var archive = ArchiveFactory.OpenArchive(temp))
            {
                foreach (var entry in archive.Entries.Where(entry => !entry.IsDirectory))
                    entry.WriteToDirectory(localFolder, new ExtractionOptions { ExtractFullPath = true, Overwrite = true });
            }

            var expectedDll = Path.Combine(localFolder, "nestopia_libretro.dll");
            if (!File.Exists(expectedDll))
            {
                AepLog.Error("[NESTOPIA] Download completed but DLL was not found at.");
                return false;
            }

            
            return true;
        }
        catch (Exception)
        {
            AepLog.Error("[NESTOPIA] Core download failed.");
            return false;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(temp) && File.Exists(temp))
            {
                try { File.Delete(temp); }
                catch { }
            }
        }
    }

    internal async Task<bool> DownloadMgbaAsync()
    {
        const string downloadUrl =
            "https://buildbot.libretro.com/nightly/windows/x86_64/latest/mgba_libretro.dll.zip";
        const string directoryName = "mgba";
        string? temp = null;

        try
        {
            
            temp = Path.Combine(Path.GetTempPath(), $"alphachannel-mgba-{Guid.NewGuid():N}.zip");
            using var response = await _httpClient.GetAsync(
                downloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                shutdownToken);
            
            response.EnsureSuccessStatusCode();
            await using (var fs = File.Create(temp))
                await response.Content.CopyToAsync(fs, shutdownToken);

            var localFolder = Path.Combine(_configDir, directoryName);
            Directory.CreateDirectory(localFolder);
            using (var archive = ArchiveFactory.OpenArchive(temp))
            {
                foreach (var entry in archive.Entries.Where(entry => !entry.IsDirectory))
                    entry.WriteToDirectory(localFolder, new ExtractionOptions { ExtractFullPath = true, Overwrite = true });
            }

            var expectedDll = Path.Combine(localFolder, "mgba_libretro.dll");
            if (!File.Exists(expectedDll))
            {
                AepLog.Error("[MGBA] Download completed but DLL was not found at.");
                return false;
            }

            
            return true;
        }
        catch (Exception)
        {
            AepLog.Error("[MGBA] Core download failed.");
            return false;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(temp) && File.Exists(temp))
            {
                try { File.Delete(temp); }
                catch { }
            }
        }
    }

    internal async Task<bool> DownloadGearsystemAsync()
    {
        const string downloadUrl =
            "https://buildbot.libretro.com/nightly/windows/x86_64/latest/gearsystem_libretro.dll.zip";
        const string directoryName = "gearsystem";
        string? temp = null;

        try
        {
            
            temp = Path.Combine(Path.GetTempPath(), $"alphachannel-gearsystem-{Guid.NewGuid():N}.zip");
            using var response = await _httpClient.GetAsync(
                downloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                shutdownToken);
            
            response.EnsureSuccessStatusCode();
            await using (var fs = File.Create(temp))
                await response.Content.CopyToAsync(fs, shutdownToken);

            var localFolder = Path.Combine(_configDir, directoryName);
            Directory.CreateDirectory(localFolder);
            using (var archive = ArchiveFactory.OpenArchive(temp))
            {
                foreach (var entry in archive.Entries.Where(entry => !entry.IsDirectory))
                    entry.WriteToDirectory(localFolder,
                        new ExtractionOptions { ExtractFullPath = true, Overwrite = true });
            }

            var expectedDll = Path.Combine(localFolder, "gearsystem_libretro.dll");
            if (!File.Exists(expectedDll))
            {
                AepLog.Error("[GEARSYSTEM] Download completed but DLL was not found at.");
                return false;
            }

            
            return true;
        }
        catch (Exception)
        {
            AepLog.Error("[GEARSYSTEM] Core download failed.");
            return false;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(temp) && File.Exists(temp))
            {
                try { File.Delete(temp); }
                catch { }
            }
        }
    }

    internal Task<bool> DownloadFFmpegAsync() => ffmpegPackage.EnsureInstalledAsync();

    private Task<long> GetNtpUtcAsync(string server = "pool.ntp.org") => NtpClock.GetUtcAsync(server, shutdownToken);

	internal static class NativeLoader
	{
		private static Resources? _resources;
		private static string? _sessionMpvPath;
		private static bool _registered;

		internal static void Register(Resources resources)
		{
			_resources = resources;
			_sessionMpvPath = resources.GetLocationMPV(prepareIfMissing: false) ?? _sessionMpvPath;
			if (_registered)
			{
				return;
			}

			_registered = true;
			NativeLibrary.SetDllImportResolver(typeof(NativeLoader).Assembly, Resolve);
		}

        internal static void Release(Resources resources)
        {
            if (ReferenceEquals(_resources, resources))
            {
                _resources = null;
            }
        }

		private static IntPtr Resolve(string name, System.Reflection.Assembly assembly, DllImportSearchPath? path)
		{
			switch (name)
			{
				case "libmpv-2":
					// A resolver registered by an older plugin assembly can still be
					// called during hot reload, after Release has cleared its Resources.
					// Keep using the DLL pinned for this game session in that case.
					// During first installation, query Resources again so a newly
					// downloaded build can become available before the first load.
					var location = _resources?.GetLocationMPV();
					if (location is not null)
					{
						_sessionMpvPath = location;
					}
					return TryLoad(location ?? _sessionMpvPath, "MPV");
				default:
					return IntPtr.Zero;
			}
		}

		private static IntPtr TryLoad(string? location, string tag)
		{
			if (string.IsNullOrWhiteSpace(location))
			{
				// No installed build has been selected yet. Let .NET's normal
				// native-library search run without logging a false load failure.
				return IntPtr.Zero;
			}
			if (NativeLibrary.TryLoad(location, out nint handle))
			{
				return handle;
			}
			AepLog.Error("[[redacted]] Failed to load native lib from.");
			return IntPtr.Zero;
		}
	}
}
