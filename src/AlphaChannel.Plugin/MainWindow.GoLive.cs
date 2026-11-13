using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Auth;
using AlphaChannel.Plugin.Video;
using AlphaChannel.Plugin.BroadcastAllowance;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

// "Go Live" self-hosted streaming - OBS pushes RTMP to our own MediaMTX ingest, friends watch the
// resulting HLS stream through the exact same "play a URL" primitive (queue.PlayNow) the existing
// YouTube/Twitch flow already uses. See Server/Live/LiveService.cs for the server half and why the
// stream key format keeps the secret out of the public HLS URL.
internal sealed partial class MainWindow
{
    private bool liveStatusDirty = true;
    private bool liveStatusLoading;
    private LiveStatusDto? liveStatus;
    private bool streamKeyRevealed;
    private bool obsConnectionChecking;
    private bool obsConnectionOnline;
    private string? obsConnectionError;
    private bool keyRotating;
    private string? keyError;
    private bool keyRegenerateConfirmPending;
    private string? livePatreonAccessMessage;
    private double obsMeasuredBitrateMbps;
    private DateTime nextObsBitrateProbeUtc = DateTime.MinValue;
    private int obsBitrateProbeRunning;
    private bool resetGoLiveScroll = true;

    internal void UpdateObsBitrateMeasurement()
    {
        var isBroadcasting =
            stream.Mode == StreamMode.Hosting &&
            CurrentSession is not null &&
            queue.Current is { IsTransient: true } current &&
            string.Equals(current.Source, "Live Stream", StringComparison.Ordinal);

        if (!isBroadcasting)
        {
            return;
        }

        if (DateTime.UtcNow < nextObsBitrateProbeUtc ||
            Interlocked.CompareExchange(ref obsBitrateProbeRunning, 1, 0) != 0)
        {
            return;
        }

        nextObsBitrateProbeUtc = DateTime.UtcNow.AddSeconds(45);
        var hlsUrl = BuildMyHlsUrl(CurrentSession!);
        _ = Task.Run(async () =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(
                    TimeSpan.FromSeconds(15));
                var measured = await HlsBitrateProbe.MeasureMbpsAsync(
                    hlsUrl,
                    timeout.Token);
                if (measured <= 0d)
                {
                    return;
                }

                Interlocked.Exchange(ref obsMeasuredBitrateMbps, measured);
            }
            catch (Exception)
            {
                
            }
            finally
            {
                Interlocked.Exchange(ref obsBitrateProbeRunning, 0);
            }
        });
    }

    private void RefreshLivePatreonAccess()
    {
        if (!HasConfiguredPatreonAccess())
        {
            patreonAccessConfirmed = false;
            livePatreonAccessMessage =
                "No active Patreon membership was found.";
            return;
        }

        patreonAccessConfirmed = true;
        livePatreonAccessMessage = null;
        obsConnectionError = null;

    }

    private enum LiveStreamGuideKind
    {
        None,
        ObsStudio,
        GenericRtmp,
    }

    private LiveStreamGuideKind liveStreamGuideKind;
    private int obsSetupGuideStep;
    private int genericStreamGuideStep;

    private void DrawLiveStreamGuideOverlay()
    {
        if (liveStreamGuideKind ==
            LiveStreamGuideKind.None)
        {
            return;
        }

        var parentPosition =
            ImGui.GetWindowPos();

        var parentSize =
            ImGui.GetWindowSize();

        var panelWidth =
            MathF.Min(
                Ui(700f),
                parentSize.X -
                Ui(40f));

        var panelHeight =
            MathF.Min(
                Ui(560f),
                parentSize.Y -
                Ui(40f));

        var panelPosition =
            parentPosition +
            new Vector2(
                (parentSize.X -
                 panelWidth) *
                0.5f,

                (parentSize.Y -
                 panelHeight) *
                0.5f);

        ImGui.SetNextWindowPos(
            parentPosition,
            ImGuiCond.Always);

        ImGui.SetNextWindowSize(
            parentSize,
            ImGuiCond.Always);

        ImGui.SetNextWindowBgAlpha(
            0f);

        const ImGuiWindowFlags overlayFlags =
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoDocking |
            ImGuiWindowFlags.NoBackground;

        if (!BeginWindowOverlay(
                "##liveStreamGuideOverlay",
                overlayFlags))
        {
            ImGui.End();

            return;
        }

        var drawList =
            ImGui.GetWindowDrawList();

        drawList.AddRectFilled(
            parentPosition,
            parentPosition +
            parentSize,
            ImGui.GetColorU32(
                new Vector4(
                    0f,
                    0f,
                    0f,
                    0.56f)));

        ImGui.SetCursorScreenPos(
            panelPosition);

        using (ImRaii.PushStyle(
                   ImGuiStyleVar.ChildRounding,
                   Ui(14f)))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.ChildBorderSize,
                   Ui(1f)))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.WindowPadding,
                   UiVec(
                       24f,
                       20f)))
        using (ImRaii.PushColor(
                   ImGuiCol.ChildBg,
                   CardBg))
        using (ImRaii.PushColor(
                   ImGuiCol.Border,
                   new Vector4(
                       Accent.X,
                       Accent.Y,
                       Accent.Z,
                       0.82f)))
        using (var panel = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(
                   "##liveStreamGuidePanel",
                   new Vector2(
                       panelWidth,
                       panelHeight),
                   true,
                   ImGuiWindowFlags.NoScrollbar |
                   ImGuiWindowFlags.NoScrollWithMouse)))
        {
            if (panel)
            {
                var contentOrigin =
                    ImGui.GetCursorScreenPos();

                var closeSize =
                    new Vector2(
                        Ui(32f),
                        Ui(32f));

                ImGui.SetCursorScreenPos(
                    new Vector2(
                        panelPosition.X +
                        panelWidth -
                        Ui(24f) -
                        closeSize.X,
                        panelPosition.Y +
                        Ui(16f)));

                DrawDjActionButton(
                    "##closeLiveStreamGuide",
                    FontAwesomeIcon.Times,
                    string.Empty,
                    closeSize,
                    false,
                    CloseLiveStreamGuide);

                ImGui.SetCursorScreenPos(
                    contentOrigin);

                switch (liveStreamGuideKind)
                {
                    case LiveStreamGuideKind.ObsStudio:
                        if (CurrentSession is { } session)
                        {
                            DrawDjGuideShell(
                                "OBS Studio Setup",
                                "Configure OBS Studio to send live video to your Alpha Channel TV.",
                                5,
                                ref obsSetupGuideStep,
                                step =>
                                {
                                    switch (step)
                                    {
                                        case 0:
                                            DrawObsGuideInstall();

                                            break;

                                        case 1:
                                            DrawObsGuideSource();

                                            break;

                                        case 2:
                                            DrawObsGuideOutput();

                                            break;

                                        case 3:
                                            DrawObsGuideConnection(
                                                session);

                                            break;

                                        case 4:
                                            DrawObsGuideGoLive();

                                            break;
                                    }
                                },
                                CloseLiveStreamGuide, Hex(0xE2E7EF));
                        }

                        break;

                    case LiveStreamGuideKind.GenericRtmp:
                        DrawDjGuideShell(
                            "Other RTMP App Setup",
                            "Connect another RTMP-compatible broadcasting app to Alpha Channel.",
                            4,
                            ref genericStreamGuideStep,
                            step =>
                            {
                                switch (step)
                                {
                                    case 0:
                                        DrawGenericStreamGuideChooseApp();

                                        break;

                                    case 1:
                                        DrawGenericStreamGuideConnection();

                                        break;

                                    case 2:
                                        DrawGenericStreamGuideQuality();

                                        break;

                                    case 3:
                                        DrawGenericStreamGuideGoLive();

                                        break;
                                }
                            },
                            CloseLiveStreamGuide, Hex(0xE2E7EF));

                        break;
                }
            }
        }

        ImGui.End();
    }

    private void CloseLiveStreamGuide()
    {
        liveStreamGuideKind =
            LiveStreamGuideKind.None;
    }

    private void DrawGenericStreamGuideChooseApp()
    {
        DrawObsGuideHeading(
            "1. Choose a broadcasting app",
            "Use software that supports a custom RTMP server and stream key.");

        ImGui.TextWrapped(
            "Alpha Channel is not limited to OBS Studio. Any broadcasting application that can publish to a custom RTMP destination may be compatible.");

        ImGui.Dummy(
            UiVec(0f, 14f));

        DrawObsGuideBullet(
            "Streamlabs Desktop",
            "A streaming-focused application based on OBS.");

        DrawObsGuideBullet(
            "XSplit Broadcaster",
            "A commercial broadcasting and scene-production application.");

        DrawObsGuideBullet(
            "vMix",
            "A production suite for advanced live-video workflows.");

        DrawObsGuideBullet(
            "FFmpeg",
            "A command-line option for sending existing video or generated output.");

        ImGui.Dummy(
            UiVec(0f, 14f));

        ImGui.TextColored(
            Gold,
            "Look for Custom RTMP or Custom Streaming Service.");

        ImGui.Dummy(
            UiVec(0f, 5f));

        ImGui.TextWrapped(
            "The exact wording and location varies between applications. If the app cannot accept both a server URL and stream key, it may not be suitable.");
    }

    private void DrawGenericStreamGuideConnection()
    {
        DrawObsGuideHeading(
            "2. Enter the connection details",
            "Copy Alpha Channel's server URL and stream key into your broadcasting app.");

        ImGui.TextWrapped(
            "Open your application's streaming, broadcast or output settings. Choose its Custom RTMP option, then enter the following values.");

        ImGui.Dummy(
            UiVec(0f, 14f));

        var server =
            BuildRtmpServer();

        DrawObsGuideCopyField(
            "Server URL",
            server,
            "##genericGuideServer");

        ImGui.Dummy(
            UiVec(0f, 12f));

        var key =
            CurrentSession is { } session
                ? Plugin.Cfg.StreamKeys.GetValueOrDefault(
                    session.AccountId)
                : null;

        if (key is { Length: > 0 })
        {
            DrawObsGuideCopyField(
                "Stream Key",
                key,
                "##genericGuideKey");

            ImGui.Dummy(
                UiVec(0f, 10f));

            ImGui.TextColored(
                Gold,
                "Keep your stream key private.");

            ImGui.Dummy(
                UiVec(0f, 4f));

            using var uiFontScale55232=UiFonts.PushRelative(0.80f);

            ImGui.TextWrapped(
                "Anyone with this key may be able to publish video to your Alpha Channel stream.");

            uiFontScale55232.Dispose();
        }
        else
        {
            ImGui.TextColored(
                Gold,
                "No stream key is saved on this installation.");

            ImGui.Dummy(
                UiVec(0f, 6f));

            ImGui.TextWrapped(
                "Close this guide and generate a stream key from Settings > Account.");
        }
    }

    private void DrawGenericStreamGuideQuality()
    {
        DrawObsGuideHeading(
            "3. Configure video and audio",
            "Use broadly compatible settings for reliable playback.");

        ImGui.TextWrapped(
            "The option names vary between applications. Use the closest available equivalents to these recommended settings.");

        ImGui.Dummy(
            UiVec(0f, 14f));

        DrawObsGuideSetting(
            "Output Resolution",
            "1280 x 720");

        DrawObsGuideSetting(
            "Frame Rate",
            "30 FPS");

        DrawObsGuideSetting(
            "Video Encoder",
            "H.264");

        DrawObsGuideSetting(
            "Rate Control",
            "CBR");

        DrawObsGuideSetting(
            "Video Bitrate",
            "4000 Kbps");

        DrawObsGuideSetting(
            "Keyframe Interval",
            "2 seconds");

        DrawObsGuideSetting(
            "Audio Encoder",
            "AAC");

        DrawObsGuideSetting(
            "Audio Bitrate",
            "160 Kbps");

        ImGui.Dummy(
            UiVec(0f, 12f));

        using var uiFontScale57002=UiFonts.PushRelative(0.80f);

        ImGui.TextColored(
            MutedText,
            "Higher resolutions and bitrates may work, but can take longer to load or cause buffering for viewers.");

        uiFontScale57002.Dispose();
    }

    private void DrawGenericStreamGuideGoLive()
    {
        DrawObsGuideHeading(
            "4. Start broadcasting",
            "Start the external stream, confirm it reaches Alpha Channel, then broadcast it to your TV.");

        DrawObsGuideNumberedLine(
            "1",
            "Start streaming or broadcasting from your chosen application.");

        DrawObsGuideNumberedLine(
            "2",
            "Return to Alpha Channel and open Media Player > Add Media > Stream Live.");

        DrawObsGuideNumberedLine(
            "3",
            "Press Check connection.");

        DrawObsGuideNumberedLine(
            "4",
            "Wait for the status card to show Stream detected.");

        DrawObsGuideNumberedLine(
            "5",
            "Press Broadcast to TV.");

        ImGui.Dummy(
            UiVec(0f, 14f));

        using (ImRaii.PushColor(
                   ImGuiCol.ChildBg,
                   CardBg))
        using (var note = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(
                   "##genericStreamGuideFinalNote",
                   new Vector2(
                       -1f,
                       Ui(76f)),
                   false,
                   ImGuiWindowFlags.NoScrollbar |
                   ImGuiWindowFlags.NoScrollWithMouse)))
        {
            if (note)
            {
                ImGui.SetCursorPos(
                    new Vector2(
                        Ui(12f),
                        Ui(10f)));

                ImGui.TextColored(
                    Gold,
                    "Connecting and broadcasting are separate steps.");

                ImGui.SetCursorPosX(
                    Ui(12f));

                ImGui.PushTextWrapPos(
                    ImGui.GetWindowWidth() -
                    Ui(12f));

                ImGui.TextWrapped(
                    "Starting your broadcasting app sends the stream to Alpha Channel. Broadcast to TV then shares it through your Watch Party.");

                ImGui.PopTextWrapPos();
            }
        }
    }

    

    private void DrawObsGuideInstall()
    {
        DrawObsGuideHeading(
            "1. Install OBS Studio",
            "OBS is the program you'll be using to share your screen / audio with your watch party.");

        ImGui.TextWrapped(
            "Download and install OBS Studio. If you already have OBS installed, you can skip this step.");

        ImGui.Dummy(
            UiVec(0f, 16f));

        using (ImRaii.PushStyle(
            ImGuiStyleVar.FrameRounding,
            8f))
        using (ImRaii.PushColor(
            ImGuiCol.Button,
            Accent)
            .Push(
                ImGuiCol.ButtonHovered,
                AccentHover)
            .Push(
                ImGuiCol.ButtonActive,
                AccentActive))
        {
            if (ImGui.Button(
                "Open OBS Website",
                UiVec(170f, 36f)))
            {
                Dalamud.Utility.Util.OpenLink(
                    "https://obsproject.com/");
            }
        }

        ImGui.Dummy(
            UiVec(0f, 14f));

        using var uiFontScale60456=UiFonts.PushRelative(0.82f);

        ImGui.TextColored(
            MutedText,
            "OBS Studio is free and open source.");

        uiFontScale60456.Dispose();
    }

    private void DrawObsGuideSource()
    {
        DrawObsGuideHeading(
            "2. Create your OBS source",
            "Choose what you want your viewers to see and hear.");

        ImGui.TextWrapped(
            "In the main OBS window, use the Sources panel at the bottom and press + to add your video source.");

        ImGui.Dummy(
            UiVec(0f, 12f));

        DrawObsGuideBullet(
            "Game Capture",
            "Best for streaming a game.");

        DrawObsGuideBullet(
            "Window Capture",
            "Streams one specific application window.");

        DrawObsGuideBullet(
            "Display Capture",
            "Streams everything visible on a monitor.");

        DrawObsGuideBullet(
            "Media Source",
            "Useful for broadcasting a local video file.");

        ImGui.Dummy(
            UiVec(0f, 14f));

        ImGui.TextColored(
            Gold,
            "Don't forget your audio.");

        ImGui.Dummy(
            UiVec(0f, 4f));

        ImGui.TextWrapped(
            "Check the OBS Audio Mixer and make sure Desktop Audio, your media source, or whichever audio source you want to broadcast is moving when sound plays.");

        ImGui.Dummy(
            UiVec(0f, 8f));

        ImGui.TextWrapped(
            "If no sound is being picked up then you'll need to check the Audio tab in the OBS settings.");
    }

    private void DrawObsGuideOutput()
    {
        DrawObsGuideHeading(
            "3. Configure streaming quality",
            "These settings are recommended for smooth Alpha Channel playback.");

        ImGui.TextWrapped(
            "These are only our recommendations. You are able to stream at higher quality levels, but you'll likely want to check that it's loading okay for your viewers.");

        ImGui.Dummy(
            UiVec(0f, 10f));

        ImGui.TextWrapped(
            "Open OBS Settings, then configure the following:");

        ImGui.Dummy(
            UiVec(0f, 12f));

        DrawObsGuideSetting(
            "Video > Base Canvas",
            "1920 x 1080");

        DrawObsGuideSetting(
            "Video > Output Resolution",
            "1280 x 720");

        DrawObsGuideSetting(
            "Video > FPS",
            "30");

        DrawObsGuideSetting(
            "Output > Encoder",
            "NVIDIA NVENC H.264 if available");

        DrawObsGuideSetting(
            "Output > Rate Control",
            "CBR");

        DrawObsGuideSetting(
            "Output > Bitrate",
            "4000 Kbps");

        DrawObsGuideSetting(
            "Output > Keyframe Interval",
            "2 seconds");

        DrawObsGuideSetting(
            "Output > Preset",
            "P5: Slow (Good Quality)");

        DrawObsGuideSetting(
            "Output > Multipass",
            "Single Pass");

        DrawObsGuideSetting(
            "Output > Profile",
            "High");

        DrawObsGuideSetting(
            "Output > Audio Bitrate",
            "160 Kbps");
    }

    private void DrawObsGuideConnection(
    CharacterSession session)
    {
        DrawObsGuideHeading(
            "4. Connect OBS to Alpha Channel",
            "Enter your Alpha Channel server and secret stream key in OBS.");

        ImGui.TextWrapped(
            "Open OBS Settings > Stream. Set Service to Custom, then enter the Server and Stream Key shown below.");

        ImGui.Dummy(
            UiVec(0f, 14f));

        var server =
            BuildRtmpServer();

        DrawObsGuideCopyField(
            "Server",
            server,
            "##guideServer");

        ImGui.Dummy(
            UiVec(0f, 12f));

        var key =
            Plugin.Cfg.StreamKeys.GetValueOrDefault(
                session.AccountId);

        if (key is { Length: > 0 })
        {
            DrawObsGuideCopyField(
                "Stream Key",
                key,
                "##guideKey");

            ImGui.Dummy(
                UiVec(0f, 8f));

            using var uiFontScale64805=UiFonts.PushRelative(0.80f);

            ImGui.TextColored(
                Gold,
                "Keep your stream key private. Anyone with it could publish to your stream.");

            uiFontScale64805.Dispose();
        }
        else
        {
            ImGui.TextColored(
                Gold,
                "You don't currently have a stream key on this installation.");

            ImGui.Dummy(
                UiVec(0f, 6f));

            ImGui.TextWrapped(
                "Close this guide and use Generate under OBS setup, then return to this step.");
        }
    }

    private void DrawObsGuideGoLive()
    {
        DrawObsGuideHeading(
            "5. Start streaming",
            "You're ready to connect OBS to your Alpha Channel TV.");

        DrawObsGuideNumberedLine(
            "1",
            "Click Start Streaming in OBS.");

        DrawObsGuideNumberedLine(
            "2",
            "Return to this page on Alpha Channel.");

        DrawObsGuideNumberedLine(
            "3",
            "Press Check connection.");

        DrawObsGuideNumberedLine(
            "4",
            "Wait for the status card to show Stream detected.");

        DrawObsGuideNumberedLine(
            "5",
            "Press Broadcast to TV.");

        ImGui.Dummy(
            UiVec(0f, 14f));

        using (ImRaii.PushColor(
            ImGuiCol.ChildBg,
            CardBg))
        using (var note =
            UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(
                "##obsGuideFinalNote",
                new Vector2(
                    -1f,
                    Ui(72f)),
                false,
                ImGuiWindowFlags.NoScrollbar)))
        {
            if (note)
            {
                ImGui.SetCursorPos(
                    UiVec(12f, 10f));

                ImGui.TextColored(
                    Gold,
                    "OBS streaming and TV broadcasting are separate.");

                ImGui.SetCursorPosX(
                    12f);

                ImGui.TextWrapped(
                    "Pressing 'Broadcast to TV' is what actually starts sharing your stream with your Alpha Channel watch party and allows you to view it in-game.");
            }
        }
    }

    private void DrawObsGuideHeading(
    string title,
    string subtitle)
    {
        using var uiFontScale67224=UiFonts.PushRelative(1.08f);

        ImGui.TextColored(
            Hex(0xE2E7EF),
            title);

        uiFontScale67224.Dispose();

        ImGui.Dummy(
            UiVec(0f, 4f));

        using var uiFontScale67447=UiFonts.PushRelative(0.82f);

        ImGui.TextColored(
            MutedText,
            subtitle);

        uiFontScale67447.Dispose();

        ImGui.Dummy(
            UiVec(0f, 16f));
    }

    private void DrawObsGuideBullet(
        string title,
        string description)
    {
        ImGui.TextColored(
            Accent,
            "•");

        ImGui.SameLine();

        ImGui.TextColored(
            Hex(0xE2E7EF),
            title);

        ImGui.SameLine();

        ImGui.TextColored(
            MutedText,
            $"— {description}");

        ImGui.Dummy(
            UiVec(0f, 5f));
    }

    private void DrawObsGuideSetting(
        string name,
        string value)
    {
        var startX =
            ImGui.GetCursorPosX();

        ImGui.TextColored(
            MutedText,
            name);

        ImGui.SameLine();

        ImGui.SetCursorPosX(
            startX + 250f);

        ImGui.TextColored(
            Hex(0xE2E7EF),
            value);

        ImGui.Dummy(
            UiVec(0f, 5f));
    }

    private void DrawObsGuideNumberedLine(
        string number,
        string text)
    {
        ImGui.TextColored(
            Accent,
            number + ".");

        ImGui.SameLine(
            0f,
            8f);

        ImGui.TextWrapped(
            text);

        ImGui.Dummy(
            UiVec(0f, 8f));
    }

    private void DrawObsGuideCopyField(
        string label,
        string value,
        string id)
    {
        using var uiFontScale69043=UiFonts.PushRelative(0.82f);

        ImGui.TextColored(
            MutedText,
            label);

        uiFontScale69043.Dispose();

        ImGui.Dummy(
            UiVec(0f, 4f));

        ImGui.SetNextItemWidth(
            -90f);

        var display =
            value;

        ImGui.InputText(
            id,
            ref display,
            512,
            ImGuiInputTextFlags.ReadOnly);

        ImGui.SameLine(
            0f,
            8f);

        if (ImGui.Button(
            $"Copy##{id}",
            UiVec(78f, 0f)))
        {
            ImGui.SetClipboardText(
                value);
        }
    }

    private string BuildRtmpServer()
    {
        var host = new Uri(Plugin.Cfg.RelayServerUrl).Host;
        return LiveBroadcastStream.PublishServer(host);
    }

    private void BeginLiveWatchPartyBroadcast(
    CharacterSession session)
    {
        if (!obsConnectionOnline)
        {
            return;
        }

        if (stream.Mode ==
            StreamMode.Viewing)
        {
            Plugin.ChatGui.Print(
                "[AlphaChannel] Leave your current Watch Party before broadcasting your live stream.");

            return;
        }

        if (stream.Mode ==
            StreamMode.Hosting)
        {
            StartLiveBroadcastInHostedRoom();

            return;
        }

        pendingWatchPartyMediaKind =
            PendingWatchPartyMediaKind.LiveStreamBroadcast;

        watchPartyCreationPopupOpen =
            true;

        createRoomPassword =
            string.Empty;

        createLockedRoomPasswordError =
            null;
    }

    private void StartLiveBroadcastInHostedRoom(
        bool navigateToWatchParty = false)
    {
        if (!CanStartOtherStudioMedia()) return;
        if (CurrentSession is not { } session ||
            !obsConnectionOnline)
        {
            return;
        }

        if (!CanStartBroadcastWithAllowance(BroadcastAllowanceMethod.Obs))
        {
            return;
        }

        Interlocked.Exchange(ref obsMeasuredBitrateMbps, 0d);
        nextObsBitrateProbeUtc = DateTime.MinValue;

        var hlsUrl =
            BuildMyHlsUrl(
                session);

        queue.PlayTransient(
            new VideoQueueEntry(
                hlsUrl,
                "Live Streaming",
                "Live Stream",
                null,
                null,
                0d,
                true));

        if (navigateToWatchParty)
        {
            currentPage =
                HomePage.WatchAlong;

            partyPanelTab =
                PartyPanelTab.NowPlaying;
        }

    }

    private string BuildMyHlsUrl(CharacterSession session)
    {
        var host =
            new Uri(
                Plugin.Cfg.RelayServerUrl)
            .Host;

        return
            $"https://{host}/live/{session.AccountId}/index.m3u8";
    }

    private void CheckObsConnection(CharacterSession session)
    {
        if (!obsConnectionChecking) _ = CheckObsConnectionAsync(session);
    }


    private void RotateStreamKey(CharacterSession session)
    {
        if (keyRotating) return;
        var request = CaptureAccountRequest(session);
        var generation = ++streamKeyRequestGeneration;
        bool Current() => IsCurrentAccountRequest(request) && generation == streamKeyRequestGeneration;
        keyRotating = true;
        keyError = null;
        var token = session.Token;
        var accountId = session.AccountId;
        _ = Task.Run(async () =>
        {
            try
            {
                var key = await liveClient.RotateKeyAsync(token).ConfigureAwait(false);
                await Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    if (!Current()) return;
                    if (key is null)
                    {
                        keyError = liveClient.LastFailure?.UserMessage ?? "Couldn't generate a stream key.";
                        return;
                    }
                    Plugin.Cfg.StreamKeys[accountId] = key;
                    Plugin.Cfg.Save();
                    automaticStreamKeyChecked = streamKeyExistsOnServer = true;
                    streamKeyRevealed = true;
                    liveStatusDirty = true;
                }).ConfigureAwait(false);
            }
            catch (Exception)
            {
                await Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    if (Current()) keyError = "Couldn't generate a stream key.";
                }).ConfigureAwait(false);
                AepLog.Warning("[GoLive] Stream key rotation failed.");
            }
            finally
            {
                await Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    if (Current()) keyRotating = false;
                }).ConfigureAwait(false);
            }
        });
    }

    private void RefreshLiveStatus(CharacterSession session)
    {
        _ = RefreshLiveStatusAsync(session);
    }

}
