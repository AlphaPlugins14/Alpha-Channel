using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using AlphaChannel.Plugin.Video;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private PixelsClient? pixelsClient;
    private PixelsBoard pixelsBoard = new();
    private PixelsScreenRenderer? pixelsRenderer;
    private string? pixelsCanvas, pixelsAccount, pixelsToken, pixelsError, pixelsHover;
    private int pixelsSelected = -1, pixelsColour = 18;
    private bool pixelsActive, pixelsPending, pixelsRanksOpen;
    private bool pixelsTvRanksOpen;
    private int pixelsTvRanksPage;
    private long pixelsLastSecond = -1, pixelsPendingAt;
    private string? pixelsLastTimer;
    private float pixelsZoom = 1;
    private Vector2 pixelsPan;
    private static readonly uint[] PixelsUiPalette = PixelsBoard.Palette.Select(rgb => 0xFF000000u | ((rgb & 255) << 16) | (rgb & 0xFF00) | (rgb >> 16)).ToArray();
    internal bool HasVisiblePluginWindow => IsOpen && !windowMinimized;
    internal bool PixelsTvControlsInteractive => pixelsActive;
    internal bool IsPixelsTvControlAt(Vector2 uv) => pixelsActive &&
        (pixelsTvRanksOpen&&float.IsFinite(uv.X)&&float.IsFinite(uv.Y)&&uv.X>=0&&uv.X<=1&&uv.Y>=0&&uv.Y<=1||
        (PixelsBoard.SquareAt(uv.X,uv.Y) >= 0 || uv.Y >= 656f/720 && uv.Y < 700f/720 && uv.X >= 200f/1280 && uv.X < 1176f/1280 || uv.X >= 1172f/1280 && uv.Y >= 12f/720 && uv.Y < 50f/720));
    private void OpenPixelsPage() { lastActivitiesPage = currentPage = HomePage.Pixels; }
    private void StartPixels()
    {
        if (!IsLocalActivityHost || CurrentSession is null || ActiveActivity != ActiveActivityKind.None) return;
        StartHostWhiteboard(WhiteboardMode.Solo,ActiveActivityKind.Pixels);
        if (!whiteboardActive) return;
        pixelsActive = true; pixelsCanvas = whiteboardSessionId; OpenPixelsPage(); UpdatePixels();
    }
    private void StartPixelsInParty()
    {
        if (stream.Mode == StreamMode.Hosting) StartPixels();
        else { pendingWatchPartyMediaKind = PendingWatchPartyMediaKind.Pixels; OpenPendingWatchPartyCreation(); }
    }
    private void StopPixels()
    {
        if (IsLocalActivityHost && pixelsCanvas == whiteboardSessionId && whiteboardActive) SendAndApplyWhiteboardCommand("X|" + pixelsCanvas);
        ResetPixelsLocal();
    }
    private void ResetPixelsLocal()
    {
        pixelsTvRanksOpen=false;pixelsTvRanksPage=0;
        pixelsClient?.Dispose(); pixelsClient = null; pixelsActive = false; pixelsCanvas = pixelsAccount = pixelsToken = null;
        pixelsBoard = new(); pixelsPending = false; pixelsRanksOpen = false; pixelsSelected = -1; pixelsHover = pixelsError = null; pixelsLastSecond = -1; pixelsLastTimer = null; whiteboardTvDirty = true;
    }
    internal void UpdatePixels()
    {
        if (stream.Mode == StreamMode.Viewing && whiteboardActive && ActivityFromCanvasUrl(stream.CurrentRoomState?.Url) == ActiveActivityKind.Pixels)
        { pixelsActive = true; pixelsCanvas = whiteboardSessionId; }
        if (!pixelsActive) return;
        if (!whiteboardActive || pixelsCanvas != whiteboardSessionId || CurrentSession is null ||
            stream.Mode == StreamMode.Viewing && ActivityFromCanvasUrl(stream.CurrentRoomState?.Url) != ActiveActivityKind.Pixels)
        { ResetPixelsLocal(); return; }
        if (!screenController.Engine.IsActive && IsMiniPlayerOpen?.Invoke() != true &&
            !(HasVisiblePluginWindow && currentPage == HomePage.Pixels))
        { pixelsClient?.Dispose(); pixelsClient = null; pixelsBoard.Ready = false; pixelsLastSecond = -1; return; }
        var session = CurrentSession;
        if (pixelsClient is null || pixelsAccount != session.AccountId || pixelsToken != session.Token)
        {
            pixelsClient?.Dispose(); pixelsBoard = new(); pixelsPending = false;
            pixelsAccount = session.AccountId; pixelsToken = session.Token; pixelsClient = new(session.Token); whiteboardTvDirty = true;
        }
        while (pixelsClient.Incoming.TryDequeue(out var e))
        {
            try
            {
                var type = e.GetProperty("type").GetString();
                if (type == "connection")
                { pixelsBoard.Ready = false; pixelsPending = false; pixelsError = e.GetProperty("connected").GetBoolean() ? null : "Reconnecting to the global board…"; }
                else if (type == "error") { pixelsError = e.GetProperty("message").GetString(); pixelsPending = false; }
                else
                {
                    var previousRevision = pixelsBoard.Revision;
                    var applied = pixelsBoard.Apply(e);
                    if (!applied) { pixelsBoard.Ready = false; pixelsClient.Reconnect(); }
                    else if (type == "pixel" && pixelsBoard.Ready && pixelsBoard.Revision > previousRevision &&
                        e.GetProperty("owner").GetString() == pixelsAccount)
                        AwardArcadeResult("pixels", $"pixel:{pixelsBoard.Revision}:{e.GetProperty("placedAt").GetInt64()}:{e.GetProperty("index").GetInt32()}",
                            AlphaChannel.Plugin.ActivityPoints.ActivityPointsService.UnitsPerPoint);
                    if (type is "snapshot" or "status") pixelsPending = false;
                    if (type == "snapshot" || type == "pixel" && e.GetProperty("owner").GetString() == pixelsAccount) pixelsError = null;
                }
                whiteboardTvDirty = true;
            }
            catch { pixelsBoard.Ready = false; pixelsClient.Reconnect(); }
        }
        if (pixelsPending && Environment.TickCount64 - pixelsPendingAt > 15000) { pixelsPending = false; pixelsBoard.Ready = false; pixelsClient.Reconnect(); }
        var second = pixelsBoard.Now / 1000;
        if (second != pixelsLastSecond)
        {
            pixelsLastSecond = second;
            var timer = pixelsBoard.Timer;
            if (timer != pixelsLastTimer) { pixelsLastTimer = timer; whiteboardTvDirty = true; }
        }
    }
    private void PlacePixelsSquare()
    {
        if (!pixelsBoard.Ready || pixelsBoard.Blocked || pixelsBoard.RemainingSeconds != 0 || pixelsSelected < 0 || pixelsPending || pixelsClient?.Connected != true) return;
        if (pixelsBoard.Colours[pixelsSelected] == pixelsColour) { pixelsError = "That square already has this colour."; return; }
        pixelsPending = true; pixelsPendingAt = Environment.TickCount64; pixelsError = null; _ = pixelsClient.Place(pixelsSelected,pixelsColour);
    }
    private string PixelsSquareDescription(int i) => i < 0 ? "" : $"({i % 128}, {i / 128}) · " +
        (pixelsBoard.Owners[i] is { } id ? pixelsBoard.Names.GetValueOrDefault(id,"Player") : "Unclaimed") +
        (pixelsBoard.PlacedAt[i] > 0 ? " · " + DateTimeOffset.FromUnixTimeMilliseconds(pixelsBoard.PlacedAt[i]).ToLocalTime().ToString("g") : "");
    internal void SetPixelsTvHover(Vector2? uv)
    {
        var next = !pixelsTvRanksOpen&&uv is { } value ? PixelsSquareDescription(PixelsBoard.SquareAt(value.X,value.Y)) : null;
        if (next == pixelsHover) return; pixelsHover = next; whiteboardTvDirty = true;
    }
    internal void SubmitPixelsTvControlAt(Vector2 uv)
    {
        if (!pixelsActive) return;
        if(pixelsTvRanksOpen)
        {
            var pageCount=Math.Max(1,(pixelsBoard.Rankings().Length+PixelsScreenRenderer.RankingsPageSize-1)/PixelsScreenRenderer.RankingsPageSize);
            pixelsTvRanksPage=Math.Clamp(pixelsTvRanksPage,0,pageCount-1);
            switch(PixelsScreenRenderer.RankingsActionAt(uv))
            {
                case 1:pixelsTvRanksOpen=false;break;
                case 2:pixelsTvRanksPage=Math.Max(0,pixelsTvRanksPage-1);break;
                case 3:pixelsTvRanksPage=Math.Min(pageCount-1,pixelsTvRanksPage+1);break;
            }
            whiteboardTvDirty=true;
            return;
        }
        var i = PixelsBoard.SquareAt(uv.X,uv.Y);
        if (i >= 0) pixelsSelected = i;
        else if (uv.Y >= 656f/720 && uv.Y < 700f/720)
        {
            var x = uv.X * 1280;
            if (x >= 200 && x < 968) pixelsColour = Math.Clamp((int)((x - 200)/32),0,23);
            else if (x >= 1024 && x < 1176) PlacePixelsSquare();
        }
        else if (uv.X >= 1172f/1280 && uv.Y >= 12f/720 && uv.Y < 50f/720)
        {pixelsTvRanksOpen=true;pixelsTvRanksPage=0;pixelsHover=null;}
        whiteboardTvDirty = true;
    }
    private void ApplyPixelsTvOverlay(byte[] frame)
    {
        pixelsRenderer ??= new PixelsScreenRenderer(Path.Combine(Plugin.PluginInterface.AssemblyLocation.DirectoryName!,"Fonts","Inter-SemiBold.ttf"));
        var result = pixelsRenderer.Render(pixelsBoard,pixelsSelected,pixelsColour,pixelsHover,pixelsTvRanksOpen,pixelsTvRanksPage,pixelsAccount);
        Buffer.BlockCopy(result,0,frame,0,frame.Length);
    }
    private void DrawPixelsPage()
    {
        ImGui.TextColored(MutedText,"One canvas for all of Alpha Channel. Paint together, wherever you are.");
        if (!pixelsActive)
        {
            ImGui.Spacing(); DrawArcadeSetupParagraph("Choose a square and colour, then place your pixel. You can replace any colour once every three minutes. Your pixels stay on the board when you leave.");
            ImGui.Spacing();
            DrawActivityStartButtons("Pixels",StartPixels,StartPixelsInParty,CurrentSession is not null && IsLocalActivityHost && CanLaunchActivity(ActiveActivityKind.Pixels));
            if (CurrentSession is null) ImGui.TextColored(MutedText,"Sign in to contribute to Pixels.");
            return;
        }
        ImGui.Spacing(); ImGui.TextColored(pixelsBoard.Blocked ? Danger : pixelsBoard.RemainingSeconds == 0 && pixelsBoard.Ready ? Good : AccentHover,pixelsBoard.Timer);
        ImGui.SameLine(); if (ImGui.Button("Rankings")) pixelsRanksOpen = true;
        ImGui.SameLine(); if (ImGui.Button("Fit board")) { pixelsZoom = 1; pixelsPan = Vector2.Zero; }
        if (IsLocalActivityHost) { ImGui.SameLine(); if (ImGui.Button("Leave Pixels")) StopPixels(); }
        if (pixelsError is not null) ImGui.TextColored(MutedText,pixelsError);
        ImGui.TextColored(MutedText,"Scroll to zoom · drag with the right mouse button to pan · click to select");
        var avail = ImGui.GetContentRegionAvail();
        var height = Math.Max(Ui(120),Math.Min(avail.Y-Ui(110),avail.X*9/16));
        var size = new Vector2(avail.X,height); var origin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##pixelsCanvas",size);
        var hovered = ImGui.IsItemHovered(); var mouse = ImGui.GetIO().MousePos;
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin,origin+size,ImGui.ColorConvertFloat4ToU32(CardBg));
        var unit = Math.Min(size.X/128,size.Y/72)*pixelsZoom; var boardSize = new Vector2(unit*128,unit*72);
        if (hovered && ImGui.GetIO().MouseWheel != 0)
        {
            var old = pixelsZoom; pixelsZoom = Math.Clamp(pixelsZoom * MathF.Pow(1.25f,ImGui.GetIO().MouseWheel),1,16);
            pixelsPan = (pixelsPan + size*.5f - (mouse-origin))*(pixelsZoom/old)-size*.5f+(mouse-origin);
            unit = Math.Min(size.X/128,size.Y/72)*pixelsZoom; boardSize = new(unit*128,unit*72);
        }
        if (hovered && ImGui.IsMouseDragging(ImGuiMouseButton.Right)) pixelsPan += ImGui.GetIO().MouseDelta;
        var limit = Vector2.Max(Vector2.Zero,(boardSize-size)*.5f); pixelsPan = Vector2.Clamp(pixelsPan,-limit,limit);
        var top = origin + (size-boardSize)*.5f + pixelsPan;
        draw.PushClipRect(origin,origin+size,true);
        // Native rectangles keep pixel edges sharp at every zoom level. Only
        // draw visible squares; the TV uses the same colour/ownership model.
        var firstX = Math.Clamp((int)MathF.Floor((origin.X-top.X)/unit),0,127);
        var firstY = Math.Clamp((int)MathF.Floor((origin.Y-top.Y)/unit),0,71);
        var lastX = Math.Clamp((int)MathF.Ceiling((origin.X+size.X-top.X)/unit),0,128);
        var lastY = Math.Clamp((int)MathF.Ceiling((origin.Y+size.Y-top.Y)/unit),0,72);
        for (var y=firstY;y<lastY;y++) for (var x=firstX;x<lastX;x++)
        {
            var colour = pixelsBoard.Colours[y*128+x];
            var p = top+new Vector2(x,y)*unit;
            draw.AddRectFilled(p,p+new Vector2(unit),colour >= 0 ? PixelsUiPalette[colour] : (x+y)%2==0 ? 0xFF311E14u : 0xFF382217u);
        }
        if (unit >= Ui(10))
        {
            for (var x=firstX;x<=lastX;x++) draw.AddLine(top+new Vector2(x*unit,0),top+new Vector2(x*unit,boardSize.Y),0x44311E14);
            for (var y=firstY;y<=lastY;y++) draw.AddLine(top+new Vector2(0,y*unit),top+new Vector2(boardSize.X,y*unit),0x44311E14);
        }
        var local = (mouse-top)/unit; var square = hovered && local.X >= 0 && local.Y >= 0 && local.X < 128 && local.Y < 72 ? (int)local.Y*128+(int)local.X : -1;
        if (pixelsSelected >= 0)
        {
            var p = top + new Vector2(pixelsSelected%128,pixelsSelected/128)*unit;
            draw.AddRect(p,p+new Vector2(unit),0xFFFFFFFF,0,ImDrawFlags.None,2);
        }
        draw.PopClipRect(); draw.AddRect(origin,origin+size,ImGui.ColorConvertFloat4ToU32(Accent),Ui(6));
        if (square >= 0) { UiFonts.SetTooltip(PixelsSquareDescription(square)); if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)) { pixelsSelected = square; whiteboardTvDirty = true; } }
        ImGui.TextColored(MutedText,pixelsSelected >= 0 ? PixelsSquareDescription(pixelsSelected) : "Select a square to paint.");
        for (var c = 0; c < PixelsBoard.Palette.Length; c++)
        {
            if (c > 0 && c%12 != 0) ImGui.SameLine(0,Ui(4));
            var rgb = PixelsBoard.Palette[c]; var color = new Vector4((rgb>>16)/255f,((rgb>>8)&255)/255f,(rgb&255)/255f,1);
            using var border = ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize,c == pixelsColour ? Ui(2) : 0).Push(ImGuiStyleVar.FrameRounding,Ui(4));
            using var ink = ImRaii.PushColor(ImGuiCol.Border,new Vector4(1,1,1,1));
            if (ImGui.ColorButton("##pixelColour"+c,color,ImGuiColorEditFlags.NoTooltip,new Vector2(Ui(24),Ui(24)))) { pixelsColour = c; whiteboardTvDirty = true; }
            if (ImGui.IsItemHovered()) UiFonts.SetTooltip($"#{rgb:X6}"+(c == pixelsColour ? " · Selected" : ""));
        }
        ImGui.SameLine(0,Ui(12));
        using (UiDisabled.Scope(!pixelsBoard.Ready || pixelsBoard.Blocked || pixelsBoard.RemainingSeconds != 0 || pixelsSelected < 0 || pixelsPending))
            if (ImGui.Button(pixelsPending ? "Placing…" : "Place pixel",new Vector2(Ui(120),Ui(30)))) PlacePixelsSquare();
    }
    private void DrawPixelsRankings()
    {
        if(!pixelsActive||currentPage!=HomePage.Pixels){pixelsRanksOpen=false;return;}
        if(!pixelsRanksOpen)return;
        DrawEventWindow("pixelsRankings","Pixels rankings",UiVec(520,480),DrawPixelsRankingsContents,
            ()=>pixelsRanksOpen=false,()=>
            {
                if(AlphaChatButton("closePixelsRankings","Close",FontAwesomeIcon.Times,ImGui.GetContentRegionAvail().X,Ui(34)))pixelsRanksOpen=false;
            });
    }
    private void DrawPixelsRankingsContents()
    {
        ImGui.TextColored(MutedText,"Current ownership · changes live with the board");
        ImGui.Spacing();
        var rows = pixelsBoard.Rankings(); var total = rows.Sum(r => r.Count);
        using(ImRaii.PushColor(ImGuiCol.PlotHistogram,Accent))
        {
            if (rows.Length == 0) ImGui.TextWrapped("The canvas is waiting for its first pixel.");
            for (var i = 0; i < rows.Length; i++)
            {
                var row = rows[i]; ImGui.TextUnformatted((i+1)+"."); ImGui.SameLine(); DrawUsername(row.Id,row.Name,AccentHover);
                if (row.Id == CurrentSession?.AccountId) { ImGui.SameLine(); ImGui.TextColored(Good,"(you)"); }
                ImGui.TextColored(MutedText,$"{row.Count:N0} squares · {100d*row.Count/(128*72):0.0}% of canvas");
                ImGui.ProgressBar(row.Count/(float)Math.Max(1,total),new Vector2(-1,Ui(5)),""); ImGui.Spacing();
            }
        }
    }
}
