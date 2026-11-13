using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private readonly record struct AddMediaShortcut(string Id,string Title,string Description,
        FontAwesomeIcon Icon,GameSystem? System=null);

    private static readonly AddMediaShortcut[] AddMediaWatchSources =
    [
        new("webLink","Web Link","Paste a link",FontAwesomeIcon.Link),
        new("youtube","YouTube","Find videos",FontAwesomeIcon.PlayCircle),
        new("internetArchive","Internet Archive","Browse archive",FontAwesomeIcon.Archive),
        new("twitch","Twitch","Watch live",FontAwesomeIcon.Tv),
        new("dailymotion","Dailymotion","Find videos",FontAwesomeIcon.Film),
        new("browser","Browser","Browse the web",FontAwesomeIcon.Globe)
    ];
    private static readonly AddMediaShortcut[] AddMediaPersonalSources =
    [
        new("localVideo","Local Video","Play a file",FontAwesomeIcon.Video),
        new("images","Images / Slideshows","Show images",FontAwesomeIcon.Images),
        new("musicDj","Music","Play live audio",FontAwesomeIcon.Music),
        new("streamLive","Stream Live","Stream your PC",FontAwesomeIcon.BroadcastTower)
    ];
    private static readonly AddMediaShortcut[] AddMediaRetroSources =
    [
        new("snes","Super Nintendo","Play games",FontAwesomeIcon.Gamepad,GameSystem.Snes),
        new("gameBoy","Game Boy / Color","Play games",FontAwesomeIcon.Gamepad,GameSystem.GameBoy),
        new("nes","NES","Play games",FontAwesomeIcon.Gamepad,GameSystem.Nes),
        new("gameBoyAdvance","Game Boy Advance","Play games",FontAwesomeIcon.Gamepad,GameSystem.GameBoyAdvance),
        new("masterSystem","Master System / SG-1000","Play games",FontAwesomeIcon.Gamepad,GameSystem.MasterSystem),
        new("gameGear","Game Gear","Play games",FontAwesomeIcon.Gamepad,GameSystem.GameGear)
    ];

    private void DrawAddMediaSourceLanding()
    {
        DrawPlayerHubPanel("##addMediaSearchPanel",ImGui.GetContentRegionAvail().X,Ui(94),false,()=>
        {
            var origin=ImGui.GetCursorScreenPos();
            using(UiFonts.PushRelative(1.12f,emphasis:true)) ImGui.TextUnformatted("What would you like to watch?");
            ImGui.SetCursorScreenPos(origin+UiVec(0,24));
            DrawAddMediaVideoSearch();
        });
        SetGameLayoutRowGap(Ui(12));
        DrawAddMediaSectionHeading("Watch & browse");
        DrawAddMediaSourceGrid(AddMediaWatchSources);
        DrawAddMediaSectionHeading("Your media & broadcasts");
        DrawAddMediaSourceGrid(AddMediaPersonalSources);
        DrawAddMediaSectionHeading("Retro Games",openRetro:true);
        DrawAddMediaSourceGrid(AddMediaRetroSources,retro:true);
    }

    private void DrawAddMediaVideoSearch()
    {
        var width=ImGui.GetContentRegionAvail().X;
        var buttonWidth=MathF.Min(Ui(108),width*.3f);
        var gap=Ui(10);
        using var padding=ImRaii.PushStyle(ImGuiStyleVar.FramePadding,
            new Vector2(Ui(10),MathF.Max(1,(Ui(36)-ImGui.GetFontSize())*.5f)))
            .Push(ImGuiStyleVar.FrameRounding,Ui(7)).Push(ImGuiStyleVar.FrameBorderSize,Ui(1));
        using var colors=ImRaii.PushColor(ImGuiCol.FrameBg,HubSurface(0x0B1526))
            .Push(ImGuiCol.FrameBgHovered,FrameBgHover).Push(ImGuiCol.FrameBgActive,AccentFieldActive)
            .Push(ImGuiCol.Border,HubTint(0x24456B));
        ImGui.SetNextItemWidth(MathF.Max(1,width-buttonWidth-gap));
        var submitted=ImGui.InputTextWithHint("##addMediaVideoSearch","Search videos or paste a link...",
            ref addMediaVideoSearchQuery,2048,ImGuiInputTextFlags.EnterReturnsTrue);
        var query=addMediaVideoSearchQuery.Trim();
        var isUrl=IsHomeSearchUrl(query);
        ImGui.SameLine(0,gap);
        using(UiDisabled.Scope(query.Length==0))
            submitted|=GameLayoutButton(isUrl?"Play":"Search",isUrl?FontAwesomeIcon.Play:FontAwesomeIcon.Search,
                buttonWidth,true,height:36);
        if(!submitted || query.Length==0) return;
        addMediaVideoSearchQuery=string.Empty;
        if(isUrl) HandlePlayNow(new VideoQueueEntry(query,query,string.Empty,null,null));
        else OpenUnifiedVideoSearch(query);
    }

    private void DrawAddMediaSectionHeading(string title,bool openRetro=false)
    {
        var origin=ImGui.GetCursorScreenPos();
        var width=ImGui.GetContentRegionAvail().X;
        using(UiFonts.PushRelative(1.08f,emphasis:true)) ImGui.TextUnformatted(title);
        if(openRetro)
        {
            using var font=UiFonts.PushRegularRelative(.88f);
            const string label="Open Retro Games →";
            var textSize=ImGui.CalcTextSize(label);
            var linkWidth=textSize.X+Ui(6);
            ImGui.SetCursorScreenPos(origin+new Vector2(MathF.Max(0,width-linkWidth),0));
            if(ImGui.InvisibleButton("##addMediaOpenRetro",new(linkWidth,Ui(22)))) currentPage=HomePage.PlaySnes;
            ImGui.GetWindowDrawList().AddText(origin+new Vector2(width-linkWidth,Ui(2)),
                ImGui.GetColorU32(ImGui.IsItemHovered()?AccentHover:Accent),label);
        }
        ImGui.SetCursorScreenPos(origin+UiVec(0,28));
    }

    private void DrawAddMediaSourceGrid(AddMediaShortcut[] sources,bool retro=false)
    {
        var origin=ImGui.GetCursorScreenPos();
        var width=ImGui.GetContentRegionAvail().X;
        var columns=retro?(width>=Ui(460)?2:1):width>=Ui(640)?3:width>=Ui(420)?2:1;
        var gap=Ui(10);
        var height=Ui(retro?50:60);
        var cardWidth=(width-gap*(columns-1))/columns;
        for(var i=0;i<sources.Length;i++)
        {
            ImGui.SetCursorScreenPos(origin+new Vector2(i%columns*(cardWidth+gap),i/columns*(height+gap)));
            DrawAddMediaSourceCard(sources[i],new(cardWidth,height));
        }
        var rows=(sources.Length+columns-1)/columns;
        ImGui.SetCursorScreenPos(origin+new Vector2(0,rows*height+(rows-1)*gap+Ui(14)));
    }

    private void DrawAddMediaSourceCard(AddMediaShortcut source,Vector2 size)
    {
        var origin=ImGui.GetCursorScreenPos();
        var clicked=ImGui.InvisibleButton($"##addMediaSource_{source.Id}",size);
        var hovered=ImGui.IsItemHovered();
        var draw=ImGui.GetWindowDrawList();
        DrawArcadeGradientPanel(draw,origin,size,hovered?HubSelected(0x192D4A):HubSurface(0x101B2F),
            HubSurface(0x0A1222),hovered?Accent:HubTint(0x24456B),8);
        draw.PushClipRect(origin+UiVec(1,1),origin+size-UiVec(1,1),true);
        try
        {
            var isRetro=source.System is not null;
            var textX=Ui(isRetro?65:57);
            if(source.System is {} system)
                DrawRetroHubConsole(draw,origin+new Vector2(Ui(12),(size.Y-Ui(48*.58f))*.5f),Ui(.58f),system);
            else
            {
                var iconOrigin=origin+new Vector2(Ui(11),(size.Y-Ui(34))*.5f);
                draw.AddRectFilled(iconOrigin,iconOrigin+UiVec(34,34),ImGui.GetColorU32(AccentInk(hovered?.16f:.09f)),Ui(7));
                if(source.Id=="internetArchive") DrawAddMediaArchiveIcon(draw,iconOrigin+UiVec(7,6));
                else UiFonts.DrawCenteredIcon(draw,Ui(18),iconOrigin+UiVec(17,17),
                    ImGui.GetColorU32(hovered?AccentHover:Accent),source.Icon.ToIconString());
            }
            var textWidth=MathF.Max(1,size.X-textX-Ui(25));
            var titleY=origin.Y+Ui(isRetro?8:12);
            using(UiFonts.PushRelative(.96f,emphasis:true))
                draw.AddText(new(origin.X+textX,titleY),ImGui.GetColorU32(Vector4.One),TruncateToWidth(source.Title,textWidth));
            using(UiFonts.PushRegularRelative(.84f))
                draw.AddText(new(origin.X+textX,titleY+Ui(21)),ImGui.GetColorU32(MutedText),TruncateToWidth(source.Description,textWidth));
            UiFonts.DrawCenteredIcon(draw,Ui(10),origin+new Vector2(size.X-Ui(15),size.Y*.5f),
                ImGui.GetColorU32(hovered?AccentHover:MutedText),FontAwesomeIcon.ChevronRight.ToIconString());
        }
        finally { draw.PopClipRect(); }
        if(clicked) OpenAddMediaShortcut(source);
    }

    private void OpenAddMediaShortcut(AddMediaShortcut source)
    {
        if(source.System is {} system)
        {
            selectedGameSystem=system;
            currentPage=HomePage.PlaySnes;
            return;
        }
        switch(source.Id)
        {
            case "webLink": OpenAddMediaSource(0); break;
            case "youtube": currentPage=HomePage.VideoGrid; break;
            case "internetArchive": currentPage=HomePage.InternetArchive; break;
            case "twitch": currentPage=HomePage.Twitch; twitchHubTab=TwitchHubTab.Discover; break;
            case "dailymotion": currentPage=HomePage.Dailymotion; break;
            case "browser": currentPage=HomePage.Browser; break;
            case "localVideo": OpenAddMediaSource(7); break;
            case "images": OpenAddMediaSource(6); break;
            case "musicDj": currentPage=HomePage.Music; musicTab=MusicTab.Radio; break;
            case "streamLive": OpenAddMediaSource(4); break;
        }
    }

    private static void DrawAddMediaArchiveIcon(ImDrawListPtr draw,Vector2 origin)
    {
        var ink=ImGui.GetColorU32(AccentHover);
        draw.AddTriangleFilled(origin+UiVec(10,0),origin+UiVec(0,5),origin+UiVec(20,5),ink);
        draw.AddRectFilled(origin+UiVec(1,6),origin+UiVec(19,8),ink);
        for(var i=0;i<4;i++)
            draw.AddRectFilled(origin+UiVec(3+i*4,9),origin+UiVec(5+i*4,18),ink);
        draw.AddRectFilled(origin+UiVec(0,19),origin+UiVec(20,21),ink);
    }
}
