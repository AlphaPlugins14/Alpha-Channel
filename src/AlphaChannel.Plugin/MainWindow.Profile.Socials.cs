using AlphaChannel.Plugin.Auth;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private readonly SocialsClient socialsClient=new();
    private bool profileSocialsTab,socialsOwnLoading,socialsSaving,socialsPageLoading;
    private ProfileSocials? socialsOwnDocument,socialsPage;
    private string? socialsOwnToken,socialsOwnError,socialsTarget,socialsViewer,socialsPageError;
    private DateTime socialsOwnRefresh,socialsPageRefresh;
    private int socialsPageGeneration;
    private bool socialEditorOpen,socialEditorSaved;
    private string? socialEditorToken,socialEditorId;
    private int socialEditorPlatform;
    private string socialEditorValue="",socialEditorLabel="";

    private void EnsureSocialsOwn(CharacterSession session)
    {
        if(socialsOwnToken!=session.Token)
        {
            socialsOwnToken=session.Token;socialsOwnDocument=null;socialsOwnError=null;
            socialsOwnLoading=socialsSaving=false;socialsOwnRefresh=DateTime.MinValue;
        }
        if(socialsOwnLoading||socialsSaving||DateTime.UtcNow<socialsOwnRefresh)return;
        socialsOwnLoading=true;socialsOwnRefresh=DateTime.UtcNow.AddMinutes(5);
        _=LoadSocialsOwnAsync(session);
    }

    private async Task LoadSocialsOwnAsync(CharacterSession session)
    {
        ProfileSocials? document=null;
        try{document=await socialsClient.GetAsync(session.Token,null,youtubeFeedToken).ConfigureAwait(false);}catch(Exception){ }
        if(Volatile.Read(ref disposeStarted)!=0||youtubeFeedToken.IsCancellationRequested)return;
        await Plugin.Framework.RunOnFrameworkThread(()=>
        {
            if(Volatile.Read(ref disposeStarted)!=0||CurrentSession?.Token!=session.Token||socialsOwnToken!=session.Token)return;
            socialsOwnLoading=false;
            if(document is not null){socialsOwnDocument=document;socialsOwnError=null;}
            else{socialsOwnError="Socials couldn't be loaded. Please try again.";socialsOwnRefresh=DateTime.UtcNow.AddSeconds(30);}
        }).ConfigureAwait(false);
    }

    private void SaveSocials(CharacterSession session,ProfileSocials document,bool closeEditor=false)
    {
        if(socialsSaving||socialsOwnLoading||socialsOwnToken!=session.Token)return;
        socialsSaving=true;socialsOwnError=null;
        _=SaveSocialsAsync(session,document,closeEditor);
    }

    private async Task SaveSocialsAsync(CharacterSession session,ProfileSocials document,bool closeEditor)
    {
        ProfileSocials? saved=null;string? error=null;
        try{saved=await socialsClient.SaveAsync(session.Token,document,youtubeFeedToken).ConfigureAwait(false);}
        catch(HttpRequestException ex){error=ex.StatusCode==System.Net.HttpStatusCode.Conflict?
            "Socials changed elsewhere. Reload them before saving again.":ex.StatusCode==System.Net.HttpStatusCode.BadRequest?
            "Check that the username or HTTPS profile link matches the selected platform.":"Socials weren't saved. Please try again.";}
        catch(Exception){error="Socials weren't saved. Please try again.";}
        if(Volatile.Read(ref disposeStarted)!=0||youtubeFeedToken.IsCancellationRequested)return;
        await Plugin.Framework.RunOnFrameworkThread(()=>
        {
            if(Volatile.Read(ref disposeStarted)!=0||CurrentSession?.Token!=session.Token||socialsOwnToken!=session.Token)return;
            socialsSaving=false;socialsOwnError=error;
            if(saved is null)return;
            socialsOwnDocument=saved;socialsOwnRefresh=DateTime.UtcNow.AddMinutes(5);
            if(closeEditor&&socialEditorToken==session.Token)socialEditorSaved=true;
        }).ConfigureAwait(false);
    }

    private void DrawSocialsPrivacy(CharacterSession session)
    {
        EnsureSocialsOwn(session);
        ImGui.TextUnformatted("Socials visibility");
        var values=new[]{"everyone","friends","none"};var labels=new[]{"Everyone","Friends Only","No one"};
        var selected=Array.IndexOf(values,socialsOwnDocument?.Visibility??"friends");
        using(UiDisabled.Scope(socialsOwnLoading||socialsSaving||socialsOwnDocument is null))
            if(PartyPopupChoice("socialsPrivacy",labels,ref selected)&&selected>=0&&socialsOwnDocument is {} document)
                SaveSocials(session,document with{Visibility=values[selected]});
        using(UiFonts.PushRegularRelative(.88f))
        using(ImRaii.TextWrapPos(0))ImGui.TextColored(MutedText,socialsSaving?"Saving…":"Changes save immediately. Only socials you add are shown.");
        DrawSocialsOwnError(session);
    }

    private void DrawSocialsOwnError(CharacterSession session)
    {
        if(socialsOwnError is not {} error)return;
        using(ImRaii.TextWrapPos(0))ImGui.TextColored(Danger,error);
        using(UiDisabled.Scope(socialsOwnLoading||socialsSaving))
            if(ImGui.SmallButton("Reload socials")){socialsOwnRefresh=DateTime.MinValue;EnsureSocialsOwn(session);}
    }

    private void RefreshProfileSocials(CharacterSession session,string target)
    {
        if(socialsTarget!=target||socialsViewer!=session.Token)
        {
            socialsPageGeneration++;socialsTarget=target;socialsViewer=session.Token;
            socialsPage=null;socialsPageError=null;socialsPageLoading=false;socialsPageRefresh=DateTime.MinValue;
        }
        if(socialsPageLoading||DateTime.UtcNow<socialsPageRefresh)return;
        socialsPageLoading=true;socialsPageRefresh=DateTime.UtcNow.AddSeconds(30);
        _=LoadProfileSocialsAsync(session,target,++socialsPageGeneration);
    }

    private async Task LoadProfileSocialsAsync(CharacterSession session,string target,int generation)
    {
        ProfileSocials? page=null;
        try{page=await socialsClient.GetAsync(session.Token,target,youtubeFeedToken).ConfigureAwait(false);}catch(Exception){ }
        if(Volatile.Read(ref disposeStarted)!=0||youtubeFeedToken.IsCancellationRequested)return;
        await Plugin.Framework.RunOnFrameworkThread(()=>
        {
            if(Volatile.Read(ref disposeStarted)!=0||CurrentSession?.Token!=session.Token||generation!=socialsPageGeneration)return;
            socialsPageLoading=false;socialsPage=page; // Fail closed instead of retaining private links after a failed permission check.
            socialsPageError=page is null?"Socials couldn't be loaded. Please try again shortly.":null;
        }).ConfigureAwait(false);
    }

    private void OpenSocialEditor(CharacterSession session,ProfileSocial? entry=null)
    {
        socialEditorId=entry?.Id;socialEditorPlatform=entry is null?0:Array.FindIndex(SocialsClient.Platforms,p=>p.Id==entry.Platform);
        socialEditorValue=entry?.Value??"";socialEditorLabel=entry?.Label??"";
        socialEditorToken=session.Token;socialEditorOpen=true;socialEditorSaved=false;socialsOwnError=null;
    }

    private void DrawProfileSocialsContent(CharacterSession session,string target,bool own)
    {
        if(own)EnsureSocialsOwn(session);else RefreshProfileSocials(session,target);
        var page=own?socialsOwnDocument:socialsPage;
        var editing=own&&InlineProfileEditing;
        if(editing)
        {
            using(UiDisabled.Scope(socialsOwnLoading||socialsSaving||page is null||page.Entries.Length>=20))
                if(GameLayoutButton("Add social",FontAwesomeIcon.Plus,Ui(126),height:28))OpenSocialEditor(session);
            ImGui.SameLine();
            using(UiFonts.PushRegularRelative(.8f))ImGui.TextColored(MutedText,socialsSaving?"Saving…":"Social changes save immediately.");
        }
        var available=ImGui.GetContentRegionAvail();
        using var padding=ImRaii.PushStyle(ImGuiStyleVar.WindowPadding,UiVec(2,2));
        using var list=UiFonts.Window(ImGui.GetFontSize(),ImRaii.Child("##profileSocialsList",new Vector2(available.X,Math.Max(Ui(30),available.Y-Ui(2))),false));
        if(!list)return;
        if(own)DrawSocialsOwnError(session);
        if(page is null||page.Hidden||page.Entries.Length==0)
        {
            var loading=own?socialsOwnLoading:socialsPageLoading;
            var error=own?socialsOwnError:socialsPageError;
            DrawSocialsPlaceholder(page?.Hidden==true?"Socials are hidden":loading?"Loading socials…":error is not null?"Socials unavailable":"No socials added yet",
                page?.Hidden==true?page.Visibility=="friends"?"This person shares their socials with friends only.":"This person has chosen to keep their socials private.":
                error??(loading?"":own?"Add the places your friends can find you from Edit profile.":"This person hasn't added any socials yet."),page?.Hidden==true);
            return;
        }
        var columns=ImGui.GetContentRegionAvail().X>=Ui(380)?2:1;
        var cardWidth=(ImGui.GetContentRegionAvail().X-Ui(6)*(columns-1))/columns;
        for(var i=0;i<page.Entries.Length;i++)
        {
            if(i%columns!=0)ImGui.SameLine(0,Ui(6));
            var entry=page.Entries[i];var platform=SocialsClient.Platforms.First(p=>p.Id==entry.Platform);
            using var id=ImRaii.PushId(entry.Id);
            var start=ImGui.GetCursorScreenPos();var size=new Vector2(cardWidth,Ui(48));var draw=ImGui.GetWindowDrawList();
            var clicked=ImGui.InvisibleButton("open",new Vector2(cardWidth-(editing?Ui(64):0),size.Y));
            var hovered=ImGui.IsItemHovered();
            if(hovered){ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);UiFonts.SetTooltip((entry.Url is null?"Copy ":"Open ")+platform.Name+"\n"+entry.Value);}
            if(clicked){if(entry.Url is null)ImGui.SetClipboardText(entry.Value);else Dalamud.Utility.Util.OpenLink(entry.Url);}
            draw.AddRectFilled(start,start+size,ImGui.GetColorU32(hovered?CardBgHover:EffectiveProfileTheme is {} theme?ThemeInset(theme):FrameBg),Ui(6));
            draw.AddRect(start,start+size,ImGui.GetColorU32(BorderSubtle),Ui(6));
            DrawSocialPlatformIcon(draw,start+UiVec(9,12),Ui(24),platform);
            var textWidth=Math.Max(Ui(20),cardWidth-Ui(editing?110:78));
            using(UiFonts.PushRelative(.92f,emphasis:true))draw.AddText(start+UiVec(43,6),ImGui.GetColorU32(Vector4.One),TruncateToWidth(platform.Name,textWidth));
            using(UiFonts.PushRegularRelative(.82f))draw.AddText(start+UiVec(43,27),ImGui.GetColorU32(MutedText),TruncateToWidth(entry.Label.Length>0?entry.Label:entry.Value,textWidth));
            if(editing)
            {
                ImGui.SetCursorScreenPos(start+new Vector2(cardWidth-Ui(59),Ui(10)));
                using(UiDisabled.Scope(socialsSaving||socialsOwnLoading))
                {
                    if(GameLayoutButton("",FontAwesomeIcon.PencilAlt,Ui(25),height:28))OpenSocialEditor(session,entry);
                    if(ImGui.IsItemHovered())UiFonts.SetTooltip("Edit social");
                    ImGui.SameLine(0,Ui(3));
                    using var remove=ImRaii.PushId("remove");
                    if(GameLayoutButton("",FontAwesomeIcon.Times,Ui(25),height:28))SaveSocials(session,page with{Entries=page.Entries.Where(e=>e.Id!=entry.Id).ToArray()});
                    if(ImGui.IsItemHovered())UiFonts.SetTooltip("Remove social");
                }
            }
            else UiFonts.DrawCenteredIcon(draw,Ui(14),start+new Vector2(cardWidth-Ui(18),Ui(24)),ImGui.GetColorU32(MutedText),
                (entry.Url is null?FontAwesomeIcon.Copy:FontAwesomeIcon.ExternalLinkAlt).ToIconString());
            ImGui.SetCursorScreenPos(start);ImGui.Dummy(size);
        }
    }

    private void DrawSocialsPlaceholder(string heading,string detail,bool hidden)
    {
        var start=ImGui.GetCursorScreenPos();var area=ImGui.GetContentRegionAvail();var draw=ImGui.GetWindowDrawList();
        draw.AddRectFilled(start,start+area,ImGui.GetColorU32(EffectiveProfileTheme is {} theme?ThemeInset(theme):FrameBg),Ui(7));
        var textWidth=Math.Max(Ui(30),area.X-Ui(24));
        using var font=UiFonts.PushRegularRelative(.88f);
        var detailSize=ImGui.CalcTextSize(detail,false,textWidth);var headingSize=ImGui.CalcTextSize(heading);
        var y=Math.Max(Ui(6),(area.Y-Ui(38)-headingSize.Y-detailSize.Y)/2);
        UiFonts.DrawCenteredIcon(draw,Ui(22),start+new Vector2(area.X/2,y+Ui(11)),ImGui.GetColorU32(EffectiveProfileTheme?.Accent??AccentHover),
            (hidden?FontAwesomeIcon.Lock:FontAwesomeIcon.Link).ToIconString());
        draw.AddText(start+new Vector2(Math.Max(Ui(12),(area.X-headingSize.X)/2),y+Ui(30)),ImGui.GetColorU32(Vector4.One),heading);
        var text=start+new Vector2(Math.Max(Ui(12),(area.X-detailSize.X)/2),y+Ui(35)+headingSize.Y);
        DrawWrappedLines(draw,text,textWidth,ImGui.GetTextLineHeight(),3,ImGui.GetColorU32(MutedText),detail);
    }

    private void DrawProfileSocialEditor()
    {
        if(!socialEditorOpen)return;
        if(CurrentSession is not {} session||session.Token!=socialEditorToken||!InlineProfileEditing||socialEditorSaved)
        {socialEditorOpen=false;return;}
        // Use the shared fixed-size themed overlay, not ImGui's native auto-sizing modal.
        DrawEventWindow("profileSocialEditor",socialEditorId is null?"Add a social account":"Edit social account",UiVec(470,440),
            ()=>DrawSocialEditorBody(session),()=>socialEditorOpen=false,()=>DrawSocialEditorFooter(session));
    }

    private void DrawSocialEditorBody(CharacterSession session)
    {
        using(UiDisabled.Scope(socialsSaving))
        {
            ImGui.TextUnformatted("Platform");
            var platformOrigin=ImGui.GetCursorScreenPos();
            var platformWidth=ImGui.GetContentRegionAvail().X;
            var selectorHeight=ImGui.GetFrameHeight();
            ImGui.SetCursorScreenPos(platformOrigin+UiVec(34,0));
            ImGui.SetNextItemWidth(Math.Max(Ui(80),platformWidth-Ui(34)));
            if(ImGui.BeginCombo("##socialPlatform",SocialsClient.Platforms[socialEditorPlatform].Name,ImGuiComboFlags.HeightRegular))
            {
                for(var i=0;i<SocialsClient.Platforms.Length;i++)
                    if(ImGui.Selectable(SocialsClient.Platforms[i].Name,i==socialEditorPlatform))socialEditorPlatform=i;
                ImGui.EndCombo();
            }
            var platform=SocialsClient.Platforms[socialEditorPlatform];
            DrawSocialPlatformIcon(ImGui.GetWindowDrawList(),platformOrigin+new Vector2(0,Math.Max(0,(selectorHeight-Ui(24))/2)),Ui(24),platform);
            ImGui.SetCursorScreenPos(new Vector2(platformOrigin.X,ImGui.GetCursorScreenPos().Y));
            ImGui.TextUnformatted(platform.Username?"Username or HTTPS profile link":"HTTPS profile link");ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##socialValue",platform.Username?"Your username or profile link":"https://…",ref socialEditorValue,1025);
            ImGui.TextUnformatted("Display label (optional)");ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##socialLabel","For example: My playlists",ref socialEditorLabel,81);
            using(UiFonts.PushRegularRelative(.85f))
            using(ImRaii.TextWrapPos(0))ImGui.TextColored(MutedText,platform.Username?"Usernames can be copied. Profile links open in your browser.":"Paste the profile, channel or playlist link you want to share.");
            DrawSocialsOwnError(session);
        }
    }

    private void DrawSocialEditorFooter(CharacterSession session)
    {
        using(UiDisabled.Scope(socialsSaving||string.IsNullOrWhiteSpace(socialEditorValue)||socialsOwnDocument is null||socialsOwnLoading))
            if(GameLayoutButton(socialsSaving?"Saving…":"Save social",FontAwesomeIcon.Check,Ui(150),true,height:34)&&socialsOwnDocument is {} document)
            {
                var platform=SocialsClient.Platforms[socialEditorPlatform];
                var entry=new ProfileSocial(socialEditorId??Guid.NewGuid().ToString("D"),platform.Id,socialEditorValue.Trim(),socialEditorLabel.Trim());
                var entries=document.Entries.Where(e=>e.Id!=entry.Id).ToList();
                var index=socialEditorId is null?entries.Count:Array.FindIndex(document.Entries,e=>e.Id==socialEditorId);
                entries.Insert(Math.Clamp(index,0,entries.Count),entry);
                SaveSocials(session,document with{Entries=entries.ToArray()},closeEditor:true);
            }
        ImGui.SameLine();
        using(UiDisabled.Scope(socialsSaving))
            if(GameLayoutButton("Cancel",FontAwesomeIcon.Times,Ui(100),height:34))socialEditorOpen=false;
    }

    private static void DrawSocialPlatformIcon(ImDrawListPtr draw,Vector2 origin,float size,SocialPlatform platform)
    {
        var color=ImGui.GetColorU32(Hex(platform.Color));var center=origin+new Vector2(size/2);
        if(platform.Id=="discord"){DrawSidebarDiscordIcon(origin+new Vector2(0,size*.1f),new Vector2(size,size*.8f),Hex(platform.Color),CardBg);return;}
        if(platform.Id=="youtube")
        {
            draw.AddRectFilled(origin+new Vector2(0,size*.15f),origin+new Vector2(size,size*.85f),color,size*.2f);
            draw.AddTriangleFilled(origin+new Vector2(size*.4f,size*.3f),origin+new Vector2(size*.4f,size*.7f),origin+new Vector2(size*.75f,size*.5f),0xFFFFFFFF);return;
        }
        if(platform.Id=="twitch")
        {
            draw.AddRectFilled(origin,origin+new Vector2(size,size*.8f),color,size*.1f);
            draw.AddTriangleFilled(origin+new Vector2(size*.2f,size*.75f),origin+new Vector2(size*.2f,size),origin+new Vector2(size*.5f,size*.75f),color);
            for(var i=0;i<2;i++)draw.AddRectFilled(origin+new Vector2(size*(.3f+i*.3f),size*.2f),origin+new Vector2(size*(.43f+i*.3f),size*.55f),0xFFFFFFFF);return;
        }
        if(platform.Id=="spotify")
        {
            draw.AddCircleFilled(center,size/2,color);
            for(var i=0;i<3;i++){var y=size*(.32f+i*.18f);draw.AddBezierCubic(origin+new Vector2(size*.23f,y),origin+new Vector2(size*.4f,y-size*.12f),origin+new Vector2(size*.6f,y-size*.12f),origin+new Vector2(size*.77f,y),ImGui.GetColorU32(CardBg),size*.065f);}
            return;
        }
        if(platform.Id=="telegram")
        {
            draw.AddCircleFilled(center,size/2,color);
            draw.AddTriangleFilled(origin+new Vector2(size*.2f,size*.45f),origin+new Vector2(size*.82f,size*.2f),origin+new Vector2(size*.65f,size*.8f),0xFFFFFFFF);return;
        }
        if(platform.Id=="steam")
        {
            // White crank and connecting rod on Steam's dark blue disc.
            // Draw the joint cutouts last so the rod cannot fill either ring.
            var background=ImGui.GetColorU32(Hex(0x173C55));
            draw.AddCircleFilled(center,size/2,background,40);
            var crank=origin+new Vector2(size*.31f,size*.73f);
            var bearing=origin+new Vector2(size*.70f,size*.34f);
            draw.AddLine(crank,bearing,0xFFFFFFFF,size*.25f);
            draw.AddLine(origin+new Vector2(size*.055f,size*.60f),crank,0xFFFFFFFF,size*.11f);
            draw.AddCircleFilled(bearing,size*.235f,0xFFFFFFFF,32);
            draw.AddCircleFilled(bearing,size*.172f,background,32);
            draw.AddCircleFilled(crank,size*.153f,0xFFFFFFFF,28);
            draw.AddCircleFilled(crank,size*.103f,background,28);
            // The small crank's arm runs into its centre, unlike the open bearing.
            draw.AddLine(origin+new Vector2(size*.055f,size*.60f),crank,0xFFFFFFFF,size*.085f);
            return;
        }
        draw.AddCircleFilled(center,size/2,ImGui.GetColorU32(Hex(platform.Color) with{W=.2f}));
        if(platform.Id=="website")UiFonts.DrawCenteredIcon(draw,size*.7f,center,color,FontAwesomeIcon.Globe.ToIconString());
        else{var mark=platform.Id switch{"playstation"=>"PS","xbox"=>"X","lastfm"=>"fm",_=>platform.Name[..1]};draw.AddText(center-ImGui.CalcTextSize(mark)/2,color,mark);}
    }
}
