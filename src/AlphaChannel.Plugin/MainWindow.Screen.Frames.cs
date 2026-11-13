using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private static readonly string[] TvFrameNames = ["None", "Minimal", "Neon", "Cinema", "Retro TV", "Ornate", "Arcade",
        "Catnap", "Little Spectators", "Dragon’s Hoard", "Botanical", "Celestial", "Storybook", "Grand Cinema", "Royal Gallery",
        "Arcade Deluxe", "Retro Deluxe", "Broadcast Booth", "Midnight Screening", "Pumpkin Patch", "Winter Lodge", "Celebration", "Raid Night"];
    private TvFrameSelection PersonalTvFrame => TvFrameAccess.AllowedPersonalFrame(Plugin.Cfg.TvFrame, CurrentVerifiedPatreonTier());
    private TvFrameSelection CurrentTvFrame => TvFrameSelection.Resolve(PersonalTvFrame,
        stream.Mode == StreamMode.Viewing, Plugin.Cfg.UseHostTvFrame, stream.CurrentRoomState?.Reason);
    private bool LocalHalloweenFrameActive
    {
        get
        {
            return HalloweenOwnsTv;
        }
    }
    private bool HalloweenFrameOverrideActive => HalloweenCountdownActive || video.State is not (VideoPlaybackState.Idle or VideoPlaybackState.Failed) &&
        HalloweenTvFrame.ShouldOverride(LocalHalloweenFrameActive,stream.Mode==StreamMode.Viewing,
            stream.CurrentRoomState?.Reason,stream.CurrentRoomState?.Url,video.CurrentMediaUrl);

    private bool frameMembershipWasActive;
    internal void UpdateTvFrame()
    {
        var tier = CurrentVerifiedPatreonTier();
        var accountId = CurrentSession?.AccountId;
        bool resolved;
        lock (patreonStateLock)
            resolved = frameMembershipWasActive || accountId is not null && verifiedPatreonAccountId == accountId;
        if (TvFrameAccess.ShouldReset(Plugin.Cfg.TvFrame, tier, resolved))
        {
            Plugin.Cfg.TvFrame = TvFrameAccess.AllowedPersonalFrame(Plugin.Cfg.TvFrame, tier);
            Plugin.Cfg.Save();
        }
        frameMembershipWasActive = tier >= 1;
        stream.HostFrameSelection = PersonalTvFrame;
        stream.HostHalloweenFrameEnabled = LocalHalloweenFrameActive;
        var frame=CurrentTvFrame;
        if(HalloweenFrameOverrideActive)
            frame=HalloweenTvFrame.At(screenController.Engine.MediaLoadPending?0:video.GetProgress().Position);
        screenController.Engine.FrameSelection = TvFrameAccess.CanRender(video.IsAudioOnly, studioSessionActive)
            ? frame : new TvFrameSelection();
    }

    private void SaveTvFrame(TvFrameSelection frame)
    {
        if (!TvFrameAccess.CanSelect(frame.Style, CurrentVerifiedPatreonTier())) return;
        Plugin.Cfg.TvFrame = frame.Normalized();
        Plugin.Cfg.Save();
        UpdateTvFrame();
    }

    private void DrawTvFramePage(float width)
    {
        var viewer = stream.Mode == StreamMode.Viewing;
        var followsHost = viewer && Plugin.Cfg.UseHostTvFrame;
        GameLayoutPanel("##screenFrameSync", width, Ui(viewer ? 104 : 112), () =>
        {
            ImGui.TextUnformatted(viewer ? "Watch Party frame" : stream.Mode == StreamMode.Hosting ? "Watch Party frame · Host" : "Your TV frame");
            if (viewer)
            {
                var follow = Plugin.Cfg.UseHostTvFrame;
                if (ImGui.Checkbox("Use host’s frame", ref follow))
                { Plugin.Cfg.UseHostTvFrame = follow; Plugin.Cfg.Save(); UpdateTvFrame(); }
                ImGui.TextColored(MutedText, follow ? "Following the host. Your personal frame is kept." : "Your frame is visible only to you.");
            }
            else
            {
                ImGui.TextColored(MutedText, stream.Mode == StreamMode.Hosting ? "Saved for this Watch Party and future sessions." : "Saved for solo playback and your next Watch Party.");
                using (ImRaii.TextWrapPos(0))
                    ImGui.TextColored(MutedText, "Your frame choice syncs with viewers when hosting a watch party.");
            }
        });
        if(HalloweenFrameOverrideActive)
            ImGui.TextWrapped("Halloween colour sync is controlling the neon frame. Your saved frame returns when the show ends.");
        AddScreenCardGap();
        followsHost = viewer && Plugin.Cfg.UseHostTvFrame;
        SynchronizeFrameBrowser(followsHost);
        var sideBySide = width >= Ui(640);
        var gap = Ui(12);
        var previewWidth = sideBySide ? (width - gap) * .55f : width;
        var settingsWidth = sideBySide ? width - gap - previewWidth : width;
        DrawTvFramePreview(previewWidth, Ui(sideBySide ? 600 : 480));
        if (sideBySide) ImGui.SameLine(0, gap);
        else AddScreenCardGap();
        DrawTvFrameSettings(settingsWidth, Ui(600), followsHost);
        DrawTvFrameColourPopup(followsHost);
    }

    private void DrawTvFramePreview(float width, float height)
    {
        GameLayoutPanel("##screenFramePreview", width, height, () =>
        {
            var frame = FramePreviewSelection;
            ImGui.TextUnformatted(video.IsAudioOnly || studioSessionActive ? "Frame preview · hidden during music" : "Frame preview");
            ImGui.Spacing();
            var origin = ImGui.GetCursorScreenPos();
            var previewOnly = frameBrowserPreviewStyle is not null;
            var size = new Vector2(ImGui.GetContentRegionAvail().X,
                MathF.Max(Ui(100), ImGui.GetContentRegionAvail().Y - Ui(previewOnly ? 190 : 150)));
            var draw = ImGui.GetWindowDrawList();
            draw.AddRectFilled(origin, origin + size, ImGui.GetColorU32(new Vector4(.025f, .035f, .055f, 1)), Ui(8));
            var premium = PremiumTvFrameArt.IsPremium(frame.Style);
            var tvWidth = premium
                ? MathF.Min(size.X * .94f / PremiumTvFrameArt.ExtentX, size.Y * .94f / PremiumTvFrameArt.ExtentY * 1.6667f)
                : MathF.Min(size.X * .79f, size.Y * .75f * 1.6667f);
            var tvSize = new Vector2(tvWidth, tvWidth / 1.6667f);
            var tvOrigin = origin + (size - tvSize) * .5f;
            DrawTvFrameThumbnail(draw, tvOrigin, tvSize, frame);
            const string label = "ALPHA CHANNEL";
            using (UiFonts.PushRelative(.76f))
                draw.AddText(tvOrigin + (tvSize - ImGui.CalcTextSize(label)) * .5f, ImGui.GetColorU32(MutedText), label);
            ImGui.Dummy(size);
            ImGui.Spacing();
            DrawTvFramePreviewNavigation(size.X, stream.Mode == StreamMode.Viewing && Plugin.Cfg.UseHostTvFrame);
            using (ImRaii.TextWrapPos(0))
                ImGui.TextColored(MutedText, frame.Style switch
                {
                    TvFrameStyle.Minimal => "A clean, understated edge.",
                    TvFrameStyle.Neon => "A slim illuminated border.",
                    TvFrameStyle.Cinema => "A dark frame with cinematic details.",
                    TvFrameStyle.RetroTv => "A classic TV bezel with a power light.",
                    TvFrameStyle.Ornate => "Warm wood with gold detailing.",
                    TvFrameStyle.Arcade => "Bold corners and colourful trim.",
                    TvFrameStyle.Catnap => "Sleepy cats and little paws resting on the frame.",
                    TvFrameStyle.LittleSpectators => "TV robots and a popcorn companion.",
                    TvFrameStyle.DragonsHoard => "A friendly dragon guarding a golden frame.",
                    TvFrameStyle.Botanical => "Green leaves and cheerful flowers.",
                    TvFrameStyle.Celestial => "Gold outlines with delicate stars.",
                    TvFrameStyle.Storybook => "An open book with ribbon bookmarks.",
                    TvFrameStyle.GrandCinema => "Silver brackets and red cinema curtains.",
                    TvFrameStyle.RoyalGallery => "Layered gold trim and colourful jewels.",
                    TvFrameStyle.ArcadeDeluxe => "Speakers, a marquee and colourful lights.",
                    TvFrameStyle.RetroDeluxe => "Vintage casing, tuning knobs and an aerial.",
                    TvFrameStyle.BroadcastBooth => "A studio surround with an On Air sign.",
                    TvFrameStyle.MidnightScreening => "Friendly bats and candlelit purple trim.",
                    TvFrameStyle.PumpkinPatch => "Pumpkins and a ghost peeking over the edge.",
                    TvFrameStyle.WinterLodge => "Snow caps, festive lights and a snowman.",
                    TvFrameStyle.Celebration => "Balloons, ribbons, stars and confetti.",
                    TvFrameStyle.RaidNight => "Angular plates, crystal corners and a crest.",
                    _ => "Just the picture, without a frame.",
                });
            if (TvFrameAccess.RequiresPatreon(FramePreviewSelection.Style))
            {
                var hostFrame = stream.Mode == StreamMode.Viewing && Plugin.Cfg.UseHostTvFrame;
                var locked = !hostFrame && !TvFrameAccess.CanSelect(FramePreviewSelection.Style, CurrentVerifiedPatreonTier());
                ImGui.Spacing();
                using (UiFonts.PushRelative(.86f))
                {
                    var badge = hostFrame ? "Patreon - Tier 1+ · Host’s frame" : previewOnly ? "Patreon - Tier 1+ · Preview only" : "Patreon - Tier 1+";
                    var textSize = ImGui.CalcTextSize(badge);
                    var badgeSize = new Vector2(textSize.X + Ui(24), Ui(24));
                    var badgeOrigin = ImGui.GetCursorScreenPos() + new Vector2((size.X - badgeSize.X) * .5f, 0);
                    draw.AddRectFilled(badgeOrigin, badgeOrigin + badgeSize, ImGui.GetColorU32(new Vector4(.35f, .16f, .55f, .45f)), Ui(12));
                    draw.AddRect(badgeOrigin, badgeOrigin + badgeSize, ImGui.GetColorU32(new Vector4(.65f, .40f, .90f, .7f)), Ui(12));
                    draw.AddText(badgeOrigin + (badgeSize - textSize) * .5f, ImGui.GetColorU32(new Vector4(.82f, .65f, 1, 1)), badge);
                    ImGui.Dummy(new Vector2(size.X, badgeSize.Y));
                }
                if (previewOnly && GameLayoutButton(locked ? "Unlock with Patreon" : "Use this frame",
                    locked ? FontAwesomeIcon.Heart : FontAwesomeIcon.Check, size.X, primary: true))
                {
                    if (locked) patreonPopupOpen = true;
                    else SelectFrameForBrowse(FramePreviewSelection.Style);
                }
            }
        });
    }

    private void DrawTvFrameSettings(float width, float height, bool followsHost)
    {
        GameLayoutPanel("##screenFrameAppearance", width, height, () =>
        {
            ImGui.TextUnformatted("Frame settings");
            ImGui.Spacing();
            DrawTvFrameBrowser(followsHost);
            var frame = FramePreviewSelection;
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            var editable = !followsHost && frameBrowserPreviewStyle is null && TvFrameAccess.CanSelect(frame.Style, CurrentVerifiedPatreonTier());
            var colourSupported = TvFrameAppearance.SupportsColour(frame.Style);
            ImGui.TextColored(colourSupported && editable ? ImGui.GetStyle().Colors[(int)ImGuiCol.Text] : FrameDisabledText, "Colour");
            DrawTvFrameColourControls(frame, colourSupported && editable);
            using (ImRaii.TextWrapPos(0))
                ImGui.TextColored(MutedText, TvFrameAppearance.ColourHelp(frame.Style));
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            var glowSupported = TvFrameAppearance.SupportsGlow(frame.Style);
            DrawTvFrameGlowControls(frame, glowSupported && editable);
            using (ImRaii.TextWrapPos(0))
                ImGui.TextColored(MutedText, followsHost ? "Following the host’s frame." : glowSupported ? "Set the accent glow strength." : "Not available for this frame.");
        });
    }
    private static Vector3 FrameRgb(uint rgb) => new(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f);

    private readonly IDalamudTextureWrap?[] framePreviewTextures = new IDalamudTextureWrap?[TvFrameNames.Length];
    private readonly TvFrameSelection?[] framePreviewSelections = new TvFrameSelection?[TvFrameNames.Length];
    private readonly bool[] framePreviewLoading = new bool[TvFrameNames.Length];
    private readonly DateTime[] framePreviewNextUpdate = new DateTime[TvFrameNames.Length];

    private async Task LoadDetailedFramePreview(TvFrameSelection frame)
    {
        var index = (int)frame.Style;
        try
        {
            var premium = PremiumTvFrameArt.IsPremium(frame.Style);
            var pixels = premium ? await PremiumTvFrameArt.GetPixels(frame).ConfigureAwait(false)
                : await Task.Run(() => TvFramePreviewRaster.Render(frame)).ConfigureAwait(false);
            var texture = await Plugin.TextureProvider.CreateFromRawAsync(
                RawImageSpecification.Rgba32(premium ? PremiumTvFrameArt.Width : TvFramePreviewRaster.Width,
                    premium ? PremiumTvFrameArt.Height : TvFramePreviewRaster.Height), pixels).ConfigureAwait(false);
            QueueWindowTextureChange(() =>
            {
                if (Volatile.Read(ref disposeStarted) != 0) { texture.Dispose(); return; }
                var previous = framePreviewTextures[index];
                framePreviewTextures[index] = texture;
                framePreviewSelections[index] = frame;
                framePreviewLoading[index] = false;
                previous?.Dispose();
            });
        }
        catch (Exception)
        {
            QueueWindowTextureChange(() =>
            {
                framePreviewLoading[index] = false;
                framePreviewNextUpdate[index] = DateTime.UtcNow.AddSeconds(5);
            });
            AepLog.Warning("[Screen] Could not prepare frame preview.");
        }
    }

    private void DrawTvFrameThumbnail(ImDrawListPtr draw, Vector2 p, Vector2 size, TvFrameSelection frame)
    {
        if (PremiumTvFrameArt.IsPremium(frame.Style) || frame.Style is TvFrameStyle.Cinema or TvFrameStyle.Ornate or TvFrameStyle.Arcade)
        {
            var selection = TvFrameAppearance.TextureKey(frame);
            var index = (int)frame.Style;
            if (framePreviewSelections[index] != selection && !framePreviewLoading[index] && DateTime.UtcNow >= framePreviewNextUpdate[index])
            {
                framePreviewLoading[index] = true;
                framePreviewNextUpdate[index] = DateTime.UtcNow.AddMilliseconds(200);
                _ = LoadDetailedFramePreview(selection);
            }
            if (framePreviewTextures[index] is { } texture)
            {
                var margin = PremiumTvFrameArt.IsPremium(frame.Style)
                    ? new Vector2((PremiumTvFrameArt.ExtentX - 1) * .5f, (PremiumTvFrameArt.ExtentY - 1) * .5f) : new Vector2(.07f, .11f);
                draw.AddImage(texture.Handle, p - size * margin, p + size * (Vector2.One + margin));
            }
            draw.AddRectFilled(p, p + size, ImGui.GetColorU32(new Vector4(.007f, .008f, .015f, 1)), Ui(2));
            return;
        }
        var trim = frame.Style == TvFrameStyle.Minimal ? size.Y * .035f : size.Y * .075f;
        if (frame.Style != TvFrameStyle.None)
        {
            var colour = frame.Style switch
            {
                TvFrameStyle.RetroTv => new Vector4(.35f, .32f, .28f, 1),
                TvFrameStyle.Ornate => new Vector4(.36f, .19f, .07f, 1),
                _ => new Vector4(.14f, .15f, .18f, 1),
            };
            var accent = new Vector4(FrameRgb(frame.Colour), 1);
            if (frame.Style == TvFrameStyle.Neon && frame.Glow != TvFrameGlow.Off)
                for (var i = 3; i >= 1; i--)
                    draw.AddRect(p - new Vector2(trim + i * 2), p + size + new Vector2(trim + i * 2),
                        ImGui.GetColorU32(new Vector4(FrameRgb(frame.Colour), (frame.Glow == TvFrameGlow.Bright ? .16f : .08f) / i)), Ui(4), ImDrawFlags.None, Ui(3));
            draw.AddRectFilled(p - new Vector2(trim), p + size + new Vector2(trim), ImGui.GetColorU32(colour), frame.Style == TvFrameStyle.RetroTv ? trim * 2 : Ui(2));
            draw.AddRect(p - new Vector2(trim * .5f), p + size + new Vector2(trim * .5f),
                ImGui.GetColorU32(frame.Style == TvFrameStyle.Neon ? accent : new Vector4(.4f, .4f, .42f, 1)), Ui(2));
            if (frame.Style == TvFrameStyle.RetroTv)
                draw.AddCircleFilled(p + new Vector2(size.X * .94f, size.Y + trim * .5f), MathF.Max(1, trim * .20f), ImGui.GetColorU32(new Vector4(.2f, 1, .1f, 1)));
        }
        draw.AddRectFilled(p, p + size, ImGui.GetColorU32(new Vector4(.007f, .008f, .015f, 1)), Ui(2));
    }
}
