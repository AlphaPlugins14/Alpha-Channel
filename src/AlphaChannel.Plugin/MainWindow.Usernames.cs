using AlphaChannel.Plugin.Auth;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private readonly UsernameStyleClient usernameStyleClient=new();
    private readonly Dictionary<string,(UsernameStyle Style,DateTime Refresh)> usernameStyles=new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string,string> usernameAccounts=new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> usernameStylePending=new(StringComparer.OrdinalIgnoreCase);
    private string? usernameStyleToken;
    private DateTime usernameNextFetch,usernameNextOwnRefresh;
    private bool usernameStyleFetching,usernameStyleSaving,usernameOwnLoaded;
    private string? usernameStyleError;
    private long usernameSaveRevision;
    private string usernameLastRequestedEffect="solid";
    private int usernamePublishFailures;
    private void RegisterUsername(string? id,string? name)
    {
        if (!Guid.TryParse(id,out var guid)) return;
        id=guid.ToString("D");
        if (!string.IsNullOrWhiteSpace(name)) usernameAccounts[name]=id;
        if (!usernameStyles.TryGetValue(id,out var cached) || DateTime.UtcNow>=cached.Refresh) usernameStylePending.Add(id);
    }

    internal void TickUsernameStyles()
    {
        TickAccountVerification();
        if (Volatile.Read(ref disposeStarted) != 0) return;
        FlushUsernameStyleSave();
        if (CurrentSession is not { } session) return;
        if (usernameStyleToken!=session.Token)
        {
            usernameStyleToken=session.Token; usernameStyles.Clear(); usernameStylePending.Clear();usernameAccounts.Clear();
            usernameOwnLoaded=false;usernameStyleFetching=false;usernameStyleSaving=false;usernameNextFetch=DateTime.MinValue;
            Plugin.Cfg.UsernameStyleCache ??= new();
            foreach(var entry in Plugin.Cfg.UsernameStyleCache.Values.Take(2048))
                if(entry is not null && Guid.TryParse(entry.AccountId,out _) && entry.UpdatedAt>0 && entry.UpdatedAt<253402298000)
                {
                    var style=new UsernameStyle(entry.AccountId,entry.Colour,entry.Effect,entry.Until);
                    usernameStyles[entry.AccountId]=(style,UsernameStyleRefreshAt(style,DateTimeOffset.FromUnixTimeSeconds(entry.UpdatedAt).UtcDateTime));
                }
            RegisterUsername(session.AccountId,session.DisplayName);
            usernameStylePending.Add(session.AccountId);
        }
        RegisterUsername(session.AccountId,session.DisplayName);
        if (usernameOwnLoaded && !usernameStyleSaving && (DateTime.UtcNow>=usernameNextOwnRefresh || SavedAlphaChatUsernameEffect(session.AccountId)!=usernameLastRequestedEffect))
            _=PublishUsernameStyleAsync(session);
        if (usernameStyleFetching || usernameStylePending.Count==0 || DateTime.UtcNow<usernameNextFetch) return;
        var ids=usernameStylePending.Take(80).ToArray();
        foreach(var id in ids) usernameStylePending.Remove(id);
        usernameStyleFetching=true;usernameNextFetch=DateTime.UtcNow.AddSeconds(2);
        _=FetchUsernameStylesAsync(session,ids,usernameSaveRevision);
    }

    private async Task FetchUsernameStylesAsync(CharacterSession session,string[] ids,long revision)
    {
        UsernameStyle[]? result=null;
        try {result=await usernameStyleClient.ReadAsync(session.Token,ids,youtubeFeedToken).ConfigureAwait(false);} catch(Exception) { }
        await Plugin.Framework.RunOnFrameworkThread(()=>
        {
            if (Volatile.Read(ref disposeStarted)!=0 || CurrentSession?.Token!=session.Token) return;
            usernameStyleFetching=false;
            if(result is null) usernameNextFetch=DateTime.UtcNow.AddMinutes(2);
            foreach(var id in ids)
            {
                usernameStylePending.Remove(id);
                if(id==session.AccountId && revision!=usernameSaveRevision) continue;
                var style=result?.FirstOrDefault(s=>s.AccountId.Equals(id,StringComparison.OrdinalIgnoreCase));
                if (style is not null) usernameStyles[id]=(style,UsernameStyleRefreshAt(style,DateTime.UtcNow));
                else if(!usernameStyles.ContainsKey(id)) usernameStyles[id]=(new(id,null,"solid",0),DateTime.UtcNow.AddMinutes(result is null ? 2 : 30));
                else usernameStyles[id]=(usernameStyles[id].Style,DateTime.UtcNow.AddMinutes(result is null ? 2 : 30));
                if(result is not null) CacheUsernameStyle(usernameStyles[id].Style);
                if(id==session.AccountId && !usernameOwnLoaded && result is not null)
                {
                    usernameOwnLoaded=true;
                    if(style is not null)
                    {
                        Plugin.Cfg.AlphaChatUsernameColours ??= new();Plugin.Cfg.AlphaChatUsernameEffects ??= new();
                        Plugin.Cfg.UsernameStylePreferencesSaved ??= new();
                        UsernameStylePreferences.RestoreIfMissing(id,style.NameColourHex,style.SelectedEffect,
                            Plugin.Cfg.AlphaChatUsernameColours,Plugin.Cfg.AlphaChatUsernameEffects,Plugin.Cfg.UsernameStylePreferencesSaved);
                        QueueUsernameStyleSave();
                    }
                    usernameNextOwnRefresh=DateTime.MinValue;
                }
            }
            if(result is not null) QueueUsernameStyleSave();
        });
    }

    private async Task PublishUsernameStyleAsync(CharacterSession session)
    {
        if(usernameStyleSaving) return;
        usernameStyleSaving=true;usernameStyleError=null;
        var requestedEffect=SavedAlphaChatUsernameEffect(session.AccountId);
        usernameLastRequestedEffect=requestedEffect;
        usernameNextOwnRefresh=DateTime.MaxValue;
        var revision=++usernameSaveRevision;
        try
        {
            var result=await usernameStyleClient.SaveAsync(session.Token,SavedAlphaChatUsernameColour(session.AccountId),
                requestedEffect,youtubeFeedToken).ConfigureAwait(false);
            await Plugin.Framework.RunOnFrameworkThread(()=>
            {
                if(CurrentSession?.Token!=session.Token || Volatile.Read(ref disposeStarted)!=0 || revision!=usernameSaveRevision) return;
                if(result?.AccountId==session.AccountId)
                {
                    usernamePublishFailures=0;
                    usernameStyles[session.AccountId]=(result,UsernameStyleRefreshAt(result,DateTime.UtcNow));
                    usernameNextOwnRefresh=UsernameStylePreferences.PublishRefreshAt(DateTime.UtcNow,
                        requestedEffect,result.NameEffect,result.NameEffectUntil);
                    CacheUsernameStyle(result);QueueUsernameStyleSave();
                }
                else usernameNextOwnRefresh=DateTime.UtcNow.AddMinutes(2);
            });
        }
        catch(Exception)
        {
            await Plugin.Framework.RunOnFrameworkThread(()=>
            {
                if(CurrentSession?.Token!=session.Token) return;
                usernameStyleError="Couldn't share your username style. Your choice is saved locally; retry to publish it.";
                usernameNextOwnRefresh=DateTime.UtcNow.AddMinutes(Math.Min(30,2*Math.Pow(2,Math.Min(4,usernamePublishFailures++))));
            });
        }
        finally {await Plugin.Framework.RunOnFrameworkThread(()=> {if(CurrentSession?.Token==session.Token) usernameStyleSaving=false;});}
    }

    private void CacheUsernameStyle(UsernameStyle style)
    {
        Plugin.Cfg.UsernameStyleCache ??= new();
        Plugin.Cfg.UsernameStyleCache[style.AccountId]=new(style.AccountId,style.NameColourHex,style.NameEffect,style.NameEffectUntil,DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        if(Plugin.Cfg.UsernameStyleCache.Count>2048)
            foreach(var id in Plugin.Cfg.UsernameStyleCache.OrderBy(p=>p.Value.UpdatedAt).Take(Plugin.Cfg.UsernameStyleCache.Count-2048).Select(p=>p.Key).ToArray()) Plugin.Cfg.UsernameStyleCache.Remove(id);
    }

    private static DateTime UsernameStyleRefreshAt(UsernameStyle style,DateTime fetched)
    {
        var refresh=fetched.AddMinutes(30);
        if(style.NameEffect!="solid" && style.NameEffectUntil>0 && style.NameEffectUntil<253402300799000)
        {
            var expiry=DateTimeOffset.FromUnixTimeMilliseconds(style.NameEffectUntil).UtcDateTime;
            if(expiry<refresh) refresh=expiry;
        }
        return refresh;
    }

    private (Vector4 Colour,string Effect) UsernameAppearance(string? id,Vector4 fallback)
    {
        if(!Guid.TryParse(id,out var guid)) return(fallback,"solid");
        id=guid.ToString("D"); RegisterUsername(id,null);
        var colour=id==CurrentSession?.AccountId ? SavedAlphaChatUsernameColour(id) :
            usernameStyles.TryGetValue(id,out var cached) ? cached.Style.NameColourHex : alphaChatUsernameColourOverrides.GetValueOrDefault(id);
        var effect=usernameStyles.TryGetValue(id,out var entry)
            ? AlphaChatNameEffects.Effective(entry.Style.NameEffect,entry.Style.NameEffectUntil,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            : alphaChatUsernameEffectOverrides.TryGetValue(id,out var live)
                ? AlphaChatNameEffects.Effective(live.Effect,live.Until,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) : "solid";
        if(id==CurrentSession?.AccountId && CurrentVerifiedPatreonTier()==0) effect="solid";
        try {return(colour is null ? fallback : ParseAvatarColor(AlphaChatClient.NormalizeUsernameColour(colour)),effect);}
        catch(ArgumentException) {return(fallback,effect);}
    }

    private string? FindUsernameAccount(string text)
    {
        if(usernameAccounts.TryGetValue(text,out var known)) return known;
        if(text==CurrentSession?.DisplayName) return CurrentSession.AccountId;
        var friend=friends.FirstOrDefault(f=>f.DisplayName==text); if(friend is not null) return friend.AccountId;
        var member=stream.Roster.FirstOrDefault(m=>m.DisplayName==text); if(member is not null) return member.UserId;
        var person=alphaChatUsers.FirstOrDefault(u=>u.DisplayName==text); if(person is not null) return person.AccountId;
        var conversation=memberDisplayNameCache.FirstOrDefault(p=>p.Value==text); if(conversation.Key is not null) return conversation.Key;
        var player=pictionaryPlayerNames.FirstOrDefault(p=>p.Value==text); if(player.Key is not null) return player.Key;
        var room=partyDirectoryRooms.FirstOrDefault(r=>r.HostDisplayName==text); if(room is not null) return room.HostAccountId;
        return usernameAccounts.Where(p=>p.Key.Length>=3 && text.Contains(p.Key,StringComparison.Ordinal))
            .OrderByDescending(p=>p.Key.Length).FirstOrDefault().Value;
    }

    private void DrawUsername(string? id,string text,Vector4? fallback=null)
    {
        RegisterUsername(id,UsernameDisplayName(id) ?? text);
        var appearance=UsernameAppearance(id,fallback ?? Vector4.One);
        ImGui.TextColored(Vector4.Zero, text + (AccountVerified(id) ? "     " : ""));
        DrawUsernameText(ImGui.GetWindowDrawList(),ImGui.GetItemRectMin(),text,fallback ?? Vector4.One,id,appearance);
        DrawAccountVerification(ImGui.GetWindowDrawList(), ImGui.GetItemRectMin() + new Vector2(ImGui.CalcTextSize(text).X + Ui(4), 0), id);
    }
    private void DrawUsername(string text,Vector4? fallback=null) => DrawUsername(FindUsernameAccount(text),text,fallback);
    private void DrawUsernameAt(ImDrawListPtr draw,Vector2 position,string text,Vector4 fallback,string? id=null,bool verificationTooltip=true)
    {
        id ??= FindUsernameAccount(text);RegisterUsername(id,UsernameDisplayName(id) ?? text);
        var appearance=UsernameAppearance(id,fallback);
        DrawUsernameText(draw,position,text,fallback,id,appearance);
        DrawAccountVerification(draw, position + new Vector2(ImGui.CalcTextSize(text).X + Ui(4), 0), id, verificationTooltip);
    }

    private string? UsernameDisplayName(string? id) => id is null ? null :
        id==CurrentSession?.AccountId ? CurrentSession.DisplayName :
        id==profilePageData?.AccountId ? profilePageData.DisplayName :
        friends.FirstOrDefault(f=>f.AccountId==id)?.DisplayName ?? stream.Roster.FirstOrDefault(m=>m.UserId==id)?.DisplayName ??
        alphaChatUsers.FirstOrDefault(u=>u.AccountId==id)?.DisplayName ?? memberDisplayNameCache.GetValueOrDefault(id) ??
        pictionaryPlayerNames.GetValueOrDefault(id) ?? usernameAccounts.FirstOrDefault(p=>p.Value==id).Key;

    private void DrawUsernameText(ImDrawListPtr draw,Vector2 position,string text,Vector4 fallback,string? id,(Vector4 Colour,string Effect) appearance)
    {
        var name=UsernameDisplayName(id);
        var offset=string.IsNullOrEmpty(name) ? -1 : text.IndexOf(name,StringComparison.Ordinal);
        if(offset<0 || text==name) {DrawAlphaChatStyledUsername(draw,position,text,appearance.Colour,appearance.Effect);return;}
        var prefix=text[..offset];
        draw.AddText(position,ImGui.GetColorU32(fallback),prefix);
        var namePosition=position+new Vector2(ImGui.CalcTextSize(prefix).X,0);
        DrawAlphaChatStyledUsername(draw,namePosition,name!,appearance.Colour,appearance.Effect);
        draw.AddText(namePosition+new Vector2(ImGui.CalcTextSize(name).X,0),ImGui.GetColorU32(fallback),text[(offset+name!.Length)..]);
    }

    private void DrawUsernamePixelText(byte[] pixels,int width,int height,int x,int y,string text,int scale,uint fallback,string? id=null)
    {
        id ??= FindUsernameAccount(text);
        RegisterUsername(id,UsernameDisplayName(id) ?? text);
        var colour=new Vector4(((fallback>>16)&255)/255f,((fallback>>8)&255)/255f,(fallback&255)/255f,1);
        var appearance=UsernameAppearance(id,colour);
        for(var i=0;i<text.Length && x+i*6*scale<width-5*scale;i++)
        {
            var sample=AlphaChatNameEffects.Sample(appearance.Effect,appearance.Colour,text.Length<2 ? 0 : i/(float)(text.Length-1),Environment.TickCount64/1000d);
            var rgb=((uint)Math.Clamp((int)(sample.Colour.X*255),0,255)<<16)|((uint)Math.Clamp((int)(sample.Colour.Y*255),0,255)<<8)|(uint)Math.Clamp((int)(sample.Colour.Z*255),0,255);
            DrawPictionaryPixelText(pixels,width,height,x+i*6*scale,y,text[i].ToString(),scale,rgb);
        }
    }
}
