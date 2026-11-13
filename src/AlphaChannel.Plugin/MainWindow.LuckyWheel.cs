using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private LuckyWheelDesign luckyWheelDraft = new();
    private LuckyWheelSnapshot? luckyWheelState;
    private WhiteboardRenderer? luckyWheelPageRenderer;
    private LuckyWheelScreenRenderer? luckyWheelScreen;
    private byte[]? luckyWheelFrame;
    private byte[]? luckyWheelPageUploadedFrame;
    private DateTime luckyWheelLastFrameUtc = DateTime.MinValue;
    private DateTime luckyWheelLastSnapshotUtc = DateTime.MinValue;
    private bool luckyWheelEditorOpen;
    private readonly RouletteRollSoundPlayer luckyWheelRoll = new("lucky-wheel-spin.wav");
    private readonly RouletteChipSoundPlayer luckyWheelFinish = new("roulette-chips.wav");
    private int luckyWheelSoundFinished = -1;
    internal bool LuckyWheelActive => luckyWheelState is not null;
    internal bool LuckyWheelTvControlsInteractive => luckyWheelState is { } state && LuckyWheelRules.CanPlayerSpin(state, CurrentSession?.AccountId ?? "", stream.HostId ?? CurrentSession?.AccountId ?? "");
    internal bool IsLuckyWheelTvControlAt(Vector2 uv) => LuckyWheelTvControlsInteractive &&
        LuckyWheelCanSpin && luckyWheelState is { Spinning: false, RemainingIds.Length: > 0 } &&
        LuckyWheelScreenRenderer.IsSpinButtonAt(uv.X, uv.Y);
    internal void SubmitLuckyWheelTvControlAt(Vector2 uv)
    {
        if (IsLuckyWheelTvControlAt(uv)) SpinLuckyWheel();
    }

    private void OpenLuckyWheelPage()
    {
        if (!CanLaunchActivity(ActiveActivityKind.LuckyWheel)) return;
        lastActivitiesPage = currentPage = HomePage.LuckyWheel;
    }
    private void StartLuckyWheel()
    {
        if (!IsLocalActivityHost || CurrentSession is null || ActiveActivity != ActiveActivityKind.None ||
            !LuckyWheelRules.ValidDesign(luckyWheelDraft)) return;
        StartHostWhiteboard(WhiteboardMode.Solo, ActiveActivityKind.LuckyWheel);
        if (whiteboardSessionId is null) return;
        var ids = luckyWheelDraft.Segments.Select(s => s.Id).ToArray();
        luckyWheelState = new(Guid.NewGuid().ToString("N"), whiteboardSessionId, 1, luckyWheelDraft.Copy(),
            ids, ids.ToArray(), false, 0, null, 0, 0, 0, LuckyWheelRules.SpinDurationMs, []);
        RefreshLuckyWheelPlayers();
        luckyWheelEditorOpen = false;
        InvalidateLuckyWheelFrame();
        SendLuckyWheelState();
    }
    private void StopLuckyWheel()
    {
        var state = luckyWheelState;
        if (IsLocalActivityHost && state is not null)
        {
            _ = stream.SendLuckyWheelCommandAsync("X|" + state.SessionId);
            if (whiteboardSessionId == state.CanvasId) SendAndApplyWhiteboardCommand("X|" + state.CanvasId);
        }
        ResetLuckyWheelLocal();
    }
    private void ResetLuckyWheelLocal()
    {
        DisposeLuckyWheelRelay();
        luckyWheelState = null;
        luckyWheelFrame = null;
        luckyWheelEditorOpen = false;
        luckyWheelSoundFinished = -1;
        ResetLuckyWheelPreview();
        luckyWheelRoll.Dispose(); luckyWheelFinish.Dispose();
        whiteboardTvDirty = true;
    }
    private void InvalidateLuckyWheelFrame()
    {
        luckyWheelFrame = null;
        luckyWheelLastFrameUtc = DateTime.MinValue;
        whiteboardTvDirty = true;
    }
    private void SpinLuckyWheel()
    {
        if (luckyWheelState is not { } state || !LuckyWheelTvControlsInteractive || !LuckyWheelCanSpin) return;
        var command = $"P|{state.SessionId}|{state.TurnNumber}";
        if (IsLocalActivityHost) AcceptLuckyWheelSpin(CurrentSession?.AccountId ?? "", command);
        else luckyWheelRelay?.SubmitSpin(command);
    }
    private bool AcceptLuckyWheelSpin(string player, string command)
    {
        if (!IsLocalActivityHost || !LuckyWheelCanSpin || luckyWheelState is not { } state ||
            !LuckyWheelRules.AcceptsSpinAction(state, player, CurrentSession?.AccountId ?? "", command)) return false;
        luckyWheelState = LuckyWheelRules.BeginSpin(state, RandomNumberGenerator.GetInt32(state.RemainingIds.Length),
            RandomNumberGenerator.GetInt32(5, 9), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) with { SpinnerId = player };
        InvalidateLuckyWheelFrame(); SendLuckyWheelState();
        return true;
    }
    private void RefreshLuckyWheelPlayers()
    {
        if (!IsLocalActivityHost || luckyWheelState is not { } state) return;
        var hostName = LocalQuizDisplayName();
        var host = new LuckyWheelPlayer(CurrentSession?.AccountId ?? "", hostName[..Math.Min(96, hostName.Length)]);
        var roster = new[] { host }.Concat(stream.Roster.Select(p => new LuckyWheelPlayer(p.UserId,
            string.IsNullOrWhiteSpace(p.DisplayName) ? "Player" : p.DisplayName[..Math.Min(96, p.DisplayName.Length)])));
        var next = LuckyWheelRules.ReconcilePlayers(state, roster);
        if (ReferenceEquals(state, next)) return;
        luckyWheelState = next;
        InvalidateLuckyWheelFrame();
        SendLuckyWheelState();
    }
    private void SkipLuckyWheelTurn()
    {
        if (!IsLocalActivityHost || luckyWheelState is not { Spinning: false, Design.EveryoneTakesTurn: true } state ||
            state.Players.Length < 2 || state.RemainingIds.Length == 0) return;
        luckyWheelState = LuckyWheelRules.AdvanceTurn(state);
        InvalidateLuckyWheelFrame(); SendLuckyWheelState();
    }
    private void ResetLuckyWheelResults()
    {
        if (!IsLocalActivityHost || luckyWheelState is not { Spinning: false } state) return;
        luckyWheelState = LuckyWheelRules.ResetResults(state);
        luckyWheelSoundFinished = -1;
        InvalidateLuckyWheelFrame(); SendLuckyWheelState();
    }
    private void ApplyLuckyWheelDesign()
    {
        if (!IsLocalActivityHost || luckyWheelState is not { Spinning: false } state || !LuckyWheelRules.ValidDesign(luckyWheelDraft)) return;
        luckyWheelState = state with { Design = luckyWheelDraft.Copy(), Revision = state.Revision + 1 };
        ResetLuckyWheelResults();
        luckyWheelEditorOpen = false;
    }
    internal void UpdateLuckyWheel()
    {
        if (stream.Mode == StreamMode.None) stream.IncomingLuckyWheelCommands.Clear();
        while (stream.IncomingLuckyWheelCommands.TryDequeue(out var incoming))
            if (stream.Mode == StreamMode.Viewing)
            {
                if (incoming.Command.StartsWith("R|", StringComparison.Ordinal)) ReceiveLuckyWheelRelayInvitation(incoming.Command);
                else
                {
                    // A legacy snapshot means the host is using the original route.
                    if (incoming.Command.StartsWith("S|", StringComparison.Ordinal)) DisposeLuckyWheelRelay();
                    ApplyLuckyWheelCommand(incoming.Command);
                }
            }
        RefreshLuckyWheelPlayers();
        UpdateLuckyWheelRelay();
        if (luckyWheelState is not { } state) return;
        if (!whiteboardActive || whiteboardSessionId != state.CanvasId)
        {
            if (IsLocalActivityHost) StopLuckyWheel(); else ResetLuckyWheelLocal();
            return;
        }
        var now = DateTime.UtcNow;
        var unix = new DateTimeOffset(now).ToUnixTimeMilliseconds();
        if (IsLocalActivityHost && state.Spinning && unix >= state.StartedUnixMs + state.DurationMs)
        {
            luckyWheelState = LuckyWheelRules.CompleteSpin(state, unix);
            RefreshLuckyWheelPlayers();
            state = luckyWheelState!;
            InvalidateLuckyWheelFrame(); SendLuckyWheelState();
        }
        if (IsLocalActivityHost && now - luckyWheelLastSnapshotUtc >= TimeSpan.FromSeconds(3)) SendLuckyWheelState();
        UpdateLuckyWheelSound(state, unix);
        var animate = state.Spinning || state.Design.Confetti && state.WinnerId is not null && unix - state.StartedUnixMs - state.DurationMs < LuckyWheelScreenRenderer.ConfettiDurationMs;
        if (now - luckyWheelLastFrameUtc < TimeSpan.FromMilliseconds(50)) return;
        if (luckyWheelFrame is null || animate)
        {
            luckyWheelLastFrameUtc = now;
            luckyWheelFrame = BuildLuckyWheelFrame(state, unix);
            whiteboardTvDirty = true;
            if (IsOpen && currentPage == HomePage.LuckyWheel && !luckyWheelEditorOpen) UploadLuckyWheelPageFrame(luckyWheelFrame);
        }
    }
    private void UpdateLuckyWheelSound(LuckyWheelSnapshot state, long unix, bool preview = false)
    {
        ref var soundFinished = ref (preview ? ref luckyWheelPreviewSoundFinished : ref luckyWheelSoundFinished);
        var roll = preview ? luckyWheelPreviewRoll : luckyWheelRoll;
        var finish = preview ? luckyWheelPreviewFinish : luckyWheelFinish;
        if (!state.Design.Sound)
        { roll.Dispose(); finish.Dispose(); return; }
        var age = unix - state.StartedUnixMs;
        var volume = Plugin.Cfg.Muted ? 0 : Plugin.Cfg.Volume;
        var spinning = state.Spin > 0 && age >= 0 && age < state.DurationMs;
        // The wheel's quartic easing has a cubic speed curve. Follow that
        // slowdown, reaching silence at 65% of the spin (the slow final coast).
        var progress = Math.Clamp(age / (double)Math.Max(1, state.DurationMs), 0, 1);
        var speed = Math.Pow(1 - progress, 3);
        var quietSpeed = Math.Pow(1 - .65, 3);
        var gain = Math.Clamp((speed - quietSpeed) / (1 - quietSpeed), 0, 1);
        var spinVolume = (int)Math.Round(volume * gain);
        roll.SetPlaying(spinning && spinVolume > 0, spinVolume);
        if (state.Spin > 0 && age >= state.DurationMs && soundFinished != state.Spin)
        {
            soundFinished = state.Spin;
            // A late join receives the result without replaying an old finish sound.
            if (age < state.DurationMs + 500 && volume > 0) finish.Play(volume);
        }
    }
    private byte[] BuildLuckyWheelFrame(LuckyWheelSnapshot state, long unix, bool preview = false)
    {
        luckyWheelScreen ??= new LuckyWheelScreenRenderer(Path.Combine(
            Plugin.PluginInterface.AssemblyLocation.DirectoryName ?? string.Empty, "Fonts", "Inter-SemiBold.ttf"),
            Path.Combine(Plugin.PluginInterface.DalamudAssetDirectory.FullName, "UIRes", "FontAwesomeFreeSolid.otf"));
        var spinning = state.Spinning && unix < state.StartedUnixMs + state.DurationMs;
        var angle = LuckyWheelRules.AngleAt(state.StartAngle, state.EndAngle, state.StartedUnixMs, state.DurationMs, unix);
        var result = !spinning && state.WinnerId is not null ? state.Design.Segments.First(s => s.Id == state.WinnerId).Text : null;
        var segments = state.WheelIds.Select(id => state.Design.Segments.First(s => s.Id == id)).ToArray();
        return luckyWheelScreen.Render(state.Design, segments, angle, result, state.Spin, spinning,
            unix - state.StartedUnixMs - state.DurationMs, state.RemainingIds.Length > 0, state.History.Skip(spinning ? 0 : 1).FirstOrDefault(), preview ? !spinning : LuckyWheelTvControlsInteractive,
            !preview && state.Design.EveryoneTakesTurn ? state.Players.FirstOrDefault()?.Name : null);
    }
    private void ApplyLuckyWheelTvOverlay(byte[] pixels, int width, int height)
    {
        if (luckyWheelState is not { } state) return;
        luckyWheelFrame ??= BuildLuckyWheelFrame(state, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Buffer.BlockCopy(luckyWheelFrame, 0, pixels, 0, Math.Min(pixels.Length, luckyWheelFrame.Length));
    }
    private void UploadLuckyWheelPageFrame(byte[] frame)
    {
        if (ReferenceEquals(frame, luckyWheelPageUploadedFrame)) return;
        if (luckyWheelPageRenderer is null && !screenController.Engine.TryInitializeGraphics()) return;
        luckyWheelPageRenderer ??= DxHandler.RunOnRenderThreadAndWait("AlphaChannel.LuckyWheelPage.Create",
            () => new WhiteboardRenderer("AlphaChannel.LuckyWheelPage"), TimeSpan.FromSeconds(30));
        luckyWheelPageRenderer.Upload(frame);
        luckyWheelPageUploadedFrame = frame;
    }
    private void SendLuckyWheelState()
    {
        if (!IsLocalActivityHost || luckyWheelState is null) return;
        UpdateAdditionalArcadePoints();
        luckyWheelLastSnapshotUtc = DateTime.UtcNow;
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(luckyWheelState)));
        if (TrySendLuckyWheelThroughCloudflare("S|" + encoded)) return;
        _ = stream.SendArcadeSnapshotAsync(StreamClient.LuckyWheelCommandPrefix, "S|" + encoded,
            $"{luckyWheelState.SessionId}|{luckyWheelState.Spin}|{luckyWheelState.Spinning}");
    }
    private void ApplyLuckyWheelCommand(string command)
    {
        if (command.StartsWith("X|", StringComparison.Ordinal))
        { if (luckyWheelState?.SessionId == command[2..]) ResetLuckyWheelLocal(); return; }
        if (!command.StartsWith("S|", StringComparison.Ordinal) || command.Length > 48000 ||
            ActivityFromCanvasUrl(stream.CurrentRoomState?.Url) != ActiveActivityKind.LuckyWheel) return;
        try
        {
            var next = JsonSerializer.Deserialize<LuckyWheelSnapshot>(Convert.FromBase64String(command[2..]));
            if (!LuckyWheelRules.ValidSnapshot(next) || next is null || next.CanvasId != whiteboardSessionId ||
                luckyWheelState is { } old && old.SessionId == next.SessionId && next.Revision <= old.Revision) return;
            luckyWheelState = next;
            UpdateAdditionalArcadePoints();
            InvalidateLuckyWheelFrame();
        }
        catch (Exception) { /* Ignore malformed activity payloads without disrupting playback. */ }
    }
    private void DrawLuckyWheelPage()
    {
        lastActivitiesPage = HomePage.LuckyWheel;
        if (!LuckyWheelActive || luckyWheelEditorOpen)
        { DrawLuckyWheelEditor(); return; }
        var state = luckyWheelState!;
        var width = Math.Min(ImGui.GetContentRegionAvail().X, Ui(1060));
        if (luckyWheelFrame is not null) UploadLuckyWheelPageFrame(luckyWheelFrame);
        if (luckyWheelPageRenderer?.PreviewHandle is { } handle && handle != nint.Zero)
        {
            var size = new Vector2(width, width * 9 / 16);
            ImGui.Image(new ImTextureID(handle), size);
            var uv = (ImGui.GetIO().MousePos - ImGui.GetItemRectMin()) / size;
            if (ImGui.IsItemHovered() && IsLuckyWheelTvControlAt(uv))
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)) SubmitLuckyWheelTvControlAt(uv);
            }
        }
        if (state.Design.EveryoneTakesTurn && state.Players.FirstOrDefault() is { } currentSpinner)
            DrawUsername($"{currentSpinner.Name}'s turn",Accent);
        if (IsLocalActivityHost)
        {
            if (state.Design.EveryoneTakesTurn && state.Players.Length > 1 && state.RemainingIds.Length > 0)
            {
                using (UiDisabled.Scope(state.Spinning))
                    if (ImGui.Button("Skip player", UiVec(150, 42))) SkipLuckyWheelTurn();
                ImGui.SameLine();
            }
            using (UiDisabled.Scope(state.Spinning))
            {
                if (ImGui.Button("Reset results", UiVec(150, 42))) ResetLuckyWheelResults();
                ImGui.SameLine();
                if (ImGui.Button("Edit wheel", UiVec(150, 42)))
                { luckyWheelDraft = state.Design.Copy(); luckyWheelEditorOpen = true; BeginLuckyWheelEditor(); }
            }
            ImGui.SameLine();
            if (ImGui.Button("End activity", UiVec(150, 42))) StopLuckyWheel();
            if (state.RemainingIds.Length == 0)
                ImGui.TextColored(MutedText, "All picked! Reset results for another round.");
            else if (state.Design.RemoveWinner)
                ImGui.TextColored(MutedText, $"{state.RemainingIds.Length} choices left");
        }
        else ImGui.TextColored(MutedText, state.Spinning ? "Where will it land?" : "Waiting for a spin…");
        if (state.History.Length > 0)
        {
            ImGui.Separator(); ImGui.TextUnformatted("Recent results");
            if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginChild("##wheelRecentResults", new Vector2(-1, Ui(110)), false)))
                foreach (var result in state.History) ImGui.TextUnformatted($"#{result.Spin}  {result.Text}");
            ImGui.EndChild();
        }
    }
}
