using AlphaChannel.Plugin.Video;
using AlphaChannel.Plugin.BroadcastAllowance;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

// Player is the single watch surface: source switcher, quiet empty deck, queue, and watch party.
internal sealed partial class MainWindow
{
    private enum PlayerDrawer
    {
        Player,
        PlayVideo,
        Queue
    }

    private void DrawPlayerDrawerTabs()
    {
        var origin=ImGui.GetCursorScreenPos();
        var width=ImGui.GetContentRegionAvail().X;
        var size=new Vector2(width/3,Ui(42));
        DrawPlayerDrawerTab(FontAwesomeIcon.Tv,"Now Playing",PlayerDrawer.Player,size);
        ImGui.SameLine(0,0);
        DrawPlayerDrawerTab(FontAwesomeIcon.Plus,"Add Media",PlayerDrawer.PlayVideo,size);
        ImGui.SameLine(0,0);
        DrawPlayerDrawerTab(FontAwesomeIcon.List,$"Queue ({queue.Entries.Count})",PlayerDrawer.Queue,size);
        ImGui.GetWindowDrawList().AddLine(origin+UiVec(0,42),origin+new Vector2(width,Ui(42)),ImGui.GetColorU32(BorderSubtle));
    }
    private double queueAddedFeedbackUntil;
    private string addMediaVideoSearchQuery = string.Empty;


    //
    // Local Video
    //

    private string localVideoSelectedPath =
        string.Empty;

    private string? localVideoError;

    private string? localVideoPatreonAccessMessage;


    //
    // Local-video Watch Party broadcast state.
    //
    // Armed means the host chose to broadcast the file. FFmpeg itself only
    // runs while at least one viewer is present.
    //

    private bool localVideoBroadcastArmed;
    private bool localVideoBroadcastStartFailed;
    private DateTime nextLocalVideoBroadcastRetryUtc = DateTime.MinValue;
    private bool localVideoBroadcastEncoderExpectedRunning;
    private bool localVideoBroadcastPaused;
    private int? localVideoBroadcastSubtitleStreamIndex;
    private int? localVideoBroadcastAudioStreamIndex;
    private readonly LocalVideoSubtitleCache localVideoSubtitleCache = new();
    private bool localVideoSubtitlesPreparing;

    private string? localVideoBroadcastPublishUrl;
    private string? localVideoBroadcastHlsUrl;
    private float pendingLocalVideoResumePosition;
    private double localVideoBroadcastStartPosition;
    private DateTime localVideoBroadcastStartTimeUtc;

    private bool CanStartLocalVideoUpload => stream.Roster.Length > 0;

    private void RefreshLocalVideoPatreonAccess()
    {
        if (!HasConfiguredPatreonAccess())
        {
            patreonAccessConfirmed = false;
            localVideoPatreonAccessMessage =
                "No active Patreon membership was found.";
            return;
        }

        patreonAccessConfirmed = true;
        localVideoPatreonAccessMessage = null;
        localVideoError = null;

    }

    internal string? ActiveLocalVideoBroadcastHlsUrl =>
        localVideoBroadcastArmed &&
        !localVideoBroadcastPaused &&
        screenController.Engine.IsLocalVideoBroadcasting
            ? localVideoBroadcastHlsUrl
            : null;

    internal bool IsLocalVideoBroadcastArmed => localVideoBroadcastArmed;

    internal string? ActiveLocalVideoBroadcastTitle
    {
        get
        {
            if (!localVideoBroadcastArmed)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(
                    localVideoSelectedPath))
            {
                return "Local Video";
            }

            var videoName =
                Path.GetFileNameWithoutExtension(
                    localVideoSelectedPath);

            return string.IsNullOrWhiteSpace(
                videoName)
                ? "Local Video"
                : $"Local Video: {videoName}";
        }
    }

    internal bool ActiveLocalVideoBroadcastPaused =>
        localVideoBroadcastArmed &&
        localVideoBroadcastPaused;


    private readonly PluginFilePicker localVideoFileDialog = new();


    private PlayerDrawer activePlayerDrawer =
        PlayerDrawer.Player;

    /// <summary>
    /// True while Add Media is displaying the source-selection landing page.
    /// Selecting a source changes this to false and opens that source's form.
    /// </summary>
    private bool showingAddMediaSources =
        true;
    private void DrawPlayerPage()
    {
        DrawPlayerDrawerTabs();

        SetGameLayoutRowGap(Ui(14));

        switch (activePlayerDrawer)
        {
            case PlayerDrawer.Player:
                DrawPlayerPreviewDrawer();
                break;

            case PlayerDrawer.PlayVideo:
                DrawPlayVideoDrawer();
                break;

            case PlayerDrawer.Queue:
                DrawQueueDrawer();
                break;

        }
    }

    private void DrawPlayerDrawerTab(FontAwesomeIcon icon,string label,PlayerDrawer drawer,Vector2 size)
    {
        var position=ImGui.GetCursorScreenPos();
        if(ImGui.InvisibleButton($"##drawer_{drawer}",size)) activePlayerDrawer=drawer;
        var selected=activePlayerDrawer==drawer;
        var draw=ImGui.GetWindowDrawList();
        if(ImGui.IsItemHovered())
            draw.AddRectFilled(position,position+size,ImGui.GetColorU32(AccentInk(.06f)),Ui(5));
        var color=ImGui.GetColorU32(selected?AccentHover:MutedText);
        var showIcon=size.X>=Ui(135);
        using(UiFonts.PushRelative(.96f,emphasis:selected))
        {
            var text=TruncateToWidth(label,MathF.Max(1,size.X-Ui(showIcon?40:12)));
            var textSize=ImGui.CalcTextSize(text);
            var x=position.X+(size.X-textSize.X-Ui(showIcon?25:0))*.5f;
            var y=position.Y+(size.Y-textSize.Y)*.5f;
            if(showIcon) UiFonts.DrawIcon(draw,ImGui.GetFontSize(),new(x,y),color,icon.ToIconString());
            draw.AddText(new(x+Ui(showIcon?25:0),y),color,text);
        }
        if(selected) draw.AddRectFilled(position+UiVec(0,39),position+new Vector2(size.X,Ui(42)),ImGui.GetColorU32(Accent),Ui(1));
    }

    private void DrawPlayerPreviewDrawer()
    {
        var engine = screenController.Engine;
        var current = queue.Current;
        if (stream.Mode == StreamMode.Viewing &&
            stream.CurrentRoomState is { Url: { Length: > 0 } roomUrl } roomState)
            current = CreatePartyMediaDisplayEntry(roomUrl,
                string.IsNullOrWhiteSpace(roomState.MediaTitle) ? "Watch Party media" : roomState.MediaTitle,
                roomState.MediaThumbnailUrl);
        current = WithStudioPlaybackDisplay(current);
        var hasPlayback = current is not null ||
                          video.IsPlayingGame ||
                          video.IsPlayingBrowser ||
                          video.IsPlayingLocalVideo ||
                          video.IsPlayingImage ||
                          (stream.Mode == StreamMode.Viewing &&
                           !string.IsNullOrWhiteSpace(stream.CurrentRoomState?.Url));

        if (!hasPlayback)
        {
            DrawPlayerStatusEmptyState();
            return;
        }

        var (title, source, _) = GetPlayerStatusMedia(current);
        var (position, duration, paused) = video.GetProgress();
        var status = engine.IsShowingWaitingScreen || video.State == Video.VideoPlaybackState.Loading
            ? "Loading"
            : paused || video.State == Video.VideoPlaybackState.Paused
                ? "Paused"
                : "Playing";

        DrawPlayerStatusMediaCard(current, title, source, status, position, duration);
        SetGameLayoutRowGap(Ui(14f));

        var availableWidth = ImGui.GetContentRegionAvail().X;
        var gap = Ui(14f);
        var sideBySide=availableWidth>=Ui(600);
        var cardWidth=sideBySide?(availableWidth-gap)*.5f:availableWidth;
        DrawPlayerViewingModeCard(cardWidth);
        if(sideBySide) ImGui.SameLine(0,gap); else SetGameLayoutRowGap(gap);
        DrawPlayerCompactQueueCard(cardWidth);
    }

    private (string Title, string Source, FontAwesomeIcon Icon) GetPlayerStatusMedia(
        Video.VideoQueueEntry? current)
    {
        var engine = screenController.Engine;

        if (current is not null)
        {
            return (
                stream.Mode == StreamMode.Viewing
                    ? WatchPartyMediaDisplay.Title(current.Url, current.Title, video.IsAudioOnly)
                    : string.IsNullOrWhiteSpace(current.Title) ? "Now Playing" : current.Title,
                current.Source,
                video.IsAudioOnly ? FontAwesomeIcon.BroadcastTower : FontAwesomeIcon.Video);
        }

        if (engine.IsPlayingGame)
        {
            var system = engine.IsPlayingSnes
                ? GameSystem.Snes
                : engine.IsPlayingNes
                    ? GameSystem.Nes
                    : engine.IsPlayingGameBoyAdvance
                        ? GameSystem.GameBoyAdvance
                        : engine.IsPlayingMasterSystem
                            ? GameSystem.MasterSystem
                        : engine.IsPlayingGameGear
                            ? GameSystem.GameGear
                            : GameSystem.GameBoy;
            var path = system switch
            {
                GameSystem.Snes => snesSelectedRomPath,
                GameSystem.Nes => nesSelectedRomPath,
                GameSystem.GameBoyAdvance => gameBoyAdvanceSelectedRomPath,
                GameSystem.MasterSystem => masterSystemSelectedRomPath,
                GameSystem.GameGear => gameGearSelectedRomPath,
                _ => gameBoySelectedRomPath
            };
            var systemName = system switch
            {
                GameSystem.Snes => "Super Nintendo",
                GameSystem.Nes => "Nintendo Entertainment System",
                GameSystem.GameBoyAdvance => "Game Boy Advance",
                GameSystem.MasterSystem => "Master System / SG-1000",
                GameSystem.GameGear => "Game Gear",
                _ => "Game Boy / Color"
            };
            var gameName = string.IsNullOrWhiteSpace(path) ? string.Empty : LibraryGameName(path);
            return (string.IsNullOrWhiteSpace(gameName) ? $"{systemName} game" : gameName, systemName, FontAwesomeIcon.Gamepad);
        }

        if (engine.IsPlayingBrowser)
        {
            return (engine.Browser?.Title ?? "Web Browser", "Alpha Channel Browser", FontAwesomeIcon.Globe);
        }

        if (engine.IsPlayingLocalVideo)
        {
            var name = string.IsNullOrWhiteSpace(localVideoSelectedPath)
                ? "Local Video"
                : Path.GetFileNameWithoutExtension(localVideoSelectedPath);
            return (name, "Local video", FontAwesomeIcon.Video);
        }

        if (engine.IsPlayingImage)
        {
            return (engine.GetMediaTitle() ?? "Images", "Image viewer", FontAwesomeIcon.Images);
        }

        var roomTitle = stream.CurrentRoomState?.MediaTitle;
        return (WatchPartyMediaDisplay.Title(stream.CurrentRoomState?.Url, roomTitle, video.IsAudioOnly), "Watch Party", FontAwesomeIcon.Tv);
    }

    private void DrawPlayerStatusMediaCard(Video.VideoQueueEntry? current,string title,string source,
        string status,float position,float duration)
    {
        var width=ImGui.GetContentRegionAvail().X;
        var horizontal=width>=Ui(640);
        var height=Ui(horizontal?224:374);
        DrawPlayerHubPanel("##playerStatusMedia",width,height,true,()=>
        {
            var origin=ImGui.GetCursorScreenPos();
            var available=ImGui.GetContentRegionAvail().X;
            var thumbWidth=horizontal?MathF.Min(Ui(230),width*.3f):MathF.Min(Ui(230),available);
            var thumbSize=new Vector2(thumbWidth,thumbWidth*9/16);
            var thumbOrigin=origin+new Vector2(0,horizontal?(height-Ui(36)-thumbSize.Y)*.5f:0);
            DrawPlayerStatusThumbnail(current,title,source,thumbOrigin,thumbSize);
            var details=horizontal?origin+new Vector2(thumbWidth+Ui(20),0):origin+new Vector2(0,thumbSize.Y+Ui(16));
            var detailsWidth=horizontal?available-thumbWidth-Ui(20):available;
            var draw=ImGui.GetWindowDrawList();
            using(UiFonts.PushRegularRelative(.76f)) draw.AddText(details,ImGui.GetColorU32(AccentHover),"NOW PLAYING");
            var titleOrigin=details+UiVec(0,22);
            float titleBottom;
            using(UiFonts.PushRelative(1.2f,emphasis:true))
                titleBottom=DrawWrappedLines(draw,titleOrigin,detailsWidth,ImGui.GetTextLineHeight(),2,ImGui.GetColorU32(Vector4.One),title);
            ImGui.SetCursorScreenPos(titleOrigin);
            ImGui.InvisibleButton("##playerMediaTitle",new Vector2(detailsWidth,MathF.Max(Ui(20),titleBottom-titleOrigin.Y)));
            if(ImGui.IsItemHovered()) UiFonts.SetTooltip(title);
            ImGui.SetCursorScreenPos(new(details.X,titleBottom+Ui(7)));
            using(UiFonts.PushRegularRelative(.94f))
                ImGui.TextColored(MutedText,TruncateToWidth(source,detailsWidth));
            ImGui.SetCursorScreenPos(new(details.X,MathF.Max(details.Y+Ui(92),ImGui.GetItemRectMax().Y+Ui(8))));
            if (screenController.Engine.AudioOnlyMode) DrawAudioOnlyStatus();
            else DrawPlayerStatusPill(status);
            if(duration>0)
            {
                ImGui.SameLine(0,Ui(14));
                using(UiFonts.PushRegularRelative(.9f)) ImGui.TextColored(MutedText,$"{FormatTime(position)} / {FormatTime(duration)}");
            }
            var actionY=MathF.Max(details.Y+Ui(144),ImGui.GetItemRectMax().Y+Ui(14));
            var miniWidth=MathF.Min(Ui(184),(detailsWidth-Ui(10))*.56f);
            var tvWidth=MathF.Min(Ui(144),detailsWidth-miniWidth-Ui(10));
            ImGui.SetCursorScreenPos(new(details.X,actionY));
            DrawPlayerStatusActionButton("##openMiniPlayerStatus",FontAwesomeIcon.Expand,"Open Mini Player",miniWidth,true,
                ()=>OnMiniPlayerRequested?.Invoke(),36);
            ImGui.SameLine(0,Ui(10));
            DrawPlayerTvButton(tvWidth,height:36,primary:false);
        });
    }

    private void DrawPlayerStatusThumbnail(
        Video.VideoQueueEntry? current,
        string title,
        string source,
        Vector2 origin,
        Vector2 size,
        bool activePlayback = true)
    {
        var drawList = ImGui.GetWindowDrawList();
        var thumbnailUrl = current?.ThumbnailUrl ??
                           (activePlayback && stream.Mode == StreamMode.Viewing
                               ? stream.CurrentRoomState?.MediaThumbnailUrl
                               : null);
        var thumbnail = thumbnails.Get(thumbnailUrl);
        if (thumbnail is not null)
        {
            drawList.AddImageRounded(thumbnail.Handle, origin, origin + size, Vector2.Zero, Vector2.One, uint.MaxValue, Ui(8f));
            return;
        }

        var engine = screenController.Engine;
        var fallbackSource = activePlayback && engine.IsPlayingGame ? "Gameplay" :
            activePlayback && engine.IsPlayingBrowser ? "Browser" : source;
        var entry = current ?? new VideoQueueEntry(string.Empty, title, fallbackSource, null, null);
        DrawPartyThumbnailFallback(entry, origin, size, usePlaybackState: activePlayback,
            compactIcon: activePlayback && engine.IsPlayingImage ? FontAwesomeIcon.Images : null);
    }

    private void DrawPlayerStatusPill(string status)
    {
        var color = status == "Playing" ? Good : status == "Paused" ? Accent : MutedText;
        ImGui.TextColored(color, "●");
        ImGui.SameLine(0f, Ui(6f));
        ImGui.TextColored(color, status);
    }

    private void DrawPlayerTvButton(float width, float height = 40f, bool recenterOnSpawn = false, bool? primary = null)
    {
        var engine = screenController.Engine;
        var spawned = stream.Mode == StreamMode.Viewing ? ViewerTvEnabled : engine.IsActive;
        DrawPlayerStatusActionButton(
            "##togglePlayerStatusTv",
            spawned ? FontAwesomeIcon.Times : FontAwesomeIcon.Tv,
            spawned ? "Despawn TV" : "Spawn TV",
            width,
            primary ?? spawned,
            () =>
            {
                if (HalloweenActivityActive) { EndHalloween(); return; }
                if (stream.Mode == StreamMode.Viewing)
                {
                    if (ViewerTvEnabled)
                    {
                        DespawnViewerTv(stopPlayback: !(IsMiniPlayerOpen?.Invoke() ?? false));
                    }
                    else
                    {
                        RequestViewerTvSpawn();
                    }
                }
                else if (engine.IsActive)
                {
                    engine.DespawnScreen();
                }
                else
                {
                    if (recenterOnSpawn)
                    {
                        engine.RecenterScreen();
                    }

                    engine.RespawnScreen();
                }
            },
            height);
    }

    private void DrawPlayerViewingModeCard(float width)
    {
        DrawPlayerHubPanel("##playerViewingMode",width,Ui(168),false,()=>
        {
            var origin=ImGui.GetCursorScreenPos();
            var available=ImGui.GetContentRegionAvail().X;
            var heading=stream.Mode switch
            {
                StreamMode.Hosting=>"Hosting Watch Party",
                StreamMode.Viewing=>"Watching together",
                _=>"Watching solo"
            };
            using(UiFonts.PushRelative(1.08f,emphasis:true)) ImGui.TextUnformatted(heading);
            ImGui.SetCursorScreenPos(origin+UiVec(0,35));
            ImGui.TextColored(Good,"●");
            ImGui.SameLine(0,Ui(6));
            var status=stream.Mode switch
            {
                StreamMode.Hosting=>stream.Roster.Length==1?"1 viewer watching":$"{stream.Roster.Length} viewers watching",
                StreamMode.Viewing=>$"With {joinedHostDisplayName??"the host"}",
                _=>"Playing solo"
            };
            ImGui.TextUnformatted(TruncateToWidth(status,MathF.Max(1,available-Ui(18))));
            if(ImGui.IsItemHovered()) UiFonts.SetTooltip(status);
            ImGui.SetCursorScreenPos(origin+UiVec(0,63));
            using(UiFonts.PushRegularRelative(.9f))
            using(ImRaii.TextWrapPos(0))
                ImGui.TextColored(MutedText,stream.Mode==StreamMode.Viewing?"Playback is synced to the host.":
                    stream.Mode==StreamMode.Hosting?"Your party is watching with you.":"Only you can currently see this.");
            ImGui.SetCursorScreenPos(origin+UiVec(0,96));
            DrawPlayerStatusActionButton("##playerWatchPartyAction",FontAwesomeIcon.Users,
                stream.Mode==StreamMode.None?"Create Watch Party":"Open Watch Party",available,stream.Mode==StreamMode.None,
                ()=>currentPage=HomePage.WatchAlong,36);
        });
    }

    private void DrawPlayerCompactQueueCard(float width)
    {
        DrawPlayerHubPanel("##playerCompactQueue",width,Ui(168),false,()=>
        {
            var origin=ImGui.GetCursorScreenPos();
            var available=ImGui.GetContentRegionAvail().X;
            using(UiFonts.PushRelative(1.08f,emphasis:true)) ImGui.TextUnformatted("Up next");
            ImGui.SetCursorScreenPos(origin+UiVec(0,35));
            if(queue.Entries.Count==0)
            {
                ImGui.TextUnformatted("Nothing queued");
                ImGui.SetCursorScreenPos(origin+UiVec(0,63));
                using(UiFonts.PushRegularRelative(.9f))
                using(ImRaii.TextWrapPos(0)) ImGui.TextColored(MutedText,"Add media whenever you're ready.");
            }
            else
            {
                var next=queue.Entries[0];
                var thumbnailSize=UiVec(80,45);
                var thumbOrigin=ImGui.GetCursorScreenPos();
                DrawPlayerStatusThumbnail(next,next.Title,next.Source,thumbOrigin,thumbnailSize,activePlayback:false);
                var textOrigin=thumbOrigin+UiVec(90,0);
                var textWidth=MathF.Max(1,available-Ui(90));
                var draw=ImGui.GetWindowDrawList();
                using(UiFonts.PushRelative(.93f,emphasis:true))
                    DrawWrappedLines(draw,textOrigin,textWidth,ImGui.GetTextLineHeight(),2,ImGui.GetColorU32(Vector4.One),next.Title);
                using(UiFonts.PushRegularRelative(.78f))
                {
                    var detail=next.Duration is {} duration?$"{next.Source} · {FormatTime((float)duration.TotalSeconds)}":next.Source;
                    draw.AddText(textOrigin+UiVec(0,32),ImGui.GetColorU32(MutedText),TruncateToWidth(detail,textWidth));
                }
                ImGui.InvisibleButton("##nextPlayerMedia",new Vector2(available,Ui(45)));
                if(ImGui.IsItemHovered()) UiFonts.SetTooltip(next.Title);
            }
            ImGui.SetCursorScreenPos(origin+UiVec(0,96));
            DrawPlayerStatusActionButton("##viewPlayerQueue",FontAwesomeIcon.List,"View Queue",available,false,
                ()=>activePlayerDrawer=PlayerDrawer.Queue,36);
        });
    }

    private void DrawPlayerStatusEmptyState()
    {
        var width=ImGui.GetContentRegionAvail().X;
        var artwork=width>=Ui(680);
        var stackedButtons=width<Ui(460);
        DrawPlayerHubPanel("##playerStatusEmpty",width,Ui(stackedButtons?270:222),true,()=>
        {
            var origin=ImGui.GetCursorScreenPos();
            var available=ImGui.GetContentRegionAvail().X;
            var textWidth=available-Ui(artwork?230:0);
            var draw=ImGui.GetWindowDrawList();
            using(UiFonts.PushRegularRelative(.78f))
                draw.AddText(origin+UiVec(4,4),ImGui.GetColorU32(AccentHover),"READY WHEN YOU ARE");
            float titleBottom;
            using(UiFonts.PushRelative(1.75f,emphasis:true))
                titleBottom=DrawWrappedLines(draw,origin+UiVec(4,32),textWidth,ImGui.GetTextLineHeight(),2,
                    ImGui.GetColorU32(Vector4.One),"Your screen. Your choice.");
            float detailBottom;
            using(UiFonts.PushRegularRelative(.94f))
                detailBottom=DrawWrappedLines(draw,new(origin.X+Ui(4),titleBottom+Ui(12)),textWidth,
                    ImGui.GetTextLineHeight(),2,ImGui.GetColorU32(MutedText),"Add a video, stream or local file to start watching.");
            var buttonY=MathF.Max(origin.Y+Ui(132),detailBottom+Ui(20));
            var gap=Ui(10);
            var addWidth=stackedButtons?MathF.Min(Ui(220),available):MathF.Min(Ui(166),(textWidth-gap)*.43f);
            var partyWidth=stackedButtons?addWidth:MathF.Min(Ui(218),textWidth-addWidth-gap);
            ImGui.SetCursorScreenPos(new(origin.X+Ui(4),buttonY));
            DrawPlayerStatusActionButton("##emptyAddMedia",FontAwesomeIcon.Plus,"Add Media",addWidth,true,
                ()=>activePlayerDrawer=PlayerDrawer.PlayVideo,38);
            if(stackedButtons) ImGui.SetCursorScreenPos(new(origin.X+Ui(4),buttonY+Ui(48))); else ImGui.SameLine(0,gap);
            DrawPlayerStatusActionButton("##emptyWatchParty",FontAwesomeIcon.Users,
                stream.Mode==StreamMode.None?"Create Watch Party":"Open Watch Party",partyWidth,false,
                ()=>currentPage=HomePage.WatchAlong,38);
            if(artwork) DrawPlayerEmptyArtwork(draw,origin+new Vector2(available-Ui(204),Ui(24)),Ui(1));
        });
        SetGameLayoutRowGap(Ui(12));
        using(UiFonts.PushRegularRelative(.86f))
        using(ImRaii.TextWrapPos(0))
            ImGui.TextColored(MutedText,"Watch on your TV, open the mini player, or bring your party along.");
    }

    private void DrawPlayerStatusActionButton(
        string id,
        FontAwesomeIcon icon,
        string label,
        float width,
        bool primary,
        Action action,
        float height = 40f)
    {
        var background = primary ? Accent : FrameBg;
        var hover = primary ? AccentHover : FrameBgHover;
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(8f)))
        using (ImRaii.PushColor(ImGuiCol.Button, background)
                   .Push(ImGuiCol.ButtonHovered, hover)
                   .Push(ImGuiCol.ButtonActive, primary ? AccentActive : hover))
        {
            var position = ImGui.GetCursorScreenPos();
            var size = new Vector2(width, Ui(height));
            if (ImGui.Button(id, size))
            {
                action();
            }
            DrawPlayerActionButtonContent(position, size, icon, label, Vector4.One);
        }
    }

    private void DrawPlayVideoDrawer()
    {
        if (pendingPlayerSearch != null)
        {
            var pendingSearch =
                pendingPlayerSearch.Trim();

            switch (playerSourceTab)
            {
                case 0:
                    // Link
                    // Kept in case something else routes here later.
                    urlInput = pendingSearch;
                    break;

                case 2:
                    // Twitch
                    twitchChannelInput = pendingSearch;
                    twitchResult = null;
                    twitchError = null;

                    if (!string.IsNullOrWhiteSpace(twitchChannelInput) &&
                        !isCheckingTwitch)
                    {
                        isCheckingTwitch = true;

                        _ = RunTwitchCheckAsync(
                            twitchChannelInput);
                    }

                    break;


            }

            pendingPlayerSearch = null;
        }

        if (showingAddMediaSources)
        {
            DrawAddMediaSourceLanding();
            return;
        }

        if(playerSourceTab is 0 or 2 or 4 or 6 or 7)
        {
            if(DrawAddMediaBackAction()) return;
            if(playerSourceTab==0) DrawLinkSource();
            else if(playerSourceTab==2) DrawTwitchCheck();
            else if(playerSourceTab==4) DrawGoLive();
            else if(playerSourceTab==6) DrawImagesSlideshows();
            else DrawLocalVideoSource();
            return;
        }

        if (ImGui.Button(
                "\u2039  All media sources##allMediaSources",
                UiVec(190f, 36f)))
        {
            showingAddMediaSources =
                true;

            return;
        }

        ImGui.Dummy(
            UiVec(0f, 8f));

        ImGui.Separator();

        ImGui.Dummy(
            UiVec(0f, 12f));

        switch (playerSourceTab)
        {

            case 5:
                currentPage = HomePage.Music;
                musicTab = MusicTab.Radio;
                break;


        }
    }

    private void OpenAddMediaSource(
        int sourceTab)
    {
        if (sourceTab == 2)
        {
            currentPage = HomePage.Twitch;
            twitchHubTab = TwitchHubTab.Discover;
            return;
        }
        playerSourceTab =
            sourceTab;

        if (sourceTab == 4)
        {
            resetGoLiveScroll = true;
        }

        showingAddMediaSources =
            false;
    }

    private void DrawQueueDrawer()
    {
        DrawQueue();
    }


    private void BeginLocalVideoWatchPartyBroadcast(
    bool startPlayback)
    {
        if (stream.Mode ==
            StreamMode.Viewing)
        {
            Plugin.ChatGui.Print(
                "[AlphaChannel] Leave your current Watch Party before broadcasting a local video.");

            return;
        }

        if (stream.Mode ==
            StreamMode.Hosting)
        {
            activeGameDialog = "Local video broadcast";

            return;
        }

        if (!startPlayback)
        {
            pendingLocalVideoResumePosition =
                MathF.Max(
                    0f,
                    video.GetProgress().Position);
        }
        else
        {
            pendingLocalVideoResumePosition =
                0f;
        }

        pendingWatchPartyMediaKind = PendingWatchPartyMediaKind.LocalVideoRoom;

        watchPartyCreationPopupOpen =
            true;

        createRoomPassword =
            string.Empty;

        createLockedRoomPasswordError =
            null;
    }

    private void StartLocalVideoBroadcastInHostedRoom(
    bool startPlayback,
    bool navigateToWatchParty = false)
    {
        if (stream.Mode !=
            StreamMode.Hosting)
        {
            return;
        }

        if (startPlayback)
        {
            pendingLocalVideoResumePosition =
                0f;

            StartSelectedLocalVideo(
                broadcast: true);
        }
        else
        {
            //
            // Restore the selected file if local playback stopped before
            // the host confirmed broadcasting, retaining any saved position.
            //

            if (!video.IsPlayingLocalVideo)
            {
                if (string.IsNullOrWhiteSpace(
                        localVideoSelectedPath) ||
                    !File.Exists(
                        localVideoSelectedPath))
                {
                    localVideoError =
                        "The selected local video file could not be found.";

                    return;
                }

                var resumed =
                    video.PlayLocalVideo(
                        localVideoSelectedPath);

                if (!resumed)
                {
                    localVideoError =
                        video.LastError ??
                        "Local video playback could not be resumed.";

                    return;
                }

                video.SetOverlayTitle(
                    Path.GetFileNameWithoutExtension(
                        localVideoSelectedPath),
                    "Local Video");

                if (pendingLocalVideoResumePosition >
                    0f)
                {
                    video.Seek(
                        pendingLocalVideoResumePosition);
                }
            }

            if (!StartLocalVideoWatchPartyBroadcast())
            {
                return;
            }

            pendingLocalVideoResumePosition =
                0f;
        }

        if (!localVideoBroadcastArmed)
        {
            return;
        }

        if (navigateToWatchParty)
        {
            currentPage =
                HomePage.WatchAlong;

            partyPanelTab =
                PartyPanelTab.NowPlaying;
        }
    }

    private bool StartLocalVideoWatchPartyBroadcast()
    {
        if (stream.Mode !=
            StreamMode.Hosting)
        {

            return false;
        }

        if (!CanStartBroadcastWithAllowance(BroadcastAllowanceMethod.LocalVideo))
        {
            return false;
        }

        if (!video.IsPlayingLocalVideo)
        {

            return false;
        }

        if (string.IsNullOrWhiteSpace(
                localVideoSelectedPath) ||
            !File.Exists(
                localVideoSelectedPath))
        {
            localVideoError =
                "The selected local video file could not be found.";

            return false;
        }

        if (CurrentSession is not { } session)
        {
            Plugin.ChatGui.Print(
                "[AlphaChannel] Sign in before broadcasting a local video.");

            return false;
        }

        var streamKey =
            Plugin.Cfg.StreamKeys
                .GetValueOrDefault(
                    session.AccountId);

        if (string.IsNullOrWhiteSpace(
                streamKey))
        {
            localVideoError =
                "No stream key is available. Generate one from Settings > Account first.";

            return false;
        }

        //
        // A single account has one MediaMTX live path. Make sure an old
        // emulator broadcaster cannot continue owning it.
        //

        StopGameWatchPartyBroadcast();
        StopLocalVideoWatchPartyBroadcast();

        screenController.Engine
            .StopLocalVideoBroadcast();

        localVideoBroadcastPublishUrl =
            $"{BuildRtmpServer()}/{streamKey}";

        localVideoBroadcastHlsUrl =
    $"{BuildMyHlsUrl(session)}" +
    $"?broadcast={Guid.NewGuid():N}&ac_content=local-video";

        localVideoBroadcastStartFailed =
            false;

        nextLocalVideoBroadcastRetryUtc = DateTime.MinValue;

        localVideoBroadcastEncoderExpectedRunning =
            false;

        localVideoBroadcastPaused =
            false;

        localVideoBroadcastArmed =
            true;

        localVideoError =
            null;


        return true;
    }


    private static bool IsBroadcastableTextSubtitle(string codec) =>
        codec.ToLowerInvariant() is "subrip" or "srt" or "ass" or "ssa" or
            "webvtt" or "mov_text" or "text";

    internal void UpdateLocalVideoBroadcastDemand()
    {
        if (!localVideoBroadcastArmed)
        {
            return;
        }

        var engine =
            screenController.Engine;

        //
        // Disarm when the room closes, hosting transfers away, the local
        // video stops, or its original file is no longer available.
        //

        if (stream.Mode != StreamMode.Hosting ||
            !video.IsPlayingLocalVideo ||
            string.IsNullOrWhiteSpace(
                localVideoSelectedPath) ||
            !File.Exists(
                localVideoSelectedPath) ||
            string.IsNullOrWhiteSpace(
                localVideoBroadcastPublishUrl))
        {
            StopLocalVideoWatchPartyBroadcast();
            return;
        }

        var (position, duration, paused) =
            video.GetProgress();

        localVideoBroadcastPaused =
            paused;

        //
        // Stop cleanly at the end instead of repeatedly launching FFmpeg
        // at the final frame.
        //

        if (duration > 0f &&
            position >=
            duration - 0.75f)
        {
            StopLocalVideoWatchPartyBroadcast();
            return;
        }

        var hasViewer = CanStartLocalVideoUpload;

        //
        // Pausing the host or losing the last viewer stops network upload.
        // On resume/rejoin, FFmpeg starts again at MPV's current timestamp.
        //

        if (!hasViewer ||
            paused)
        {
            localVideoSubtitlesPreparing = false;
            localVideoBroadcastEncoderExpectedRunning =
                false;

            if (engine.IsLocalVideoBroadcasting)
            {
                engine.StopLocalVideoBroadcast();
            }

            localVideoBroadcastStartFailed =
                false;
            nextLocalVideoBroadcastRetryUtc = DateTime.MinValue;

            return;
        }

        var encoderRunning =
            engine.IsLocalVideoBroadcasting;

        // MPV provides each track's overall FFmpeg stream index. The
        // extraction map 0:s:N instead counts subtitle streams only.
        // Include non-text subtitle tracks in that count, even though we
        // cannot render them through libass.
        if (engine.MediaLoadPending) return;
        var selectedAudioId = video.GetSelectedAudioTrackId();
        var audioTracks = video.GetAudioTracks();
        var selectedAudioTrack = audioTracks.FirstOrDefault(track => track.Id == selectedAudioId);
        if ((selectedAudioId.HasValue || audioTracks.Count > 0) && selectedAudioTrack is null) return;
        if (selectedAudioTrack is { FfmpegStreamIndex: null })
        {
            localVideoBroadcastEncoderExpectedRunning = false;
            engine.StopLocalVideoBroadcast();
            localVideoError = "The selected audio track cannot be broadcast. Choose another audio track.";
            return;
        }
        var audioStreamIndex = selectedAudioTrack?.FfmpegStreamIndex;
        var selectedSubtitleId = video.GetSelectedSubtitleId();
        var sourceSubtitleTracks = video.GetEmbeddedSubtitleTracks()
            .Where(track => track.FfmpegStreamIndex.HasValue)
            .OrderBy(track => track.FfmpegStreamIndex).ToArray();
        var selectedSubtitleOrdinal = Array.FindIndex(sourceSubtitleTracks, track =>
            track.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) == selectedSubtitleId);
        int? subtitleStreamIndex = selectedSubtitleOrdinal >= 0 &&
            IsBroadcastableTextSubtitle(sourceSubtitleTracks[selectedSubtitleOrdinal].Codec)
                ? selectedSubtitleOrdinal : null;

        //
        // If FFmpeg was expected to remain alive but exited, report the
        // failure and avoid relaunching it every framework frame.
        //

        if (localVideoBroadcastEncoderExpectedRunning &&
            !encoderRunning)
        {
            localVideoBroadcastEncoderExpectedRunning =
                false;

            localVideoBroadcastStartFailed =
                true;
            nextLocalVideoBroadcastRetryUtc = DateTime.UtcNow.AddSeconds(10);

            localVideoError =
                engine.LocalVideoBroadcastError ??
                "The local video relay encoder stopped unexpectedly.";

            return;
        }

        if (encoderRunning)
        {
            if (localVideoBroadcastSubtitleStreamIndex != subtitleStreamIndex ||
                localVideoBroadcastAudioStreamIndex != audioStreamIndex)
            {
                localVideoBroadcastEncoderExpectedRunning = false;
                engine.StopLocalVideoBroadcast();
                localVideoBroadcastStartFailed = false;
                encoderRunning = false;
            }
        }

        if (encoderRunning)
        {
            //
            // Detect a host seek. Small differences are normal because MPV
            // and FFmpeg begin independently. A large difference after the
            // startup grace period means FFmpeg should be restarted at MPV's
            // new timestamp.
            //

            var runningFor =
                DateTime.UtcNow -
                localVideoBroadcastStartTimeUtc;

            var expectedPosition =
                localVideoBroadcastStartPosition +
                runningFor.TotalSeconds;

            var drift =
                Math.Abs(
                    position -
                    expectedPosition);

            if (runningFor <
                    TimeSpan.FromSeconds(5) ||
                drift <= 4d)
            {
                return;
            }

            localVideoBroadcastEncoderExpectedRunning =
                false;

            engine.StopLocalVideoBroadcast();

            localVideoBroadcastStartFailed =
                false;
        }

        if (localVideoBroadcastStartFailed)
        {
            if (DateTime.UtcNow < nextLocalVideoBroadcastRetryUtc)
                return;
            localVideoBroadcastStartFailed = false;
        }

        string? subtitleFile = null;
        localVideoSubtitlesPreparing = false;
        if (subtitleStreamIndex is { } ordinal)
        {
            var ffmpegPath = engine.Resources.GetLocationFFmpeg();
            if (string.IsNullOrWhiteSpace(ffmpegPath)) return;
            var prepared = localVideoSubtitleCache.Prepare(ffmpegPath, localVideoSelectedPath, ordinal);
            if (prepared is null)
            {
                localVideoSubtitlesPreparing = true;
                return;
            }
            if (prepared.Error is not null)
            {
                localVideoError = prepared.Error;
                localVideoBroadcastStartFailed = true;
                nextLocalVideoBroadcastRetryUtc = DateTime.UtcNow.AddSeconds(10);
                return;
            }
            subtitleFile = prepared.Path;
        }

        var configuredQuality = Plugin.Cfg.LocalVideoBroadcastQuality;
        var patreonTier = ConfiguredPatreonTier();
        var quality = patreonTier switch
        {
            >= 3 => configuredQuality,
            >= 2 when configuredQuality != LocalVideoBroadcastQuality.DataSaver =>
                LocalVideoBroadcastQuality.Standard,
            _ => LocalVideoBroadcastQuality.DataSaver,
        };

        var started =
            engine.StartLocalVideoBroadcast(
                localVideoSelectedPath,
                localVideoBroadcastPublishUrl,
                position,
                quality,
                subtitleFile, audioStreamIndex);

        if (!started)
        {
            localVideoBroadcastStartFailed =
                true;
            nextLocalVideoBroadcastRetryUtc = DateTime.UtcNow.AddSeconds(10);

            localVideoBroadcastEncoderExpectedRunning =
                false;

            localVideoError =
                engine.LastError ??
                engine.LocalVideoBroadcastError ??
                "The local video broadcast could not be started.";

            return;
        }

        // Each encoder run creates a new HLS timeline, including track changes,
        // seeks and resumes. Viewers must reopen it rather than reuse an ended
        // playlist with the same URL and reset segment sequence numbers.
        localVideoBroadcastHlsUrl = LiveBroadcastStream.RenewPlaybackSession(localVideoBroadcastHlsUrl!);

        localVideoBroadcastStartPosition =
            position;

        localVideoBroadcastSubtitleStreamIndex = subtitleStreamIndex;
        localVideoBroadcastAudioStreamIndex = audioStreamIndex;

        localVideoBroadcastStartTimeUtc =
            DateTime.UtcNow;

        localVideoBroadcastEncoderExpectedRunning =
            true;

        localVideoError =
            null;

    }


    private void StopLocalVideoWatchPartyBroadcast()
    {
        localVideoBroadcastEncoderExpectedRunning =
            false;

        localVideoBroadcastSubtitleStreamIndex = null;
        localVideoBroadcastAudioStreamIndex = null;
        localVideoSubtitlesPreparing = false;

        screenController.Engine
            .StopLocalVideoBroadcast();

        localVideoBroadcastArmed =
            false;

        localVideoBroadcastStartFailed =
            false;
        nextLocalVideoBroadcastRetryUtc = DateTime.MinValue;

        localVideoBroadcastPaused =
            false;

        localVideoBroadcastPublishUrl =
            null;

        localVideoBroadcastHlsUrl =
            null;
    }

    private void StartSelectedLocalVideo(
    bool broadcast)
    {
        if (!CanStartOtherStudioMedia()) return;
        localVideoError =
            null;

        if (string.IsNullOrWhiteSpace(
                localVideoSelectedPath) ||
            !File.Exists(
                localVideoSelectedPath))
        {
            localVideoError =
                "The selected local video file could not be found.";

            return;
        }

        var started =
            video.PlayLocalVideo(
                localVideoSelectedPath);

        if (!started)
        {
            localVideoError =
                video.LastError ??
                "Local video playback could not be started.";

            return;
        }

        video.SetOverlayTitle(
            Path.GetFileNameWithoutExtension(
                localVideoSelectedPath),
            "Local Video");

        if (broadcast &&
            !StartLocalVideoWatchPartyBroadcast())
        {
            localVideoError ??=
                "The video is playing locally, but its Watch Party broadcast could not be started.";
        }
    }

    private void DrawLocalVideoPatreonHeart(
        Vector2 center)
    {
        var drawList =
            ImGui.GetWindowDrawList();

        var heart =
            FontAwesomeIcon.Heart.ToIconString();

        var padlock =
            FontAwesomeIcon.Lock.ToIconString();

        float iconFontSize;
        Vector2 heartSize;
        Vector2 lockSize;

        using (UiFonts.PushIcon())
        {
            iconFontSize =
                ImGui.GetFontSize();

            using var uiFontScale70286=UiFonts.PushRelative(2.05f);

            heartSize =
                ImGui.CalcTextSize(
                    heart);
            uiFontScale70286.Dispose();
            using var uiFontScale70435=UiFonts.PushRelative(0.62f);

            lockSize =
                ImGui.CalcTextSize(
                    padlock);

            uiFontScale70435.Dispose();
        }

        UiFonts.DrawIcon(drawList, iconFontSize * 2.05f, center -
            heartSize *
            0.5f, ImGui.GetColorU32(
                PatreonOrange), heart);

        UiFonts.DrawIcon(drawList, iconFontSize * 0.62f, center -
            lockSize *
            0.5f, ImGui.GetColorU32(
                CardBg), padlock);
    }

    private void DrawLocalVideoBroadcastSettings()
    {
        var patreonTier = ConfiguredPatreonTier();
        var permittedQuality = patreonTier switch
        {
            >= 3 => Plugin.Cfg.LocalVideoBroadcastQuality,
            >= 2 when Plugin.Cfg.LocalVideoBroadcastQuality != LocalVideoBroadcastQuality.DataSaver =>
                LocalVideoBroadcastQuality.Standard,
            _ => LocalVideoBroadcastQuality.DataSaver,
        };
        if (Plugin.Cfg.LocalVideoBroadcastQuality != permittedQuality)
        {
            Plugin.Cfg.LocalVideoBroadcastQuality = permittedQuality;
            Plugin.Cfg.Save();
        }

        DrawLocalVideoBroadcastStrip();
        DrawLocalVideoBroadcastDialog();
    }

    private void DrawLocalVideoBroadcastQualitySelector(
        int patreonTier)
    {
        using var uiFontScale81674=UiFonts.PushRelative(0.72f);
        ImGui.TextColored(
            MutedText,
            "BROADCAST QUALITY");
        ImGui.SameLine(0f, Ui(6f));
        using (UiFonts.PushIcon())
        {
            ImGui.TextColored(
                MutedText,
                FontAwesomeIcon.InfoCircle.ToIconString());
        }
        if (ImGui.IsItemHovered())
        {
            UiFonts.SetTooltip(
                "Video quality will not exceed source resolution.");
        }
        uiFontScale81674.Dispose();

        var quality = Plugin.Cfg.LocalVideoBroadcastQuality;
        var preview = quality switch
        {
            LocalVideoBroadcastQuality.High => "High",
            LocalVideoBroadcastQuality.Standard => "Standard",
            _ => "Data Saver",
        };

        var selectorWidth = MathF.Min(
            Ui(330f),
            MathF.Max(
                Ui(220f),
                ImGui.GetContentRegionAvail().X - Ui(24f)));

        using (UiDisabled.Scope(localVideoBroadcastArmed))
        using (ImRaii.PushStyle(
                   ImGuiStyleVar.FrameBorderSize,
                   1f)
               .Push(
                   ImGuiStyleVar.FrameRounding,
                   Ui(6f)))
        using (ImRaii.PushColor(
                   ImGuiCol.FrameBg,
                   FrameBg)
               .Push(
                   ImGuiCol.FrameBgHovered,
                   FrameBgHover)
               .Push(
                   ImGuiCol.FrameBgActive,
                   AccentFieldActive)
               .Push(
                   ImGuiCol.Border,
                   new Vector4(Accent.X, Accent.Y, Accent.Z, 0.55f)))
        {
            ImGui.SetNextItemWidth(selectorWidth);
            if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginCombo(
                    "##localVideoBroadcastQuality",
                    preview)))
            {
                if (ImGui.Selectable(
                        "Data Saver",
                        quality == LocalVideoBroadcastQuality.DataSaver))
                {
                    Plugin.Cfg.LocalVideoBroadcastQuality =
                        LocalVideoBroadcastQuality.DataSaver;
                    Plugin.Cfg.Save();
                }

                DrawLocalVideoPatreonQualityOption(
                    "Standard",
                    LocalVideoBroadcastQuality.Standard,
                    quality,
                    patreonTier,
                    2);
                DrawLocalVideoPatreonQualityOption(
                    "High",
                    LocalVideoBroadcastQuality.High,
                    quality,
                    patreonTier,
                    3);

                ImGui.EndCombo();
            }
        }

        if (localVideoBroadcastArmed &&
            ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            UiFonts.SetTooltip(
                "Stop broadcasting to change quality.");
        }

        using var uiFontScale84682=UiFonts.PushRelative(0.72f);
        ImGui.TextColored(
            MutedText,
            quality switch
            {
                LocalVideoBroadcastQuality.High => "1080p · 5 Mbps",
                LocalVideoBroadcastQuality.Standard => "720p · ~2.8 Mbps",
                _ => "480p · 1 Mbps",
            });
        using (ImRaii.TextWrapPos(0f))
            ImGui.TextColored(MutedText, "Higher quality consumes more broadcast allowance credits.");

        if (patreonTier < 3)
        {
            ImGui.TextColored(
                PatreonOrange,
                patreonTier >= 2
                    ? "High available with Patreon Tier 3"
                    : "Standard: Patreon Tier 2 · High: Patreon Tier 3");
            if (ImGui.IsItemClicked())
            {
                OpenPatreonMembershipPage();
            }
        }
        uiFontScale84682.Dispose();
    }

    private void DrawLocalVideoPatreonQualityOption(
        string label,
        LocalVideoBroadcastQuality option,
        LocalVideoBroadcastQuality selected,
        int patreonTier,
        int requiredTier)
    {
        var available = patreonTier >= requiredTier;
        using (UiDisabled.Scope(!available))
        {
            if (ImGui.Selectable(
                    $"{label}  (Patreon Tier {requiredTier})",
                    selected == option))
            {
                Plugin.Cfg.LocalVideoBroadcastQuality = option;
                Plugin.Cfg.Save();
            }
        }

        if (!available &&
            ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            UiFonts.SetTooltip($"Available with patreon tier {requiredTier}");
        }
    }

    private void DrawLocalVideoBroadcastAllowancePlaceholder()
    {
        if (DrawAccountBroadcastRestriction("Local video")) return;
        var allowance = BroadcastAllowanceDisplayFor(
            BroadcastAllowanceMethod.LocalVideo);
        var remaining = allowance.Remaining;
        var remainingLabel = allowance.RemainingLabel;
        var previewLabel = allowance.Detail;

        var allowanceWidth = MathF.Max(1f, ImGui.GetContentRegionAvail().X);
        var allowanceStartX = ImGui.GetCursorPosX();

        using var uiFontScale86886=UiFonts.PushRelative(0.76f);
        var heading = System.Globalization.CultureInfo.InvariantCulture.TextInfo
            .ToTitleCase(allowance.Heading.ToLowerInvariant())
            .Replace("Weekly Broadcast Allowance", "weekly broadcast allowance");
        using (ImRaii.TextWrapPos(allowanceStartX + allowanceWidth))
            ImGui.TextColored(MutedText, heading);
        var remainingSize = ImGui.CalcTextSize(remainingLabel);
        if (ImGui.CalcTextSize(heading).X + remainingSize.X + Ui(12f) <= allowanceWidth)
        {
            ImGui.SameLine();
            ImGui.SetCursorPosX(allowanceStartX + allowanceWidth - remainingSize.X);
        }
        ImGui.TextColored(
            MutedText,
            remainingLabel);
        uiFontScale86886.Dispose();

        var origin = ImGui.GetCursorScreenPos();
        var size = new Vector2(
            allowanceWidth,
            Ui(14f));
        var fillSize = new Vector2(
            size.X * remaining,
            size.Y);
        var drawList = ImGui.GetWindowDrawList();
        ImGui.Dummy(size);

        drawList.AddRectFilled(
            origin,
            origin + size,
            ImGui.GetColorU32(AccentPanelBg),
            Ui(7f));
        drawList.AddRectFilled(
            origin,
            origin + fillSize,
            ImGui.GetColorU32(Accent),
            Ui(7f));
        drawList.PushClipRect(origin, origin + fillSize, true);
        var stripeStep = Ui(13f);
        for (var x = origin.X - size.Y;
             x < origin.X + fillSize.X + size.Y;
             x += stripeStep)
        {
            drawList.AddLine(
                new Vector2(x, origin.Y + size.Y),
                new Vector2(x + size.Y, origin.Y),
                ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.16f)),
                Ui(4f));
        }
        drawList.PopClipRect();
        drawList.AddRect(
            origin,
            origin + size,
            ImGui.GetColorU32(BorderSubtle),
            Ui(7f));

        using var uiFontScale88589=UiFonts.PushRelative(0.76f);
        ImGui.TextColored(
            MutedText,
            previewLabel);
        DrawPatreonUpgradeLink(allowanceStartX + allowanceWidth);
        uiFontScale88589.Dispose();
    }

    private static void DrawPlayerActionButtonContent(
        Vector2 buttonPos,
        Vector2 buttonSize,
        FontAwesomeIcon icon,
        string label,
        Vector4 color)
    {
        var iconText = icon.ToIconString();

        Vector2 iconSize;

        using (UiFonts.PushIcon())
        {
            iconSize = ImGui.CalcTextSize(iconText);
        }

        var labelSize = ImGui.CalcTextSize(label);

        var gap = string.IsNullOrEmpty(label) ? 0f : 6f;

        var totalWidth =
            iconSize.X +
            gap +
            labelSize.X;

        var start = new Vector2(
            buttonPos.X + (buttonSize.X - totalWidth) * 0.5f,
            buttonPos.Y + (buttonSize.Y - labelSize.Y) * 0.5f);
        var iconPosition = new Vector2(start.X,
            buttonPos.Y + (buttonSize.Y - iconSize.Y) * 0.5f);

        using (UiFonts.PushIcon())
        {
            UiFonts.DrawText(ImGui.GetWindowDrawList(), ImGui.GetFontSize(), iconPosition, ImGui.GetColorU32(color), iconText);
        }

        UiFonts.DrawText(ImGui.GetWindowDrawList(), ImGui.GetFontSize(), start + new Vector2(iconSize.X + gap, 0f), ImGui.GetColorU32(color), label);
    }
}

