using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Auth;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    // Prepared release: publish only after the scheduled server deployment
    // has added the privacy endpoints and completed its additive migration.
    private static bool ProfileFriendsEnabled => true;

    private bool profileFriendsTab, profileFriendsLoading;
    private ProfileFriendsPage? profileFriendsPage;
    private int? profileFriendsCount;
    private string? profileFriendsTarget, profileFriendsViewer, profileFriendsError;
    private int profileFriendsGeneration;
    private DateTime nextProfileFriendsRefresh;
    private string friendsPrivacy = "friends";
    private string? friendsPrivacyToken, friendsPrivacyError;
    private bool friendsPrivacyLoading, friendsPrivacySaving;
    private DateTime friendsPrivacyRetry;

    private void ResetProfileFriends()
    {
        profileFriendsGeneration++;
        profileFriendsTarget = profileFriendsViewer = profileFriendsError = null;
        profileFriendsPage = null; profileFriendsCount = null; profileFriendsLoading = false;
        nextProfileFriendsRefresh = DateTime.MinValue;
    }

    private void EnsureFriendsPrivacy(CharacterSession session)
    {
        if (!ProfileFriendsEnabled) return;
        if (friendsPrivacyToken != session.Token)
        {
            friendsPrivacyToken = session.Token; friendsPrivacy = "friends";
            friendsPrivacyLoading = friendsPrivacySaving = false; friendsPrivacyError = null;
            friendsPrivacyRetry = DateTime.MinValue;
        }
        if (friendsPrivacyLoading || friendsPrivacySaving || DateTime.UtcNow < friendsPrivacyRetry) return;
        friendsPrivacyLoading = true; friendsPrivacyRetry = DateTime.MaxValue;
        _ = UpdateFriendsPrivacyAsync(session, null);
    }

    private async Task UpdateFriendsPrivacyAsync(CharacterSession session, string? visibility)
    {
        var privacy = visibility is null
            ? await friendsClient.GetFriendsVisibilityAsync(session.Token).ConfigureAwait(false)
            : await friendsClient.SetFriendsVisibilityAsync(session.Token, visibility).ConfigureAwait(false);
        await Plugin.Framework.RunOnFrameworkThread(() =>
        {
            if (Volatile.Read(ref disposeStarted) != 0 || CurrentSession?.Token != session.Token || friendsPrivacyToken != session.Token) return;
            friendsPrivacyLoading = friendsPrivacySaving = false;
            if (privacy is not null)
            {
                friendsPrivacy = privacy.Visibility; friendsPrivacyError = null;
                friendsPrivacyRetry = DateTime.UtcNow.AddMinutes(10);
                if (visibility is not null && profileFriendsTarget == session.AccountId)
                {
                    profileFriendsGeneration++; profileFriendsLoading = false;
                    profileFriendsPage = null; nextProfileFriendsRefresh = DateTime.MinValue;
                }
            }
            else
            {
                friendsPrivacyError = visibility is null ? "Couldn't load friends privacy. Try again." : "Privacy wasn't saved. Please try again.";
                friendsPrivacyRetry = DateTime.UtcNow.AddSeconds(30);
            }
        }).ConfigureAwait(false);
    }

    private void DrawFriendsPrivacy(CharacterSession session)
    {
        if (!ProfileFriendsEnabled) return;
        EnsureFriendsPrivacy(session);
        ImGui.TextUnformatted("Friends list visibility");
        var values = new[] { "everyone", "friends", "none" };
        var labels = new[] { "Everyone", "Friends Only", "No one" };
        using (UiDisabled.Scope(friendsPrivacyLoading || friendsPrivacySaving || friendsPrivacyError is not null))
        {
            var selected = Array.IndexOf(values, friendsPrivacy);
            if (PartyPopupChoice("friendsPrivacy", labels, ref selected) && selected >= 0)
            {
                friendsPrivacySaving = true; friendsPrivacyError = null;
                _ = UpdateFriendsPrivacyAsync(session, values[selected]);
            }
        }
        using (UiFonts.PushRegularRelative(.88f))
        using (ImRaii.TextWrapPos(0)) ImGui.TextColored(MutedText, friendsPrivacySaving ? "Saving…" :
            "Changes save immediately. Your friend count is always visible. This does not remove any friends.");
        if (friendsPrivacyError is { } error)
        {
            using (ImRaii.TextWrapPos(0)) ImGui.TextColored(Danger, error);
            if (ImGui.SmallButton("Retry##friendsPrivacy"))
            { friendsPrivacyError = null; friendsPrivacyRetry = DateTime.MinValue; EnsureFriendsPrivacy(session); }
        }
    }

    private void RefreshProfileFriends(CharacterSession session, string target, bool more = false)
    {
        if (!ProfileFriendsEnabled) return;
        if (profileFriendsTarget != target || profileFriendsViewer != session.Token)
        {
            ResetProfileFriends(); profileFriendsTarget = target; profileFriendsViewer = session.Token;
        }
        if (profileFriendsLoading || !more && DateTime.UtcNow < nextProfileFriendsRefresh) return;
        var offset = more ? profileFriendsPage?.NextOffset ?? 0 : 0;
        profileFriendsLoading = true; nextProfileFriendsRefresh = DateTime.UtcNow.AddSeconds(30);
        _ = LoadProfileFriendsAsync(session, target, offset, ++profileFriendsGeneration);
    }

    private async Task LoadProfileFriendsAsync(CharacterSession session, string target, int offset, int generation)
    {
        var page = await friendsClient.GetProfileFriendsAsync(session.Token, target, offset).ConfigureAwait(false);
        await Plugin.Framework.RunOnFrameworkThread(() =>
        {
            if (Volatile.Read(ref disposeStarted) != 0 || CurrentSession?.Token != session.Token ||
                generation != profileFriendsGeneration || profileFriendsTarget != target || profileFriendsViewer != session.Token) return;
            profileFriendsLoading = false;
            if (page is null)
            {
                profileFriendsPage = null; profileFriendsError = "Friends are unavailable. Try again shortly.";
                return;
            }
            profileFriendsPage = offset > 0 && !page.Hidden && profileFriendsPage is { Hidden: false } previous
                ? page with { Items = previous.Items.Concat(page.Items).DistinctBy(p => p.AccountId).ToArray() } : page;
            profileFriendsCount = page.TotalCount;
            profileFriendsError = null;
            ObservePatreonPeople(page.Items.Select(p => p.AccountId));
        }).ConfigureAwait(false);
    }

    private void DrawProfileFriendsContent(CharacterSession session, string target)
    {
        RefreshProfileFriends(session, target);
        var available = ImGui.GetContentRegionAvail();
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, UiVec(2, 2));
        using var list = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##profileFriendsList",
            new Vector2(available.X, MathF.Max(Ui(30), available.Y - Ui(2))), false));
        if (!list) return;
        if (profileFriendsPage is not { Hidden: false, Items.Length: > 0 } page)
        {
            DrawProfileFriendsEmpty();
            return;
        }
        var origin = ImGui.GetCursorScreenPos(); var width = ImGui.GetContentRegionAvail().X;
        var columns = width >= Ui(570) ? 3 : width >= Ui(340) ? 2 : 1;
        var gap = Ui(6); var height = Ui(48); var tileWidth = (width - gap * (columns - 1)) / columns;
        for (var i = 0; i < page.Items.Length; i++)
        {
            var person = page.Items[i];
            using var id = ImRaii.PushId(person.AccountId);
            var start = origin + new Vector2(i % columns * (tileWidth + gap), i / columns * (height + gap));
            ImGui.SetCursorScreenPos(start);
            var clicked = ImGui.InvisibleButton("##profileFriend", new Vector2(tileWidth, height));
            var hovered = ImGui.IsItemHovered(); var draw = ImGui.GetWindowDrawList();
            var accent = EffectiveProfileTheme?.Accent ?? AccentHover;
            draw.AddRectFilled(start, start + new Vector2(tileWidth, height),
                ImGui.GetColorU32(EffectiveProfileTheme is { } theme ? ThemeInset(theme) : FrameBg), Ui(6));
            draw.AddRect(start, start + new Vector2(tileWidth, height), ImGui.GetColorU32(hovered ? accent : BorderSubtle), Ui(6));
            DrawAvatarAt(start + UiVec(7, 10), person.AvatarIcon, person.AvatarColorHex, Ui(28), person.AvatarImageUrl);
            draw.AddCircleFilled(start + UiVec(44, 16), Ui(3.5f), ImGui.GetColorU32(person.Online ? Good : MutedText));
            var supporter = PatreonBadgeTier(person.AccountId) > 0;
            var name = FitPartyTextToWidth(person.DisplayName, MathF.Max(Ui(12), tileWidth - Ui(62) - (supporter ? Ui(18) : 0)));
            DrawUsernameAt(draw, start + UiVec(53, 7), name, Vector4.One, person.AccountId);
            var heart = start + UiVec(53, 7) + new Vector2(ImGui.CalcTextSize(name).X + Ui(4), 0);
            if (supporter) UiFonts.DrawIcon(draw, ImGui.GetFontSize(), heart, ImGui.GetColorU32(PatreonOrange), FontAwesomeIcon.Heart.ToIconString());
            if (person.MutualFriend)
            {
                ImGui.SetCursorScreenPos(start + UiVec(53, 27));
                using (UiFonts.PushRegularRelative(.80f)) ImGui.TextColored(MutedText, "Mutual friend");
            }
            if (hovered)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                UiFonts.SetTooltip(supporter && ImGui.IsMouseHoveringRect(heart, heart + UiVec(18, 18)) ? "Patreon Supporter" : "View Profile");
            }
            if (clicked) { OpenProfilePage(session, person.AccountId, person.DisplayName); return; }
        }
        ImGui.SetCursorScreenPos(origin + new Vector2(0, (page.Items.Length + columns - 1) / columns * (height + gap)));
        if (page.NextOffset is not null)
            using (UiDisabled.Scope(profileFriendsLoading))
                if (ImGui.Button(profileFriendsLoading ? "Loading…" : "Load more friends", UiVec(-1, 28)))
                    RefreshProfileFriends(session, target, more: true);
        ImGui.Dummy(Vector2.Zero);
    }

    private void DrawProfileFriendsEmpty()
    {
        var hidden = profileFriendsPage?.Hidden == true;
        var heading = hidden ? "Friends list is private" : profileFriendsError is not null ? "Friends unavailable" :
            profileFriendsLoading ? "Loading friends…" : "No friends yet";
        var detail = hidden ? profileFriendsPage!.Visibility == "friends"
            ? "This person shares their friends list with friends only." : "This person has chosen to keep their friends list private."
            : profileFriendsError ?? "";
        var area = ImGui.GetContentRegionAvail(); var start = ImGui.GetCursorScreenPos(); var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(start, start + area, ImGui.GetColorU32(EffectiveProfileTheme is { } theme ? ThemeInset(theme) : FrameBg), Ui(8));
        var wrapWidth = MathF.Max(1, area.X - Ui(32));
        Vector2 headingSize, detailSize;
        using (UiFonts.PushRelative(1.02f, emphasis: true)) headingSize = ImGui.CalcTextSize(heading, false, wrapWidth);
        using (UiFonts.PushRegularRelative(.88f)) detailSize = ImGui.CalcTextSize(detail, false, wrapWidth);
        var y = MathF.Max(Ui(6), (area.Y - Ui(34) - headingSize.Y - (detail.Length == 0 ? 0 : detailSize.Y + Ui(5))) / 2);
        UiFonts.DrawCenteredIcon(draw, Ui(23), start + new Vector2(area.X / 2, y + Ui(12)),
            ImGui.GetColorU32(EffectiveProfileTheme?.Accent ?? AccentHover), (hidden ? FontAwesomeIcon.Lock : FontAwesomeIcon.UserFriends).ToIconString());
        ImGui.SetCursorScreenPos(start + new Vector2(MathF.Max(Ui(16), (area.X - headingSize.X) / 2), y + Ui(34)));
        using (UiFonts.PushRelative(1.02f, emphasis: true))
        using (ImRaii.TextWrapPos(ImGui.GetCursorPosX() + wrapWidth)) ImGui.TextUnformatted(heading);
        if (detail.Length == 0) return;
        ImGui.SetCursorScreenPos(start + new Vector2(MathF.Max(Ui(16), (area.X - detailSize.X) / 2), y + Ui(39) + headingSize.Y));
        using (UiFonts.PushRegularRelative(.88f))
        using (ImRaii.TextWrapPos(ImGui.GetCursorPosX() + wrapWidth)) ImGui.TextColored(MutedText, detail);
    }
}
