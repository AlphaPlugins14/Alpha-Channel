using AlphaChannel.BroadcastStudio;
using AlphaChannel.Plugin.BroadcastAllowance;
using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private StudioClient? studioClient;
    private Task studioCleanup = Task.CompletedTask;
    private StudioSettings studioSettings = new();
    private bool studioInitialized, studioBroadcastRequested, studioPublished;
    private bool studioSessionActive;
    private bool studioPreviewStarting;
    private Task? studioAudioPreparation;
    private string? studioPreviewUrl;
    internal bool IsStudioPreviewActive => studioSessionActive && !studioPublished;
    private bool StudioOwnPreviewPlaying => studioSessionActive && studioPreviewUrl != null &&
        video.CurrentMediaUrl?.Split('#')[0] == studioPreviewUrl;
    private string? studioSelectedTrackId;
    private string? studioLastPlayingTrackId;
    private string? studioAccount;
    private DateTime studioStartedUtc, studioNextCheckUtc;
    private Task<PreparedLiveAudioSource?>? studioStreamProbe;
    private CancellationTokenSource? studioStreamProbeCancellation;
    private string studioPlaylistName = "";
    private float studioSeekPosition;
    private bool studioSeeking;
    private string? studioLastSession;
    private readonly PluginFilePicker studioFileDialog = new();
    private StudioSnapshot StudioState => studioClient?.Snapshot ?? new();
    private string DjBroadcastContentTitle => spotifyOwnsStudio ? SpotifyBroadcastTitle : string.IsNullOrWhiteSpace(Plugin.Cfg.DjBroadcastName)
        ? "Live DJ broadcast" : Plugin.Cfg.DjBroadcastName.Trim();
    private bool StudioOwnStreamPlaying => radioCredentials is { } radio && queue.Current is { Source: "Music / DJ" } current &&
        current.Url.StartsWith(radio.ListenUrl, StringComparison.OrdinalIgnoreCase);
    private static string StudioQualityLabel(int bitrate) => bitrate switch
    {
        96 => "Data Saver - 96 kbps",
        128 => "Standard - 128 kbps",
        256 => "High Quality - 256 kbps",
        320 => "Highest Quality - 320 kbps",
        _ => "Recommended - 192 kbps"
    };
    private static int StudioQualityRequiredTier(int bitrate) => bitrate switch { 256 => 2, 320 => 3, _ => 0 };
    private int AllowedStudioBitrate(int bitrate)
    {
        var tier = CurrentVerifiedPatreonTier();
        return bitrate switch
        {
            96 or 128 or 192 => bitrate,
            256 when tier >= 2 => 256,
            320 when tier >= 3 => 320,
            320 when tier >= 2 => 256,
            _ => 192
        };
    }
    private void EnforceStudioQualityAccess()
    {
        var allowed = AllowedStudioBitrate(studioSettings.Bitrate);
        if (allowed == studioSettings.Bitrate) return;
        if (studioBroadcastRequested)
        {
            StopStudioBroadcast();
            musicMessage = "Broadcast stopped because its quality requires an active Patreon tier. Choose an available quality and go live again.";
        }
        studioSettings = studioSettings with { Bitrate = allowed };
        StudioSettingsChanged();
    }
    private void EnsureStudio()
    {
        if (!CanUseBroadcastStudio) return;
        if (!studioInitialized)
        {
            studioInitialized = true;
            studioSettings = (Plugin.Cfg.StudioSettings ?? new()) with { MicEnabled = false, TalkHeld = false,
                ApplicationProcessId = 0, ApplicationStartedUtcTicks = 0, ApplicationCaptureEnabled = false,
                MonitorVolume = Plugin.Cfg.Muted ? 0 : Plugin.Cfg.Volume / 100f };
            if (!studioSettings.DecksConfigured)
                studioSettings = studioSettings with { DeckAVolume = studioSettings.MusicVolume,
                    DeckBVolume = studioSettings.MusicVolume, DecksConfigured = true };
            Plugin.Cfg.StudioTalkKey = Math.Clamp(Plugin.Cfg.StudioTalkKey, 112, 123);
            studioLastSession = CurrentSession?.AccountId;
        }
        EnforceStudioQualityAccess();
        if (!studioSessionActive || studioClient != null) return;
        try
        {
            var resources = screenController.Engine.Resources;
            var ffmpeg = resources.GetLocationFFmpeg(prepareIfMissing: false);
            if (ffmpeg == null)
            {
                studioAudioPreparation ??= resources.PrepareFFmpegAsync();
                musicMessage = resources.FFmpegUnavailableMessage;
                return;
            }
            studioClient = new StudioClient(Path.Combine(Plugin.PluginInterface.AssemblyLocation.DirectoryName!, "Browsers", "WebView"),
                ffmpeg, studioSettings, Plugin.Cfg.StudioQueue.ToArray(), studioCleanup);
        }
        catch { musicMessage = "Studio audio could not start. Check the plugin's audio helper files."; }
    }
    private void StudioSettingsChanged(bool save = true)
    {
        studioClient?.Send(new StudioCommand("settings") { Settings = studioSettings });
        if (!save) return;
        Plugin.Cfg.StudioSettings = studioSettings with { MicEnabled = false, TalkHeld = false,
            ApplicationProcessId = 0, ApplicationStartedUtcTicks = 0, ApplicationCaptureEnabled = false };
        Plugin.Cfg.Save();
    }
    private void StudioQueueChanged()
    {
        if (!Plugin.Cfg.StudioQueue.Any(track => track.Id == studioSelectedTrackId)) studioSelectedTrackId = null;
        studioClient?.Send(new StudioCommand("queue") { Tracks = Plugin.Cfg.StudioQueue.ToArray() });
        Plugin.Cfg.Save();
    }
    private void StudioAction(string kind, string? track = null)
    {
        if (!CanUseBroadcastStudio || !studioSessionActive || studioPreviewStarting) return;
        if (kind == "play") { studioSessionActive = true; if (track is not null) studioSelectedTrackId = track; }
        if (kind is "next" or "previous") { studioSessionActive = true; studioSelectedTrackId = null; }
        studioClient?.Send(new StudioCommand(kind) { TrackId = track });
    }
    private void StudioDeckAction(string kind, int deck, string? track = null, double position = 0)
    {
        if (!CanUseBroadcastStudio || !studioSessionActive || studioPreviewStarting || StudioUsesApplication) return;
        EnsureStudio();
        if (kind is "play-deck" or "fade-deck") studioSessionActive = true;
        studioClient?.Send(new StudioCommand(kind) { Deck = deck, TrackId = track, Position = position });
    }
    private void OpenStudioPreview()
    {
        if (!studioSessionActive || studioPreviewUrl == null || stream.Mode == StreamMode.Viewing) return;
        // Do not put this private loopback URL in the party's queue/current media.
        queue.StopPlayback();
        video.Play(BuildPartyAudioPresentationUrl(studioPreviewUrl));
        if (!StudioOwnPreviewPlaying || video.State is VideoPlaybackState.Failed or VideoPlaybackState.Idle)
        {
            musicMessage = video.LastError ?? screenController.Engine.LastError ??
                "The TV could not start Spotify's audio. End the current activity and try Play again.";
            StopStudioBroadcast("Local preview was not accepted", pauseSpotify: false);
            return;
        }
        video.SetOverlayTitle(DjBroadcastContentTitle, "Broadcast Studio");
        video.SetOutputMuted(true);
    }
    private void ReturnStudioToReady()
    {
        if (!studioSessionActive || stream.Mode == StreamMode.Viewing) { StopStudioBroadcast(); return; }
        studioClient?.Send(new StudioCommand("off-air"));
        studioBroadcastRequested = studioPublished = false;
        studioAccount = null;
        CancelStudioStreamProbe();
        if (radioCredentials is { } radio) PreparedLiveAudioSource.Discard(radio.ListenUrl);
        djBroadcastingToWatchParty = false;
        OpenStudioPreview();
    }
    private void BeginStudioBroadcast()
    {
        if (!CanUseBroadcastStudio || !studioSessionActive || studioPreviewStarting) return;
        if (stream.Mode == StreamMode.Viewing || radioCredentials?.SourcePassword is not { Length: > 0 } || !StudioState.Ready) return;
        if (stream.Mode == StreamMode.Hosting) { StartStudioBroadcast(); return; }
        pendingWatchPartyMediaKind = PendingWatchPartyMediaKind.StudioBroadcast;
        watchPartyCreationPopupOpen = true; createRoomPassword = ""; createLockedRoomPasswordError = null;
    }
    private void StartStudioBroadcast()
    {
        if (!CanUseBroadcastStudio || !studioSessionActive || studioPreviewStarting) return;
        if (stream.Mode != StreamMode.Hosting || radioCredentials is not { SourcePassword: { Length: > 0 } password } radio ||
            studioBroadcastRequested || !CanStartBroadcastWithAllowance(BroadcastAllowanceMethod.DjLive)) return;
        EnsureStudio();
        if (!StudioState.Ready) return;
        EnforceStudioQualityAccess();
        studioAccount = CurrentSession?.AccountId;
        studioBroadcastRequested = true; studioPublished = false; studioStartedUtc = DateTime.UtcNow;
        studioNextCheckUtc = DateTime.MinValue; djIsOnAir = false;
        CancelStudioStreamProbe();
        
        studioClient?.Send(new StudioCommand("live") { Destination = new StudioDestination(radio.SourceHost,
            8443, radio.Mount, radio.SourceUser, password, Plugin.Cfg.DjBroadcastName) { UseTls = true } });
    }
    private void StopStudioBroadcast([System.Runtime.CompilerServices.CallerMemberName] string reason = "", bool pauseSpotify = true)
    {
        if (spotifyOwnsStudio)
        {
            AepLog.Warning($"[Spotify] TV session ended. reason={reason} previewStarting={studioPreviewStarting} " +
                $"captureSelected={spotifyCaptureSelected} captureActive={StudioState.ApplicationCapturing} " +
                $"helperReady={StudioState.Ready} previewOwned={StudioOwnPreviewPlaying} videoState={video.State} " +
                $"nonVideo={screenController.Engine.HasActiveNonVideoPlayback} mode={stream.Mode}");
            if (!pauseSpotify && string.IsNullOrWhiteSpace(musicMessage))
                musicMessage = StudioState.ApplicationError ?? StudioState.Error ??
                    video.LastError ?? screenController.Engine.LastError ??
                    "Spotify's TV session stopped before it was ready. Press Play to try again.";
        }
        if (studioBroadcastRequested)
            { }
        var stopSource = studioBroadcastRequested || studioSessionActive || StudioState.Playing ||
            StudioState.DeckA.TrackId != null || StudioState.DeckB.TrackId != null ||
            studioSettings.ApplicationCaptureEnabled || studioSettings.MicEnabled;
        if (stopSource) studioClient?.Send(new StudioCommand("stop"));
        var ownedPreview = StudioOwnPreviewPlaying ||
            studioSessionActive && studioPreviewStarting && video.CurrentMediaUrl is null &&
            !screenController.Engine.HasActiveNonVideoPlayback;
        studioSessionActive = false;
        studioPreviewStarting = false;
        studioAudioPreparation = null;
        studioPreviewUrl = null;
        studioSelectedTrackId = null;
        studioSettings = studioSettings with { ApplicationCaptureEnabled = false, MicEnabled = false, TalkHeld = false };
        if (stopSource) StudioSettingsChanged(false);
        var wasPublished = studioPublished;
        if (studioBroadcastRequested) djIsOnAir = false;
        studioBroadcastRequested = false; studioPublished = false; studioAccount = null;
        CancelStudioStreamProbe();
        if (radioCredentials is { } radio) PreparedLiveAudioSource.Discard(radio.ListenUrl);
        if (wasPublished && StudioOwnStreamPlaying || ownedPreview)
        { djBroadcastingToWatchParty = false; queue.StopPlayback(); }
        var retired = studioClient;
        studioClient = null;
        if (retired is not null)
        {
            retired.Dispose();
            studioCleanup = retired.CleanupCompletion;
        }
        if (spotifyOwnsStudio) RestoreSpotifyStudioSettings(pauseSpotify);
    }
    private void CancelStudioStreamProbe()
    {
        studioStreamProbeCancellation?.Cancel();
        studioStreamProbeCancellation?.Dispose(); studioStreamProbeCancellation = null;
        if (studioStreamProbe is { } probe)
            _ = probe.ContinueWith(completed => { if (completed.IsCompletedSuccessfully) completed.Result?.Dispose(); },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        studioStreamProbe = null;
    }
    internal void UpdateBroadcastStudio()
    {
        while (musicLibraryCompletions.TryDequeue(out var completion)) completion();
        if (studioClient == null && !(studioSessionActive && studioPreviewStarting)) return;
        if (!CanUseBroadcastStudio)
        {
            StopStudioBroadcast("Studio playback eligibility changed", pauseSpotify: false);
            return;
        }
        if (!ContinueStudioAudioPreparation()) return;
        EnforceStudioQualityAccess();
        var state = StudioState;
        if (state.RemovedTrackIds.Length > 0 && Plugin.Cfg.StudioQueue.RemoveAll(track => state.RemovedTrackIds.Contains(track.Id)) > 0)
            StudioQueueChanged();
        if (studioPreviewStarting)
        {
            // File preparation and prior-session cleanup are not connection time.
            if (studioClient?.IsPreparing == true) studioStartedUtc = DateTime.UtcNow;
            if (state.Ready && state.PreviewUrl is { Length: > 0 } url)
            {
                studioPreviewUrl = url;
                OpenStudioPreview();
                studioPreviewStarting = false;
                if (spotifyOwnsStudio) spotifyBroadcastDeadline = DateTime.UtcNow.AddSeconds(25);
            }
            else if (state.BroadcastState == "FAILED")
            {
                musicMessage = state.Error ?? "Studio audio could not start. Press Play to try again.";
                StopStudioBroadcast("Studio helper failed during startup", pauseSpotify: false);
            }
            else if ((DateTime.UtcNow - studioStartedUtc).TotalSeconds > 15)
            { musicMessage = "Studio audio could not start. Try Start again."; StopStudioBroadcast(pauseSpotify: false); }
            return;
        }
        if (studioSessionActive && !studioPublished && (!StudioOwnPreviewPlaying || video.State is VideoPlaybackState.Idle or VideoPlaybackState.Failed))
        {
            if (spotifyOwnsStudio && video.State is (VideoPlaybackState.Failed or VideoPlaybackState.Idle))
            {
                musicMessage = "Spotify's TV audio stopped. Press Play to reconnect it.";
                AepLog.Warning($"[Spotify] Local TV audio ended unexpectedly ({video.State}).");
            }
            StopStudioBroadcast(pauseSpotify: false);
            return;
        }
        if (state.TrackId != studioLastPlayingTrackId)
        {
            // Follow automatic advancement unless the user has highlighted
            // a different song that they have not started yet.
            if (studioSelectedTrackId == studioLastPlayingTrackId) studioSelectedTrackId = null;
            studioLastPlayingTrackId = state.TrackId;
        }
        if (!state.Ready && state.BroadcastState == "FAILED" && studioSessionActive)
        { StopStudioBroadcast(pauseSpotify: false); return; }
        if (studioPublished && StudioOwnStreamPlaying && queue.Current is { } current && current.Title != DjBroadcastContentTitle)
        {
            current.Title = DjBroadcastContentTitle;
            video.SetOverlayTitle(current.Title, current.Source);
        }

        if (studioSettings.ApplicationCaptureEnabled && (stream.Mode == StreamMode.Viewing || CurrentSession?.AccountId != studioLastSession))
        { studioSettings = studioSettings with { ApplicationCaptureEnabled = false }; StudioSettingsChanged(); }
        if (studioSettings.MicEnabled && (stream.Mode == StreamMode.Viewing || CurrentSession?.AccountId != studioLastSession))
        { studioSettings = studioSettings with { MicEnabled = false, TalkHeld = false }; StudioSettingsChanged(); }
        studioLastSession = CurrentSession?.AccountId;
        var held = studioSettings.MicEnabled && studioSettings.PushToTalk && Plugin.KeyState[(VirtualKey)Plugin.Cfg.StudioTalkKey];
        if (held != studioSettings.TalkHeld)
        { studioSettings = studioSettings with { TalkHeld = held }; StudioSettingsChanged(false); }
        if (!studioBroadcastRequested) return;
        if (stream.Mode != StreamMode.Hosting || CurrentSession?.AccountId != studioAccount || state.BroadcastState == "FAILED" ||
            studioPublished && !StudioOwnStreamPlaying)
        {
            if (state.Error != null) musicMessage = state.Error;
            StopStudioBroadcast(stream.Mode != StreamMode.Hosting ? "Watch Party is no longer hosted" :
                CurrentSession?.AccountId != studioAccount ? "Account changed" :
                state.BroadcastState == "FAILED" ? "Uploader failed" : "TV playback changed or stopped", pauseSpotify: false); return;
        }
        if (studioPublished) return;
        if ((DateTime.UtcNow - studioStartedUtc).TotalSeconds > 45)
        { musicMessage = "Icecast did not confirm the studio stream. Check your connection and try again."; StopStudioBroadcast(pauseSpotify: false); return; }
        if (state.BroadcastState != "LIVE") { CancelStudioStreamProbe(); return; }
        // Only a fresh probe of this studio source may publish its TV feed.
        // DJ Live's background status check can still describe a previous source.
        if (studioStreamProbe is { IsCompleted: true } probe)
        {
            studioStreamProbe = null;
            studioStreamProbeCancellation?.Dispose(); studioStreamProbeCancellation = null;
            PreparedLiveAudioSource? available = null;
            try { available = probe.GetAwaiter().GetResult(); } catch { }
            studioNextCheckUtc = DateTime.UtcNow.AddSeconds(3);
            if (available != null && radioCredentials is { } radio)
            {
                PreparedLiveAudioSource.Store(radio.ListenUrl, available);
                djIsOnAir = true;
                var listeningMuted = Plugin.Cfg.Muted;
                StartDjBroadcastInHostedRoom(radio);
                // The studio monitor owns local listening; only the returning
                // TV feed is silenced to avoid hearing a delayed duplicate.
                Plugin.Cfg.Muted = listeningMuted;
                video.SetOutputMuted(true);
                djAutoMuteNoticeVisible = false;
                djAutoMutedStreamUrl = null;
                Plugin.Cfg.Save();
                studioPublished = djBroadcastingToWatchParty;
                
                if (!studioPublished) StopStudioBroadcast();
            }
            else available?.Dispose();
        }
        if (!studioPublished && studioStreamProbe == null && DateTime.UtcNow >= studioNextCheckUtc && radioCredentials is { } connection)
        {
            studioStreamProbeCancellation = new CancellationTokenSource();
            studioStreamProbe = PreparedLiveAudioSource.OpenAsync(connection.ListenUrl,
                AlphaChannel.Plugin.Net.PluginHttpClients.CreateProbeClient, studioStreamProbeCancellation.Token);
        }
    }
    private void DrawStudioDialog(string kind)
    {
        if (kind == "studioTools")
        {
            using (UiFonts.PushRelative(.90f))
            using (ImRaii.PushColor(ImGuiCol.Text, MutedText))
                ImGui.TextWrapped("Refresh your audio devices, or restart the studio audio helper if playback has stopped.");
            if (GameLayoutButton("Refresh devices", FontAwesomeIcon.Sync, Ui(170))) { StudioAction("devices"); CloseMusicDialog(); }
            ImGui.SameLine();
            if (GameLayoutButton("Restart audio", FontAwesomeIcon.Redo, Ui(170)))
            { StopStudioBroadcast(); studioClient?.Dispose(); studioClient = null; studioSettings = studioSettings with { MicEnabled = false, TalkHeld = false,
                ApplicationCaptureEnabled = false, ApplicationProcessId = 0, ApplicationStartedUtcTicks = 0 }; StartStudioActivity(); CloseMusicDialog(); }
        }
        else if (kind == "studioSave")
        {
            ImGui.TextWrapped("Save this music queue as a named playlist."); ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##studioPlaylistName", "Playlist name...", ref studioPlaylistName, 100);
            var name = studioPlaylistName.Trim();
            using (UiDisabled.Scope(name.Length == 0))
                if (GameLayoutButton("Save playlist", FontAwesomeIcon.Save, Ui(170), true))
                {
                    var existing = Plugin.Cfg.StudioPlaylists.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (existing == null) Plugin.Cfg.StudioPlaylists.Add(new StudioPlaylist { Name = name, Tracks = Plugin.Cfg.StudioQueue.ToList() });
                    else existing.Tracks = Plugin.Cfg.StudioQueue.ToList();
                    Plugin.Cfg.Save(); CloseMusicDialog();
                }
            ImGui.TextColored(MutedText, "Saving an existing name replaces that playlist."); DrawMusicCancel();
        }
        else if (kind == "studioLoad")
        {
            ImGui.TextWrapped("Load a saved playlist to replace the queue. Song files stay on your PC.");
            using var list = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##studioSavedPlaylists", new Vector2(-1, Ui(330))));
            foreach (var playlist in Plugin.Cfg.StudioPlaylists.ToArray())
            {
                using var id = ImRaii.PushId(playlist.Name);
                ImGui.TextUnformatted($"{playlist.Name} · {playlist.Tracks.Count} songs");
                if (GameLayoutButton("Load", FontAwesomeIcon.FolderOpen, Ui(100), true))
                { Plugin.Cfg.StudioQueue = playlist.Tracks.Select(t => t with { Id = Guid.NewGuid().ToString("N") }).ToList(); StudioQueueChanged(); CloseMusicDialog(); }
                ImGui.SameLine(); if (GameLayoutButton("Delete", FontAwesomeIcon.Trash, Ui(100), danger: true))
                { Plugin.Cfg.StudioPlaylists.Remove(playlist); Plugin.Cfg.Save(); }
            }
            if (Plugin.Cfg.StudioPlaylists.Count == 0) ImGui.TextColored(MutedText, "No saved playlists yet.");
        }
    }
}
