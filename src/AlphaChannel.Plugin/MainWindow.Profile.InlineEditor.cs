using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Auth;
using AlphaChannel.Plugin.Core;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private ProfileEditDraft? inlineProfileDraft;
    private string? inlineProfileToken;
    private bool inlineProfileSaving, inlineThemeLoading;
    private PatreonProfileTheme? inlineThemeGrant;
    private string? inlineProfileError;
    private bool inlineStatusEditing, inlineAboutEditing;
    private bool inlineStatusFocus, inlineAboutFocus;
    private DateTime inlineSaveShakeUntil;
    private bool inlineUnsavedNoticePending;
    private bool inlineProfileScrollToTop;
    private double inlineUnsavedNoticeStarted = -1d;
    private bool InlineProfileEditing => inlineProfileDraft is not null && CurrentSession?.Token == inlineProfileToken;

    private bool AllowInlineProfileNavigation()
    {
        if (!InlineProfileEditing) { inlineProfileDraft = null; return true; }
        if (!inlineProfileDraft!.CanNavigate(inlineProfileSaving))
        {
            inlineSaveShakeUntil = DateTime.UtcNow.AddSeconds(.6);
            inlineUnsavedNoticePending = true;
            inlineProfileScrollToTop = true;
            return false;
        }
        CancelInlineProfileEdit();
        return true;
    }

    private void CancelInlineProfileEdit()
    {
        inlineProfileDraft = null;
        inlineProfileError = null;
        inlineStatusEditing = inlineAboutEditing = false;
        inlineStatusFocus = inlineAboutFocus = false;
        inlineUnsavedNoticeStarted = -1d;
        inlineUnsavedNoticePending = false;
    }

    private void BeginInlineProfileEdit(CharacterSession session, AccountProfileDto profile)
    {
        inlineProfileToken = session.Token;
        inlineProfileDraft = new(new(profile.AvatarIcon, profile.AvatarColorHex, profile.Bio ?? "",
            profile.StatusMessage ?? "", !string.IsNullOrEmpty(profile.AvatarImageUrl), null, viewedProfileTheme?.SelectedTheme));
        inlineProfileError = null;
        inlineStatusEditing = inlineAboutEditing = false;
        inlineStatusFocus = inlineAboutFocus = false;
        inlineThemeGrant = null;
        inlineThemeLoading = true;
        _ = LoadInlineProfileThemeAsync(session, inlineProfileDraft);
    }

    private async Task LoadInlineProfileThemeAsync(CharacterSession session, ProfileEditDraft draft)
    {
        PatreonProfileTheme? grant = null;
        try { grant = await patreonWorkerClient.GetProfileThemeAsync(session.Token, youtubeFeedToken).ConfigureAwait(false); }
        catch (Exception) { }
        await Plugin.Framework.RunOnFrameworkThread(() =>
        {
            if (!ReferenceEquals(inlineProfileDraft, draft) || CurrentSession?.Token != session.Token || Volatile.Read(ref disposeStarted) != 0) return;
            inlineThemeLoading = false;
            if (grant?.AccountId.Equals(session.AccountId, StringComparison.OrdinalIgnoreCase) != true)
            { inlineProfileError = "Couldn't load theme choices. You can still edit your profile."; return; }
            inlineThemeGrant = grant;
            draft.Theme = grant.SelectedTheme;
            draft.Baseline = draft.Baseline with { Theme = grant.SelectedTheme };
        });
    }

    private void DrawInlineProfileAvatar(Vector2 origin, float diameter, AccountProfileDto profile)
    {
        var draft = inlineProfileDraft!;
        DrawInlineDraftAvatar(origin,diameter,profile,draft);
        var draw = ImGui.GetWindowDrawList();
        var next = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(origin);
        using (UiDisabled.Scope(inlineProfileSaving))
            if (ImGui.InvisibleButton("##inlineAvatar",new Vector2(diameter))) ImGui.OpenPopup("Change profile picture");
        if (ImGui.IsItemHovered())
        {
            draw.AddCircleFilled(origin+new Vector2(diameter*.5f),diameter*.5f,ImGui.GetColorU32(new Vector4(0,0,0,.55f)),64);
            var text="Change picture";
            draw.AddText(origin+new Vector2((diameter-ImGui.CalcTextSize(text).X)*.5f,diameter*.6f),uint.MaxValue,text);
            UiFonts.DrawIcon(draw, Ui(24f), origin+new Vector2(diameter*.5f-Ui(12f),diameter*.3f), uint.MaxValue, FontAwesomeIcon.Camera.ToIconString());
        }
        ImGui.SetCursorScreenPos(next);
        DrawInlineAvatarPopup(profile);
    }

    private void DrawInlineDraftAvatar(Vector2 origin,float diameter,AccountProfileDto profile,ProfileEditDraft draft)
    {
        DrawAvatarChip(draft.Icon, draft.Color, diameter, draft.UseImage && draft.ImagePath is null ? profile.AvatarImageUrl : null);
        var draw = ImGui.GetWindowDrawList();
        if (draft.UseImage && draft.ImagePath is { } path && Plugin.TextureProvider.GetFromFile(path).TryGetWrap(out var image, out _))
        {
            var (uv0,uv1) = CoverUvs(image.Width,image.Height,diameter,diameter);
            draw.AddImageRounded(image.Handle,origin,origin+new Vector2(diameter),uv0,uv1,uint.MaxValue,diameter*.5f);
        }
    }

    private void DrawInlineAvatarPopup(AccountProfileDto profile)
    {
        ImGui.SetNextWindowSize(UiVec(430f,0f),ImGuiCond.Always);
        using var style=ImRaii.PushStyle(ImGuiStyleVar.WindowPadding,UiVec(16f,16f))
            .Push(ImGuiStyleVar.WindowRounding,Ui(10f));
        if (!UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginPopup("Change profile picture",ImGuiWindowFlags.AlwaysAutoResize))) return;
        var draft=inlineProfileDraft!;
        ImGui.TextUnformatted("Profile picture");
        using (UiDisabled.Scope(inlineProfileSaving))
        {
            if (ImGui.RadioButton("Icon + colour",!draft.UseImage)) draft.UseImage=false;
            ImGui.SameLine();
            if (ImGui.RadioButton("Image",draft.UseImage)) draft.UseImage=true;
            DrawInlineDraftAvatar(ImGui.GetCursorScreenPos(),Ui(80f),profile,draft);
            if (draft.UseImage)
            {
                ImGui.TextWrapped(draft.ImagePath is { } selected ? Path.GetFileName(selected) : "Choose a PNG, JPG or WebP image (up to 5 MB).");
                DrawSettingsExplanation("Resized to fit within 512 × 512 and saved as a lossless PNG before upload. Smaller images are not enlarged.");
                if (ImGui.Button("Browse image",UiVec(140f,32f)))
                {
                    var editingDraft=draft;
                    profileAvatarFileDialog.OpenFileDialog("Select Profile Picture",".png,.jpg,.jpeg,.webp",(success,path)=>
                    {
                        if (!success || !ReferenceEquals(inlineProfileDraft,editingDraft) || !InlineProfileEditing || inlineProfileSaving) return;
                        try
                        {
                            if (!File.Exists(path) || new FileInfo(path).Length is <= 0 or > Video.AvatarImagePreparation.MaximumSourceBytes ||
                                Path.GetExtension(path).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg" or ".webp"))
                            { inlineProfileError="Choose a PNG, JPG or WebP image up to 5 MB."; return; }
                            editingDraft.ImagePath=path;
                            editingDraft.UseImage=true;
                            inlineProfileError=null;
                        }
                        catch (Exception) { inlineProfileError="Couldn't read that image."; }
                    });
                }
            }
            else
            {
                ImGui.TextUnformatted("Icon");
                DrawIconPicker(ref draft.Icon);
                ImGui.Spacing();
                ImGui.TextUnformatted("Colour");
                DrawColorPicker(ref draft.Color);
            }
            if (ImGui.Button("Done",UiVec(110f,32f))) ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private void DrawInlineProfileStatus(Vector2 origin,float avatarSize,float maxWidth)
    {
        var draft=inlineProfileDraft!;
        var position=origin+new Vector2(avatarSize*.6f,avatarSize-Ui(inlineStatusEditing ? 44f : 36f));
        ImGui.SetCursorScreenPos(position);
        using var disabled=UiDisabled.Scope(inlineProfileSaving);
        using var style=ImRaii.PushStyle(ImGuiStyleVar.FrameRounding,Ui(16f));
        var width=MathF.Min(maxWidth,Ui(250f));
        if (inlineStatusEditing)
        {
            if (inlineStatusFocus) { ImGui.SetKeyboardFocusHere(); inlineStatusFocus=false; }
            ImGui.SetNextItemWidth(MathF.Max(Ui(50f),width-Ui(32f)));
            ImGui.InputTextWithHint("##inlineStatus","Add a status",ref draft.Status,65);
            var fieldMin=ImGui.GetItemRectMin();
            var fieldMax=ImGui.GetItemRectMax();
            ImGui.SetCursorScreenPos(new Vector2(fieldMax.X+Ui(4f),(fieldMin.Y+fieldMax.Y)*.5f-Ui(14f)));
            if (DrawInlineProfileEditButton("##statusDone",true)) inlineStatusEditing=false;
            using var uiFontScale8748=UiFonts.PushRelative(.75f);
            UiFonts.DrawText(ImGui.GetWindowDrawList(), ImGui.GetFontSize(), new Vector2(MathF.Max(fieldMin.X+Ui(8f),origin.X+avatarSize+Ui(8f)),fieldMax.Y+Ui(2f)), ImGui.GetColorU32(MutedText), $"{draft.Status.Length} / 64");
            uiFontScale8748.Dispose();
        }
        else
        {
            var size=new Vector2(width,Ui(32f));
            if (ImGui.InvisibleButton("##inlineStatus",size)) inlineStatusEditing=inlineStatusFocus=true;
            var draw=ImGui.GetWindowDrawList();
            var theme=EffectiveProfileTheme;
            draw.AddRectFilled(position,position+size,ImGui.GetColorU32(theme?.Background ?? CardBg),Ui(16f));
            draw.AddRect(position,position+size,ImGui.GetColorU32(theme?.Accent ?? Accent),Ui(16f));
            var label=TruncateToWidth(draft.Status.Length==0 ? "Add a status" : draft.Status,width-Ui(44f));
            draw.AddText(position+new Vector2(Ui(12f),(size.Y-ImGui.GetTextLineHeight())*.5f),uint.MaxValue,label);
            DrawInlineProfileEditGlyph(position+new Vector2(width-Ui(18f),size.Y*.5f),false);
        }
        if (ImGui.IsItemHovered()) UiFonts.SetTooltip("Edit status (64 characters maximum)");
    }

    private bool DrawInlineProfileEditButton(string id,bool editing)
    {
        var origin=ImGui.GetCursorScreenPos();
        var size=UiVec(28f,28f);
        var clicked=ImGui.InvisibleButton(id,size);
        if (ImGui.IsItemHovered())
            ImGui.GetWindowDrawList().AddCircleFilled(origin+size*.5f,Ui(14f),
                ImGui.GetColorU32((EffectiveProfileTheme?.Accent ?? AccentHover) with { W=.12f }));
        DrawInlineProfileEditGlyph(origin+size*.5f,editing);
        return clicked;
    }

    private void DrawInlineProfileEditGlyph(Vector2 center,bool editing)
    {
        using var font=UiFonts.PushIcon();
        var glyph=(editing ? FontAwesomeIcon.Check : FontAwesomeIcon.PencilAlt).ToIconString();
        var glyphSize=ImGui.CalcTextSize(glyph);
        var tint=EffectiveProfileTheme?.Accent ?? AccentHover;
        if (inlineProfileSaving) tint.W=.4f;
        UiFonts.DrawText(ImGui.GetWindowDrawList(), ImGui.GetFontSize(), center-glyphSize*.5f, ImGui.GetColorU32(tint), glyph);
    }

    private void DrawInlineProfileControls(CharacterSession session)
    {
        var draft=inlineProfileDraft!;
        var width=ImGui.GetContentRegionAvail().X;
        var accent=EffectiveProfileTheme?.Accent ?? Accent;
        DrawSocialHeading("Editing profile");
        using (UiDisabled.Scope(inlineProfileSaving))
        {
            DrawSocialHelper("Profile theme");
            using (UiDisabled.Scope(inlineThemeLoading || inlineThemeGrant is null))
            {
                ImGui.SetNextItemWidth(width);
                if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginCombo("##inlineTheme",ProfileThemes.Find(draft.Theme)?.Name ?? "Default")))
                {
                    if (ImGui.Selectable("Default",draft.Theme is null)) draft.Theme=null;
                    foreach(var theme in ProfileThemes.All)
                        if (ImGui.Selectable(theme.Name,draft.Theme==theme.Id)) draft.Theme=theme.Id;
                    ImGui.EndCombo();
                }
            }
            var entitled=inlineThemeGrant is { Entitled:true } grant && DateTimeOffset.UtcNow.ToUnixTimeSeconds()<grant.ValidUntil && CurrentVerifiedPatreonTier()>0;
            DrawSocialHelper(inlineThemeLoading ? "Loading themes…" : entitled ? "Patreon theme" : "Patreon themes: preview only");
            var origin=ImGui.GetCursorScreenPos();
            var shake=Math.Max(0,(inlineSaveShakeUntil-DateTime.UtcNow).TotalSeconds);
            ImGui.SetCursorScreenPos(origin+new Vector2((float)(Math.Sin(shake*65)*shake*Ui(8f)),0));
            using (UiDisabled.Scope(draft.Dirty && (inlineThemeLoading || draft.Theme!=draft.Baseline.Theme && draft.Theme is not null && !entitled)))
            using (ImRaii.PushColor(ImGuiCol.Button,accent*new Vector4(.45f,.45f,.45f,1f)))
                if (ImGui.Button(inlineProfileSaving ? "Saving…" : "Save changes",new Vector2(width,Ui(34f)))) _=SaveInlineProfileAsync(session,draft);
            ImGui.SetCursorScreenPos(origin+new Vector2(0,Ui(40f)));
            if (ImGui.Button("Cancel##inlineProfile",new Vector2(width,Ui(30f)))) CancelInlineProfileEdit();
        }
        if (inlineProfileError is { } error && ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem))
            UiFonts.SetTooltip(error);
    }

    private async Task SaveInlineProfileAsync(CharacterSession session,ProfileEditDraft draft)
    {
        if (inlineProfileSaving || !InlineSaveIsCurrent(session,draft)) return;
        // Socials and visibility have their own saves. Let Save changes finish
        // editing normally even when there are no remaining profile edits.
        if (!draft.Dirty) { CancelInlineProfileEdit(); return; }
        inlineProfileSaving=true;
        inlineProfileError=null;
        var values=draft.Values;
        try
        {
            if (values.UseImage && values.ImagePath is null && string.IsNullOrEmpty(profilePageData?.AvatarImageUrl))
                throw new InvalidOperationException("Choose an image, or switch to Icon + colour.");
            var outcome=await authClient.UpdateProfileAsync(session.Token,new(null,values.Icon ?? AvatarIcons[0],values.Color,values.Bio,values.Status)).ConfigureAwait(false);
            if (outcome.Account is not { } updated) throw new InvalidOperationException("Couldn't save your profile. Please try again.");
            await PublishInlineProfileSave(session,draft,updated,fields:true);
            if (values.UseImage && values.ImagePath is { } imagePath)
            {
                var avatar=await authClient.UploadAvatarAsync(session.Token,imagePath).ConfigureAwait(false);
                if (avatar is null) throw new InvalidOperationException("Text saved, but the picture couldn't be uploaded. Please try again.");
                await PublishInlineProfileSave(session,draft,avatar,avatar:true);
            }
            else if (!values.UseImage && !string.IsNullOrEmpty(session.AvatarImageUrl))
            {
                var avatar=await authClient.ClearAvatarAsync(session.Token).ConfigureAwait(false);
                if (avatar is null) throw new InvalidOperationException("Text saved, but the picture couldn't be removed. Please try again.");
                await PublishInlineProfileSave(session,draft,avatar,avatar:true);
            }
            if (values.Theme!=draft.Baseline.Theme)
            {
                var grant=await patreonWorkerClient.SaveProfileThemeAsync(session.Token,values.Theme,youtubeFeedToken).ConfigureAwait(false);
                await Plugin.Framework.RunOnFrameworkThread(()=>
                {
                    if (!InlineSaveIsCurrent(session,draft)) return;
                    viewedProfileThemeAccount=session.AccountId;
                    viewedProfileTheme=inlineThemeGrant=grant;
                    draft.Baseline=draft.Baseline with { Theme=values.Theme };
                });
            }
            await Plugin.Framework.RunOnFrameworkThread(()=> { if (InlineSaveIsCurrent(session,draft)) CancelInlineProfileEdit(); });
        }
        catch (Exception exception)
        {
            await Plugin.Framework.RunOnFrameworkThread(()=>
            {
                if (InlineSaveIsCurrent(session,draft)) inlineProfileError=exception is InvalidOperationException ? exception.Message : "Couldn't save all changes. Please try again.";
            });
        }
        finally { await Plugin.Framework.RunOnFrameworkThread(()=>inlineProfileSaving=false); }
    }

    private bool InlineSaveIsCurrent(CharacterSession session,ProfileEditDraft draft) =>
        Volatile.Read(ref disposeStarted)==0 && CurrentSession?.Token==session.Token && ReferenceEquals(inlineProfileDraft,draft);

    private Task PublishInlineProfileSave(CharacterSession session,ProfileEditDraft draft,AccountSummary updated,bool fields=false,bool avatar=false) =>
        Plugin.Framework.RunOnFrameworkThread(()=>
        {
            if (!InlineSaveIsCurrent(session,draft)) return;
            ApplyAvatarSummary(session,updated);
            if (profilePageData is { } profile)
                profilePageData=profile with { AvatarIcon=updated.AvatarIcon,AvatarColorHex=updated.AvatarColorHex,
                    AvatarImageUrl=updated.AvatarImageUrl,Bio=updated.Bio,StatusMessage=updated.StatusMessage };
            if (fields) draft.Baseline=draft.Baseline with { Icon=draft.Icon,Color=draft.Color,Bio=draft.Bio,Status=draft.Status };
            if (avatar)
            {
                draft.ImagePath=null;
                draft.Baseline=draft.Baseline with { UseImage=draft.UseImage,ImagePath=null };
            }
        });

    private void DrawInlineProfileNotices()
    {
        if (inlineUnsavedNoticePending) { inlineUnsavedNoticeStarted=ImGui.GetTime(); inlineUnsavedNoticePending=false; }
        DrawSlidingNotice("##inlineUnsavedNotice",ref inlineUnsavedNoticeStarted,
            "You have unsaved changes, please save or cancel changes");
    }
}
