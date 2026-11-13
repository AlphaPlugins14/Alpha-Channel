using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private void DrawTwitchCheck(bool includeFavourites = true)
    {
        if (includeFavourites) RefreshTwitchFavouritesIfStale();
        var width=ImGui.GetContentRegionAvail().X;
        DrawPlayerHubPanel("##twitchSearchPanel",width,Ui(includeFavourites?162:72),true,()=>
        {
            var origin=ImGui.GetCursorScreenPos();var available=ImGui.GetContentRegionAvail().X;
            var draw=ImGui.GetWindowDrawList();
            if (includeFavourites)
            {
            using(UiFonts.PushRegularRelative(.78f)) draw.AddText(origin,ImGui.GetColorU32(AccentHover),"TWITCH");
            var artwork=width>=Ui(580);
            using(UiFonts.PushRelative(1.6f,emphasis:true))
                DrawWrappedLines(draw,origin+UiVec(0,21),available-Ui(artwork?65:0),ImGui.GetTextLineHeight(),2,
                    ImGui.GetColorU32(Vector4.One),"Find your next live channel.");
            if(artwork) UiFonts.DrawCenteredIcon(draw,Ui(38),origin+new Vector2(available-Ui(22),Ui(25)),
                ImGui.GetColorU32(AccentInk(.32f)),FontAwesomeIcon.BroadcastTower.ToIconString());
            ImGui.SetCursorScreenPos(origin+UiVec(0,64));
            }
            var buttonWidth=MathF.Min(Ui(108),available*.3f);
            using(var padding=ImRaii.PushStyle(ImGuiStyleVar.FramePadding,
                      new Vector2(Ui(10),MathF.Max(1,(Ui(36)-ImGui.GetFontSize())*.5f)))
                  .Push(ImGuiStyleVar.FrameRounding,Ui(7)).Push(ImGuiStyleVar.FrameBorderSize,Ui(1)))
            using(var colors=ImRaii.PushColor(ImGuiCol.FrameBg,HubSurface(0x0A1526)).Push(ImGuiCol.FrameBgHovered,FrameBgHover)
                      .Push(ImGuiCol.FrameBgActive,AccentFieldActive).Push(ImGuiCol.Border,HubTint(0x345476)))
            {
                ImGui.SetNextItemWidth(MathF.Max(1,available-buttonWidth-Ui(10)));
                var submitted=ImGui.InputTextWithHint("##twitchChannel","Enter a Twitch channel name or URL",ref twitchChannelInput,2048,
                    ImGuiInputTextFlags.EnterReturnsTrue);
                ImGui.SameLine(0,Ui(10));
                using(UiDisabled.Scope(isCheckingTwitch || string.IsNullOrWhiteSpace(twitchChannelInput)))
                    submitted|=GameLayoutButton("Search",FontAwesomeIcon.Search,buttonWidth,true,height:36);
                if(submitted && !isCheckingTwitch && !string.IsNullOrWhiteSpace(twitchChannelInput))
                {
                    isCheckingTwitch=true;twitchResult=null;twitchError=null;twitchResultIsOffline=false;twitchCheckedChannelName=null;
                    _=RunTwitchCheckAsync(twitchChannelInput.Trim());
                }
            }
            if (includeFavourites) using(UiFonts.PushRegularRelative(.86f))
                draw.AddText(origin+UiVec(0,112),ImGui.GetColorU32(MutedText),
                    isCheckingTwitch?"Checking channel...":"Enter a channel name or paste a Twitch URL.");
        });
        if(twitchError is {} error)
        {
            SetGameLayoutRowGap(Ui(9));
            using(ImRaii.TextWrapPos(0)) ImGui.TextColored(Danger,error);
        }
        if(!isCheckingTwitch && (twitchResult is not null || twitchResultIsOffline && twitchCheckedChannelName is not null))
        {
            SetGameLayoutRowGap(Ui(12));
            DrawTwitchSearchedChannelCard();
        }
        if(ImGui.GetTime()<queueAddedFeedbackUntil)
        {
            SetGameLayoutRowGap(Ui(6));
            using(UiFonts.PushIcon()) ImGui.TextColored(Good,FontAwesomeIcon.Check.ToIconString());
            ImGui.SameLine(0,Ui(6));
            using(UiFonts.PushRegularRelative(.86f)) ImGui.TextColored(Good,"Video added to queue");
        }
        SetGameLayoutRowGap(Ui(16));
        if (includeFavourites) DrawTwitchFavourites();
    }

    private void DrawTwitchSearchedChannelCard()
    {
        var info=twitchResult;var name=info?.ChannelName??twitchCheckedChannelName;
        if(string.IsNullOrWhiteSpace(name)) return;
        var width=ImGui.GetContentRegionAvail().X;var live=info is not null;
        DrawPlayerHubPanel("##twitchSearchedChannel",width,Ui(live?146:94),live,()=>
        {
            var origin=ImGui.GetWindowPos();var draw=ImGui.GetWindowDrawList();
            var imageSize=live && width>=Ui(580)?UiVec(160,90):UiVec(100,56);
            var imageOrigin=origin+new Vector2(Ui(14),(Ui(live?146:94)-imageSize.Y)*.5f);
            DrawTwitchHubThumbnail(info,imageOrigin,imageSize);
            var content=origin+new Vector2(imageSize.X+Ui(30),Ui(14));
            var textWidth=MathF.Max(1,width-(content.X-origin.X)-Ui(14));
            DrawTwitchHubChannelHeading(name,live?"Live":"Offline",live?Danger:MutedText,content,textWidth-Ui(78));
            using(UiFonts.PushRegularRelative(.94f))
                draw.AddText(content+UiVec(0,29),ImGui.GetColorU32(MutedText),
                    FitTwitchText(live?PrepareTwitchDisplayText(info!.Title):"This channel isn't live right now.",textWidth));
            if(info is not null)
            {
                using(UiFonts.PushRegularRelative(.84f))
                    draw.AddText(content+UiVec(0,53),ImGui.GetColorU32(MutedText),FitTwitchText(TwitchHubMetadata(info,false),textWidth));
                ImGui.SetCursorScreenPos(content+UiVec(0,82));
                var watchWidth=MathF.Min(Ui(114),(textWidth-Ui(9))*.45f);
                var queueWidth=MathF.Min(Ui(140),textWidth-watchWidth-Ui(9));
                if(GameLayoutButton("Watch now",FontAwesomeIcon.Play,watchWidth,true,height:32)) PlayTwitchHubStream(info);
                ImGui.SameLine(0,Ui(9));
                if(GameLayoutButton("Add to queue",FontAwesomeIcon.Plus,queueWidth,height:32))
                {
                    HandleAddToQueue(new VideoQueueEntry(info.Url,info.Title,info.ChannelName,null,info.ThumbnailUrl));
                    if(!ShouldUseViewerMediaActions) queueAddedFeedbackUntil=ImGui.GetTime()+2;
                }
            }
            ImGui.SetCursorScreenPos(origin+new Vector2(width-Ui(80),Ui(12)));
            DrawTwitchFavouriteButton("searchedTwitchFavourite",name,IsFavouriteTwitchChannel(name),UiVec(30,30));
            ImGui.SetCursorScreenPos(origin+new Vector2(width-Ui(42),Ui(12)));
            if(GameLayoutButton("",FontAwesomeIcon.Times,Ui(30),height:30))
            { twitchResult=null;twitchResultIsOffline=false;twitchCheckedChannelName=null; }
            if(ImGui.IsItemHovered()) UiFonts.SetTooltip("Dismiss channel result");
        },contentPadding:0);
    }

    private void DrawTwitchFavourites()
    {
        var favourites=GetFavouriteTwitchChannels();
        var origin=ImGui.GetCursorScreenPos();var width=ImGui.GetContentRegionAvail().X;
        using(UiFonts.PushRelative(1.06f,emphasis:true)) ImGui.TextUnformatted($"Favourite channels ({favourites.Count})");
        var checkedText=isRefreshingTwitchFavourites?"Checking...":twitchFavouritesLastRefreshUtc is {} refreshed?
            $"Checked {FormatTwitchFavouriteCheckAge(refreshed)}":"Not checked yet";
        if(width>=Ui(500))
        {
            using var font=UiFonts.PushRegularRelative(.8f);
            var textSize=ImGui.CalcTextSize(checkedText);
            ImGui.SetCursorScreenPos(origin+new Vector2(width-Ui(44)-textSize.X,Ui(4)));
            ImGui.TextColored(MutedText,checkedText);
        }
        ImGui.SetCursorScreenPos(origin+new Vector2(width-Ui(30),-Ui(3)));
        using(UiDisabled.Scope(isRefreshingTwitchFavourites))
            if(GameLayoutButton(string.Empty,FontAwesomeIcon.SyncAlt,Ui(30),height:28)) RefreshTwitchFavouritesIfStale(forceRefresh:true);
        if(ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            UiFonts.SetTooltip($"Refresh favourite channels. Statuses update automatically every 15 minutes.\n{checkedText}");
        ImGui.SetCursorScreenPos(origin+UiVec(0,34));
        if(favourites.Count==0) { DrawEmptyTwitchFavourites();return; }
        var ordered=favourites.OrderByDescending(channel=>twitchFavouriteStatuses.TryGetValue(channel,out var status) && status.Stream is not null)
            .ThenBy(channel=>channel,StringComparer.OrdinalIgnoreCase).ToArray();
        var columns=width>=Ui(620)?2:1;var gap=Ui(12);var cardWidth=(width-gap*(columns-1))/columns;
        var grid=ImGui.GetCursorScreenPos();var rowY=0f;
        for(var row=0;row<ordered.Length;row+=columns)
        {
            var rowHeight=0f;
            for(var col=0;col<columns && row+col<ordered.Length;col++)
            {
                var channel=ordered[row+col];twitchFavouriteStatuses.TryGetValue(channel,out var status);
                rowHeight=MathF.Max(rowHeight,Ui(status?.Stream is not null?126:94));
                ImGui.SetCursorScreenPos(grid+new Vector2(col*(cardWidth+gap),rowY));
                if(DrawTwitchFavouriteCard(channel,status,cardWidth)) return;
            }
            rowY+=rowHeight+gap;
        }
        ImGui.SetCursorScreenPos(grid+new Vector2(0,rowY));
    }

    private bool DrawTwitchFavouriteCard(string channel,TwitchFavouriteStatus? status,float width)
    {
        var info=status?.Stream;var live=info is not null;var offline=status?.IsOffline==true;
        var error=!string.IsNullOrWhiteSpace(status?.Error) && !offline;
        var state=live?"Live":offline?"Offline":error?"Unavailable":isRefreshingTwitchFavourites?"Checking":"Not checked";
        var removed=false;
        using var id=ImRaii.PushId(channel);
        DrawPlayerHubPanel("##twitchFavourite",width,Ui(live?126:94),false,()=>
        {
            var origin=ImGui.GetWindowPos();var draw=ImGui.GetWindowDrawList();
            var imageSize=UiVec(90,live?102:70);var imageOrigin=origin+UiVec(12,12);
            DrawTwitchHubThumbnail(info,imageOrigin,imageSize);
            var content=origin+UiVec(114,13);var textWidth=MathF.Max(1,width-Ui(126));
            DrawTwitchHubChannelHeading(info?.ChannelName??channel,state,live||error?Danger:MutedText,content,textWidth-Ui(38),compact:true);
            var detail=live?PrepareTwitchDisplayText(info!.Title):error?status!.Error!:
                offline?"This channel isn't live right now.":"Waiting for a status check.";
            using(UiFonts.PushRegularRelative(.84f))
                draw.AddText(content+UiVec(0,28),ImGui.GetColorU32(MutedText),FitTwitchText(detail,textWidth));
            if(live)
            {
                using(UiFonts.PushRegularRelative(.78f))
                    draw.AddText(content+UiVec(0,51),ImGui.GetColorU32(MutedText),FitTwitchText(TwitchHubMetadata(info!,true),textWidth));
                ImGui.SetCursorScreenPos(content+UiVec(0,73));
                if(GameLayoutButton("Watch",FontAwesomeIcon.Play,MathF.Min(Ui(94),textWidth),true,height:28)) PlayTwitchHubStream(info!);
            }
            else if(status is not null)
            {
                using(UiFonts.PushRegularRelative(.74f))
                    draw.AddText(content+UiVec(0,51),ImGui.GetColorU32(MutedText),FitTwitchText($"Checked {FormatTwitchFavouriteCheckAge(status.CheckedAtUtc)}",textWidth));
            }
            ImGui.SetCursorScreenPos(content+UiVec(0,27));
            ImGui.InvisibleButton("##twitchDetails",new(textWidth,Ui(live?42:34)));
            if(ImGui.IsItemHovered()) UiFonts.SetTooltip(detail+(live?"\n"+TwitchHubMetadata(info!,false):string.Empty));
            ImGui.SetCursorScreenPos(origin+new Vector2(width-Ui(40),Ui(10)));
            removed=DrawTwitchFavouriteButton("favouriteCard",channel,true,UiVec(30,30));
        },contentPadding:0);
        return removed;
    }

    private void DrawTwitchHubChannelHeading(string channel,string state,Vector4 stateColor,Vector2 position,float width,bool compact=false)
    {
        var draw=ImGui.GetWindowDrawList();
        float badgeWidth;
        using(UiFonts.PushRegularRelative(.75f)) badgeWidth=ImGui.CalcTextSize(state).X+Ui(14);
        Vector2 badge;
        using(UiFonts.PushRelative(compact?.94f:1.1f,emphasis:true))
        {
            var title=FitTwitchText(PrepareTwitchDisplayText(channel),MathF.Max(1,width-badgeWidth-Ui(8)));
            var titleSize=ImGui.CalcTextSize(title);
            draw.AddText(position,ImGui.GetColorU32(Vector4.One),title);
            badge=position+new Vector2(titleSize.X+Ui(8),0);
        }
        draw.AddRectFilled(badge,badge+new Vector2(badgeWidth,Ui(19)),ImGui.GetColorU32(stateColor with {W=.13f}),Ui(5));
        using(UiFonts.PushRegularRelative(.75f))
            draw.AddText(badge+UiVec(7,2),ImGui.GetColorU32(stateColor),state);
        ImGui.SetCursorScreenPos(position);
        ImGui.InvisibleButton("##twitchChannelName",new(MathF.Max(1,width),Ui(22)));
        if(ImGui.IsItemHovered()) UiFonts.SetTooltip(channel+" · "+state);
    }

    private void DrawTwitchHubThumbnail(TwitchStreamInfo? info,Vector2 position,Vector2 size,bool showLive=true)
    {
        var draw=ImGui.GetWindowDrawList();var thumbnail=info is null?null:thumbnails.Get(info.ThumbnailUrl);
        if(thumbnail is not null)
        {
            // Portrait favourite thumbnails crop the usual 16:9 Twitch image.
            var visible=MathF.Min(1,size.X/size.Y/(16f/9));
            draw.AddImageRounded(thumbnail.Handle,position,position+size,new((1-visible)*.5f,0),new((1+visible)*.5f,1),uint.MaxValue,Ui(6));
        }
        else
        {
            draw.AddRectFilled(position,position+size,ImGui.GetColorU32(HubSurface(0x1B283D)),Ui(6));
            UiFonts.DrawCenteredIcon(draw,Ui(22),position+size*.5f,ImGui.GetColorU32(MutedText with {W=.65f}),FontAwesomeIcon.Tv.ToIconString());
        }
        if(info is not null && showLive)
        {
            using var font=UiFonts.PushRegularRelative(.68f);
            var badge=position+UiVec(4,4);var badgeSize=ImGui.CalcTextSize("LIVE")+UiVec(12,6);
            draw.AddRectFilled(badge,badge+badgeSize,ImGui.GetColorU32(Danger),Ui(4));
            draw.AddText(badge+UiVec(6,3),ImGui.GetColorU32(Vector4.One),"LIVE");
        }
    }

    private static string TwitchHubMetadata(TwitchStreamInfo info,bool compact)
    {
        var viewers=info.ViewerCount is {} count?FormatArchiveDownloads(count)+(compact?string.Empty:" viewers"):null;
        return string.IsNullOrWhiteSpace(info.Category)?viewers??"Live on Twitch":
            viewers is null?info.Category:info.Category+" · "+viewers;
    }

    private void PlayTwitchHubStream(TwitchStreamInfo info)=>
        HandlePlayNow(new VideoQueueEntry(info.Url,info.Title,info.ChannelName,null,info.ThumbnailUrl));

    private void DrawEmptyTwitchFavourites()
    {
        DrawPlayerHubPanel("##emptyTwitchFavourites",ImGui.GetContentRegionAvail().X,Ui(90),false,()=>
        {
            var origin=ImGui.GetCursorScreenPos();var width=ImGui.GetContentRegionAvail().X;
            UiFonts.DrawCenteredIcon(ImGui.GetWindowDrawList(),Ui(29),origin+UiVec(16,25),
                ImGui.GetColorU32(AccentInk(.28f)),FontAwesomeIcon.Star.ToIconString());
            var content=origin+UiVec(48,0);
            using(UiFonts.PushRelative(1.08f,emphasis:true))
                ImGui.GetWindowDrawList().AddText(content,ImGui.GetColorU32(Vector4.One),"Keep your favourites close.");
            using(UiFonts.PushRegularRelative(.86f))
                DrawWrappedLines(ImGui.GetWindowDrawList(),content+UiVec(0,27),MathF.Max(1,width-Ui(48)),ImGui.GetTextLineHeight(),2,
                    ImGui.GetColorU32(MutedText),"Open Discover and use a channel's star to save it here.");
        });
    }
}
