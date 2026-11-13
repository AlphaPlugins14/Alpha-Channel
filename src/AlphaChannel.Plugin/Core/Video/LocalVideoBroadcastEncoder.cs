using System.Diagnostics;
using System.Globalization;

namespace AlphaChannel.Plugin.Video;

/// <summary>
/// Runs a second, muted-to-the-host FFmpeg reader over the current local
/// video file and publishes it to AlphaChannel's RTMP relay.
///
/// The local MPV renderer remains responsible for what the host sees and
/// hears. FFmpeg is only started while a Watch Party viewer is present.
/// </summary>
internal sealed class LocalVideoBroadcastEncoder : IDisposable
{
    private readonly BroadcastDiagnosticsTracker diagnostics = new();
    private readonly object lifecycleLock = new();
    private volatile bool starting;
    private long generation;
    private Func<string, bool>? restart;
    private string activeCodec = BroadcastVideoCodec.Software;
    private Process? process;
    private bool stopping;
    private bool disposed;

    internal string? LastError
    {
        get;
        private set;
    }

    internal BroadcastDiagnosticsSnapshot Diagnostics => diagnostics.Snapshot;

    internal bool IsRunning
    {
        get
        {
            if (starting) return true;
            if (activeCodec != BroadcastVideoCodec.Software && restart is not null && process is not null)
            {
                try { if (!process.HasExited || process.ExitCode != 0) return true; } catch { }
            }
            var current =
                process;

            if (current is null)
            {
                return false;
            }

            try
            {
                return !current.HasExited;
            }
            catch
            {
                return false;
            }
        }
    }

    internal bool Start(
        string ffmpegPath,
        string sourcePath,
        string publishUrl,
        double startPositionSeconds,
        LocalVideoBroadcastQuality quality,
        string? subtitleFile, int frameRate = 30, int? audioStreamIndex = null)
    {
        lock (lifecycleLock)
        {
            if (disposed || !File.Exists(ffmpegPath) || !File.Exists(sourcePath) || string.IsNullOrWhiteSpace(publishUrl))
            {
                LastError = "FFmpeg, the local video file, or the relay address is unavailable.";
                return false;
            }
            StopCore();
            var session = ++generation;
            var requestedAt = Stopwatch.GetTimestamp();
            restart = codec => StartCore(ffmpegPath, sourcePath, publishUrl,
                startPositionSeconds + Stopwatch.GetElapsedTime(requestedAt).TotalSeconds,
                quality, subtitleFile, frameRate, codec, audioStreamIndex);
            starting = true;
            LastError = null;
            diagnostics.Start("Local video");
            _ = Task.Run(async () =>
            {
                var settings = quality.Settings(frameRate);
                var codec = await BroadcastVideoCodec.SelectAsync(ffmpegPath, settings.MaximumWidth,
                    settings.MaximumHeight, settings.MaximumFrameRate, settings.VideoBitrateKbps);
                lock (lifecycleLock)
                {
                    if (disposed || session != generation) return;
                    try
                    {
                        if (!restart(codec) && codec != BroadcastVideoCodec.Software)
                        {
                            BroadcastVideoCodec.Reject(ffmpegPath, codec);
                            restart(BroadcastVideoCodec.Software);
                        }
                    }
                    finally { starting = false; }
                }
            });
            return true;
        }
    }

    private bool StartCore(
        string ffmpegPath,
        string sourcePath,
        string publishUrl,
        double startPositionSeconds,
        LocalVideoBroadcastQuality quality,
        string? subtitleFile,
        int frameRate,
        string codec, int? audioStreamIndex)
    {
        if (disposed)
        {
            LastError =
                "The local video broadcaster has already been disposed.";

            return false;
        }

        StopCore();

        LastError =
            null;

        if (string.IsNullOrWhiteSpace(
                ffmpegPath) ||
            !File.Exists(
                ffmpegPath))
        {
            LastError =
                "FFmpeg is not installed yet. Try again in a few seconds.";

            return false;
        }

        if (string.IsNullOrWhiteSpace(
                sourcePath) ||
            !File.Exists(
                sourcePath))
        {
            LastError =
                "The selected local video file could not be found.";

            return false;
        }

        if (string.IsNullOrWhiteSpace(
                publishUrl))
        {
            LastError =
                "The relay publish address is unavailable.";

            return false;
        }

        stopping =
            false;

        activeCodec = codec;
        var settings =
            quality.Settings(frameRate);

        diagnostics.Start("Local video");

        try
        {
            var startInfo =
                new ProcessStartInfo
                {
                    FileName =
                        ffmpegPath,

                    // The ASS filter can refer to the cached captions by their
                    // filename, avoiding a Windows drive colon in its graph.
                    WorkingDirectory = subtitleFile is null
                        ? Path.GetDirectoryName(sourcePath)!
                        : Path.GetDirectoryName(subtitleFile)!,

                    UseShellExecute =
                        false,

                    CreateNoWindow =
                        true,

                    RedirectStandardInput =
                        false,

                    RedirectStandardOutput =
                        false,

                    RedirectStandardError =
                        true
                };

            //
            // Do not replace this with one assembled argument string.
            // ArgumentList safely handles spaces in local filenames.
            //
            // Never log ArgumentList: publishUrl contains the private
            // AlphaChannel stream key.
            //

            startInfo.ArgumentList.Add(
                "-hide_banner");

            startInfo.ArgumentList.Add(
                "-loglevel");

            startInfo.ArgumentList.Add(
                "warning");

            startInfo.ArgumentList.Add("-nostats");
            startInfo.ArgumentList.Add("-stats_period");
            startInfo.ArgumentList.Add("2");
            startInfo.ArgumentList.Add("-progress");
            startInfo.ArgumentList.Add("pipe:2");

            startInfo.ArgumentList.Add(
                "-nostdin");

            //
            // Seek before opening the input so a viewer joining midway
            // through a long file does not require FFmpeg to decode from
            // the beginning.
            //

            startInfo.ArgumentList.Add(
                "-ss");

            startInfo.ArgumentList.Add(
                Math.Max(
                        0d,
                        startPositionSeconds)
                    .ToString(
                        "0.###",
                        CultureInfo.InvariantCulture));

            //
            // Read the file at normal playback speed rather than
            // transcoding and uploading it as quickly as possible.
            //

            startInfo.ArgumentList.Add(
                "-re");

            startInfo.ArgumentList.Add(
                "-i");

            startInfo.ArgumentList.Add(
                sourcePath);

            startInfo.ArgumentList.Add(
                "-map");

            startInfo.ArgumentList.Add(
                "0:v:0");

            startInfo.ArgumentList.Add(
                "-map");

            startInfo.ArgumentList.Add(
                audioStreamIndex is >= 0
                    ? "0:" + audioStreamIndex.Value.ToString(CultureInfo.InvariantCulture)
                    : "0:a:0?");

            // Fit inside the selected profile while retaining the source's
            // aspect ratio. min(iw/ih, profile dimensions) prevents a small
            // source from ever being enlarged.
            startInfo.ArgumentList.Add(
                "-vf");

            var scaleFilter =
                $"scale=w='min(iw,{settings.MaximumWidth})':h='min(ih,{settings.MaximumHeight})':force_original_aspect_ratio=decrease:force_divisible_by=2:flags=lanczos";
            var seekSeconds = Math.Max(0d, startPositionSeconds)
                .ToString("0.###", CultureInfo.InvariantCulture);
            var filter = subtitleFile is not null
                // -ss resets incoming video PTS to zero. Restore source time
                // while libass matches subtitle events, then restore relay
                // timestamps for normal HLS playback.
                ? $"setpts=PTS+{seekSeconds}/TB,ass=filename={EscapeFilterFilename(Path.GetFileName(subtitleFile))},setpts=PTS-{seekSeconds}/TB,{scaleFilter}"
                : scaleFilter;
            startInfo.ArgumentList.Add(
                filter);

            // Cap the automatically selected source rate instead of forcing
            // slower videos up to the selected FPS. Keep relay output CFR.
            startInfo.ArgumentList.Add("-fpsmax");
            startInfo.ArgumentList.Add(settings.MaximumFrameRate.ToString("0.###", CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-fps_mode");
            startInfo.ArgumentList.Add("cfr");

            foreach (var option in BroadcastVideoCodec.Options(codec))
                startInfo.ArgumentList.Add(option);

            startInfo.ArgumentList.Add(
                "-b:v");

            startInfo.ArgumentList.Add(
                $"{settings.VideoBitrateKbps}k");

            startInfo.ArgumentList.Add(
                "-maxrate");

            startInfo.ArgumentList.Add(
                $"{settings.VideoBitrateKbps}k");

            startInfo.ArgumentList.Add(
                "-bufsize");

            startInfo.ArgumentList.Add(
                $"{settings.VideoBitrateKbps * 2}k");

            startInfo.ArgumentList.Add(
                "-c:a");

            startInfo.ArgumentList.Add(
                "aac");

            startInfo.ArgumentList.Add(
                "-b:a");

            startInfo.ArgumentList.Add(
                $"{settings.AudioBitrateKbps}k");

            startInfo.ArgumentList.Add(
                "-ar");

            startInfo.ArgumentList.Add(
                "48000");

            // Surround-sound files otherwise remain 5.1-channel AAC. Send a
            // stereo HLS track for consistent relay and viewer compatibility.
            startInfo.ArgumentList.Add("-ac");
            startInfo.ArgumentList.Add("2");

            //
            // Frequent keyframes help newly joined HLS viewers begin
            // displaying the stream sooner.
            //

            startInfo.ArgumentList.Add(
                "-g");

            startInfo.ArgumentList.Add(
                ((int)settings.MaximumFrameRate * 2).ToString(CultureInfo.InvariantCulture));

            startInfo.ArgumentList.Add(
                "-keyint_min");

            startInfo.ArgumentList.Add(
                "1");

            startInfo.ArgumentList.Add(
                "-sc_threshold");

            startInfo.ArgumentList.Add(
                "0");

            startInfo.ArgumentList.Add(
                "-f");

            startInfo.ArgumentList.Add(
                "flv");

            if (publishUrl.StartsWith("rtmps://", StringComparison.OrdinalIgnoreCase))
            {
                startInfo.ArgumentList.Add("-tls_verify");
                startInfo.ArgumentList.Add("1");
                startInfo.ArgumentList.Add("-ca_file");
                startInfo.ArgumentList.Add(AlphaChannel.Media.MediaTlsTrust.BundlePath);
            }
            startInfo.ArgumentList.Add(publishUrl);

            var newProcess =
                new Process
                {
                    StartInfo =
                        startInfo,

                    EnableRaisingEvents =
                        true
                };

            newProcess.Exited += (_, _) =>
            {
                // Never retry from the event itself; Stop/replacement may already
                // own this session and dispose it while this callback is queued.
                _ = Task.Run(() =>
                {
                    lock (lifecycleLock)
                    {
                        if (disposed || stopping || !ReferenceEquals(process, newProcess)) return;
                        int? code = null;
                        try { code = newProcess.ExitCode; } catch { }
                        if (code == 0) { diagnostics.Stop(0); return; }
                        if (codec != BroadcastVideoCodec.Software && restart is not null)
                        {
                            starting = true;
                            try
                            {
                                BroadcastVideoCodec.Reject(ffmpegPath, codec);
                                {}
                                StopCore();
                                restart(BroadcastVideoCodec.Software);
                            }
                            finally { starting = false; }
                        }
                        else
                        {
                            StopCore();
                            LastError = "The local video relay encoder stopped unexpectedly.";
                            diagnostics.Failed("FFmpeg stopped unexpectedly.", code);
                            AepLog.Warning("[LOCAL-VIDEO-BROADCAST] FFmpeg stopped unexpectedly.");
                        }
                    }
                });
            };

            process = newProcess;
            if (!newProcess.Start())
            {
                process = null;
                newProcess.Dispose();

                LastError =
                    "FFmpeg could not be started.";

                diagnostics.Failed("FFmpeg could not start.");

                return false;
            }

            //
            // Consume stderr so FFmpeg cannot block when its error
            // pipe becomes full. Do not log the raw output because it
            // can contain both the private path and relay stream key.
            //

            newProcess.ErrorDataReceived +=
                (_, args) =>
                {
                    if (!string.IsNullOrWhiteSpace(args.Data))
                    {
                        if (!diagnostics.TryConsumeProgress(args.Data))
                        {
                            var failure = diagnostics.ObserveEncoderError(args.Data);
                            if (failure != BroadcastEncoderFailure.None)
                                AepLog.Warning($"[LOCAL-VIDEO-BROADCAST] Encoder failure category={failure}.");
                        }
                    }
                };

            newProcess.BeginErrorReadLine();

            

            return true;
        }
        catch (Exception exception)
        {
            LastError =
                $"The local video broadcaster could not start: {exception.Message}";

            AepLog.Warning(
                "[LOCAL-VIDEO-BROADCAST] Failed to start.");

            diagnostics.Failed("FFmpeg could not start.");

            StopCore();

            return false;
        }
    }

    private static string EscapeFilterFilename(string filename)
    {
        // FFmpeg parses filter option values, then the enclosing filtergraph.
        // ArgumentList removes shell quoting, but both FFmpeg levels still
        // require escaping (notably apostrophes and commas in movie names).
        static string Escape(string value, string special)
        {
            var result = new System.Text.StringBuilder(value.Length);
            foreach (var character in value)
            {
                if (special.Contains(character)) result.Append('\\');
                result.Append(character);
            }
            return result.ToString();
        }

        return Escape(Escape(filename, "\\':"), "\\'[],;");
    }

    internal void Stop()
    {
        lock (lifecycleLock)
        {
            ++generation;
            starting = false;
            restart = null;
            StopCore();
        }
    }

    private void StopCore()
    {
        var current =
            process;

        process =
            null;

        if (current is null)
        {
            return;
        }

        stopping =
            true;

        try
        {
            if (!current.HasExited)
            {
                current.Kill(
                    entireProcessTree: true);

                current.WaitForExit(
                    2000);
            }
        }
        catch (Exception)
        {
            AepLog.Warning(
                "[LOCAL-VIDEO-BROADCAST] Failed to stop FFmpeg cleanly.");
        }
        finally
        {
            int? exitCode = null;
            try { if (current.HasExited) exitCode = current.ExitCode; } catch { }
            diagnostics.Stop(exitCode);
            current.Dispose();

            stopping =
                false;
        }

        
    }

    public void Dispose()
    {
        lock (lifecycleLock)
        {
            if (disposed) return;
            disposed = true;
            Stop();
        }
    }
}
