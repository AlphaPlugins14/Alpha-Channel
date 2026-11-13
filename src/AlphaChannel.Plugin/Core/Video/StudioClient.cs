using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using AlphaChannel.BroadcastStudio;

namespace AlphaChannel.Plugin.Video;

internal sealed class StudioClient : IDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly StudioCommandQueue commands = new();
    private BrowserProcessJob? job;
    private readonly object processLock = new();
    private readonly Task runner;
    private bool processTreeStopped;
    private StudioSnapshot snapshot = new();
    private int disposed;
    private int finished;
    private int preparing = 1;
    internal bool IsPreparing => Volatile.Read(ref preparing) != 0;
    internal Task CleanupCompletion => runner;
    internal StudioSnapshot Snapshot => Volatile.Read(ref snapshot);
    internal void Send(StudioCommand command)
    {
        if (Volatile.Read(ref disposed) != 0 || Volatile.Read(ref finished) != 0) return;
        if (commands.TryWrite(command)) return;
        // Never silently drop a Stop or playback action when the helper stalls.
        Volatile.Write(ref snapshot, new StudioSnapshot { Error = "Studio audio stopped because its controls were overloaded. Press Restart audio to retry.", BroadcastState = "FAILED" });
        CancelRunner();
    }
    internal StudioClient(string directory, string ffmpeg, StudioSettings settings, StudioTrack[] queue,
        Task? previousCleanup = null)
    {
        Send(new StudioCommand("init") { Ffmpeg = ffmpeg, Settings = settings, ExcludedProcessId = Environment.ProcessId });
        Send(new StudioCommand("queue") { Tracks = queue });
        // RunAsync performs synchronous file copying before its first await.
        // Schedule the entire operation, not just its pipe reads, off the UI thread.
        runner = Task.Run(async () =>
        {
            try
            {
                if (previousCleanup is not null) await previousCleanup.ConfigureAwait(false);
                await RunAsync(directory).ConfigureAwait(false);
            }
            catch (Exception)
            {
                if (Volatile.Read(ref disposed) == 0)
                    Volatile.Write(ref snapshot, new StudioSnapshot { Error = "Studio audio could not finish preparing. Press Play to retry.", BroadcastState = "FAILED" });
                CancelRunner();
                Volatile.Write(ref finished, 1);
                commands.Dispose();
                stop.Dispose();
            }
            finally { Volatile.Write(ref preparing, 0); }
        });
    }
    private Process StartHelper(ProcessStartInfo start)
    {
        lock (processLock)
        {
            if (processTreeStopped || Volatile.Read(ref disposed) != 0) throw new OperationCanceledException();
            stop.Token.ThrowIfCancellationRequested();
            var process = Process.Start(start) ?? throw new IOException();
            try { job!.Attach(process); return process; }
            catch
            {
                try { process.Kill(true); process.WaitForExit(2000); } catch { }
                process.Dispose();
                throw;
            }
        }
    }
    private void StopProcessTree()
    {
        lock (processLock)
        {
            if (processTreeStopped) return;
            processTreeStopped = true;
            try
            {
                job?.StopAndWait();
                
            }
            catch (Exception) { AepLog.Warning("[STUDIO] Process cleanup."); }
            finally { job?.Dispose(); }
        }
    }
    private void CancelRunner()
    {
        try { _ = stop.CancelAsync(); } catch (ObjectDisposedException) { }
    }
    private async Task RunAsync(string directory)
    {
        HelperRuntimeLease? runtime = null;
        var name = "AlphaChannel.Studio." + Guid.NewGuid().ToString("N");
        try
        {
            using (new MediaLifecycleTiming(MediaLifecycleStage.StudioPreparation))
            {
                stop.Token.ThrowIfCancellationRequested();
                runtime = new HelperRuntimeLease(directory, stop.Token);
                stop.Token.ThrowIfCancellationRequested();
                job = new BrowserProcessJob();
            }
            directory = runtime.DirectoryPath;
            using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var start = new ProcessStartInfo(Path.Combine(directory, "AlphaChannel.WebViewHost.exe"))
                { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            start.ArgumentList.Add("--studio"); start.ArgumentList.Add(name);
            start.Environment["DOTNET_DISABLE_GUI_ERRORS"] = "1";
            using var process = StartHelper(start);
            stop.Token.ThrowIfCancellationRequested();
            Volatile.Write(ref preparing, 0);
            var diagnostics = process.StandardError.ReadToEndAsync();
            _ = diagnostics.ContinueWith(failed => { _ = failed.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            using (new MediaLifecycleTiming(MediaLifecycleStage.StudioConnection))
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(12));
                var connection = pipe.WaitForConnectionAsync(timeout.Token);
                var exited = process.WaitForExitAsync(timeout.Token);
                if (await Task.WhenAny(connection, exited).ConfigureAwait(false) != connection)
                {
                    await exited.ConfigureAwait(false);
                    throw new IOException("Studio helper exited before connecting.");
                }
                await connection.ConfigureAwait(false);
                // Retire the losing exit wait rather than leaving it subscribed
                // to a live helper for the duration of playback.
                await timeout.CancelAsync().ConfigureAwait(false);
                try { await exited.ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
            using var reader = new StreamReader(pipe, leaveOpen: true);
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            var write = Task.Run(async () =>
            {
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        var command = await commands.ReadAsync(stop.Token).ConfigureAwait(false);
                        await writer.WriteLineAsync(JsonSerializer.Serialize(command).AsMemory(), stop.Token);
                    }
                }
                catch { CancelRunner(); }
            });
            try
            {
                while (await reader.ReadLineAsync(stop.Token).ConfigureAwait(false) is { } line)
                    if (JsonSerializer.Deserialize<StudioSnapshot>(line) is { } next)
                    {
                        var previous = Snapshot;
                        if (next.BroadcastState != previous.BroadcastState || next.Error != previous.Error)
                            { }
                        if (Volatile.Read(ref disposed) == 0 && !stop.IsCancellationRequested)
                            Volatile.Write(ref snapshot, next);
                    }
                if (!stop.IsCancellationRequested) throw new IOException("Studio helper disconnected.");
            }
            finally
            {
                CancelRunner();
                // Closing the pipe releases both readers and writers before waiting.
                pipe.Dispose();
                try { await write.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch (TimeoutException) { AepLog.Warning("[STUDIO] Pipe writer did not stop within 2 seconds."); }
                catch (Exception) { }
            }
        }
        catch
        {
            if (!stop.IsCancellationRequested) Volatile.Write(ref snapshot, new StudioSnapshot { Error = "Studio audio stopped. Press Restart audio to retry.", BroadcastState = "FAILED" });
        }
        finally
        {
            using var timing = new MediaLifecycleTiming(MediaLifecycleStage.StudioCleanup);
            Volatile.Write(ref finished, 1);
            if (Volatile.Read(ref disposed) == 0 && Snapshot.BroadcastState != "FAILED") Volatile.Write(ref snapshot, new StudioSnapshot
                { Error = "Studio audio stopped. Press Restart audio to retry.", BroadcastState = "FAILED" });
            CancelRunner();
            StopProcessTree();
            runtime?.Dispose();
            commands.Dispose();
            stop.Dispose();
        }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        CancelRunner();
        // The runner owns its job, pipes and temporary files through finally.
        // A replacement may await CleanupCompletion without blocking the UI.
    }
}
