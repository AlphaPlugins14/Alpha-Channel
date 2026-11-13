using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private static readonly string[] UnifiedProviderLabels=["All","YouTube","Dailymotion","Internet Archive"];

    private void DrawUnifiedSearchPage()
    {
        DrawUnifiedSearchControls();
        DrawYouTubeSearchUsage();
        SetGameLayoutRowGap(Ui(10));
        DrawUnifiedProviderStatuses();
        SetGameLayoutRowGap(Ui(9));
        var results=BuildUnifiedSearchResults();
        var title=string.IsNullOrWhiteSpace(unifiedSearchCompletedQuery)?"Search results":$"Results for “{unifiedSearchCompletedQuery}”";
        var width=ImGui.GetContentRegionAvail().X;
        var count=$"({results.Count})";
        float countWidth;
        using(UiFonts.PushRegularRelative(.94f)) countWidth=ImGui.CalcTextSize(count).X;
        using(UiFonts.PushRelative(1.16f,emphasis:true))
            ImGui.TextUnformatted(TruncateToWidth(title,MathF.Max(1,width-countWidth-Ui(10))));
        if(ImGui.IsItemHovered()) UiFonts.SetTooltip(title);
        ImGui.SameLine(0,Ui(9));
        using(UiFonts.PushRegularRelative(.94f)) ImGui.TextColored(MutedText,count);
        SetGameLayoutRowGap(Ui(10));
        if(results.Count==0)
        {
            ImGui.TextColored(MutedText,IsUnifiedSearchRunning?"Searching providers for playable videos...":"Enter a search above to find videos.");
            return;
        }
        DrawUnifiedResultsGrid(results);
    }

    private void DrawUnifiedSearchControls()
    {
        var width=ImGui.GetContentRegionAvail().X;
        var inner=MathF.Max(1,width-Ui(36));
        var positions=new Vector2[6];var widths=new float[6];
        var x=0f;var y=0f;
        using(UiFonts.PushRegularRelative(.84f))
        {
            for(var i=0;i<6;i++)
            {
                widths[i]=MathF.Min(inner,i<4?ImGui.CalcTextSize(UnifiedProviderLabels[i]).X+Ui(i==0?34:27):Ui(i==4?140:130));
                var gap=i==0?0:Ui(i==4?12:6);
                if(x+gap+widths[i]>inner+.5f && x>0) { x=0;y+=Ui(38);gap=0; }
                positions[i]=new(x+gap,y);x+=gap+widths[i];
            }
        }
        DrawPlayerHubPanel("##unifiedSearchControls",width,Ui(112)+y,false,()=>
        {
            var origin=ImGui.GetCursorScreenPos();
            var buttonWidth=MathF.Min(Ui(108),inner*.3f);
            using(var style=ImRaii.PushStyle(ImGuiStyleVar.FramePadding,
                      new Vector2(Ui(10),MathF.Max(1,(Ui(36)-ImGui.GetFontSize())*.5f)))
                  .Push(ImGuiStyleVar.FrameRounding,Ui(7)).Push(ImGuiStyleVar.FrameBorderSize,Ui(1)))
            using(var colors=ImRaii.PushColor(ImGuiCol.FrameBg,HubSurface(0x0B1526)).Push(ImGuiCol.Border,HubTint(0x24456B)))
            {
                ImGui.SetNextItemWidth(MathF.Max(1,inner-buttonWidth-Ui(10)));
                var submitted=ImGui.InputTextWithHint("##unifiedVideoSearch","Search videos across all providers...",
                    ref unifiedSearchQuery,300,ImGuiInputTextFlags.EnterReturnsTrue);
                ImGui.SameLine(0,Ui(10));
                using(UiDisabled.Scope(string.IsNullOrWhiteSpace(unifiedSearchQuery)))
                    submitted|=GameLayoutButton("Search",FontAwesomeIcon.Search,buttonWidth,true,height:36);
                if(submitted && !string.IsNullOrWhiteSpace(unifiedSearchQuery)) StartUnifiedSearch();
            }
            using var font=UiFonts.PushRegularRelative(.84f);
            using var padding=ImRaii.PushStyle(ImGuiStyleVar.FramePadding,
                new Vector2(Ui(6),MathF.Max(1,(Ui(30)-ImGui.GetFontSize())*.5f))).Push(ImGuiStyleVar.FrameRounding,Ui(6));
            var all=unifiedShowYouTube && unifiedShowDailymotion && unifiedShowArchive;
            for(var i=0;i<4;i++)
            {
                ImGui.SetCursorScreenPos(origin+UiVec(0,46)+positions[i]);
                var selected=i==0?all:!all && (i==1?unifiedShowYouTube:i==2?unifiedShowDailymotion:unifiedShowArchive);
                if(!DrawUnifiedFilterChip(UnifiedProviderLabels[i],selected,i==0?Accent:UnifiedProviderColor((UnifiedSearchProvider)(i-1)),widths[i],i==0)) continue;
                switch(i)
                {
                    case 0: unifiedShowYouTube=unifiedShowDailymotion=unifiedShowArchive=true; break;
                    case 1: unifiedShowYouTube=!unifiedShowYouTube; break;
                    case 2: unifiedShowDailymotion=!unifiedShowDailymotion; break;
                    case 3: unifiedShowArchive=!unifiedShowArchive; break;
                }
            }
            ImGui.SetCursorScreenPos(origin+UiVec(0,46)+positions[4]);
            ImGui.SetNextItemWidth(widths[4]);
            if(UiFonts.Window(ImGui.GetFontSize(),ImGui.BeginCombo("##unifiedOrder",$"Order: {GetVideoSearchOrderName(unifiedSearchOrder)}")))
            {
                try
                {
                    foreach(var option in Enum.GetValues<VideoSearchOrder>())
                    {
                        if(ImGui.Selectable(GetVideoSearchOrderName(option),unifiedSearchOrder==option)) unifiedSearchOrder=option;
                        if(unifiedSearchOrder==option) ImGui.SetItemDefaultFocus();
                    }
                }
                finally { ImGui.EndCombo(); }
            }
            ImGui.SetCursorScreenPos(origin+UiVec(0,46)+positions[5]);
            ImGui.SetNextItemWidth(widths[5]);
            if(UiFonts.Window(ImGui.GetFontSize(),ImGui.BeginCombo("##unifiedDuration",$"Duration: {GetVideoDurationFilterName(unifiedDurationFilter)}")))
            {
                try
                {
                    foreach(var option in Enum.GetValues<VideoDurationFilter>())
                    {
                        if(ImGui.Selectable(GetVideoDurationFilterName(option),unifiedDurationFilter==option)) unifiedDurationFilter=option;
                        if(unifiedDurationFilter==option) ImGui.SetItemDefaultFocus();
                    }
                }
                finally { ImGui.EndCombo(); }
            }
        });
    }

    private bool DrawUnifiedFilterChip(string label,bool selected,Vector4 color,float width,bool all)
    {
        var origin=ImGui.GetCursorScreenPos();var size=new Vector2(width,Ui(30));
        var clicked=ImGui.InvisibleButton($"##unifiedProvider_{label}",size);
        var draw=ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin,origin+size,ImGui.GetColorU32(selected?Accent:ImGui.IsItemHovered()?CardBgHover:HubSurface(0x101A2D)),Ui(6));
        draw.AddRect(origin,origin+size,ImGui.GetColorU32(selected?AccentHover:HubTint(0x24456B)),Ui(6));
        if(all) UiFonts.DrawCenteredIcon(draw,Ui(12),origin+UiVec(13,15),ImGui.GetColorU32(Vector4.One),FontAwesomeIcon.Globe.ToIconString());
        else draw.AddCircleFilled(origin+UiVec(11,15),Ui(3),ImGui.GetColorU32(selected?Vector4.One:color),12);
        draw.AddText(origin+new Vector2(Ui(all?24:20),(size.Y-ImGui.GetTextLineHeight())*.5f),ImGui.GetColorU32(Vector4.One),label);
        return clicked;
    }

    private static Vector4 UnifiedProviderColor(UnifiedSearchProvider provider)=>provider switch
    {
        UnifiedSearchProvider.YouTube=>Hex(0xFF0033),
        UnifiedSearchProvider.Dailymotion=>Hex(0x168AFF),
        _=>Hex(0xEAB308)
    };

    private void DrawUnifiedProviderStatuses()
    {
        using var font=UiFonts.PushRegularRelative(.85f);
        var origin=ImGui.GetCursorScreenPos();var width=ImGui.GetContentRegionAvail().X;
        var x=0f;var y=0f;var height=ImGui.GetTextLineHeight()+Ui(4);
        for(var i=0;i<3;i++)
        {
            var provider=(UnifiedSearchProvider)i;
            var searching=i==0?unifiedYouTubeSearching:i==1?unifiedDailymotionSearching:unifiedArchiveSearching;
            var error=i==0?unifiedYouTubeError:i==1?unifiedDailymotionError:unifiedArchiveError;
            var count=i==0?unifiedYouTubeResults.Count:i==1?unifiedDailymotionResults.Count:unifiedArchiveResults.Count;
            var label=$"{UnifiedProviderLabels[i+1]}  {count}";
            var itemWidth=ImGui.CalcTextSize(label).X+Ui(19);
            if(x+itemWidth>width && x>0) { x=0;y+=height+Ui(4); }
            var position=origin+new Vector2(x,y);
            ImGui.SetCursorScreenPos(position);
            ImGui.InvisibleButton($"##unifiedStatus_{provider}",new(itemWidth,height));
            var draw=ImGui.GetWindowDrawList();
            var color=error is not null?Danger:UnifiedProviderColor(provider);
            var center=position+new Vector2(Ui(5),height*.5f);
            if(searching)
            {
                for(var dot=0;dot<8;dot++)
                {
                    var angle=dot*MathF.Tau/8;
                    var phase=(int)(ImGui.GetTime()*8)%8;
                    draw.AddCircleFilled(center+new Vector2(MathF.Cos(angle),MathF.Sin(angle))*Ui(4),Ui(1),
                        ImGui.GetColorU32(color with {W=dot==phase?1:.25f}),8);
                }
            }
            else draw.AddCircleFilled(center,Ui(3),ImGui.GetColorU32(color),12);
            draw.AddText(position+new Vector2(Ui(16),(height-ImGui.GetTextLineHeight())*.5f),ImGui.GetColorU32(MutedText),label);
            if(ImGui.IsItemHovered()) UiFonts.SetTooltip(error is not null?$"{UnifiedProviderLabels[i+1]}: {error}":
                searching?$"Searching {UnifiedProviderLabels[i+1]}...":$"{count} results from {UnifiedProviderLabels[i+1]}");
            x+=itemWidth+Ui(20);
        }
        ImGui.SetCursorScreenPos(origin+new Vector2(0,y+height+ImGui.GetStyle().ItemSpacing.Y));
    }

    private void DrawUnifiedResultsGrid(IReadOnlyList<UnifiedSearchResult> results,
        string childId = "##unifiedResults", bool resetScroll = false, bool dailymotionPage = false)
    {
        using var padding=ImRaii.PushStyle(ImGuiStyleVar.WindowPadding,dailymotionPage ? Vector2.Zero : UiVec(4,4));
        using var child=UiFonts.Window(ImGui.GetFontSize(),ImRaii.Child(childId,
            new Vector2(ImGui.GetContentRegionAvail().X,MathF.Max(Ui(100),ImGui.GetContentRegionAvail().Y)),false,
            ImGuiWindowFlags.AlwaysUseWindowPadding));
        if(!child) return;
        if(resetScroll) ImGui.SetScrollY(0);
        var available=MathF.Max(1,ImGui.GetContentRegionAvail().X-(dailymotionPage ? Ui(12) : 0));
        var gap=Ui(12);
        var columns=dailymotionPage ? BrowseVideoGridColumns(available) : Math.Clamp((int)((available+gap)/(Ui(205)+gap)),1,5);
        var width=(available-gap*(columns-1))/columns;
        var origin=ImGui.GetCursorScreenPos();
        var height=dailymotionPage ? BrowseVideoCardHeight : (width-Ui(12))*9/16+Ui(142);
        for(var i=0;i<results.Count;i++)
        {
            ImGui.SetCursorScreenPos(origin+new Vector2(i%columns*(width+gap),i/columns*(height+gap)));
            DrawUnifiedResultCard(results[i],width,height,i,dailymotionPage);
        }
    }

    private void DrawUnifiedResultCard(UnifiedSearchResult result,float width,float height,int index,bool dailymotionPage=false)
    {
        if (dailymotionPage) { DrawSourceBrowseVideoCard(result, width); return; }
        var origin=ImGui.GetCursorScreenPos();var size=new Vector2(width,height);
        if(!ImGui.IsRectVisible(origin,origin+size)) { ImGui.Dummy(size);return; }
        if(result.Provider==UnifiedSearchProvider.YouTube && result.Video is {} youtube)
            result=result with {Video=VisibleYouTubeCardMetadata(youtube)};
        using var id=ImRaii.PushId($"unified_{result.Provider}_{result.ExactKey}_{index}");
        var imageSize=new Vector2(width-Ui(12),(width-Ui(12))*9/16);
        DrawPlayerHubPanel("##unifiedCard",width,height,false,()=>
        {
            var draw=ImGui.GetWindowDrawList();
            var cardOrigin=ImGui.GetWindowPos();
            var imageOrigin=cardOrigin+UiVec(6,6);
            ImGui.SetCursorScreenPos(imageOrigin);
            if(ImGui.InvisibleButton("##thumbnail",imageSize)) PlayUnifiedResult(result);
            draw.AddRectFilled(imageOrigin,imageOrigin+imageSize,ImGui.GetColorU32(FrameBg),Ui(7));
            var thumbnail=thumbnails.Get(result.ThumbnailUrl);
            if(thumbnail is not null) draw.AddImageRounded(thumbnail.Handle,imageOrigin,imageOrigin+imageSize,
                Vector2.Zero,Vector2.One,uint.MaxValue,Ui(7));
            else if(result.ThumbnailUrl is null) DrawArchiveMissingThumbnail(imageOrigin,imageSize,draw);
            using(UiFonts.PushRegularRelative(.75f))
            {
                if(!dailymotionPage)
                {
                var label=UnifiedProviderLabels[(int)result.Provider+1];
                var badgeSize=ImGui.CalcTextSize(label)+UiVec(23,8);
                var badge=imageOrigin+UiVec(5,5);
                draw.AddRectFilled(badge,badge+badgeSize,ImGui.GetColorU32(HubSurface(0x080F1D) with {W=.94f}),Ui(5));
                draw.AddCircleFilled(badge+new Vector2(Ui(7),badgeSize.Y*.5f),Ui(2.5f),ImGui.GetColorU32(UnifiedProviderColor(result.Provider)),12);
                draw.AddText(badge+UiVec(14,4),ImGui.GetColorU32(Vector4.One),label);
                }
                if(result.Duration is {} duration)
                {
                    var text=FormatArchiveDuration(duration);var textSize=ImGui.CalcTextSize(text);
                    var min=imageOrigin+imageSize-textSize-UiVec(10,8);
                    draw.AddRectFilled(min-UiVec(4,2),min+textSize+UiVec(4,2),ImGui.GetColorU32(new Vector4(0,0,0,.8f)),Ui(3));
                    draw.AddText(min,uint.MaxValue,text);
                }
            }
            var content=cardOrigin+new Vector2(Ui(9),imageSize.Y+Ui(16));
            var textWidth=width-Ui(18);
            float titleHeight;
            using(UiFonts.PushRelative(dailymotionPage?1.08f:.97f,emphasis:true))
            {
                titleHeight=ImGui.GetTextLineHeight()*2;
                DrawWrappedLines(draw,content,textWidth,ImGui.GetTextLineHeight(),2,ImGui.GetColorU32(Vector4.One),result.Title);
            }
            ImGui.SetCursorScreenPos(content);
            ImGui.InvisibleButton("##resultTitle",new(textWidth,titleHeight));
            if(ImGui.IsItemHovered()) UiFonts.SetTooltip(result.Title);
            var creator=string.IsNullOrWhiteSpace(result.Creator)?UnifiedProviderLabels[(int)result.Provider+1]:result.Creator;
            var creatorOrigin=content+new Vector2(0,titleHeight+Ui(5));
            using(UiFonts.PushRegularRelative(dailymotionPage?1f:.86f))
            {
                if(dailymotionPage) UiFonts.DrawCenteredIcon(draw,Ui(12),creatorOrigin+UiVec(6,6),ImGui.GetColorU32(AccentHover),FontAwesomeIcon.User.ToIconString());
                draw.AddText(creatorOrigin+UiVec(dailymotionPage?17:0,0),ImGui.GetColorU32(MutedText),TruncateToWidth(creator,textWidth-Ui(dailymotionPage?17:0)));
            }
            DrawUnifiedCardMetadata(result,creatorOrigin+UiVec(0,22),textWidth,dailymotionPage);
            ImGui.SetCursorScreenPos(cardOrigin+new Vector2(Ui(8),height-Ui(42)));
            var optionsWidth=Ui(40);var playWidth=width-Ui(16)-Ui(7)-optionsWidth;
            var collection=result.Archive is {Episodes.Count:>1};
            if(GameLayoutButton(collection?"View episodes":"Play",collection?FontAwesomeIcon.List:FontAwesomeIcon.Play,
                    playWidth,true,height:34)) PlayUnifiedResult(result);
            ImGui.SameLine(0,Ui(7));
            if(result.Archive is {} archive)
                DrawInternetArchiveQueueOptions(archive,collection?null:archive.Episodes.FirstOrDefault(),34);
            else if(result.Video is {} videoResult)
            {
                if(result.Provider==UnifiedSearchProvider.YouTube)
                {
                    var anchor=ImGui.GetCursorScreenPos();
                    if(GameLayoutButton(string.Empty,FontAwesomeIcon.EllipsisH,optionsWidth,height:34)) OpenYouTubeCardMenu(videoResult,anchor);
                    if(ImGui.IsItemHovered()) UiFonts.SetTooltip("Video options");
                }
                else DrawUnifiedVideoOptions(videoResult);
            }
        },contentPadding:0);
    }

    private void DrawUnifiedCardMetadata(UnifiedSearchResult result,Vector2 origin,float width,bool dailymotionPage=false)
    {
        using var font=UiFonts.PushRegularRelative(dailymotionPage ? .95f : .78f);
        var draw=ImGui.GetWindowDrawList();var color=ImGui.GetColorU32(MutedText);
        var views=result.Archive is {} archive?$"{FormatArchiveDownloads(Math.Max(0,archive.Downloads))} views":
            result.Video?.ViewCount is {} count?FormatViewCount(count):null;
        if(dailymotionPage && result.Video?.ViewCount==1) views="1 view";
        var episodes=result.Archive is {Episodes.Count:>1} collection?$"{collection.Episodes.Count} episodes":null;
        var episodeWidth=episodes is null?0:ImGui.CalcTextSize(episodes).X+Ui(17);
        if(views is not null)
        {
            UiFonts.DrawCenteredIcon(draw,Ui(12),origin+UiVec(6,6),color,FontAwesomeIcon.Eye.ToIconString());
            draw.AddText(origin+UiVec(17,0),color,TruncateToWidth(views,MathF.Max(1,width-Ui(17)-(episodes is null?0:episodeWidth+Ui(7)))));
        }
        if(episodes is not null)
        {
            var position=origin+new Vector2(MathF.Max(0,width-episodeWidth),0);
            UiFonts.DrawCenteredIcon(draw,Ui(11),position+UiVec(5,6),color,FontAwesomeIcon.List.ToIconString());
            draw.AddText(position+UiVec(17,0),color,episodes);
        }
    }

    private void DrawUnifiedVideoOptions(VideoSearchEntry video, Action? removeFromRecent = null, bool removingRecent = false)
    {
        CapturePartyPopupBounds();
        if(GameLayoutButton(string.Empty,FontAwesomeIcon.EllipsisH,Ui(40),height:34)) ImGui.OpenPopup("##unifiedVideoOptions");
        if(ImGui.IsItemHovered()) UiFonts.SetTooltip("Video options");
        var inParty=stream.Mode is StreamMode.Hosting or StreamMode.Viewing;
        var rowHeight=MathF.Max(Ui(24),ImGui.GetTextLineHeight()+Ui(8));
        if(!BeginPartyPopup("##unifiedVideoOptions",new(Ui(240),Ui(46)+ImGui.GetTextLineHeight()+
                (QueueManager.StandardSlotCount+1+(inParty?1:0)+(removeFromRecent is not null?1:0))*(rowHeight+ImGui.GetStyle().ItemSpacing.Y)))) return;
        if(inParty && DrawYouTubeMenuRow("Add to Watch Party",FontAwesomeIcon.Users))
        { AddPartySearchResult(video);ImGui.CloseCurrentPopup(); }
        ImGui.TextColored(MutedText,"ADD TO PERSONAL QUEUE");
        for(var slot=0;slot<QueueManager.StandardSlotCount;slot++)
        {
            var name=queueManager.Slots[slot]?.Name??$"Queue {slot+1}";
            if(DrawYouTubeMenuRow($"{name}##unifiedSlot{slot}",FontAwesomeIcon.ListUl,IsVideoInQueueSlot(video,slot)))
            { ToggleVideoInQueueSlot(video,slot);ImGui.CloseCurrentPopup(); }
        }
        ImGui.Separator();
        if(DrawYouTubeMenuRow("Copy video URL",FontAwesomeIcon.Copy))
        { ImGui.SetClipboardText(video.Url);ImGui.CloseCurrentPopup(); }
        if(removeFromRecent is not null)
        {
            ImGui.Separator();
            using(UiDisabled.Scope(removingRecent))
                if(DrawYouTubeMenuRow("Remove from recent",FontAwesomeIcon.Trash))
                {removeFromRecent();ImGui.CloseCurrentPopup();}
        }
        ImGui.EndPopup();
    }
}
