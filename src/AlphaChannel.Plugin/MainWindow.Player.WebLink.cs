using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private static readonly (string Name,FontAwesomeIcon Icon)[] WebLinkPopularSources =
    [
        ("YouTube",FontAwesomeIcon.PlayCircle),("Twitch",FontAwesomeIcon.BroadcastTower),
        ("Vimeo",FontAwesomeIcon.Film),("Dailymotion",FontAwesomeIcon.Film),
        ("Reddit",FontAwesomeIcon.Link),("Bilibili",FontAwesomeIcon.Tv),
        ("Internet Archive",FontAwesomeIcon.Archive),("Cartoon Vault",FontAwesomeIcon.Tv),
        ("9GAG",FontAwesomeIcon.Images),("TubiTV",FontAwesomeIcon.PlayCircle),("Kick",FontAwesomeIcon.BroadcastTower)
    ];

    private bool DrawAddMediaBackAction()
    {
        const string label="‹ All media sources";
        var origin=ImGui.GetCursorScreenPos();
        using var font=UiFonts.PushRegularRelative(.94f);
        var clicked=ImGui.InvisibleButton("##addMediaAllSources",new(ImGui.CalcTextSize(label).X+Ui(8),Ui(24)));
        ImGui.GetWindowDrawList().AddText(origin+UiVec(0,3),ImGui.GetColorU32(ImGui.IsItemHovered()?AccentHover:Accent),label);
        if(ImGui.IsItemHovered()) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        SetGameLayoutRowGap(Ui(10));
        if(clicked) showingAddMediaSources=true;
        return clicked;
    }

    private void DrawLinkSource()
    {
        var width=ImGui.GetContentRegionAvail().X;
        var inSolo=stream.Mode==StreamMode.None;
        var buttonCount=inSolo?3:2;
        var columns=width>=Ui(inSolo?580:420)?buttonCount:1;
        var rows=(buttonCount+columns-1)/columns;
        var showFeedback=ImGui.GetTime()<queueAddedFeedbackUntil;
        var height=Ui(228+(rows-1)*48+(showFeedback?24:0));
        DrawPlayerHubPanel("##webLinkForm",width,height,true,()=>
        {
            var origin=ImGui.GetCursorScreenPos();
            var available=ImGui.GetContentRegionAvail().X;
            var artwork=width>=Ui(600);
            var draw=ImGui.GetWindowDrawList();
            using(UiFonts.PushRegularRelative(.78f))
                draw.AddText(origin,ImGui.GetColorU32(AccentHover),"WEB LINK");
            using(UiFonts.PushRelative(1.65f,emphasis:true))
                DrawWrappedLines(draw,origin+UiVec(0,22),available-Ui(artwork?116:0),ImGui.GetTextLineHeight(),2,
                    ImGui.GetColorU32(Vector4.One),"Paste a link. Press play.");
            if(artwork)
            {
                var art=origin+new Vector2(available-Ui(84),Ui(22));
                UiFonts.DrawCenteredIcon(draw,Ui(49),art,ImGui.GetColorU32(AccentInk(.22f)),FontAwesomeIcon.Link.ToIconString());
                draw.AddCircleFilled(art+UiVec(47,16),Ui(16),ImGui.GetColorU32(AccentInk(.17f)),32);
                draw.AddTriangleFilled(art+UiVec(43,9),art+UiVec(43,23),art+UiVec(54,16),ImGui.GetColorU32(AccentInk(.45f)));
            }
            ImGui.SetCursorScreenPos(origin+UiVec(0,74));
            using(var padding=ImRaii.PushStyle(ImGuiStyleVar.FramePadding,
                      new Vector2(Ui(12),MathF.Max(1,(Ui(36)-ImGui.GetFontSize())*.5f)))
                  .Push(ImGuiStyleVar.FrameRounding,Ui(7)).Push(ImGuiStyleVar.FrameBorderSize,Ui(1)))
            using(var colors=ImRaii.PushColor(ImGuiCol.FrameBg,HubSurface(0x0A1526))
                      .Push(ImGuiCol.FrameBgHovered,FrameBgHover).Push(ImGuiCol.FrameBgActive,AccentFieldActive)
                      .Push(ImGuiCol.Border,HubTint(0x345476)))
            {
                ImGui.SetNextItemWidth(MathF.Max(1,available-Ui(54)));
                ImGui.InputTextWithHint("##url","Paste a video or webpage link...",ref urlInput,2000);
                ImGui.SameLine(0,Ui(6));
                if(GameLayoutButton(string.Empty,FontAwesomeIcon.Clipboard,Ui(48),primary:true,height:36))
                {
                    var clipboard=ImGui.GetClipboardText();
                    if(!string.IsNullOrWhiteSpace(clipboard)) urlInput=clipboard.Trim();
                }
                if(ImGui.IsItemHovered()) UiFonts.SetTooltip("Paste from clipboard");
            }
            using(UiFonts.PushRegularRelative(.86f))
                DrawWrappedLines(draw,origin+UiVec(0,122),available,ImGui.GetTextLineHeight(),2,
                    ImGui.GetColorU32(MutedText),"Direct video links and supported webpage URLs.");
            var query=urlInput.Trim();
            var buttonWidth=(available-Ui(10)*(columns-1))/columns;
            using(UiDisabled.Scope(query.Length==0))
            {
                for(var i=0;i<buttonCount;i++)
                {
                    ImGui.SetCursorScreenPos(origin+new Vector2(i%columns*(buttonWidth+Ui(10)),Ui(154+i/columns*48)));
                    var party=inSolo && i==1;
                    var label=i==0?"Play now":party?"Play in Watch Party":"Add to queue";
                    var icon=i==0?FontAwesomeIcon.Play:party?FontAwesomeIcon.Users:FontAwesomeIcon.Plus;
                    if(!GameLayoutButton(label,icon,buttonWidth,primary:i==0,height:38)) continue;
                    var entry=new VideoQueueEntry(query,query,string.Empty,null,null);
                    if(i==0) HandlePlayNow(entry);
                    else if(party) BeginWebLinkWatchParty(entry);
                    else HandleAddToQueue(entry);
                    urlInput=string.Empty;
                }
            }
            if(showFeedback)
            {
                ImGui.SetCursorScreenPos(origin+UiVec(0,204+(rows-1)*48));
                using(UiFonts.PushRegularRelative(.87f)) ImGui.TextColored(Good,"✓ Video added to queue");
            }
        });
        SetGameLayoutRowGap(Ui(16));
        DrawWebLinkPopularSources();
        DrawWebLinkHelp();
    }

    private void DrawWebLinkPopularSources()
    {
        using(UiFonts.PushRelative(1.1f,emphasis:true)) ImGui.TextUnformatted("Popular sources");
        using(UiFonts.PushRegularRelative(.86f))
        using(ImRaii.TextWrapPos(0)) ImGui.TextColored(MutedText,"A few places to start — many other websites work too.");
        SetGameLayoutRowGap(Ui(10));
        var origin=ImGui.GetCursorScreenPos();
        var width=ImGui.GetContentRegionAvail().X;
        var columns=width>=Ui(660)?4:width>=Ui(500)?3:width>=Ui(330)?2:1;
        var gap=Ui(9);var height=Ui(44);var cardWidth=(width-gap*(columns-1))/columns;
        var draw=ImGui.GetWindowDrawList();
        for(var i=0;i<WebLinkPopularSources.Length;i++)
        {
            var source=WebLinkPopularSources[i];
            var position=origin+new Vector2(i%columns*(cardWidth+gap),i/columns*(height+gap));
            ImGui.SetCursorScreenPos(position);
            ImGui.Dummy(new(cardWidth,height));
            if(ImGui.IsItemHovered()) UiFonts.SetTooltip(source.Name+" is an example source; paste its video or webpage link above.");
            DrawArcadeGradientPanel(draw,position,new(cardWidth,height),HubSurface(0x111C30),HubSurface(0x0B1425),HubTint(0x24456B),7);
            var iconOrigin=position+UiVec(9,8);
            draw.AddRectFilled(iconOrigin,iconOrigin+UiVec(28,28),ImGui.GetColorU32(AccentInk(.08f)),Ui(6));
            if(source.Name=="Internet Archive") DrawAddMediaArchiveIcon(draw,iconOrigin+UiVec(4,3));
            else UiFonts.DrawCenteredIcon(draw,Ui(16),iconOrigin+UiVec(14,14),ImGui.GetColorU32(Accent),source.Icon.ToIconString());
            using(UiFonts.PushRegularRelative(.88f))
                draw.AddText(position+new Vector2(Ui(45),(height-ImGui.GetTextLineHeight())*.5f),ImGui.GetColorU32(Vector4.One),
                    TruncateToWidth(source.Name,MathF.Max(1,cardWidth-Ui(53))));
        }
        var rows=(WebLinkPopularSources.Length+columns-1)/columns;
        ImGui.SetCursorScreenPos(origin+new Vector2(0,rows*height+(rows-1)*gap+Ui(16)));
    }

    private void DrawWebLinkHelp()
    {
        const string examples="These are examples, not a complete list of supported websites.";
        const string direct="Direct video links are usually most reliable. Some webpage links can be scanned for an embedded video.";
        const string tryLink="Not sure? Paste the link and try it.";
        var width=ImGui.GetContentRegionAvail().X;
        var textWidth=MathF.Max(1,width-Ui(66));
        float headingHeight,examplesHeight,directHeight,tryHeight;
        using(UiFonts.PushRelative(1.04f,emphasis:true)) headingHeight=ImGui.CalcTextSize("About supported links",false,textWidth).Y;
        using(UiFonts.PushRegularRelative(.86f))
        {
            examplesHeight=ImGui.CalcTextSize(examples,false,textWidth).Y;
            directHeight=ImGui.CalcTextSize(direct,false,textWidth).Y;
            tryHeight=ImGui.CalcTextSize(tryLink,false,textWidth).Y;
        }
        var height=Ui(36+9+6+10)+headingHeight+examplesHeight+directHeight+tryHeight;
        DrawPlayerHubPanel("##webLinkHelp",width,height,false,()=>
        {
            var origin=ImGui.GetCursorScreenPos();
            UiFonts.DrawCenteredIcon(ImGui.GetWindowDrawList(),Ui(18),origin+UiVec(8,9),
                ImGui.GetColorU32(AccentHover),FontAwesomeIcon.InfoCircle.ToIconString());
            ImGui.SetCursorScreenPos(origin+UiVec(30,0));
            using(UiFonts.PushRelative(1.04f,emphasis:true))
            using(ImRaii.TextWrapPos(0)) ImGui.TextUnformatted("About supported links");
            using var body=UiFonts.PushRegularRelative(.86f);
            ImGui.SetCursorScreenPos(origin+new Vector2(Ui(30),headingHeight+Ui(9)));
            using(ImRaii.TextWrapPos(0)) ImGui.TextColored(MutedText,examples);
            ImGui.SetCursorScreenPos(origin+new Vector2(Ui(30),headingHeight+Ui(15)+examplesHeight));
            using(ImRaii.TextWrapPos(0)) ImGui.TextColored(MutedText,direct);
            ImGui.SetCursorScreenPos(origin+new Vector2(Ui(30),headingHeight+Ui(25)+examplesHeight+directHeight));
            using(ImRaii.TextWrapPos(0)) ImGui.TextColored(AccentHover,tryLink);
        });
    }
}
