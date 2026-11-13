using System.Diagnostics;
using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Auth;
using AlphaChannel.Plugin.Video;
using AlphaChannel.Plugin.YouTube;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
namespace AlphaChannel.Plugin;
internal sealed partial class MainWindow
{
    private readonly WatchHistoryClient watchHistoryClient=new();
    private readonly PopularVideoTracker watchHistoryTracker=new();
    private readonly List<(string Account,WatchHistoryPlay Play)> watchHistoryPending=[];
    private bool watchHistorySubmitting;
    private double nextHistorySample;
    private DateTime nextHistorySubmit;
    private string? historyPrivacyAccount,historyPrivacyToken,historyPrivacyError;
    private string historyPrivacy="friends";
    private bool historyPrivacyLoading,historyPrivacySaving;
    private DateTime historyPrivacyRetry;
    private bool profileHistoryTab,profileHistoryLoading;
    private string? historyRemovalKey,historyRemovalError;
    private string? profileHistoryTarget,profileHistoryViewer,profileHistoryError;
    private WatchHistoryPage? profileHistoryPage;
    private DateTime nextProfileHistoryRefresh;
    private int profileHistoryGeneration;
    private void TickWatchHistory()
    {
        var now=Stopwatch.GetTimestamp()/(double)Stopwatch.Frequency;
        if(now<nextHistorySample)return;nextHistorySample=now+.25;
        var session=CurrentSession;
        watchHistoryPending.RemoveAll(p=>p.Account!=session?.AccountId);
        if(session is not null)EnsureHistoryPrivacy(session);
        var recordingReady=session is not null&&historyPrivacyToken==session.Token&&!historyPrivacyLoading&&
            !historyPrivacySaving&&historyPrivacyError is null&&historyPrivacy!="none";
        if(!recordingReady)watchHistoryPending.Clear();
        var url=video.CurrentMediaUrl;
        var identity=WatchHistoryClient.Identity(url);
        if(identity is null&&ArchivePopularityClient.IsArchiveUrl(url))identity=WatchHistoryClient.Identity(video.CurrentResolvedMediaUrl);
        var (position,duration,paused)=video.GetProgress();
        var eligible=recordingReady&&Plugin.ClientState.IsLoggedIn&&identity is not null&&!video.IsPlayingLocalVideo;
        if(watchHistoryTracker.Sample(eligible?$"{session!.AccountId}:{identity}:{video.PlaybackAttemptId}":null,
            eligible&&video.State==VideoPlaybackState.Playing&&!paused,position,duration,video.SeekVersion,now))
        {
            var value=identity!.Value;
            searchResolver.TryGetVideoMetadata(url!,out var cached);
            var current=queue.Current;
            var room=stream.Mode==StreamMode.Viewing?stream.CurrentRoomState:null;
            var title=room?.MediaTitle??current?.Title??cached?.Title??value.Id;
            if(Uri.TryCreate(title,UriKind.Absolute,out _))title=value.Provider switch
            {"youtube"=>"YouTube video", "dailymotion"=>"Dailymotion video", _=>"Internet Archive video"};
            var creator=cached?.ChannelName??current?.Source??"";
            var thumb=room?.MediaThumbnailUrl??current?.ThumbnailUrl??cached?.ThumbnailUrl;
            if(watchHistoryPending.Count>=32)watchHistoryPending.RemoveAt(0);
            watchHistoryPending.Add((session!.AccountId,new(value.Provider,value.Id,value.Episode,
                HistoryText(title,512),HistoryText(creator,256),thumb,duration>0&&double.IsFinite(duration)?duration:null,
                Guid.NewGuid().ToString("N"),watchHistoryTracker.WatchedSeconds,DateTimeOffset.UtcNow.ToUnixTimeSeconds())));
        }
        if(recordingReady&&session is not null&&watchHistoryPending.Count>0&&!watchHistorySubmitting&&DateTime.UtcNow>=nextHistorySubmit)
        {
            watchHistorySubmitting=true;_=SubmitWatchHistoryAsync(session,watchHistoryPending[0].Play);
        }
    }
    private static string HistoryText(string text,int limit)=>new(text.Where(c=>!char.IsControl(c)).Take(limit).ToArray());
    private async Task SubmitWatchHistoryAsync(CharacterSession session,WatchHistoryPlay initial)
    {
        var completed=false;var accepted=false;
        try
        {
            var play=initial;
            // Fill direct-link/viewer metadata using the same public source clients as the browse pages.
            try
            {
                if(play.Provider=="archive")
                {
                    var item=await internetArchive.ResolveIdentifierAsync(play.VideoId,youtubeFeedToken).ConfigureAwait(false);
                    if(item is not null)
                    {
                        var episode=item.Episodes.FirstOrDefault(e=>ArchivePopularityClient.Identity(e.VideoUrl)?.Episode==play.EpisodeKey);
                        play=play with {Title=HistoryText(episode is not null&&item.Episodes.Count>1?item.Title+" — "+episode.Title:item.Title,512),
                            Creator=HistoryText(item.Creator,256),ThumbnailUrl=item.ThumbnailUrl};
                    }
                }
                else if(play.Provider=="dailymotion")
                {
                    var item=await dailymotionClient.GetVideoAsync(play.VideoId,youtubeFeedToken).ConfigureAwait(false);
                    play=play with {Title=HistoryText(item.Title??play.Title,512),Creator=HistoryText(item.ChannelName??play.Creator,256),ThumbnailUrl=item.ThumbnailUrl??play.ThumbnailUrl};
                }
                else
                {
                    var item=await searchResolver.GetVideoEntryAsync("https://www.youtube.com/watch?v="+play.VideoId,youtubeFeedToken).ConfigureAwait(false);
                    if(item is not null)play=play with {Title=HistoryText(item.Title,512),Creator=HistoryText(item.ChannelName,256),ThumbnailUrl=item.ThumbnailUrl??play.ThumbnailUrl};
                }
            }
            catch(OperationCanceledException) when(youtubeFeedToken.IsCancellationRequested){throw;}
            catch { /* Missing display metadata must not discard a qualified watch. */ }
            var result=await watchHistoryClient.SubmitAsync(session.Token,play,youtubeFeedToken).ConfigureAwait(false);
            completed=result.Completed;accepted=result.Accepted||result.Duplicate;
        }
        catch(OperationCanceledException) when(youtubeFeedToken.IsCancellationRequested){return;}
        catch { }
        if(Volatile.Read(ref disposeStarted)!=0)return;
        await Plugin.Framework.RunOnFrameworkThread(()=>
        {
            if(Volatile.Read(ref disposeStarted)!=0)return;
            if(completed)watchHistoryPending.RemoveAll(p=>p.Account==session.AccountId&&p.Play.EventId==initial.EventId);
            watchHistorySubmitting=false;nextHistorySubmit=DateTime.UtcNow.AddSeconds(completed?1:60);
            if(accepted&&profileHistoryTarget==session.AccountId)nextProfileHistoryRefresh=DateTime.MinValue;
        }).ConfigureAwait(false);
    }
    private void EnsureHistoryPrivacy(CharacterSession session)
    {
        if(historyPrivacyAccount!=session.AccountId||historyPrivacyToken!=session.Token)
        {
            historyPrivacyAccount=session.AccountId;historyPrivacyToken=session.Token;historyPrivacy="friends";
            historyPrivacyLoading=false;historyPrivacySaving=false;historyPrivacyError=null;historyPrivacyRetry=DateTime.MinValue;
        }
        if(historyPrivacyLoading||historyPrivacySaving||DateTime.UtcNow<historyPrivacyRetry)return;
        historyPrivacyLoading=true;historyPrivacyRetry=DateTime.MaxValue;
        _=UpdateHistoryPrivacyAsync(session,null);
    }
    private async Task UpdateHistoryPrivacyAsync(CharacterSession session,string? value)
    {
        WatchHistoryPrivacy? privacy=null;
        try {privacy=await watchHistoryClient.PrivacyAsync(session.Token,value,youtubeFeedToken).ConfigureAwait(false);}
        catch(OperationCanceledException) when(youtubeFeedToken.IsCancellationRequested){return;}
        catch { }
        if(Volatile.Read(ref disposeStarted)!=0)return;
        await Plugin.Framework.RunOnFrameworkThread(()=>
        {
            if(Volatile.Read(ref disposeStarted)!=0||CurrentSession?.Token!=session.Token||historyPrivacyToken!=session.Token)return;
            historyPrivacyLoading=historyPrivacySaving=false;
            if(privacy is not null)
            {
                historyPrivacy=privacy.Visibility;historyPrivacyError=null;historyPrivacyRetry=DateTime.UtcNow.AddMinutes(10);
                if(privacy.Visibility=="none")watchHistoryPending.Clear();
                if(profileHistoryTarget==session.AccountId)
                {
                    profileHistoryGeneration++;profileHistoryLoading=false;
                    profileHistoryPage=privacy.Visibility=="none"?new WatchHistoryPage("none",true,[],null):null;
                    nextProfileHistoryRefresh=DateTime.MinValue;
                }
            }
            else {historyPrivacyError=value is null?"Couldn't load watch history privacy. Try again.":"Privacy wasn't saved. Please try again.";historyPrivacyRetry=DateTime.UtcNow.AddSeconds(30);}
        }).ConfigureAwait(false);
    }
    private void DrawHistoryPrivacy(CharacterSession session)
    {
        EnsureHistoryPrivacy(session);
        var values=new[]{"everyone","friends","none"};var labels=new[]{"Everyone","Friends Only","No one"};
        ImGui.TextUnformatted("Recently watched visibility");
        using(UiDisabled.Scope(historyPrivacyLoading||historyPrivacySaving||historyPrivacyError is not null))
        {
            var selected=Array.IndexOf(values,historyPrivacy);
            // Use the shared nested popup so it stays above the settings popup.
            if(PartyPopupChoice("historyPrivacy",labels,ref selected)&&selected>=0)
            {historyPrivacySaving=true;historyPrivacyError=null;_=UpdateHistoryPrivacyAsync(session,values[selected]);}
        }
        using(UiFonts.PushRegularRelative(.88f))
        using(ImRaii.TextWrapPos(0))ImGui.TextColored(MutedText,historyPrivacySaving?"Saving…":"Changes save immediately. No one pauses recording and hides history. Earlier entries are kept.");
        if(historyPrivacyError is {} error)
        {
            using(ImRaii.TextWrapPos(0))ImGui.TextColored(Danger,error);
            if(ImGui.SmallButton("Retry##historyPrivacy")){historyPrivacyError=null;historyPrivacyRetry=DateTime.MinValue;EnsureHistoryPrivacy(session);}
        }
    }
    private void RefreshProfileHistory(CharacterSession session,string accountId,bool more=false)
    {
        if(profileHistoryTarget!=accountId||profileHistoryViewer!=session.Token)
        {
            profileHistoryGeneration++;profileHistoryTarget=accountId;profileHistoryViewer=session.Token;
            profileHistoryPage=null;profileHistoryError=null;historyRemovalError=null;profileHistoryLoading=false;nextProfileHistoryRefresh=DateTime.MinValue;
        }
        if(profileHistoryLoading||!more&&DateTime.UtcNow<nextProfileHistoryRefresh)return;
        var offset=more?profileHistoryPage?.NextOffset??0:0;
        if(!more&&accountId!=session.AccountId)profileHistoryPage=null;
        profileHistoryLoading=true;nextProfileHistoryRefresh=DateTime.UtcNow.AddSeconds(30);
        _=LoadProfileHistoryAsync(session,accountId,offset,++profileHistoryGeneration);
    }
    private async Task LoadProfileHistoryAsync(CharacterSession session,string target,int offset,int generation)
    {
        WatchHistoryPage? page=null;
        try {page=await watchHistoryClient.GetAsync(session.Token,target,offset,youtubeFeedToken).ConfigureAwait(false);}
        catch(OperationCanceledException) when(youtubeFeedToken.IsCancellationRequested){return;}
        catch { }
        if(Volatile.Read(ref disposeStarted)!=0)return;
        await Plugin.Framework.RunOnFrameworkThread(()=>
        {
            if(Volatile.Read(ref disposeStarted)!=0||CurrentSession?.Token!=session.Token||generation!=profileHistoryGeneration||profileHistoryTarget!=target)return;
            profileHistoryLoading=false;
            if(page is not null)
            {
                profileHistoryPage=offset>0&&!page.Hidden&&profileHistoryPage is {} previous&&!previous.Hidden
                    ?page with {Items=previous.Items.Concat(page.Items).DistinctBy(i=>i.Key).Take(100).ToArray()}:page;
                profileHistoryError=null;
            }
            else {profileHistoryPage=null;profileHistoryError="Watch history is unavailable. Try again shortly.";}
        }).ConfigureAwait(false);
    }
    private void DrawProfileHistoryHeader(CharacterSession session,AccountProfileDto profile,bool own)
    {
        var origin=ImGui.GetCursorScreenPos();var width=ImGui.GetContentRegionAvail().X;
        if(ProfileFriendsEnabled)RefreshProfileFriends(session, profile.AccountId);
        else profileFriendsTab=false;
        if(own)EnsureHistoryPrivacy(session);
        void Tab(string text,bool selected,Action click)
        {
            var start=ImGui.GetCursorScreenPos();var size=new Vector2(ImGui.CalcTextSize(text).X+Ui(14),Ui(28));
            if(ImGui.InvisibleButton("##profileTab"+text,size))click();
            var draw=ImGui.GetWindowDrawList();
            draw.AddText(start+UiVec(7,3),ImGui.GetColorU32(selected?EffectiveProfileTheme?.Accent??AccentHover:MutedText),text);
            if(selected)draw.AddRectFilled(start+new Vector2(0,size.Y-Ui(2)),start+size,ImGui.GetColorU32(EffectiveProfileTheme?.Accent??Accent));
        }
        using(UiFonts.PushRelative(1.10f,emphasis:true))
        {
            Tab("About",!profileHistoryTab&&!profileFriendsTab&&!profileSocialsTab,()=> {profileHistoryTab=false;profileFriendsTab=false;profileSocialsTab=false;});
            ImGui.SameLine(0,Ui(8));
            Tab("Socials",profileSocialsTab,()=> {profileHistoryTab=false;profileFriendsTab=false;profileSocialsTab=true;});
            if(ProfileFriendsEnabled)
            {
                ImGui.SameLine(0,Ui(8));
                Tab($"Friends ({profileFriendsCount?.ToString() ?? "…"})",profileFriendsTab,()=>
                {profileHistoryTab=false;profileFriendsTab=true;profileSocialsTab=false;});
            }
            ImGui.SameLine(0,Ui(8));
            Tab("Recently watched",profileHistoryTab,()=> {profileHistoryTab=true;profileFriendsTab=false;profileSocialsTab=false;nextProfileHistoryRefresh=DateTime.MinValue;});
        }
        if(InlineProfileEditing&&own)
        {
            ImGui.SetCursorScreenPos(origin+new Vector2(MathF.Max(0,width-Ui(30)),0));
            CapturePartyPopupBounds();
            if(GameLayoutButton("",FontAwesomeIcon.Cog,Ui(28),height:28))ImGui.OpenPopup("##historyPrivacySettings");
            if(ImGui.IsItemHovered())UiFonts.SetTooltip(ProfileFriendsEnabled?"Profile privacy":"Watch history privacy");
            var privacyPopupSize=ProfileFriendsEnabled
                ?new Vector2(Ui(340),Ui(240)+ImGui.GetTextLineHeightWithSpacing()*12)
                :new Vector2(Ui(340),Ui(170)+ImGui.GetTextLineHeightWithSpacing()*8);
            if(BeginPartyPopup("##historyPrivacySettings",privacyPopupSize))
            {
                using(UiFonts.PushRegularRelative(.88f))
                using(ImRaii.TextWrapPos(0))
                    ImGui.TextColored(MutedText,"Visibility changes apply instantly and do not require a save");
                ImGui.Dummy(UiVec(0,12));
                DrawHistoryPrivacy(session);
                if(ProfileFriendsEnabled)
                {
                    ImGui.Dummy(UiVec(0,12));
                    DrawFriendsPrivacy(session);
                }
                ImGui.Dummy(UiVec(0,12));
                DrawSocialsPrivacy(session);
                ImGui.EndPopup();
            }
            if(!profileHistoryTab&&!profileFriendsTab&&!profileSocialsTab)
            {
                ImGui.SetCursorScreenPos(origin+new Vector2(MathF.Max(0,width-Ui(66)),0));
                using var disabled=UiDisabled.Scope(inlineProfileSaving);
                if(DrawInlineProfileEditButton("##inlineAbout",inlineAboutEditing))
                {inlineAboutEditing=!inlineAboutEditing;inlineAboutFocus=inlineAboutEditing;}
            }
        }
        ImGui.SetCursorScreenPos(origin+UiVec(0,32));
        if(profileHistoryTarget!=profile.AccountId||profileHistoryViewer!=session.Token)
        {
            profileHistoryGeneration++;profileHistoryTarget=profile.AccountId;profileHistoryViewer=session.Token;
            profileHistoryPage=null;profileHistoryError=null;historyRemovalError=null;profileHistoryLoading=false;nextProfileHistoryRefresh=DateTime.MinValue;
        }
    }
    private void DrawProfileHistoryContent(CharacterSession session,string target)
    {
        RefreshProfileHistory(session,target);
        var available=ImGui.GetContentRegionAvail();
        using var padding=ImRaii.PushStyle(ImGuiStyleVar.WindowPadding,UiVec(2,2));
        using var list=UiFonts.Window(ImGui.GetFontSize(),ImRaii.Child("##profileHistoryList",new Vector2(available.X,MathF.Max(Ui(30),available.Y-Ui(2))),false));
        if(!list)return;
        if(profileHistoryPage is not {} page||page.Hidden||page.Items.Length==0)
        {
            var hidden=profileHistoryPage?.Hidden==true;
            var area=ImGui.GetContentRegionAvail();var start=ImGui.GetCursorScreenPos();var draw=ImGui.GetWindowDrawList();
            var accent=EffectiveProfileTheme?.Accent??AccentHover;
            draw.AddRectFilled(start,start+area,ImGui.GetColorU32(EffectiveProfileTheme is {} theme?ThemeInset(theme):FrameBg),Ui(8));
            var heading=hidden?"Watch history is private":profileHistoryError is not null?"History unavailable":profileHistoryLoading?"Loading watch history…":"Nothing watched yet";
            var detail=hidden?profileHistoryPage!.Visibility=="friends"?"This person shares their history with friends only.":"This person has chosen to keep their history private."
                :profileHistoryError??(profileHistoryLoading?"":"Qualified videos will appear here after watching.");
            var textWidth=MathF.Max(1,area.X-Ui(32));
            Vector2 headingSize,detailSize;
            using(UiFonts.PushRelative(1.02f,emphasis:true))headingSize=ImGui.CalcTextSize(heading,false,textWidth);
            using(UiFonts.PushRegularRelative(.88f))detailSize=string.IsNullOrEmpty(detail)?Vector2.Zero:ImGui.CalcTextSize(detail,false,textWidth);
            var iconHeight=Ui(24);
            var detailGap=detailSize.Y>0?Ui(5):0;
            var blockHeight=iconHeight+Ui(10)+headingSize.Y+detailGap+detailSize.Y;
            var y=MathF.Max(Ui(8),(area.Y-blockHeight)/2);
            UiFonts.DrawCenteredIcon(draw,Ui(23),start+new Vector2(area.X/2,y+iconHeight/2),ImGui.GetColorU32(accent),
                (hidden?FontAwesomeIcon.Lock:FontAwesomeIcon.History).ToIconString());
            y+=iconHeight+Ui(10);
            ImGui.SetCursorScreenPos(start+new Vector2(MathF.Max(Ui(16),(area.X-headingSize.X)/2),y));
            using(UiFonts.PushRelative(1.02f,emphasis:true))
            using(ImRaii.TextWrapPos(ImGui.GetCursorPosX()+textWidth))ImGui.TextUnformatted(heading);
            if(detailSize.Y>0)
            {
                ImGui.SetCursorScreenPos(start+new Vector2(MathF.Max(Ui(16),(area.X-detailSize.X)/2),y+headingSize.Y+detailGap));
                using(UiFonts.PushRegularRelative(.88f))
                using(ImRaii.TextWrapPos(ImGui.GetCursorPosX()+textWidth))ImGui.TextColored(MutedText,detail);
            }
            return;
        }
        if(historyRemovalError is {} removalError)
            using(ImRaii.TextWrapPos(0))ImGui.TextColored(Danger,removalError);
        foreach(var entry in page.Items)
        {
            using var id=ImRaii.PushId(entry.Key);
            var start=ImGui.GetCursorScreenPos();var width=ImGui.GetContentRegionAvail().X;
            var height=Ui(46);var draw=ImGui.GetWindowDrawList();
            draw.AddRectFilled(start,start+new Vector2(width,height),ImGui.GetColorU32(EffectiveProfileTheme is {} theme?ThemeInset(theme):FrameBg),Ui(6));
            var thumbSize=UiVec(66,38);var thumbOrigin=start+UiVec(5,4);var texture=thumbnails.Get(entry.ThumbnailUrl);
            if(texture is not null)draw.AddImageRounded(texture.Handle,thumbOrigin,thumbOrigin+thumbSize,Vector2.Zero,Vector2.One,uint.MaxValue,Ui(4));
            else UiFonts.DrawCenteredIcon(draw,Ui(20),thumbOrigin+thumbSize/2,ImGui.GetColorU32(MutedText),FontAwesomeIcon.Film.ToIconString());
            var textWidth=MathF.Max(Ui(20),width-Ui(172));
            ImGui.SetCursorScreenPos(start+UiVec(80,5));
            using(UiFonts.PushRelative(.98f,emphasis:true))ImGui.TextUnformatted(TruncateToWidth(entry.Title,textWidth));
            ImGui.SetCursorScreenPos(start+UiVec(80,26));
            var age=DateTimeOffset.UtcNow.ToUnixTimeSeconds()-entry.WatchedAtUnix;
            var time=age<60?"Just now":age<3600?$"{age/60}m ago":age<86400?$"{age/3600}h ago":age<172800?"Yesterday":$"{age/86400} days ago";
            using(UiFonts.PushRegularRelative(.82f))ImGui.TextColored(MutedText,TruncateToWidth(entry.Source+" · "+time,textWidth));
            ImGui.SetCursorScreenPos(start+new Vector2(MathF.Max(Ui(80),width-Ui(83)),Ui(6)));
            // Icon-only buttons share an empty label; keep their ImGui IDs distinct.
            using(ImRaii.PushId("historyPlay"))
                if(GameLayoutButton("",FontAwesomeIcon.Play,Ui(32),height:34))
                    HandlePlayNow(new VideoQueueEntry(entry.Url,entry.Title,entry.Creator,entry.Duration,entry.ThumbnailUrl));
            ImGui.SameLine(0,Ui(5));
            using(ImRaii.PushId("historyOptions"))
                DrawUnifiedVideoOptions(new VideoSearchEntry(entry.Title,entry.Url,entry.Creator,entry.Duration,entry.ThumbnailUrl),
                    session.AccountId==target?()=>BeginRemoveProfileHistory(session,target,entry.Key):null,historyRemovalKey is not null);
            ImGui.SetCursorScreenPos(start+new Vector2(0,height+Ui(4)));
        }
        if(page.NextOffset is not null)
            using(UiDisabled.Scope(profileHistoryLoading))
                if(ImGui.Button(profileHistoryLoading?"Loading…":"Show older videos",UiVec(-1,30)))RefreshProfileHistory(session,target,more:true);
    }

    private void BeginRemoveProfileHistory(CharacterSession session,string owner,string key)
    {
        if(CurrentSession?.Token!=session.Token||session.AccountId!=owner||profilePageAccountId!=owner||historyRemovalKey is not null)return;
        historyRemovalKey=key;historyRemovalError=null;
        // Invalidate reads already in flight so they cannot restore a deleted row.
        profileHistoryGeneration++;profileHistoryLoading=false;
        nextProfileHistoryRefresh=DateTime.MaxValue;
        _=RemoveProfileHistoryAsync(session,owner,key);
    }
    private async Task RemoveProfileHistoryAsync(CharacterSession session,string owner,string key)
    {
        var removed=false;
        try {await watchHistoryClient.RemoveAsync(session.Token,owner,key,youtubeFeedToken).ConfigureAwait(false);removed=true;}
        catch(OperationCanceledException) when(youtubeFeedToken.IsCancellationRequested){return;}
        catch { }
        if(Volatile.Read(ref disposeStarted)!=0)return;
        await Plugin.Framework.RunOnFrameworkThread(()=>
        {
            if(Volatile.Read(ref disposeStarted)!=0)return;
            if(historyRemovalKey==key)historyRemovalKey=null;
            if(CurrentSession?.Token!=session.Token||profileHistoryTarget!=owner||profileHistoryViewer!=session.Token)return;
            profileHistoryGeneration++;profileHistoryLoading=false;
            nextProfileHistoryRefresh=DateTime.MinValue;
            if(removed&&profileHistoryPage is {} page)
                profileHistoryPage=page with {Items=page.Items.Where(i=>i.Key!=key).ToArray(),NextOffset=page.NextOffset is {} offset?Math.Max(0,offset-1):null};
            historyRemovalError=removed?null:"Couldn't remove this video. Please try again.";
        }).ConfigureAwait(false);
    }

}
