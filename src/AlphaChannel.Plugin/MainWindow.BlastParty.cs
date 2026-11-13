using System.Text.Json;
using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private static readonly JsonSerializerOptions BlastJson = new() { PropertyNameCaseInsensitive = true };
    private BlastPartyClient? blastClient;
    private BlastState? blastState, blastPrevious;
    private string? blastCanvas, blastAccount, blastToken, blastHost;
    private bool blastHostMode;
    private DateTime blastFrameUtc, blastReceivedUtc;
    private WhiteboardRenderer? blastPageRenderer;
    private readonly byte[] blastPixels = new byte[WhiteboardRenderer.Width * WhiteboardRenderer.Height * 4];
    internal bool BlastPartyActive => blastCanvas is not null;
    internal bool BlastPartyTvControlsInteractive => BlastPartyActive;
    private BlastPlayer? LocalBlastPlayer => blastState?.Players.FirstOrDefault(p => p.Id == CurrentSession?.AccountId);
    private long BlastNow => blastState is null ? 0 : blastState.Now + Math.Min(500, (long)(DateTime.UtcNow - blastReceivedUtc).TotalMilliseconds);
    private string BlastCanvasKeySuffix => blastClient?.Key is { } key ? "&key=" + key : "";

    private void StartBlastParty()
    {
        if (CurrentSession is not { } account || stream.Mode != StreamMode.Hosting || !CanLaunchActivity(ActiveActivityKind.BlastParty) || ActiveActivity != ActiveActivityKind.None) return;
        StartHostWhiteboard(WhiteboardMode.Solo, ActiveActivityKind.BlastParty);
        blastCanvas = whiteboardSessionId; blastAccount = account.AccountId; blastToken = account.Token; blastHost = stream.HostId; blastHostMode = true;
        blastClient = new BlastPartyClient(blastCanvas!, account.Token, null, stream.Roster.Select(p => p.UserId).Take(64).ToArray());
        whiteboardTvDirty = true;
    }
    private void StopBlastParty()
    {
        blastClient?.Send(new { type = "end" });
        if (IsLocalActivityHost && whiteboardSessionId == blastCanvas && blastCanvas is { } canvas)
            SendAndApplyWhiteboardCommand("X|" + canvas);
        ResetBlastPartyLocal();
    }
    private void ResetBlastPartyLocal()
    {
        blastClient?.Dispose(); blastClient = null; blastState = blastPrevious = null;
        blastCanvas = blastAccount = blastToken = blastHost = null;
        RetireArcadePageRenderer(ref blastPageRenderer);
    }
    internal void UpdateBlastParty()
    {
        var url = stream.CurrentRoomState?.Url;
        var watching = stream.Mode == StreamMode.Viewing && ActivityFromCanvasUrl(url) == ActiveActivityKind.BlastParty;
        if (BlastPartyActive && (CurrentSession?.AccountId != blastAccount || CurrentSession?.Token != blastToken ||
            blastHostMode != (stream.Mode == StreamMode.Hosting) || blastHost != stream.HostId ||
            !whiteboardActive || whiteboardSessionId != blastCanvas || !blastHostMode && !watching)) ResetBlastPartyLocal();
        if (!BlastPartyActive && watching && whiteboardActive && CurrentSession is { } account && url is not null)
        {
            var marker = url.IndexOf("&key=", StringComparison.Ordinal);
            var key = marker < 0 ? null : url[(marker + 5)..];
            if (ArcadeRelayClient.Hex(key, 64) && ArcadeRelayClient.Hex(whiteboardSessionId, 32))
            {
                blastCanvas = whiteboardSessionId; blastAccount = account.AccountId; blastToken = account.Token; blastHost = stream.HostId; blastHostMode = false;
                blastClient = new BlastPartyClient(blastCanvas!, account.Token, key, []);
            }
        }
        if (blastClient is null) return;
        if (blastHostMode) blastClient.SetMembers(stream.Roster.Select(p => p.UserId).Take(64).ToArray());
        if (blastClient.TakeSnapshot() is { } raw)
        {
            try
            {
                var state = JsonSerializer.Deserialize<BlastState>(raw, BlastJson);
                if (state is { Width: 19, Height: 11, Players.Length: <= 73, Crates.Length: <= 209, Pickups.Length: <= 209, Bombs.Length: <= 64, Flames.Length: <= 209, Drops.Length: <= 209 } &&
                    state.Players.All(p => p.X is >= 0 and < 19 && p.Y is >= 0 and < 11) &&
                    (blastState is null || state.Revision > blastState.Revision))
                { blastPrevious = blastState; blastState = state; blastReceivedUtc = DateTime.UtcNow; }
            }
            catch (JsonException) { }
        }
        if (DateTime.UtcNow - blastFrameUtc < TimeSpan.FromMilliseconds(50)) return;
        blastFrameUtc = DateTime.UtcNow;
        var page = IsOpen && currentPage == HomePage.BlastParty;
        var tv = ShouldRenderArcadeTv;
        if (!page && !tv) return;
        RenderBlastParty(blastPixels);
        if (page)
        {
            if (blastPageRenderer is null && !screenController.Engine.TryInitializeGraphics()) return;
            blastPageRenderer ??= DxHandler.RunOnRenderThreadAndWait("AlphaChannel.BlastPartyPage.Create",
                () => new WhiteboardRenderer("AlphaChannel.BlastPartyPage"), TimeSpan.FromSeconds(30));
            blastPageRenderer.Upload(blastPixels);
        }
        if (tv) screenController.Engine.UpdateWhiteboard(blastPixels);
        whiteboardTvDirty = false;
    }
    private void DrawBlastPartyPage()
    {
        lastActivitiesPage = HomePage.BlastParty;
        if (!BlastPartyActive)
        {
            ImGui.TextWrapped("Blast Party");
            ImGui.Spacing();
            ImGui.TextWrapped("A two-minute maze battle for 2-8 players. Click a clear tile to move and use the Bomb button to place a bomb. Destroy crates for powerups, refill one bomb every 15 seconds, and score a point for each opponent defeated.");
            ImGui.Spacing();
            ImGui.TextColored(MutedText, "Respawn after five seconds with three seconds of protection. A single Lava Surge pickup appears in the final 40 seconds.");
            ImGui.Spacing();
            using (UiDisabled.Scope(CurrentSession is null || !CanLaunchActivity(ActiveActivityKind.BlastParty) || stream.Mode == StreamMode.Viewing))
            {
                if (ImGui.Button(stream.Mode == StreamMode.Hosting ? "Open Blast Party lobby" : "Create a Watch Party", new Vector2(-1, Ui(42))))
                {
                    if (stream.Mode == StreamMode.Hosting) StartBlastParty();
                    else { pendingWatchPartyMediaKind = PendingWatchPartyMediaKind.BlastParty; OpenPendingWatchPartyCreation(); }
                }
            }
            if (stream.Mode == StreamMode.Viewing) ImGui.TextColored(MutedText, "The host opens the lobby. Everyone can ready up on their TV or here.");
            return;
        }
        var available = ImGui.GetContentRegionAvail();
        var width = MathF.Min(available.X, MathF.Max(Ui(260), (available.Y - Ui(90)) * 16 / 9));
        var size = new Vector2(width, width * 9 / 16);
        var origin = ImGui.GetCursorScreenPos() + new Vector2(MathF.Max(0, (available.X - width) / 2), 0);
        ImGui.SetCursorScreenPos(origin); ImGui.InvisibleButton("##blastParty", size);
        if (ImGui.IsItemHovered())
        {
            var uv = (ImGui.GetIO().MousePos - origin) / size;
            if (IsBlastPartyTvControlAt(uv)) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)) SubmitBlastPartyTvControlAt(uv);
        }
        if (blastPageRenderer?.PreviewHandle is { } handle && handle != nint.Zero)
            ImGui.GetWindowDrawList().AddImage(new ImTextureID(handle), origin, origin + size);
        ImGui.SetCursorScreenPos(origin + new Vector2(0, size.Y + Ui(10)));
        ImGui.TextColored(MutedText, blastClient?.Connected == true ? "Click a tile to move. Bombs block paths. No keyboard input is captured." : blastClient?.Status ?? "Connecting...");
        if (blastHostMode && ImGui.Button("End Blast Party")) StopBlastParty();
    }
    internal bool IsBlastPartyTvControlAt(Vector2 uv)
    {
        if (!BlastPartyActive || blastClient?.Connected != true || blastState is not { Closed: false } s || uv.X is < 0 or > 1 || uv.Y is < 0 or > 1) return false;
        var x = uv.X * 1280; var y = uv.Y * 720;
        if (s.Phase is "lobby" or "results") return x is >= 380 and <= 900 && (y is >= 500 and <= 550 || blastHostMode && s.Players.Count(p => p.Ready && p.Connected) >= 2 && y is >= 565 and <= 615);
        if (s.Phase != "playing" || LocalBlastPlayer is not { Alive: true, Playing: true } me) return false;
        if (x is >= 1030 and <= 1210 && y is >= 638 and <= 695) return me.Ammo > 0;
        var tx = (int)((x - 70) / 60); var ty = (int)((y - 30) / 60);
        return tx is > 0 and < 18 && ty is > 0 and < 10 && !(tx % 2 == 0 && ty % 2 == 0) && !s.Crates.Contains(ty * 19 + tx) && !BlastLavaAt(tx, ty, s, BlastNow);
    }
    internal void SubmitBlastPartyTvControlAt(Vector2 uv)
    {
        if (!IsBlastPartyTvControlAt(uv) || blastState is not { } s) return;
        var x = uv.X * 1280; var y = uv.Y * 720;
        if (s.Phase is "lobby" or "results")
        { if (y <= 550) blastClient?.Send(new { type = "ready", ready = LocalBlastPlayer?.Ready != true }); else blastClient?.Send(new { type = "start" }); }
        else if (y >= 638) blastClient?.Send(new { type = "bomb" });
        else blastClient?.Send(new { type = "move", x = (int)((x - 70) / 60), y = (int)((y - 30) / 60) });
    }
}
