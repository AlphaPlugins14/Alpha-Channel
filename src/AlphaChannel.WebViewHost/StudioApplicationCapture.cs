using System.Diagnostics;
using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AlphaChannel.WebViewHost;

// Owns activation and the recorder even when the user switches sources during startup.
// No endpoint-wide fallback: a failed application capture must never capture other apps.
internal sealed class StudioApplicationCapture : IDisposable
{
    [SupportedOSPlatformGuard("windows10.0.20348")]
    internal static bool Supported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348);
    private readonly CancellationTokenSource stop = new();
    private readonly object bufferLock = new();
    private readonly BufferedWaveProvider buffer = new(new WaveFormat(48000, 16, 2), TimeSpan.FromMilliseconds(200))
        { ReadFully = true, DiscardOnBufferOverflow = true };
    private readonly Task worker;
    private volatile bool running;
    private string? error;
    private int disposed;
    internal bool Running => running;
    internal string? Error => Volatile.Read(ref error);
    internal Task Completion => worker;

    internal StudioApplicationCapture(int processId, long startedUtcTicks)
    {
        worker = Task.Run(async () =>
        {
            try
            {
                if (!Supported) throw new PlatformNotSupportedException();
                using var process = Process.GetProcessById(processId);
                if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != startedUtcTicks)
                    throw new InvalidOperationException("Application changed.");
                using var recorder = await new WasapiRecorderBuilder()
                    .WithProcessLoopback((uint)processId, ProcessLoopbackMode.IncludeTargetProcessTree)
                    .WithFormat(buffer.WaveFormat).BuildAsync().ConfigureAwait(false);
                stop.Token.ThrowIfCancellationRequested();
                recorder.DataAvailable += (data, flags, _, _) =>
                {
                    lock (bufferLock)
                    {
                        if (stop.IsCancellationRequested) return;
                        if (buffer.BufferedDuration.TotalMilliseconds > 100) buffer.ClearBuffer();
                        if ((flags & AudioClientBufferFlags.Silent) == 0) buffer.AddSamples(data);
                    }
                };
                recorder.RecordingStopped += (_, e) =>
                {
                    if (!stop.IsCancellationRequested)
                        Volatile.Write(ref error, e.Exception == null ? "Application capture stopped. Turn capture on to retry." :
                            "Application audio disconnected. Turn capture off and on to retry.");
                    running = false;
                };
                recorder.StartRecording(); running = true;
                while (!stop.IsCancellationRequested && running)
                {
                    await Task.Delay(200, stop.Token).ConfigureAwait(false);
                    if (process.HasExited)
                    {
                        Volatile.Write(ref error, "Application closed. Refresh and choose a running application.");
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (PlatformNotSupportedException) { Volatile.Write(ref error, "Application capture requires Windows build 20348 or later."); }
            catch { Volatile.Write(ref error, "Application audio could not start. Refresh and choose a running application."); }
            finally
            {
                running = false;
                lock (bufferLock) buffer.ClearBuffer();
            }
        });
    }
    internal void Read(Span<byte> destination)
    {
        destination.Clear();
        lock (bufferLock)
            if (running && Volatile.Read(ref disposed) == 0) buffer.Read(destination);
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel();
        // Release the current recorder before another capture opens. If COM activation
        // is still pending, its worker will dispose the result before starting it.
        try { worker.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _ = worker.ContinueWith(_ => stop.Dispose(), TaskScheduler.Default);
        lock (bufferLock) buffer.ClearBuffer();
    }
}
