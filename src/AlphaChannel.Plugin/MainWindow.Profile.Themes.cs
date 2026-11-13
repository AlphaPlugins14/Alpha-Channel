using AlphaChannel.Plugin.Auth;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private PatreonProfileTheme? viewedProfileTheme;
    private string? viewedProfileThemeAccount;
    private DateTime nextProfileThemeRefresh;
    private bool profileThemeRefreshBusy;
    private string? themeEditorToken;
    private string? themeEditorChoice;
    private bool themeEditorLoaded, themeEditorBusy;
    private PatreonProfileTheme? themeEditorMembership;
    private string? themeEditorError;
    private ProfileThemeDesign? EffectiveProfileTheme => InlineProfileEditing && inlineProfileDraft is { } draft && !inlineThemeLoading
        ? ProfileThemes.Find(draft.Theme) : viewedProfileThemeAccount == profilePageAccountId &&
        viewedProfileTheme is { Entitled:true } theme && DateTimeOffset.UtcNow.ToUnixTimeSeconds() < theme.ValidUntil
        ? ProfileThemes.Find(theme.Theme) : null;

    internal void TickProfileThemes()
    {
        if (!IsOpen || windowMinimized || currentPage != HomePage.Profile || CurrentSession is not { } session ||
            profilePageAccountId is not { } id || profileThemeRefreshBusy || DateTime.UtcNow < nextProfileThemeRefresh) return;
        _ = LoadProfileThemeAsync(session, id, profilePageLoadGeneration);
    }

    private async Task LoadProfileThemeAsync(CharacterSession session, string id, long generation)
    {
        profileThemeRefreshBusy = true;
        nextProfileThemeRefresh = DateTime.UtcNow.AddMinutes(1);
        PatreonProfileTheme? result = null;
        try { result = await patreonWorkerClient.GetProfileThemeAsync(session.Token, youtubeFeedToken, id).ConfigureAwait(false); }
        catch (Exception) { /* Missing verification means default styling, without breaking profiles. */ }
        finally
        {
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                profileThemeRefreshBusy = false;
                if (Volatile.Read(ref disposeStarted) != 0 || CurrentSession?.Token != session.Token || !IsCurrentProfileLoad(generation)) return;
                viewedProfileThemeAccount = id;
                viewedProfileTheme = result?.AccountId.Equals(id, StringComparison.OrdinalIgnoreCase) == true ? result : null;
            });
        }
    }

    private async Task LoadThemeEditorAsync(CharacterSession session, bool save = false, string? choice = null)
    {
        themeEditorBusy = true;
        try
        {
            var result = save ? await patreonWorkerClient.SaveProfileThemeAsync(session.Token, choice, youtubeFeedToken).ConfigureAwait(false)
                : await patreonWorkerClient.GetProfileThemeAsync(session.Token, youtubeFeedToken).ConfigureAwait(false);
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (Volatile.Read(ref disposeStarted) != 0 || CurrentSession?.Token != session.Token || themeEditorToken != session.Token) return;
                if (!result.AccountId.Equals(session.AccountId, StringComparison.OrdinalIgnoreCase)) return;
                themeEditorMembership = result;
                themeEditorChoice = result.SelectedTheme;
                themeEditorLoaded = true;
                themeEditorError = null;
                if (save && profilePageAccountId == session.AccountId)
                { viewedProfileThemeAccount = session.AccountId; viewedProfileTheme = result; }
            });
        }
        catch (Exception)
        {
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (themeEditorToken == session.Token && Volatile.Read(ref disposeStarted) == 0)
                    themeEditorError = save ? "Couldn't save the theme. Check your active Patreon membership and try again." : "Couldn't load profile themes. Please try again.";
            });
        }
        finally
        {
            await Plugin.Framework.RunOnFrameworkThread(() =>
            { if (themeEditorToken == session.Token && Volatile.Read(ref disposeStarted) == 0) themeEditorBusy = false; });
        }
    }

    private void DrawProfileThemeSettings(CharacterSession session)
    {
        if (themeEditorToken != session.Token)
        {
            themeEditorToken = session.Token;
            themeEditorLoaded = false; themeEditorChoice = null; themeEditorError = null; themeEditorMembership = null;
            _ = LoadThemeEditorAsync(session);
        }
        DrawOtherSettingsPanel("##profileThemeSettings", 320f, () =>
        {
            SettingsSection("Profile theme", "Exclusive designs for active Patreon members. Other users see your chosen theme on your profile.");
            var entitled = themeEditorMembership is { Entitled:true } membership &&
                DateTimeOffset.UtcNow.ToUnixTimeSeconds() < membership.ValidUntil && CurrentVerifiedPatreonTier() > 0;
            // Everyone can browse previews; only active members can apply them.
            ImGui.SetNextItemWidth(MathF.Min(ImGui.GetContentRegionAvail().X, Ui(340f)));
            if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginCombo("##profileThemeChoice", ProfileThemes.Find(themeEditorChoice)?.Name ?? "Default")))
            {
                if (ImGui.Selectable("Default", themeEditorChoice is null)) themeEditorChoice = null;
                foreach (var theme in ProfileThemes.All)
                    if (ImGui.Selectable(theme.Name, themeEditorChoice == theme.Id)) themeEditorChoice = theme.Id;
                ImGui.EndCombo();
            }
            var selected = ProfileThemes.Find(themeEditorChoice);
            DrawSettingsHelper(selected?.Description ?? "Your regular Alpha Channel profile design.");
            var origin = ImGui.GetCursorScreenPos();
            var size = new Vector2(ImGui.GetContentRegionAvail().X, Ui(116f));
            ImGui.Dummy(size);
            var draw = ImGui.GetWindowDrawList();
            draw.AddRectFilled(origin, origin+size, ImGui.GetColorU32(selected?.Background ?? CardBg), Ui(10f));
            DrawProfileThemeDecoration(draw, origin, size, selected);
            draw.AddRect(origin, origin+size, ImGui.GetColorU32(selected?.Accent ?? SettingsReviewBorder), Ui(10f));
            draw.AddText(origin+UiVec(16f,16f), ImGui.GetColorU32(Vector4.One), "About");
            var insetMin = origin+UiVec(16f,44f);
            var insetMax = origin+size-UiVec(16f,16f);
            draw.AddRectFilled(insetMin,insetMax,ImGui.GetColorU32(selected is null ? FrameBg : ThemeInset(selected)),Ui(8f));
            if (selected is not null)
                draw.AddRect(insetMin,insetMax,ImGui.GetColorU32(selected.Accent with { W=.25f }),Ui(8f));
            draw.AddText(insetMin+UiVec(12f,12f),ImGui.GetColorU32(MutedText),"A little preview of your profile theme.");
            DrawSettingsHelper(entitled ? "Themes stay visible while your membership is active." : "Active Patreon membership required to use these themes.");
            using (UiDisabled.Scope(!themeEditorLoaded || themeEditorBusy || !entitled && themeEditorChoice is not null))
                if (ImGui.Button(themeEditorBusy ? "Saving…##profileTheme" : "Save theme##profileTheme", UiVec(138f,36f)))
                    _ = LoadThemeEditorAsync(session, save:true, choice:themeEditorChoice);
            if (themeEditorError is { } error)
            {
                ImGui.TextColored(Danger,error);
                if (!themeEditorBusy && ImGui.SmallButton("Retry##profileTheme")) _ = LoadThemeEditorAsync(session);
            }
            else if (!themeEditorBusy && themeEditorMembership is { } grant && DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= grant.ValidUntil)
            {
                if (ImGui.SmallButton("Refresh membership##profileTheme")) _ = LoadThemeEditorAsync(session);
            }
        }, autoHeight:true);
    }

    private static Vector4 ThemeInset(ProfileThemeDesign theme) => new(theme.Background.X*.7f, theme.Background.Y*.7f, theme.Background.Z*.7f,1f);

    private static void DrawProfileThemeDecoration(ImDrawListPtr draw, Vector2 min, Vector2 size, ProfileThemeDesign? theme)
    {
        if (theme is null) return;
        var max = min+size;
        var colour = ImGui.GetColorU32(theme.Accent with { W=.36f });
        var glow = theme.Secondary with { W=.24f };
        draw.PushClipRect(min+UiVec(2f,2f),max-UiVec(2f,2f),true);
        draw.AddRectFilledMultiColor(min,max,ImGui.GetColorU32(glow),ImGui.GetColorU32(glow with { W=.025f }),
            ImGui.GetColorU32(glow with { W=0f }),ImGui.GetColorU32(glow with { W=0f }));
        // Edge lighting and artwork stay beneath the readable content insets.
        ThemeGlow(draw, new(max.X-Ui(28f),min.Y+Ui(12f)), Ui(100f), theme.Accent, .075f);
        draw.AddLine(min+UiVec(12f,2f),new(max.X-Ui(12f),min.Y+Ui(2f)),
            ImGui.GetColorU32(theme.Secondary with { W=.42f }),Ui(1f));
        draw.AddLine(new(min.X+Ui(2f),min.Y+Ui(12f)),new(min.X+Ui(2f),max.Y-Ui(12f)),
            ImGui.GetColorU32(theme.Accent with { W=.25f }),Ui(1f));
        if (theme.Id=="neon-arcade")
        {
            var start = new Vector2(MathF.Max(min.X,max.X-Ui(190f)),min.Y);
            var gridBottom = MathF.Min(max.Y,min.Y+Ui(110f));
            for(var x=start.X;x<max.X;x+=Ui(20f)) draw.AddLine(new(x,start.Y),new(x,gridBottom),
                ImGui.GetColorU32(theme.Accent with { W=.15f }));
            for(var y=start.Y;y<gridBottom;y+=Ui(20f)) draw.AddLine(new(start.X,y),new(max.X,y),
                ImGui.GetColorU32(theme.Secondary with { W=.22f }));
            for(var i=0;i<3;i++)
            {
                var p=new Vector2(max.X-Ui(10f+i*7f),min.Y+Ui(12f+i*7f));
                draw.AddRectFilled(p,p+UiVec(4f,4f),colour);
            }
        }
        else if (theme.Id=="after-hours")
        {
            ThemeGlow(draw,new(max.X-Ui(65f),min.Y+Ui(32f)),Ui(65f),theme.Secondary,.12f);
            for(var i=0;i<22;i++)
            {
                var x=max.X-Ui(10f+i*6f); var h=Ui(6f+(i*17%38));
                draw.AddRectFilled(new(x,min.Y+Ui(52f)-h),new(x+Ui(3f),min.Y+Ui(52f)),
                    ImGui.GetColorU32(Vector4.Lerp(theme.Accent,theme.Secondary,i/21f) with { W=.25f }),Ui(1f));
                draw.AddRectFilled(new(x,max.Y-Ui(3f)-MathF.Min(h,Ui(10f))),new(x+Ui(3f),max.Y-Ui(3f)),colour);
            }
        }
        else if(theme.Id=="midnight-premiere")
        {
            var film=new Vector2(max.X-Ui(112f),min.Y-Ui(12f));
            var along=UiVec(.8f,.6f); var across=UiVec(-.6f,.8f);
            draw.AddQuadFilled(film,film+along*150f,film+along*150f+across*34f,film+across*34f,
                ImGui.GetColorU32(theme.Background with { W=.8f }));
            for(var i=0;i<7;i++)
            {
                var p=film+along*(i*22f+4f);
                foreach(var edge in new[]{3f,26f})
                    draw.AddQuadFilled(p+across*edge,p+across*edge+along*7f,
                        p+across*(edge+4f)+along*7f,p+across*(edge+4f),colour);
                draw.AddLine(p+across*10f,p+across*23f,colour);
            }
        }
        else if(theme.Id=="haunted-screening")
        {
            UiFonts.DrawIcon(draw, Ui(27f), new(max.X-Ui(58f),min.Y+Ui(15f)), ImGui.GetColorU32(theme.Secondary with { W=.4f }), FontAwesomeIcon.Ghost.ToIconString());
            var corner=new Vector2(max.X,min.Y);
            for(var ray=0;ray<5;ray++)
            {
                var angle=MathF.PI*.5f+ray*MathF.PI/8f;
                draw.AddLine(corner,corner+new Vector2(MathF.Cos(angle),MathF.Sin(angle))*Ui(65f),
                    ImGui.GetColorU32(theme.Secondary with { W=.23f }));
            }
            for(var ring=1;ring<=3;ring++)
                for(var ray=0;ray<4;ray++)
                {
                    var a=MathF.PI*.5f+ray*MathF.PI/8f;
                    var b=a+MathF.PI/8f;
                    draw.AddLine(corner+new Vector2(MathF.Cos(a),MathF.Sin(a))*Ui(ring*18f),
                        corner+new Vector2(MathF.Cos(b),MathF.Sin(b))*Ui(ring*18f),
                        ImGui.GetColorU32(theme.Secondary with { W=.23f }));
                }
            var p=new Vector2(max.X-Ui(22f),min.Y+Ui(36f));
            draw.AddCircleFilled(p,Ui(11f),colour);
            draw.AddLine(p+UiVec(0f,-10f),p+UiVec(2f,-15f),colour,Ui(3f));
            draw.AddTriangleFilled(p+UiVec(-6f,-3f),p+UiVec(-2f,-3f),p+UiVec(-4f,-6f),ImGui.GetColorU32(theme.Background));
            draw.AddTriangleFilled(p+UiVec(2f,-3f),p+UiVec(6f,-3f),p+UiVec(4f,-6f),ImGui.GetColorU32(theme.Background));
            draw.AddLine(p+UiVec(-4f,4f),p+UiVec(4f,4f),ImGui.GetColorU32(theme.Background),Ui(2f));
        }
        else if(theme.Id=="cozy-watch-party")
        {
            var p=new Vector2(max.X-Ui(28f),min.Y+Ui(28f));
            ThemeGlow(draw,p+UiVec(0f,22f),Ui(65f),theme.Accent,.12f);
            draw.AddTriangleFilled(p+UiVec(-10f,-10f),p+UiVec(10f,-10f),p+UiVec(18f,12f),colour);
            draw.AddTriangleFilled(p+UiVec(-10f,-10f),p+UiVec(18f,12f),p+UiVec(-18f,12f),colour);
            draw.AddLine(p+UiVec(0f,12f),p+UiVec(0f,26f),colour,Ui(2f));
            draw.AddLine(p+UiVec(-8f,27f),p+UiVec(8f,27f),colour,Ui(2f));
            var plant=p+UiVec(-40f,16f);
            draw.AddLine(plant+UiVec(0f,-15f),plant+UiVec(0f,10f),colour,Ui(1.5f));
            for(var i=0;i<3;i++)
            {
                var leaf=plant+UiVec(0f,-i*7f);
                draw.AddTriangleFilled(leaf,leaf+UiVec(-11f,-9f),leaf+UiVec(-7f,1f),colour);
                draw.AddTriangleFilled(leaf,leaf+UiVec(11f,-6f),leaf+UiVec(7f,3f),colour);
            }
            draw.AddQuadFilled(plant+UiVec(-8f,5f),plant+UiVec(8f,5f),plant+UiVec(5f,15f),plant+UiVec(-5f,15f),colour);
        }
        else if(theme.Id=="starlight")
        {
            for(var i=0;i<12;i++)
            {
                var p=new Vector2(max.X-Ui(18f+(i*37%180)),min.Y+Ui(12f+(i*19%48)));
                var r=Ui(i%4==0?6f:2f);
                draw.AddQuadFilled(p-new Vector2(0,r),p+new Vector2(r*.25f,0),p+new Vector2(0,r),p-new Vector2(r*.25f,0),colour);
                draw.AddQuadFilled(p-new Vector2(r,0),p-new Vector2(0,r*.25f),p+new Vector2(r,0),p+new Vector2(0,r*.25f),colour);
            }
        }
        draw.PopClipRect();
    }

    private static void ThemeGlow(ImDrawListPtr draw, Vector2 center, float radius, Vector4 tint, float opacity)
    {
        for(var layer=12;layer>0;layer--)
            draw.AddCircleFilled(center,radius*layer/12f,ImGui.GetColorU32(tint with { W=opacity/12f }),48);
    }

    private static void DrawProfileThemeHalo(ImDrawListPtr draw, Vector2 center, float radius, ProfileThemeDesign theme)
    {
        for(var i=8;i>0;i--)
            draw.AddCircle(center,radius+Ui(i*.7f),ImGui.GetColorU32(theme.Accent with { W=.025f*(9-i) }),64,Ui(1f));
        draw.AddCircle(center,radius+Ui(3f),ImGui.GetColorU32(theme.Secondary with { W=.6f }),64,Ui(1f));
    }
}
