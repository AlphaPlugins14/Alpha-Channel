using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Auth;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private readonly WorldBeaconPlacement worldPlacement = new();
    private bool worldPublishBusy, worldReadBusy, worldJoinBusy;
    private string? worldPublishedToken, worldPublishedLocation;
    private readonly WorldBeaconDiscoveryState worldDiscovery = new();
    private CancellationTokenSource? worldReadCancellation;
    private DateTime worldNextPublish, worldNextRead;
    private Vector3 worldPublishedPosition;
    private WorldBeacon[] worldBeacons = [];
    private WorldBeacon[]? worldSortedSource;
    private (WorldBeacon Beacon,float Distance)[] worldVisibleBeacons = [];
    private DateTime worldNextVisibilitySort;
    private string? worldSelectedHost, worldBeaconError, worldReadError;
    private string? worldPanelHost;
    private Vector2 worldPanelPosition;

    private DateTime worldNextLocationCheck;
    private WatchPartyTvLocation worldCachedLocation;
    private bool worldCachedLocationReady;
    private uint worldCachedTerritory;
    private bool WorldLocationReady(out WatchPartyTvLocation location)
    {
        location = default;
        if (!Plugin.Cfg.WorldMode || CurrentSession is null ||
            Plugin.Condition[ConditionFlag.BoundByDuty] || Plugin.Condition[ConditionFlag.BoundByDuty56] ||
            Plugin.Condition[ConditionFlag.BoundByDuty95] || Plugin.Condition[ConditionFlag.ParticipatingInCustomMatch] ||
            !WatchPartyTvLocation.IsLocalPlayerReady()) return false;
        if (DateTime.UtcNow >= worldNextLocationCheck || worldCachedTerritory != Plugin.ClientState.TerritoryType)
        {
            worldNextLocationCheck = DateTime.UtcNow.AddMilliseconds(500);
            worldCachedTerritory = Plugin.ClientState.TerritoryType;
            worldCachedLocationReady = WatchPartyTvLocation.TryReadCurrent(out worldCachedLocation) &&
                (!WatchPartyTvLocation.IsResidentialInterior() || worldCachedLocation.Plot is >= 0);
        }
        location = worldCachedLocation; return worldCachedLocationReady;
    }

    internal void TickWorldMode()
    {
        var now = DateTime.UtcNow;
        var ready = WorldLocationReady(out var location);
        var session = CurrentSession;
        var locationKey = ready ? location.Encode() : null;
        var engine = screenController.Engine;
        var publish = ready && session?.IsGuest == false && characterDirectorySettingsLoaded && !hideLinkedCharacterName &&
            stream.IsConnected && stream.Mode == StreamMode.Hosting &&
            !stream.IsPrivate && stream.RoomKind == RoomKind.Public && engine.IsActive && engine.HasWorldPlacement;
        worldPlacement.Observe(publish ? locationKey : null, engine.ScreenPosition, now);

        if (!worldPublishBusy && now >= worldWithdrawRetry)
        {
            if (worldPublishedToken is { } oldToken &&
                (!publish || oldToken != session?.Token || worldPublishedLocation != locationKey))
                _ = MutateWorldBeaconAsync(oldToken, null, default);
            else if (publish && session is not null && worldPlacement.Ready(now) &&
                (worldPublishedToken is null || Vector3.DistanceSquared(worldPublishedPosition,worldPlacement.Position) > 0.0001f || now >= worldNextPublish))
                _ = MutateWorldBeaconAsync(session.Token, worldPlacement.Location!, worldPlacement.Position);
        }

        if (worldDiscovery.Observe(session?.Token, locationKey, stream.Mode != StreamMode.None))
        {
            worldReadCancellation?.Cancel();
            worldBeacons = [];
            worldSelectedHost = null;
            worldReadError = null;
            worldNextRead = now;
        }
        if (worldDiscovery.Enabled && session is not null && !worldReadBusy && now >= worldNextRead)
            _ = ReadWorldBeaconsAsync(session.Token, locationKey!);
    }

    private DateTime worldWithdrawRetry;
    private async Task MutateWorldBeaconAsync(string token, string? location, Vector3 position)
    {
        worldPublishBusy = true;
        try
        {
            if (location is null) await CharacterDirectory.DeleteBeaconAsync(token, youtubeFeedToken).ConfigureAwait(false);
            else await CharacterDirectory.PublishBeaconAsync(token, location, position, youtubeFeedToken).ConfigureAwait(false);
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (Volatile.Read(ref disposeStarted) != 0) return;
                worldPublishedToken = location is null ? null : token;
                worldPublishedLocation = location;
                worldPublishedPosition = position;
                worldNextPublish = DateTime.UtcNow.AddMinutes(location is null ? 0 : 3);
                worldWithdrawRetry = DateTime.MinValue;
                worldBeaconError = null;
            });
        }
        catch (Exception)
        {
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (Volatile.Read(ref disposeStarted) != 0) return;
                worldNextPublish = worldWithdrawRetry = DateTime.UtcNow.AddMinutes(2);
                worldBeaconError = "World mode couldn't update your beacon. Retrying shortly.";
            });
        }
        finally
        {
            await Plugin.Framework.RunOnFrameworkThread(() => worldPublishBusy = false);
        }
    }

    private async Task ReadWorldBeaconsAsync(string token, string location)
    {
        worldReadBusy = true;
        worldNextRead = DateTime.UtcNow.AddMinutes(3);
        var generation = worldDiscovery.Generation;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(youtubeFeedToken);
        worldReadCancellation = cancellation;
        try
        {
            var result = await CharacterDirectory.GetBeaconsAsync(token, location, cancellation.Token).ConfigureAwait(false);
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (Volatile.Read(ref disposeStarted) != 0 || stream.Mode != StreamMode.None || !worldDiscovery.Matches(token, location, generation)) return;
                worldBeacons = result.Where(b => b.LocationKey == location && b.AccountId != CurrentSession?.AccountId &&
                    float.IsFinite(b.X) && float.IsFinite(b.Y) && float.IsFinite(b.Z) && !AlphaChatBlocked(b.AccountId)).ToArray();
                worldNextRead = DateTime.UtcNow.AddMinutes(worldBeacons.Length == 0 ? 5 : 3);
                worldReadError = null;
            });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception)
        {
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (stream.Mode != StreamMode.None || !worldDiscovery.Matches(token, location, generation)) return;
                worldNextRead = DateTime.UtcNow.AddMinutes(2);
                worldReadError = "Nearby watch parties are temporarily unavailable. Retrying shortly.";
            });
        }
        finally
        {
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (ReferenceEquals(worldReadCancellation,cancellation)) worldReadCancellation = null;
                worldReadBusy = false;
            });
        }
    }

    internal void DrawWorldBeacons()
    {
        if (worldSelectedHost is null) worldPanelHost = null;
        if (worldBeacons.Length == 0 || stream.Mode != StreamMode.None || !WorldLocationReady(out var location) || Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent] ||
            Plugin.Condition[ConditionFlag.WatchingCutscene] || Plugin.Condition[ConditionFlag.WatchingCutscene78]) return;
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player is null) return;
        var viewport = ImGui.GetMainViewport();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Span<Vector2> occupied = stackalloc Vector2[12]; var occupiedCount = 0;
        var locationCode = location.Encode();
        if (!ReferenceEquals(worldSortedSource,worldBeacons) || DateTime.UtcNow >= worldNextVisibilitySort)
        {
            worldSortedSource = worldBeacons; worldNextVisibilitySort = DateTime.UtcNow.AddMilliseconds(250);
            worldVisibleBeacons = worldBeacons.Where(b => b.ExpiresAt > now && b.LocationKey == locationCode &&
                !AlphaChatBlocked(b.AccountId))
            .Select(b => (Beacon: b, Distance: Vector3.Distance(player.Position, new Vector3(b.X,b.Y,b.Z))))
            .Where(b => b.Distance <= 50f).OrderBy(b => b.Distance).Take(12).ToArray();
        }
        foreach (var entry in worldVisibleBeacons)
        {
            var b = entry.Beacon;
            if (b.ExpiresAt <= now || b.LocationKey != locationCode || AlphaChatBlocked(b.AccountId)) continue;
            if (!Plugin.GameGui.WorldToScreen(new Vector3(b.X,b.Y,b.Z), out var screen) ||
                screen.X < viewport.Pos.X + Ui(48) || screen.X > viewport.Pos.X + viewport.Size.X - Ui(48) ||
                screen.Y < viewport.Pos.Y + Ui(72) || screen.Y > viewport.Pos.Y + viewport.Size.Y - Ui(72)) continue;
            var overlaps = false;
            for (var i=0;i<occupiedCount;i++) if (Vector2.DistanceSquared(occupied[i],screen) < Ui(80)*Ui(80)) { overlaps = true; break; }
            if (overlaps) continue; occupied[occupiedCount++] = screen;
            var size = UiVec(96,144);
            ImGui.SetNextWindowPos(screen - size * 0.5f, ImGuiCond.Always);
            ImGui.SetNextWindowSize(size, ImGuiCond.Always);
            ImGui.SetNextWindowBgAlpha(0);
            using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoSavedSettings |
                ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing |
                ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoBackground;
            if (UiFonts.Window(ImGui.GetFontSize(), ImGui.Begin("##worldBeacon" + b.AccountId, flags)))
            {
                ImGui.SetCursorPos(UiVec(20,10));
                if (ImGui.InvisibleButton("##beacon",UiVec(56,124))) worldSelectedHost = worldSelectedHost == b.AccountId ? null : b.AccountId;
                var hovered = ImGui.IsItemHovered();
                if (hovered) UiFonts.SetTooltip($"{b.Room.HostDisplayName}'s Watch Party · {entry.Distance:0} yalms");
                DrawWorldCrystal(ImGui.GetWindowDrawList(),screen,hovered || worldSelectedHost == b.AccountId,
                    Math.Clamp((55f-entry.Distance)/20f,0.25f,1f));
                // Measure the popup while a real ImGui window is active. Font
                // pushes/draw-list access outside Begin would expose Debug##Default.
                if (worldSelectedHost == b.AccountId) DrawWorldBeaconPanel(b,screen,viewport.Pos,viewport.Size);
            }
            ImGui.End();
        }
    }

    private void DrawWorldCrystal(ImDrawListPtr draw, Vector2 center, bool hovered, float alpha)
    {
        var time = (float)ImGui.GetTime();
        center.Y += MathF.Sin(time * 1.8f) * Ui(4);
        var pulse = 0.85f + MathF.Sin(time * 2.2f) * 0.15f;
        uint Color(float r, float g, float b, float opacity = 1f) =>
            ImGui.GetColorU32(new Vector4(r,g,b,alpha*opacity));
        for (var i=hovered ? 7 : alpha < .75f ? 2 : 4;i>0;i--)
            draw.AddCircleFilled(center,Ui(8+i*5),Color(0.08f,0.65f,1f,0.022f*pulse),48);
        var top = center-UiVec(0,52);
        var bottom = center+UiVec(0,52);
        var ul = center+UiVec(-21,-14); var ur = center+UiVec(21,-14);
        var ll = center+UiVec(-21,14); var lr = center+UiVec(21,14);
        var coreTop = center-UiVec(0,24); var coreBottom = center+UiVec(0,24);
        Vector2[] edges = [top,ur,lr,bottom,ll,ul,top];
        // A soft blue pass sits behind the sharp crystal, like the TV edge halo.
        // Wider, faint strokes blend into a tighter glow along the silhouette.
        for (var layer=hovered ? 7 : alpha < .75f ? 2 : 4;layer>=1;layer--)
        {
            var glow = Color(0.08f,0.40f,1f,(0.016f+(7-layer)*0.003f)*pulse*(hovered?1.2f:1f));
            for (var edge=1;edge<edges.Length;edge++)
                draw.AddLine(edges[edge-1],edges[edge],glow,Ui(2+layer*3));
        }
        uint FacetColor(float r, float g, float b) => Color(r,g,b,0.72f);
        // Translucent facets retain their shading while letting the world show through.
        draw.AddTriangleFilled(top,ul,coreTop,FacetColor(0.13f,0.55f,0.98f));
        draw.AddTriangleFilled(top,ur,coreTop,FacetColor(0.48f,0.91f,1f));
        draw.AddTriangleFilled(ul,ll,coreTop,FacetColor(0.07f,0.36f,0.76f));
        draw.AddTriangleFilled(ll,coreBottom,coreTop,FacetColor(0.15f,0.72f,0.95f));
        draw.AddTriangleFilled(ur,lr,coreTop,FacetColor(0.12f,0.66f,1f));
        draw.AddTriangleFilled(lr,coreBottom,coreTop,FacetColor(0.38f,0.86f,1f));
        draw.AddTriangleFilled(bottom,ll,coreBottom,FacetColor(0.23f,0.85f,1f));
        draw.AddTriangleFilled(bottom,lr,coreBottom,FacetColor(0.58f,0.95f,1f));
        var outline = Color(0.55f,0.94f,1f,hovered ? 0.85f : 0.68f);
        for (var i=1;i<edges.Length;i++) draw.AddLine(edges[i-1],edges[i],outline,Ui(hovered?1.8f:1.2f));
        foreach (var point in new[] {ul,ur,ll,lr})
        {
            draw.AddLine(point,point.Y<center.Y?coreTop:coreBottom,Color(0.6f,0.96f,1f,0.55f),Ui(1));
        }
        draw.AddLine(top,coreTop,Color(0.8f,1f,1f,0.8f),Ui(1));
        draw.AddLine(coreTop,center-UiVec(0,12),Color(0.85f,1f,1f,0.75f),Ui(1));
        draw.AddLine(center+UiVec(0,12),coreBottom,Color(0.85f,1f,1f,0.75f),Ui(1));
        draw.AddLine(coreBottom,bottom,Color(0.9f,1f,1f,0.85f),Ui(1));
        // Small deterministic sparkles drift within the crystal without any textures.
        for (var i=0;i<13;i++)
        {
            var y = ((i*17+time*5)%76)-38;
            var x = MathF.Sin(i*8.7f)*14*(1-MathF.Abs(y)/52);
            var sparkle = center+UiVec(x,y);
            var brightness = 0.25f+0.55f*MathF.Pow(MathF.Max(0,MathF.Sin(time*2+i*2.1f)),4);
            draw.AddCircleFilled(sparkle,Ui(i%4==0?1.5f:0.85f),Color(0.85f,1f,1f,brightness),8);
        }
        for (var i=0;i<48;i++)
        {
            var a = i*MathF.Tau/48; var next=(i+1)*MathF.Tau/48;
            Vector2 Orbit(float angle) => center+UiVec(MathF.Cos(angle)*37,MathF.Sin(angle)*9+4);
            draw.AddLine(Orbit(a),Orbit(next),Color(0.35f,0.94f,1f,0.09f),Ui(5));
            draw.AddLine(Orbit(a),Orbit(next),Color(0.55f,0.98f,1f,a<MathF.PI?0.9f:0.4f),Ui(1.2f));
        }
        DrawWorldCrystalScreen(draw,center,alpha,hovered);
        // Paired broadcast waves echo the mockup's orbiting accents.
        foreach (var side in new[] {-1f,1f})
            for (var ring=0;ring<2;ring++)
                for (var segment=0;segment<16;segment++)
                {
                    Vector2 Wave(float t) => center+UiVec(side*(29+ring*8+MathF.Cos(t)*6),MathF.Sin(t)*22);
                    var a = -1.05f+segment*2.1f/16;
                    // Slow, shallow fade with the outer wave slightly delayed.
                    var waveOpacity = (0.36f+0.10f*MathF.Sin(time*MathF.Tau/4.5f-ring*0.6f))*(ring==0?1f:0.75f);
                    draw.AddLine(Wave(a),Wave(a+2.1f/16),Color(0.35f,0.93f,1f,waveOpacity),Ui(1.3f));
                }
    }

    private void DrawWorldCrystalScreen(ImDrawListPtr draw, Vector2 center, float alpha, bool hovered)
    {
        uint Color(float r, float g, float b, float opacity) =>
            ImGui.GetColorU32(new Vector4(r,g,b,alpha*opacity));
        // The screen is a recessed octagonal glass facet, with bevels sharing
        // the crystal's palette and seams rather than an opaque UI glyph.
        Vector2[] outer = [UiVec(-13,-12),UiVec(13,-12),UiVec(16,-9),UiVec(16,9),
            UiVec(13,12),UiVec(-13,12),UiVec(-16,9),UiVec(-16,-9)];
        Vector2[] inner = [UiVec(-11,-9),UiVec(11,-9),UiVec(13,-7),UiVec(13,7),
            UiVec(11,9),UiVec(-11,9),UiVec(-13,7),UiVec(-13,-7)];
        for (var i=0;i<outer.Length;i++)
        {
            var next = (i+1)%outer.Length;
            var light = i is 0 or 6 or 7;
            var bevel = light ? Color(0.45f,0.92f,1f,0.50f) : Color(0.08f,0.60f,0.9f,0.36f);
            draw.AddTriangleFilled(center+outer[i],center+outer[next],center+inner[next],bevel);
            draw.AddTriangleFilled(center+outer[i],center+inner[next],center+inner[i],bevel);
            draw.AddTriangleFilled(center,center+inner[i],center+inner[next],Color(0.025f,0.26f,0.5f,0.18f));
            draw.AddLine(center+outer[i],center+outer[next],Color(0.45f,0.94f,1f,hovered?0.80f:0.64f),Ui(0.9f));
            draw.AddLine(center+inner[i],center+inner[next],Color(0.35f,0.86f,1f,0.40f),Ui(0.7f));
        }
        // Connect the bezel directly to the surrounding crystal geometry.
        foreach (var side in new[] {-1f,1f})
        {
            draw.AddLine(center+UiVec(side*21,-14),center+UiVec(side*13,-12),Color(0.55f,0.95f,1f,0.58f),Ui(0.9f));
            draw.AddLine(center+UiVec(side*21,14),center+UiVec(side*13,12),Color(0.4f,0.91f,1f,0.48f),Ui(0.9f));
        }
        // Subtle refraction across the glass keeps the symbol inside the crystal.
        draw.AddLine(center-UiVec(0,9),center+UiVec(0,9),Color(0.5f,0.93f,1f,0.16f),Ui(0.8f));
        draw.AddLine(center+UiVec(-12,-8),center+UiVec(11,8),Color(0.5f,0.93f,1f,0.13f),Ui(0.8f));
        var playTop = center+UiVec(-4,-6);
        var playBottom = center+UiVec(-4,6);
        var playTip = center+UiVec(6,0);
        var playCore = center+UiVec(-1,0);
        draw.AddTriangleFilled(playTop,playTip,playCore,Color(0.55f,0.96f,1f,0.66f));
        draw.AddTriangleFilled(playBottom,playTip,playCore,Color(0.10f,0.65f,0.95f,0.50f));
        draw.AddTriangleFilled(playTop,playBottom,playCore,Color(0.3f,0.86f,1f,0.56f));
        draw.AddTriangle(playTop,playBottom,playTip,Color(0.6f,0.97f,1f,0.72f),Ui(0.85f));
        draw.AddLine(playTop,playCore,Color(0.65f,0.97f,1f,0.5f),Ui(0.7f));
        draw.AddLine(playBottom,playCore,Color(0.4f,0.91f,1f,0.45f),Ui(0.7f));
        draw.AddLine(playCore,playTip,Color(0.4f,0.91f,1f,0.45f),Ui(0.7f));
        // A small base is etched into the lower facet, not a separate stand.
        draw.AddLine(center+UiVec(-5,14),center+UiVec(5,14),Color(0.4f,0.9f,1f,0.38f),Ui(1.5f));
    }

    private void DrawWorldBeaconPanel(WorldBeacon b, Vector2 anchor, Vector2 viewportPos, Vector2 viewportSize)
    {
        var room = b.Room;
        TryReadWatchPartyMetadata(room,out _,out _,out _,out var description);
        if (string.IsNullOrWhiteSpace(description)) description = "Come watch together.";
        var width = MathF.Min(Ui(420),viewportSize.X-Ui(16));
        var contentWidth = MathF.Max(Ui(100),width-Ui(36));
        var tags = DirectoryRoomTags(room).Where(t => t.Icon != FontAwesomeIcon.Eye &&
            t.Icon != FontAwesomeIcon.Server && t.Text != WatchPartyVisibilityText(room.Kind)).ToArray();
        var tagRows = LayoutDirectoryRoomTags(ImGui.GetWindowDrawList(),Vector2.Zero,contentWidth,tags,render:false);
        var descriptionHeight = Math.Clamp(ImGui.CalcTextSize(description,false,contentWidth).Y,ImGui.GetTextLineHeight(),ImGui.GetTextLineHeight()*2);
        var tagsY = Ui(55)+descriptionHeight+Ui(12);
        var mediaY = tagsY+tagRows*Ui(28)+Ui(3);
        var footerY = mediaY+(room.HasMedia ? Ui(43) : Ui(6));
        var size = new Vector2(width,footerY+Ui(56));
        var min = viewportPos+UiVec(8,8);
        var max = Vector2.Max(min,viewportPos+viewportSize-size-UiVec(8,8));
        // Hold the card still while open. Per-frame world projection can jitter
        // with the game's camera/rendering, and should not move click targets.
        if (worldPanelHost != b.AccountId)
        {
            worldPanelHost = b.AccountId;
            worldPanelPosition = anchor+new Vector2(Ui(57),-size.Y*0.45f);
        }
        var pos = Vector2.Clamp(worldPanelPosition,min,max);
        pos = new Vector2(MathF.Round(pos.X),MathF.Round(pos.Y));
        worldPanelPosition = pos;
        ImGui.SetNextWindowPos(pos,ImGuiCond.Always);
        ImGui.SetNextWindowSize(size,ImGuiCond.Always);
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding,UiVec(18,15));
        using var rounding = ImRaii.PushStyle(ImGuiStyleVar.WindowRounding,Ui(12));
        using var border = ImRaii.PushStyle(ImGuiStyleVar.WindowBorderSize,Ui(1));
        using var colors = ImRaii.PushColor(ImGuiCol.WindowBg,new Vector4(0.035f,0.055f,0.10f,0.97f))
            .Push(ImGuiCol.Border,new Vector4(0.1f,0.8f,1f,0.9f));
        if (UiFonts.Window(ImGui.GetFontSize(), ImGui.Begin("##worldBeaconJoin",ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoScrollWithMouse)))
        {
            var draw = ImGui.GetWindowDrawList();
            draw.PushClipRect(pos+UiVec(1,1),pos+size-UiVec(1,1),true);
            draw.AddRectFilledMultiColor(pos+UiVec(1,1),pos+new Vector2(width-Ui(1),Ui(48)),
                ImGui.GetColorU32(new Vector4(0.04f,0.35f,0.55f,0.16f)),
                ImGui.GetColorU32(new Vector4(0.04f,0.2f,0.5f,0.06f)),0,0);
            draw.PopClipRect();
            var title = $"{room.HostDisplayName}'s Watch Party";
            var titleWidth = MathF.Max(Ui(60),width-Ui(142));
            UiFonts.DrawText(draw, ImGui.GetFontSize()*1.24f, pos+UiVec(18,17), ImGui.GetColorU32(Vector4.One), ArcadeFitText(title,titleWidth/1.24f));
            var publicPos = pos+new Vector2(width-Ui(109),Ui(25));
            draw.AddCircleFilled(publicPos,Ui(4),ImGui.GetColorU32(new Vector4(0.3f,0.95f,0.45f,1f)),16);
            draw.AddText(publicPos+UiVec(10,-7),ImGui.GetColorU32(MutedText),"Public");
            ImGui.SetCursorScreenPos(pos+new Vector2(width-Ui(32),Ui(14)));
            if (ImGui.InvisibleButton("##closeBeacon",UiVec(20,22))) worldSelectedHost = null;
            var closeColor = ImGui.GetColorU32(ImGui.IsItemHovered()?Vector4.One:MutedText);
            var closeCenter = pos+new Vector2(width-Ui(22),Ui(25));
            draw.AddLine(closeCenter-UiVec(4,4),closeCenter+UiVec(4,4),closeColor,Ui(1.5f));
            draw.AddLine(closeCenter+UiVec(-4,4),closeCenter+UiVec(4,-4),closeColor,Ui(1.5f));
            var descriptionPos = pos+UiVec(18,55);
            draw.PushClipRect(descriptionPos,descriptionPos+new Vector2(contentWidth,descriptionHeight),true);
            ImGui.SetCursorScreenPos(descriptionPos);
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX()+contentWidth);
            using (ImRaii.PushColor(ImGuiCol.Text,MutedText)) ImGui.TextUnformatted(description);
            ImGui.PopTextWrapPos();
            draw.PopClipRect();
            if (ImGui.IsMouseHoveringRect(descriptionPos,descriptionPos+new Vector2(contentWidth,descriptionHeight))) UiFonts.SetTooltip(description);
            LayoutDirectoryRoomTags(draw,pos+new Vector2(Ui(18),tagsY),contentWidth,tags,render:true);
            if (room.HasMedia)
            {
                var mediaPos = pos+new Vector2(Ui(18),mediaY);
                draw.AddRectFilled(mediaPos,mediaPos+new Vector2(contentWidth,Ui(34)),
                    ImGui.GetColorU32(new Vector4(0.06f,0.14f,0.23f,0.85f)),Ui(7));
                UiFonts.DrawIcon(draw, ImGui.GetFontSize(), mediaPos+UiVec(10,9), ImGui.GetColorU32(new Vector4(0.2f,0.85f,1f,1f)), (room.Paused?FontAwesomeIcon.Pause:FontAwesomeIcon.Play).ToIconString());
                var mediaTitle = string.IsNullOrWhiteSpace(room.MediaTitle) ? room.Paused?"Playback paused":"Playing now" : room.MediaTitle;
                draw.AddText(mediaPos+UiVec(32,9),ImGui.GetColorU32(Vector4.One),ArcadeFitText(mediaTitle,contentWidth-Ui(42)));
                if (ImGui.IsMouseHoveringRect(mediaPos,mediaPos+new Vector2(contentWidth,Ui(34)))) UiFonts.SetTooltip(mediaTitle);
            }
            var footerPos = pos+new Vector2(Ui(18),footerY);
            UiFonts.DrawIcon(draw, ImGui.GetFontSize()*1.12f, footerPos+UiVec(0,11), ImGui.GetColorU32(MutedText), FontAwesomeIcon.UserFriends.ToIconString());
            draw.AddText(footerPos+UiVec(25,12),ImGui.GetColorU32(MutedText),$"{room.ViewerCount+1} watching");
            ImGui.SetCursorScreenPos(pos+new Vector2(width-Ui(242),footerY));
            using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding,Ui(8)).Push(ImGuiStyleVar.ButtonTextAlign,new Vector2(0.5f)))
            using (ImRaii.PushColor(ImGuiCol.Button,new Vector4(0.07f,0.15f,0.24f,1f))
                .Push(ImGuiCol.ButtonHovered,new Vector4(0.10f,0.25f,0.38f,1f))
                .Push(ImGuiCol.ButtonActive,new Vector4(0.04f,0.12f,0.2f,1f))
                .Push(ImGuiCol.Text,Vector4.One))
                if (ImGui.Button("View Room",UiVec(102,38))) ViewWorldBeaconRoom(room);
            ImGui.SetCursorScreenPos(pos+new Vector2(width-Ui(132),footerY));
            using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding,Ui(8)).Push(ImGuiStyleVar.ButtonTextAlign,new Vector2(0.5f)))
            using (ImRaii.PushColor(ImGuiCol.Button,new Vector4(0.06f,0.43f,0.98f,1f))
                .Push(ImGuiCol.ButtonHovered,new Vector4(0.12f,0.58f,1f,1f))
                .Push(ImGuiCol.ButtonActive,new Vector4(0.04f,0.32f,0.8f,1f))
                .Push(ImGuiCol.Text,Vector4.One))
            using (UiDisabled.Scope(worldJoinBusy))
                if (ImGui.Button(worldJoinBusy ? "Checking…" : "Join Room",UiVec(114,38))) _ = JoinWorldBeaconAsync(b);
        }
        ImGui.End();
    }

    private void ViewWorldBeaconRoom(RoomDirectoryDto room)
    {
        if (!AllowInlineProfileNavigation()) return;
        currentPage = HomePage.PartyDirectory;
        ResetPartyDirectoryFilters();
        partyDirectorySearch = room.HostDisplayName;
        partyDirectoryFocusSearchRequested = true;
        worldSelectedHost = null;
        worldPanelHost = null;
        OpenUi();
        RefreshPartyDirectoryIfNeeded();
    }

    private async Task JoinWorldBeaconAsync(WorldBeacon beacon)
    {
        if (stream.Mode != StreamMode.None || CurrentSession is not { } session || !AllowInlineProfileNavigation() || !EnsureWatchPartyJoinAllowed()) return;
        worldJoinBusy = true;
        try
        {
            var matches = await CharacterDirectory.GetBeaconsAsync(session.Token,beacon.LocationKey,youtubeFeedToken,beacon.AccountId).ConfigureAwait(false);
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (Volatile.Read(ref disposeStarted) != 0 || stream.Mode != StreamMode.None || CurrentSession?.Token != session.Token ||
                    !WorldLocationReady(out var location) || location.Encode() != beacon.LocationKey || AlphaChatBlocked(beacon.AccountId)) return;
                var current = matches.FirstOrDefault(b => b.AccountId == beacon.AccountId);
                worldSelectedHost = null;
                if (current is null)
                {
                    worldBeacons = worldBeacons.Where(b => b.AccountId != beacon.AccountId).ToArray();
                    Plugin.ChatGui.Print("[Alpha Channel] This watch-party beacon is no longer available.");
                }
                else if (AllowInlineProfileNavigation()) { OpenUi(); JoinPartyDirectoryRoom(current.Room); }
            });
        }
        catch (Exception)
        {
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (Volatile.Read(ref disposeStarted) == 0)
                    Plugin.ChatGui.Print("[Alpha Channel] Couldn't check this watch party. Please try again.");
            });
        }
        finally { await Plugin.Framework.RunOnFrameworkThread(() => worldJoinBusy = false); }
    }

    private void WithdrawWorldBeaconOnShutdown()
    {
        // Best effort; the lease handles crashes or revoked credentials.
        foreach (var token in new[] {worldPublishedToken, worldPublishBusy ? CurrentSession?.Token : null}
            .Where(t => t is not null).Distinct()) _ = WithdrawAsync(token!);
        async Task WithdrawAsync(string token)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await CharacterDirectory.DeleteBeaconAsync(token,timeout.Token).ConfigureAwait(false); }
            catch (Exception) { /* Lease expiry is the fallback. */ }
        }
    }
}
