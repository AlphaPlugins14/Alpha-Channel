using System.Buffers;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace AlphaChannel.Plugin.Video;

/// <summary>
/// Encodes raw emulator video frames and stereo PCM audio with FFmpeg,
/// then publishes the resulting H.264/AAC stream to an RTMP endpoint.
/// </summary>
internal sealed class GameBroadcastEncoderSession : IDisposable
{
    private readonly BroadcastDiagnosticsTracker diagnostics = new();
    private readonly object _frameLock =
        new();
    private readonly object _stopLock =
        new();

    private readonly object _lifecycleLock = new();
    private volatile bool _starting;
    private long _generation;
    private long _captureGeneration;
    private string _codec = BroadcastVideoCodec.Software;
    private string _encoderPath = string.Empty;
    private Func<string, bool>? _restart;
    private Process? _process;
    private Stream? _ffmpegInput;
    private NamedPipeServerStream? _ffmpegAudioPipe;

    private string? _ffmpegAudioPipeName;

    private Thread? _writerThread;
    private CancellationTokenSource? _cancel;
    private Task? _stderrTask;
    private Task? _audioWriterTask;

    private byte[]? _pendingFrame;
    private readonly Queue<(byte[] Frame, long Sequence)> _pendingBrowserFrames = new();
    private int PendingVideoFrames => _pendingBrowserFrames.Count + (_pendingFrame is null ? 0 : 1);
    private long _capturedFrameSequence;
    private long _pendingFrameSequence;
    private long _videoTimelineStartTick;
    // Start() returns while codec selection is still asynchronous. A timestamp
    // is usable only after StartCore has established the actual video origin.
    internal long VideoTimelineStartTick => _running && !_starting
        ? Interlocked.Read(ref _videoTimelineStartTick) : 0;
    private long _pendingAudioFrames;
    private long _pendingAudioGapFrames;
    private static readonly byte[] AudioSilence = new byte[4096 * 4];
    private int _audioSampleRate;

    private readonly Queue<byte[]> _pendingAudio =
    new();

    private int _frameWidth;
    private int _frameHeight;
    private int _frameBytes;
    private bool _inputIsBgra;
    private double _inputFrameRate;
    private double _captureFrameRate;
    private double _captureBudget;
    private int _maxPendingAudioPackets = 256;

    // Temporary framebuffer diagnostics.

    private volatile bool _running;
    private volatile bool _stopping;
    private bool _disposed;
    private bool _cleanupFailed;
    internal bool CleanupSucceeded => !_cleanupFailed;

    internal bool IsRunning
    {
        get
        {
            if (_starting) return true;
            // Keep the browser capture pump alive while the exit callback queues
            // a hardware-to-software retry. Stop clears _restart explicitly.
            if (_codec != BroadcastVideoCodec.Software && _restart is not null && _process is not null) return true;
            if (!_running) return false;
            var process = _process;
            if (process is null) return false;
            try { return !process.HasExited; }
            catch { return false; } // The process may be exiting or disposed concurrently.
        }
    }

    internal BroadcastDiagnosticsSnapshot Diagnostics => diagnostics.Snapshot;


    /// <summary>
    /// Starts FFmpeg and prepares it to receive tightly-packed
    /// RGB565 little-endian frames through stdin.
    /// </summary>
    internal bool Start(
        string ffmpegPath,
        string publishUrl,
        int width,
        int height,
        double fps,
        int audioSampleRate,
        int? outputWidth = null,
        int? outputHeight = null,
        int maxPendingAudioPackets = 256,
        int ffmpegAudioQueuePackets = 0,
        string sourceName = "Retro game",
        double? outputFrameRate = null,
        int? videoBitrateKbps = null,
        int audioBitrateKbps = 128,
        bool inputIsBgra = false,
        int? encoderThreads = null)
    {
        lock (_lifecycleLock)
        {
            if (_disposed || !File.Exists(ffmpegPath) || string.IsNullOrWhiteSpace(publishUrl) ||
                width <= 0 || height <= 0 || fps <= 1 || audioSampleRate <= 0) return false;
            if (_starting || IsRunning) return true;
            StopCore();
            var generation = ++_generation;
            _encoderPath = ffmpegPath;
            _restart = codec => StartCore(ffmpegPath, publishUrl, width, height, fps, audioSampleRate,
                outputWidth, outputHeight, maxPendingAudioPackets, ffmpegAudioQueuePackets, sourceName,
                outputFrameRate, videoBitrateKbps, audioBitrateKbps, inputIsBgra, encoderThreads, codec);
            _starting = true;
            diagnostics.Start(sourceName, outputWidth ?? width, outputHeight ?? height, outputFrameRate ?? fps);
            _ = Task.Run(async () =>
            {
                using var timing = new MediaLifecycleTiming(MediaLifecycleStage.EncoderPreparation);
                var codec = await BroadcastVideoCodec.SelectAsync(ffmpegPath, outputWidth ?? width,
                    outputHeight ?? height, Math.Min(fps, outputFrameRate ?? fps), videoBitrateKbps ?? 1200);
                lock (_lifecycleLock)
                {
                    if (_disposed || generation != _generation) return;
                    try
                    {
                        if (!_restart(codec) && codec != BroadcastVideoCodec.Software)
                        {
                            BroadcastVideoCodec.Reject(ffmpegPath, codec);
                            _restart(BroadcastVideoCodec.Software);
                        }
                    }
                    finally { _starting = false; }
                }
            });
            return true;
        }
    }

    private bool StartCore(
        string ffmpegPath,
        string publishUrl,
        int width,
        int height,
        double fps,
        int audioSampleRate,
        int? outputWidth = null,
        int? outputHeight = null,
        int maxPendingAudioPackets = 256,
        int ffmpegAudioQueuePackets = 0,
        string sourceName = "Retro game",
        double? outputFrameRate = null,
        int? videoBitrateKbps = null,
        int audioBitrateKbps = 128,
        bool inputIsBgra = false,
        int? encoderThreads = null,
        string codec = BroadcastVideoCodec.Software)
    {
        if (_disposed)
        {
            AepLog.Error(
                "[GAME-BROADCAST] Cannot start after disposal.");

            return false;
        }

        if (_running)
        {
            AepLog.Warning(
                "[GAME-BROADCAST] Broadcast is already running.");

            return true;
        }

        if (string.IsNullOrWhiteSpace(ffmpegPath) ||
            !File.Exists(ffmpegPath))
        {
            AepLog.Error(
                "[GAME-BROADCAST] FFmpeg was not found.");

            return false;
        }

        if (string.IsNullOrWhiteSpace(publishUrl))
        {
            AepLog.Error(
                "[GAME-BROADCAST] Publish URL was empty.");

            return false;
        }

        if (width <= 0 ||
            height <= 0)
        {
            AepLog.Error(
                "[GAME-BROADCAST] Invalid video size: [redacted]x[redacted]");

            return false;
        }

        if (fps <= 1)
        {
            AepLog.Error(
                "[GAME-BROADCAST] Invalid frame rate.");

            return false;
        }
        if (audioSampleRate <= 0)
        {
            AepLog.Error(
                "[GAME-BROADCAST] Invalid audio sample rate.");

            return false;
        }

        StopCore();

        if (_cleanupFailed)
        {
            diagnostics.Failed("The previous encoder workers could not stop safely.");
            return false;
        }
        _codec = codec;
        _frameWidth =
            width;

        _frameHeight =
            height;

        _inputIsBgra = inputIsBgra;
        _inputFrameRate = fps;
        _captureFrameRate = outputFrameRate is > 0d && !inputIsBgra
            ? Math.Min(fps, outputFrameRate.Value)
            : fps;
        _captureBudget = fps - _captureFrameRate;
        _capturedFrameSequence = _pendingFrameSequence = 0;

        _frameBytes =
           checked(
               width *
               height *
               (inputIsBgra ? 4 : 2));

        _maxPendingAudioPackets = Math.Max(1, maxPendingAudioPackets);
        _audioSampleRate = audioSampleRate;
        var encodedFrameRate = outputFrameRate is > 0d
            ? Math.Min(fps, outputFrameRate.Value)
            : fps;
        diagnostics.Start(sourceName, outputWidth ?? width, outputHeight ?? height, encodedFrameRate);

        try
        {
            //
            // IMPORTANT:
            //
            // Do not log Arguments or publishUrl here.
            // The RTMP URL contains the user's stream secret.
            //

            _ffmpegAudioPipeName =
    $"AlphaChannelAudio_{Guid.NewGuid():N}";

            _ffmpegAudioPipe =
                new NamedPipeServerStream(
                    _ffmpegAudioPipeName,
                    PipeDirection.Out,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

            var arguments =
                string.Join(
                    " ",
                    "-hide_banner",
                    "-loglevel warning",
                    "-nostats",
                    "-stats_period 2",
                    "-progress pipe:2",

// Keep emulator RGB565 pixels packed. Browser frames are already BGRA.
"-f rawvideo",
inputIsBgra ? "-pix_fmt bgra" : "-pix_fmt rgb565le",
$"-video_size {width}x{height}",
$"-framerate {(inputIsBgra ? fps : _captureFrameRate).ToString(
    "0.######",
    System.Globalization.CultureInfo.InvariantCulture)}",
"-i pipe:0",

ffmpegAudioQueuePackets > 0 ? $"-thread_queue_size {ffmpegAudioQueuePackets}" : string.Empty,
"-f s16le",
$"-ar {audioSampleRate}",
"-ac 2",
$"-i \\\\.\\pipe\\{_ffmpegAudioPipeName}",

// Explicitly map the video and audio inputs into the output.
"-map",
"0:v:0",

"-map",
"1:a:0",

BuildVideoFilter(outputWidth, outputHeight, inputIsBgra ? outputFrameRate : null, fps),

// Codec-specific low-latency options shared with the capability test.
string.Join(" ", BroadcastVideoCodec.Options(codec)),
codec == BroadcastVideoCodec.Software && encoderThreads is > 0 ? $"-threads {encoderThreads.Value}" : string.Empty,

videoBitrateKbps is > 0 ? $"-b:v {videoBitrateKbps.Value}k" : string.Empty,
videoBitrateKbps is > 0 ? $"-maxrate {videoBitrateKbps.Value}k" : string.Empty,
videoBitrateKbps is > 0 ? $"-bufsize {videoBitrateKbps.Value * 2}k" : string.Empty,

"-c:a",
"aac",

"-b:a",
$"{Math.Max(32, audioBitrateKbps)}k",

// Encode AAC at a standard broadly-supported sample rate.
// FFmpeg resamples the emulator PCM when necessary.
"-ar",
"48000",
                    // Keep keyframes frequent for live playback.
                    $"-g {Math.Max(
                        1,
                        (int)Math.Round(encodedFrameRate * 2.0))}",
                    "-keyint_min 1",
                    "-sc_threshold 0",

                    // RTMP uses FLV as the transport container.
                    "-f flv",

                    publishUrl.StartsWith("rtmps://", StringComparison.OrdinalIgnoreCase) ? "-tls_verify 1" : string.Empty,
                    publishUrl.StartsWith("rtmps://", StringComparison.OrdinalIgnoreCase)
                        ? "-ca_file " + QuoteArgument(AlphaChannel.Media.MediaTlsTrust.BundlePath) : string.Empty,

                    QuoteArgument(
                        publishUrl));

            var startInfo =
                new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = false
                };

            _process =
                new Process
                {
                    StartInfo = startInfo,
                    EnableRaisingEvents = true
                };

            _process.Exited +=
                OnFFmpegExited;



            //
            // Do NOT log the complete FFmpeg command line here because
            // it contains the RTMP stream key.
            //

            if (!_process.Start())
            {
                AepLog.Error(
                    "[GAME-BROADCAST] FFmpeg Process.Start returned false.");

                diagnostics.Failed("FFmpeg could not start.");

                CleanupProcess();

                return false;
            }

            _ffmpegInput =
                _process.StandardInput.BaseStream;

            _cancel =
                new CancellationTokenSource();

            _videoTimelineStartTick = Stopwatch.GetTimestamp();
            _running =
                true;

            //
            // Drain stderr asynchronously so FFmpeg can never block
            // because its stderr pipe filled up.
            //

            var stderrProcess = _process;
            var stderrCancellation = _cancel.Token;
            _stderrTask =
                Task.Run(
                    () => ReadFFmpegErrorsAsync(
                        stderrProcess,
                        stderrCancellation));

            //
            // Emulator callbacks never write to FFmpeg themselves.
            // This worker performs all potentially-blocking pipe I/O.
            //

            var writerInput = _ffmpegInput;
            var writerCancellation = _cancel.Token;
            _writerThread =
                new Thread(
                    () => WriterLoop(writerInput, writerCancellation))
                {
                    IsBackground = true,
                    Name = "alpha-game-broadcast"
                };

            _writerThread.Start();

            //
            // Audio is written through a separate named pipe because stdin
            // is already used by the raw video stream.
            //

            var workerCancellation =
                _cancel.Token;

            var sessionAudioPipe = _ffmpegAudioPipe;
            _audioWriterTask = Task.Run(
                async () =>
                {
                    try
                    {
                        var audioPipe =
                            sessionAudioPipe;

                        if (audioPipe is null)
                        {
                            return;
                        }

                        await audioPipe.WaitForConnectionAsync(
                            workerCancellation);



                        while (_running && !workerCancellation.IsCancellationRequested)
                        {
                            byte[]? audio =
      null;
                            long gapFrames = 0;

                            lock (_frameLock)
                            {
                                if (workerCancellation.IsCancellationRequested) break;
                                if (_pendingAudio.Count > 0)
                                {
                                    audio =
                                        _pendingAudio.Dequeue();
                                    _pendingAudioFrames -= audio.Length / 4;
                                    gapFrames = _pendingAudioGapFrames;
                                    _pendingAudioGapFrames = 0;
                                }

                                diagnostics.SetQueues(PendingVideoFrames, _pendingAudio.Count);
                            }

                            if (audio is null)
                            {
                                await Task.Delay(
                                    2,
                                    workerCancellation);

                                continue;
                            }

                            await WriteAudioWithGapAsync(audioPipe, audio, gapFrames, workerCancellation);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Normal during shutdown.
                    }
                    catch (IOException)
                    {
                        if (_running)
                        {
                            AepLog.Warning(
                                "[GAME-BROADCAST] Audio pipe stopped.");
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                        // Normal during shutdown.
                    }
                    catch (Exception)
                    {
                        if (_running)
                        {
                            AepLog.Warning(
                                "[GAME-BROADCAST] Audio writer stopped.");
                        }
                    }
                });



            return true;
        }
        catch (Exception)
        {
            AepLog.Error(
                "[GAME-BROADCAST] Failed to start FFmpeg.");

            diagnostics.Failed("FFmpeg could not start.");

            StopCore();

            return false;
        }
    }

    internal void SubmitAudio(
    IntPtr data,
    int frames)
    {
        var captureGeneration = Interlocked.Read(ref _captureGeneration);
        if (!_running ||
            data == IntPtr.Zero ||
            frames <= 0)
        {
            return;
        }

        try
        {
            // Libretro supplies signed 16-bit stereo PCM:
            // 2 channels * 2 bytes per sample.
            int bytes =
                frames * 4;

            var audio =
                new byte[bytes];

            Marshal.Copy(
                data,
                audio,
                0,
                bytes);

            lock (_frameLock)
            {
                if (!_running || captureGeneration != _captureGeneration) return;
                _pendingAudio.Enqueue(
                    audio);
                _pendingAudioFrames += frames;

                // Allow bounded headroom for browser encoder startup/scheduler
                // stalls. Bound by duration as well as packet count: Chromium
                // and WebView2 can supply different-sized capture packets.
                long discarded = 0;
                while (_pendingAudio.Count > _maxPendingAudioPackets ||
                       (_inputIsBgra && _pendingAudioFrames > _audioSampleRate))
                {
                    var discardedFrames = _pendingAudio.Dequeue().Length / 4;
                    _pendingAudioFrames -= discardedFrames;
                    // Raw PCM has no timestamps. Removing samples permanently
                    // shortens its clock and stalls the wall-clock video input.
                    // Keep only a count of the lost duration; restore it with
                    // silence before the next retained packet reaches FFmpeg.
                    if (_inputIsBgra) _pendingAudioGapFrames += discardedFrames;

                    discarded++;
                }

                diagnostics.DroppedAudio(discarded);
                diagnostics.SetQueues(PendingVideoFrames, _pendingAudio.Count);
            }
        }
        catch (Exception)
        {
            if (_running)
            {
                AepLog.Warning(
                    "[GAME-BROADCAST] Audio submission failed.");
            }
        }
    }


    /// <summary>
    /// Submits one RGB565 emulator frame.
    ///
    /// The libretro framebuffer pointer is only valid for the duration
    /// of its callback, so the frame must be copied immediately.
    ///
    /// The input pitch may contain padding. FFmpeg is given tightly
    /// packed rows, so each row is copied separately.
    /// </summary>
    internal unsafe void SubmitVideoFrame(
    IntPtr data,
    int width,
    int height,
    int pitch)
    {
        var captureGeneration = Interlocked.Read(ref _captureGeneration);
        if (!_running ||
            _inputIsBgra ||
            data == IntPtr.Zero)
        {
            return;
        }

        if (width != _frameWidth ||
            height != _frameHeight)
        {
            return;
        }

        //
        // Gambatte/libretro is supplying a 16-bit framebuffer.
        //
        // The source pitch can be larger than width * 2:
        //
        //   160 pixel image = 320 useful bytes
        //   Gambatte pitch   = 512 bytes
        //
        // Therefore we walk each source row using pitch rather
        // than treating the native framebuffer as tightly packed.
        //

        const int sourceBytesPerPixel =
            2;

        var sourceRowBytes =
            width *
            sourceBytesPerPixel;

        if (pitch <
            sourceRowBytes)
        {
            return;
        }

        var destinationRowBytes =
            width *
            sourceBytesPerPixel;

        var requiredBytes =
            destinationRowBytes *
            height;

        if (requiredBytes !=
            _frameBytes)
        {
            return;
        }

        // Skip frames before copying or sending them to FFmpeg. Audio is
        // submitted separately for every emulator callback.
        if (_captureFrameRate < _inputFrameRate)
        {
            _captureBudget += _captureFrameRate;
            if (_captureBudget < _inputFrameRate)
                return;
            _captureBudget -= _inputFrameRate;
        }

        byte[]? frame =
            null;

        try
        {
            frame =
                ArrayPool<byte>.Shared.Rent(
                    requiredBytes);

            fixed (byte* destinationBase =
                   frame)
            {
                var sourceBase =
                    (byte*)data;

                for (var y = 0;
                     y < height;
                     y++)
                {
                    var sourceRow =
                        sourceBase +
                        (y * pitch);

                    var destinationRow =
                        destinationBase +
                        (y * destinationRowBytes);

                    Buffer.MemoryCopy(sourceRow, destinationRow,
                        destinationRowBytes, sourceRowBytes);
                }
            }


            //
            // Hand the packed emulator frame to the FFmpeg writer.
            //
            // If FFmpeg is still busy with the previous frame, replace that
            // pending frame with the newest one rather than blocking Gambatte.
            //

            lock (_frameLock)
            {
                if (!_running || captureGeneration != _captureGeneration) return;
                var replaced = _pendingFrame is not null;
                if (_pendingFrame is not null)
                {
                    ArrayPool<byte>.Shared.Return(
                        _pendingFrame);
                }

                _pendingFrame =
                    frame;
                _pendingFrameSequence = ++_capturedFrameSequence;

                diagnostics.CapturedFrame(replaced);

                frame =
                    null;

                Monitor.Pulse(
                    _frameLock);
            }
        }
        catch (Exception)
        {
            AepLog.Warning(
                "[GAME-BROADCAST] Frame submission failed.");
        }
        finally
        {
            if (frame is not null)
            {
                ArrayPool<byte>.Shared.Return(
                    frame);
            }
        }
    }

    internal void DiscardPendingAudio()
    {
        lock (_frameLock)
        {
            // Privacy discards the captured content, not its elapsed duration.
            if (_inputIsBgra) _pendingAudioGapFrames += _pendingAudioFrames;
            _pendingAudio.Clear();
            _pendingAudioFrames = 0;
            diagnostics.SetQueues(PendingVideoFrames, 0);
        }
    }

    // Alpha Channel embedded-browser integration. CEF already supplies BGRA32,
    // so browser frames can enter the established FFmpeg queue without a
    // needless RGB565 round trip.
    internal unsafe void SubmitBgraVideoFrame(IntPtr data, int width, int height, int pitch, long? captureTimestamp = null)
    {
        var captureGeneration = Interlocked.Read(ref _captureGeneration);
        if (!_running || !_inputIsBgra || data == IntPtr.Zero || width != _frameWidth || height != _frameHeight)
            return;

        var rowBytes = checked(width * 4);
        var requiredBytes = checked(rowBytes * height);
        if (pitch < rowBytes || requiredBytes != _frameBytes) return;

        byte[]? frame = null;
        try
        {
            frame = ArrayPool<byte>.Shared.Rent(requiredBytes);
            fixed (byte* destinationBase = frame)
            {
                var sourceBase = (byte*)data;
                for (var y = 0; y < height; y++)
                    Buffer.MemoryCopy(sourceBase + y * pitch, destinationBase + y * rowBytes,
                        rowBytes, rowBytes);
            }

            lock (_frameLock)
            {
                if (!_running || captureGeneration != _captureGeneration) return;
                // Browser capture is paced by wall time, not emulator callbacks.
                // Count scheduler stalls as missing slots too, so a slower pump
                // cannot permanently shorten video relative to continuous PCM.
                var elapsed = Math.Max(0, (captureTimestamp ?? Stopwatch.GetTimestamp()) - _videoTimelineStartTick);
                _pendingFrameSequence = _capturedFrameSequence = Math.Max(_capturedFrameSequence + 1,
                    1 + (long)(elapsed * _inputFrameRate / Stopwatch.Frequency));
                // FFmpeg's interleaved inputs may consume raw video in bursts.
                // Preserve intermediate images through brief pipe stalls instead
                // of replacing them and later filling their slots with repeats.
                var limit = Math.Clamp((int)Math.Ceiling(_inputFrameRate / 2), 2, 15);
                var replaced = _pendingBrowserFrames.Count >= limit;
                if (replaced) ArrayPool<byte>.Shared.Return(_pendingBrowserFrames.Dequeue().Frame);
                _pendingBrowserFrames.Enqueue((frame, _pendingFrameSequence));
                diagnostics.CapturedFrame(replaced);
                diagnostics.SetQueues(PendingVideoFrames, _pendingAudio.Count);
                frame = null;
                Monitor.Pulse(_frameLock);
            }
        }
        catch (Exception)
        {
            AepLog.Warning("[GAME-BROADCAST] Browser frame submission failed.");
        }
        finally
        {
            if (frame is not null) ArrayPool<byte>.Shared.Return(frame);
        }
    }


    private void WriterLoop(Stream input, CancellationToken cancellation)
    {
        using var timeline = new BroadcastVideoTimeline();
        var nextTimingLogUtc = DateTime.MinValue;
        long repairedSlots = 0;
        try
        {
            while (_running && !cancellation.IsCancellationRequested)
            {
                byte[]? frame =
                    null;
                long sequence;

                lock (_frameLock)
                {
                    while (_running && !cancellation.IsCancellationRequested &&
                           _pendingFrame is null && _pendingBrowserFrames.Count == 0)
                    {
                        Monitor.Wait(
                            _frameLock,
                            100);
                    }

                    if (!_running || cancellation.IsCancellationRequested)
                    {
                        break;
                    }

                    if (_pendingBrowserFrames.TryDequeue(out var browserFrame))
                    {
                        frame = browserFrame.Frame;
                        sequence = browserFrame.Sequence;
                    }
                    else
                    {
                        frame = _pendingFrame;
                        sequence = _pendingFrameSequence;
                        _pendingFrame = null;
                    }

                    diagnostics.SetQueues(PendingVideoFrames, _pendingAudio.Count);
                }

                if (frame is null)
                {
                    continue;
                }

                try
                {

                    repairedSlots += timeline.Write(input, frame, _frameBytes, sequence, cancellation);
                    frame = null; // The timeline owns this pooled frame now.
                    if (repairedSlots > 0 && DateTime.UtcNow >= nextTimingLogUtc)
                    {

                        repairedSlots = 0;
                        nextTimingLogUtc = DateTime.UtcNow.AddSeconds(30);
                    }
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    break;
                }
                catch (IOException)
                {
                    if (_running)
                    {
                        AepLog.Error(
                            "[GAME-BROADCAST] FFmpeg video pipe closed.");
                    }

                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception)
                {
                    if (_running)
                    {
                        AepLog.Error(
                            "[GAME-BROADCAST] Video writer failed.");
                    }

                    break;
                }
                finally
                {
                    if (frame is not null) ArrayPool<byte>.Shared.Return(frame);
                }
            }
        }
        finally
        {
            // A cancelled worker may finish after a new broadcast starts.
            // It must not stop that replacement session.
            if (!cancellation.IsCancellationRequested) _running = false;
        }
    }


    private async Task ReadFFmpegErrorsAsync(
        Process process,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line =
                    await process.StandardError.ReadLineAsync();

                if (line is null)
                {
                    break;
                }

                if (cancellationToken.IsCancellationRequested) break;

                if (!string.IsNullOrWhiteSpace(line))
                {
                    if (diagnostics.TryConsumeProgress(line))
                    {
                        continue;
                    }

                    var failure = diagnostics.ObserveEncoderError(line);
                    var details = BroadcastEncoderErrors.Details(line);
                    if (BroadcastEncoderErrors.IsFailure(failure, details, line))
                        AepLog.Warning($"[FFMPEG-BROADCAST] Encoder failure category={failure} details={details}.");
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // Normal during shutdown.
        }
        catch (InvalidOperationException)
        {
            // Process already exited/disposed.
        }
        catch (Exception)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                AepLog.Warning(
                    "[GAME-BROADCAST] FFmpeg stderr reader stopped.");
            }
        }
    }


    internal static async Task WriteAudioWithGapAsync(Stream output, byte[] audio, long gapFrames, CancellationToken cancellation)
    {
        while (gapFrames > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            var frames = (int)Math.Min(gapFrames, AudioSilence.Length / 4);
            await output.WriteAsync(AudioSilence.AsMemory(0, frames * 4), cancellation).ConfigureAwait(false);
            gapFrames -= frames;
        }
        await output.WriteAsync(audio.AsMemory(), cancellation).ConfigureAwait(false);
    }

    internal void MaskPendingBrowserFrames(byte[] privacyFrame)
    {
        lock (_frameLock)
        {
            if (!_inputIsBgra || privacyFrame.Length != _frameBytes) return;
            foreach (var pending in _pendingBrowserFrames)
                Buffer.BlockCopy(privacyFrame, 0, pending.Frame, 0, _frameBytes);
        }
    }

    private void OnFFmpegExited(object? sender, EventArgs args)
    {
        // Cleanup/retry runs outside Process.Exited and is serialized with Stop.
        if (sender is not Process exited) return;
        _ = Task.Run(() =>
        {
            lock (_lifecycleLock)
            {
                if (_disposed || _stopping || !ReferenceEquals(exited, _process)) return;
                // Process.Exited may precede the stderr worker's final reads.
                // Give it a bounded chance to retain the safe failure category
                // before Stop cancels and disposes the old encoder session.
                try { _stderrTask?.Wait(TimeSpan.FromMilliseconds(250)); } catch { }
                int? code = null;
                try { code = exited.ExitCode; } catch { }
                var failedCodec = _codec;
                if (failedCodec != BroadcastVideoCodec.Software && _restart is not null)
                {
                    _starting = true;
                    try
                    {
                        BroadcastVideoCodec.Reject(_encoderPath, failedCodec);
                        {}
                        StopCore();
                        _restart(BroadcastVideoCodec.Software);
                    }
                    finally { _starting = false; }
                }
                else
                {
                    StopCore();
                    diagnostics.Failed("FFmpeg stopped unexpectedly.", code);
                    AepLog.Warning("[GAME-BROADCAST] FFmpeg exited unexpectedly (code [redacted]).");
                }
            }
        });
    }

    internal void Stop()
    {
        lock (_lifecycleLock)
        {
            ++_generation;
            _starting = false;
            _restart = null;
            StopCore();
        }
    }

    private void StopCore()
    {
        lock (_stopLock)
        {
        if (_stopping)
        {
            return;
        }

        _stopping =
            true;

        try
        {
            _running =
                false;

            _cancel?.Cancel();

            // Release blocked input writes before joining either worker. Never
            // flush a live pipe during shutdown: the peer may no longer read it.
            try { _ffmpegAudioPipe?.Dispose(); } catch { }
            _ffmpegAudioPipe = null;
            try { _ffmpegInput?.Dispose(); } catch { }
            _ffmpegInput = null;
            try
            {
                if (_process is { HasExited: false } && !_process.WaitForExit(500))
                {
                    _process.Kill(entireProcessTree: true);
                    if (!_process.WaitForExit(2000)) _cleanupFailed = true;
                }
            }
            catch (Exception)
            {
                // The process may exit naturally between the timeout and Kill.
                // An already-exited process must not poison replacement startup.
                var exited = false;
                try { exited = _process is null || _process.HasExited; } catch { }
                if (!exited)
                {
                    _cleanupFailed = true;
                    AepLog.Warning("[GAME-BROADCAST] Encoder exit cleanup failed.");
                }
            }

            lock (_frameLock)
            {
                Monitor.PulseAll(
                    _frameLock);
            }


            if (_writerThread is not null &&
                _writerThread != Thread.CurrentThread)
            {
                try
                {
                    if (!_writerThread.Join(2000))
                    {
                        _cleanupFailed = true;
                        AepLog.Warning("[GAME-BROADCAST] Video writer did not stop within 2 seconds.");
                    }
                }
                catch
                {
                    // Best-effort shutdown.
                }
            }

            _writerThread =
                null;


            lock (_frameLock)
            {
                if (_pendingFrame is not null)
                {
                    ArrayPool<byte>.Shared.Return(
                        _pendingFrame);

                    _pendingFrame =
                        null;
                }

                _pendingAudio.Clear();
                while (_pendingBrowserFrames.TryDequeue(out var browserFrame))
                    ArrayPool<byte>.Shared.Return(browserFrame.Frame);
                _pendingAudioFrames = 0;
                _pendingAudioGapFrames = 0;
                Interlocked.Increment(ref _captureGeneration);
            }


            //
            // Closing the audio pipe releases both our writer and
            // FFmpeg's named-pipe input if either side is still waiting.
            //

            try
            {
                _ffmpegAudioPipe?.Dispose();
            }
            catch
            {
                // Best effort.
            }

            _ffmpegAudioPipe =
                null;

            _ffmpegAudioPipeName =
                null;


            //
            // Closing stdin gives FFmpeg EOF and allows it to finish the
            // FLV stream cleanly where possible.
            //

            try
            {
                _ffmpegInput?.Dispose();
            }
            catch
            {
                // Best effort.
            }

            _ffmpegInput =
                null;


            if (_process is not null)
            {
                try
                {
                    if (!_process.HasExited)
                    {
                        if (!_process.WaitForExit(
                                2000))
                        {
                            _process.Kill(
                                entireProcessTree: true);

                            _process.WaitForExit(
                                2000);
                        }
                    }
                }
                catch
                {
                    // Best-effort process cleanup.
                }
            }

            _cleanupFailed |= !WaitForWorker(
                _audioWriterTask,
                "audio writer");
            _cleanupFailed |= !WaitForWorker(
                _stderrTask,
                "stderr reader");

            _audioWriterTask =
                null;
            _stderrTask =
                null;


            CleanupProcess();

            diagnostics.Stop();


            try
            {
                _cancel?.Dispose();
            }
            catch
            {
                // Best effort.
            }

            _cancel =
                null;



        }
        finally
        {
            _stopping =
                false;
        }
        }
    }


    private void CleanupProcess()
    {
        if (_process is null)
        {
            return;
        }

        try
        {
            _process.Exited -=
                OnFFmpegExited;
        }
        catch
        {
            // Best effort.
        }

        try
        {
            _process.Dispose();
        }
        catch
        {
            // Best effort.
        }

        _process =
            null;
    }


    private static string BuildVideoFilter(
        int? outputWidth,
        int? outputHeight,
        double? outputFrameRate,
        double inputFrameRate)
    {
        var filters = new List<string>();

        if (outputWidth is > 0 && outputHeight is > 0)
        {
            filters.Add($"scale={outputWidth}:{outputHeight}:flags=fast_bilinear");
        }

        if (outputFrameRate is > 0d && outputFrameRate.Value < inputFrameRate - 0.01d)
        {
            filters.Add(
                $"fps={outputFrameRate.Value.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)}");
        }

        return filters.Count == 0
            ? string.Empty
            : $"-vf {string.Join(',', filters)}";
    }

    private static string QuoteArgument(
        string value)
    {
        //
        // The publish URL contains '&' / '?' characters and potentially
        // other command-line-sensitive characters. ProcessStartInfo is
        // not using a shell, but quoting still keeps the URL one argument.
        //

        return "\"" +
               value.Replace(
                   "\"",
                   "\\\"") +
               "\"";
    }

    private static bool WaitForWorker(
        Task? task,
        string name)
    {
        if (task is null || task.IsCompleted)
        {
            return true;
        }

        try
        {
            if (!task.Wait(TimeSpan.FromSeconds(2)))
            {
                AepLog.Warning(
                    "[GAME-BROADCAST] [redacted] did not stop within 2 seconds.");
                return false;
            }
        }
        catch (Exception)
        {
            return task.IsCompleted;
        }
        return true;
    }


    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }
        GC.SuppressFinalize(this);
    }
}
