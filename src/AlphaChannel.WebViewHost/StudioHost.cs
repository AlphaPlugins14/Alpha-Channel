using System.IO.Pipes;
using System.Text.Json;
using AlphaChannel.BroadcastStudio;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AlphaChannel.WebViewHost;

internal sealed class StudioHost : IDisposable
{
    private readonly StudioMixer mixer = new();
    private readonly StudioCommandQueue commands = new();
    private readonly WaveFormat format = new(48000, 16, 2);
    private StudioSettings settings = new();
    private readonly StudioDeckPlayback decks;
    internal StudioHost() => decks = new((track, offset) => new StudioDecoder(ffmpeg, track.Path, offset));
    private StudioBroadcast? broadcast;
    private StudioPreview? preview;
    private StudioApplicationCapture? applicationCapture;
    private StudioApplication[] applications = [];
    private int excludedProcessId;
    private string? applicationError;
    private WasapiRecorder? recorder;
    private WasapiPlayer? player;
    private MMDevice? micDevice, outputDevice;
    private BufferedWaveProvider? microphone, monitor;
    private StudioDevice[] inputs = [], outputs = [];
    private volatile bool microphoneRunning;
    private string ffmpeg = "", error = "", deviceError = "";
    private readonly object micLock = new();

    internal static async Task<int> RunAsync(string pipeName)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var stop = new CancellationTokenSource();
        await pipe.ConnectAsync(10000, stop.Token);
        using var host = new StudioHost();
        using var reader = new StreamReader(pipe, leaveOpen: true);
        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        var read = Task.Run(async () =>
        {
            try { while (await reader.ReadLineAsync(stop.Token) is { } line)
                if (JsonSerializer.Deserialize<StudioCommand>(line) is { } command)
                    await host.commands.WriteAsync(command, stop.Token); }
            finally { stop.Cancel(); }
        });
        try { await host.LoopAsync(writer, stop.Token); }
        catch (OperationCanceledException) { }
        finally { stop.Cancel(); try { await read; } catch { } }
        return 0;
    }
    private async Task LoopAsync(StreamWriter writer, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
        var mic = new byte[StudioDecoder.BlockBytes];
        var output = new byte[mic.Length];
        var local = new byte[mic.Length];
        var applicationAudio = new byte[mic.Length];
        var tick = 0;
        float musicPeak = 0, micPeak = 0, leftPeak = 0, rightPeak = 0;
        var clipped = false;
        EnumerateDevices();
        while (await timer.WaitForNextTickAsync(token))
        {
            var commandTime = System.Diagnostics.Stopwatch.StartNew();
            for (var processed = 0; processed < 4 && commands.TryRead(out var command); processed++)
            {
                try { Execute(command); } catch { error = "The requested studio action failed. Check your file or audio device."; }
                if (commandTime.ElapsedMilliseconds >= 4) break;
            }
            byte[] song = [];
            if (settings.MusicSource == StudioMusicSource.Application)
            {
                applicationCapture?.Read(applicationAudio);
                song = applicationCapture == null ? [] : applicationAudio;
            }
            var deckAudio = settings.MusicSource == StudioMusicSource.SongQueue ? decks.Read(.02) : (Array.Empty<byte>(), Array.Empty<byte>());
            Array.Clear(mic);
            lock (micLock) microphone?.Read(mic.AsSpan());
            var levels = settings.MusicSource == StudioMusicSource.Application
                ? mixer.Mix(song, mic, output, local, settings)
                : mixer.MixDecks(deckAudio.Item1, deckAudio.Item2, mic, output, local,
                    settings with { Crossfader = decks.Crossfader });
            musicPeak = Math.Max(musicPeak, levels.Music); micPeak = Math.Max(micPeak, levels.Mic);
            leftPeak = Math.Max(leftPeak, levels.Left); rightPeak = Math.Max(rightPeak, levels.Right); clipped |= levels.Clipping;
            if (monitor is { } destination)
            {
                if (destination.BufferedDuration.TotalMilliseconds > 160) destination.ClearBuffer();
                destination.AddSamples(local.AsSpan());
            }
            preview?.Push(output);
            broadcast?.Push((byte[])output.Clone());
            if (++tick % 10 != 0) continue;
            var active = decks.Snapshot(decks.ActiveDeck);
            var snapshot = new StudioSnapshot { NextTrack = decks.UpcomingTrack, RemovedTrackIds = decks.RemovedTrackIds, PreviewUrl = preview?.Url, Ready = !string.IsNullOrEmpty(ffmpeg), Playing = decks.Playing,
                DeckA = decks.Snapshot(0), DeckB = decks.Snapshot(1), Crossfader = decks.Crossfader, Fading = decks.Fading,
                TrackId = active.TrackId, Position = active.Position, Duration = active.Duration,
                MusicLevel = musicPeak, MicLevel = micPeak, OutputLeft = leftPeak, OutputRight = rightPeak,
                Clipping = clipped, MicActive = microphoneRunning && settings.MicEnabled && (!settings.PushToTalk || settings.TalkHeld),
                BroadcastState = broadcast?.State ?? "OFF AIR", BroadcastSeconds = broadcast?.Seconds ?? 0,
                Error = broadcast?.Error ?? (error.Length > 0 ? error : decks.Error), DeviceError = deviceError.Length > 0 ? deviceError : null,
                Microphones = inputs, Outputs = outputs,
                ApplicationCaptureSupported = StudioApplicationCapture.Supported, Applications = applications,
                ApplicationCapturing = applicationCapture?.Running == true,
                ApplicationError = applicationCapture?.Error ?? applicationError };
            await writer.WriteLineAsync(JsonSerializer.Serialize(snapshot).AsMemory(), token);
            musicPeak = micPeak = leftPeak = rightPeak = 0; clipped = false;
        }
    }
    private void Execute(StudioCommand command)
    {
        if (command.Kind is "load-deck" or "play-deck" or "seek-deck" or "fade-deck" or "play" or "next" or "previous" or "seek") error = "";
        switch (command.Kind)
        {
            case "init":
                ffmpeg = command.Ffmpeg ?? ""; excludedProcessId = command.ExcludedProcessId;
                applications = StudioApplications.List(excludedProcessId); goto case "settings";
            case "settings":
                if (command.Settings is not { } next) break;
                var changeMic = next.MicEnabled != settings.MicEnabled || next.MicDevice != settings.MicDevice;
                var changeOutput = player == null || next.OutputDevice != settings.OutputDevice;
                var changeApplication = next.MusicSource != settings.MusicSource ||
                    next.ApplicationProcessId != settings.ApplicationProcessId ||
                    next.ApplicationStartedUtcTicks != settings.ApplicationStartedUtcTicks ||
                    next.ApplicationCaptureEnabled != settings.ApplicationCaptureEnabled;
                decks.Settings(next);
                settings = next;
                if (changeApplication) SetApplication();
                if (changeMic) SetMicrophone();
                if (changeOutput) SetOutput();
                break;
            case "devices": EnumerateDevices(); SetMicrophone(); SetOutput(); break;
            case "applications":
                applications = StudioApplications.List(excludedProcessId);
                if (applicationCapture?.Error != null) SetApplication();
                break;
            case "queue": decks.Queue(command.Tracks ?? []); break;
            case "load-deck":
                if (settings.MusicSource == StudioMusicSource.SongQueue) decks.Load(Math.Clamp(command.Deck, 0, 1), command.TrackId);
                break;
            case "play-deck":
                if (settings.MusicSource == StudioMusicSource.SongQueue) decks.Play(Math.Clamp(command.Deck, 0, 1));
                break;
            case "pause-deck": decks.Pause(Math.Clamp(command.Deck, 0, 1)); break;
            case "stop-deck": decks.StopDeck(Math.Clamp(command.Deck, 0, 1)); break;
            case "unload-deck": decks.Unload(Math.Clamp(command.Deck, 0, 1)); break;
            case "seek-deck":
                if (settings.MusicSource == StudioMusicSource.SongQueue) decks.Seek(Math.Clamp(command.Deck, 0, 1), command.Position);
                break;
            case "fade-deck":
                if (settings.MusicSource == StudioMusicSource.SongQueue) decks.FadeTo(Math.Clamp(command.Deck, 0, 1));
                break;
            case "crossfader": decks.SetCrossfader((float)command.Position); break;
            case "play": if (settings.MusicSource == StudioMusicSource.SongQueue) decks.LegacyPlay(command.TrackId); break;
            case "pause": decks.PauseAll(); break;
            case "next": if (settings.MusicSource == StudioMusicSource.SongQueue) decks.Next(1); break;
            case "previous": if (settings.MusicSource == StudioMusicSource.SongQueue) decks.Next(-1); break;
            case "seek": if (settings.MusicSource == StudioMusicSource.SongQueue) decks.Seek(decks.ActiveDeck, command.Position); break;
            case "preview": preview ??= new StudioPreview(); break;
            case "off-air": broadcast?.Dispose(); broadcast = null; break;
            case "live":
                if (command.Destination == null || broadcast != null) break;
                error = "";
                broadcast = new StudioBroadcast(ffmpeg, command.Destination, settings.Bitrate);
                break;
            case "stop":
                preview?.Dispose(); preview = null;
                broadcast?.Dispose(); broadcast = null;
                decks.Stop();
                settings = settings with { ApplicationCaptureEnabled = false, MicEnabled = false, TalkHeld = false };
                SetApplication(); SetMicrophone();
                monitor?.ClearBuffer();
                break;
        }
    }
    private void SetApplication()
    {
        applicationCapture?.Dispose(); applicationCapture = null; applicationError = null;
        if (monitor != null) monitor.ClearBuffer();
        if (settings.MusicSource != StudioMusicSource.Application || !settings.ApplicationCaptureEnabled) return;
        if (!StudioApplicationCapture.Supported)
        { applicationError = "Application capture requires Windows build 20348 or later."; return; }
        var app = applications.FirstOrDefault(a => a.ProcessId == settings.ApplicationProcessId &&
            a.StartedUtcTicks == settings.ApplicationStartedUtcTicks);
        if (app == null || StudioApplications.IsExcluded(app.ProcessId, excludedProcessId))
        { applicationError = "Refresh and choose a running application to capture."; return; }
        applicationCapture = new StudioApplicationCapture(app.ProcessId, app.StartedUtcTicks);
    }
    private void EnumerateDevices()
    {
        try
        {
            using var devices = new MMDeviceEnumerator();
            inputs = List(devices, DataFlow.Capture); outputs = List(devices, DataFlow.Render);
        }
        catch { deviceError = "Audio devices could not be listed. Press Refresh devices to retry."; }
    }
    private static StudioDevice[] List(MMDeviceEnumerator devices, DataFlow flow)
    {
        var list = new List<StudioDevice>();
        foreach (var device in devices.EnumerateAudioEndPoints(flow, DeviceState.Active))
            using (device) list.Add(new StudioDevice(device.ID, device.FriendlyName));
        return list.ToArray();
    }
    private void SetMicrophone()
    {
        microphoneRunning = false;
        recorder?.Dispose(); recorder = null; micDevice?.Dispose(); micDevice = null;
        lock (micLock) microphone = null;
        if (!settings.MicEnabled) return;
        try
        {
            using var devices = new MMDeviceEnumerator();
            micDevice = settings.MicDevice.Length > 0 ? devices.GetDevice(settings.MicDevice) : devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            var buffer = new BufferedWaveProvider(format, TimeSpan.FromMilliseconds(200)) { ReadFully = true, DiscardOnBufferOverflow = true };
            lock (micLock) microphone = buffer;
            recorder = new WasapiRecorderBuilder().WithDevice(micDevice).WithFormat(format).Build();
            recorder.DataAvailable += (data, flags, _, _) =>
            {
                lock (micLock)
                {
                    if (buffer.BufferedDuration.TotalMilliseconds > 100) buffer.ClearBuffer();
                    if ((flags & AudioClientBufferFlags.Silent) == 0) buffer.AddSamples(data);
                }
            };
            recorder.RecordingStopped += (_, e) => { microphoneRunning = false; if (e.Exception != null) deviceError = "Microphone disconnected. Choose an available microphone and refresh devices."; };
            recorder.StartRecording(); microphoneRunning = true; deviceError = "";
        }
        catch { deviceError = "Microphone could not start. Choose another device and refresh devices."; }
    }
    private void SetOutput()
    {
        player?.Dispose(); player = null; monitor = null; outputDevice?.Dispose(); outputDevice = null;
        try
        {
            using var devices = new MMDeviceEnumerator();
            outputDevice = settings.OutputDevice.Length > 0 ? devices.GetDevice(settings.OutputDevice) : devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            monitor = new BufferedWaveProvider(format, TimeSpan.FromMilliseconds(300)) { ReadFully = true, DiscardOnBufferOverflow = true };
            player = new WasapiPlayerBuilder().WithDevice(outputDevice).WithLatency(80).Build();
            player.PlaybackStopped += (_, e) => { if (e.Exception != null) deviceError = "Local monitoring disconnected. Choose an available output device and refresh devices."; };
            player.Init(monitor); player.Play();
        }
        catch { deviceError = "Local monitoring could not start. Choose an available output device and refresh devices."; }
    }
    public void Dispose()
    {
        try { preview?.Dispose(); applicationCapture?.Dispose(); broadcast?.Dispose(); decks.Dispose(); recorder?.Dispose(); player?.Dispose(); micDevice?.Dispose(); outputDevice?.Dispose(); }
        finally { commands.Dispose(); }
    }
}
