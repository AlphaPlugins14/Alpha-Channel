using AlphaChannel.Plugin.Auth;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private bool partyBansOpen, partyModerationBusy;
    private string? partyModerationNotice, partyModerationError, partyBansAccount;
    private PartyBan[] partyBans = [];
    private string? partyModerationBusyToken;
    private int partyBansGeneration;

    private void OpenPartyBans()
    {
        if (stream.Mode != StreamMode.Hosting || CurrentSession is not { } session) return;
        partyBansOpen = true;
        partyBansAccount = session.AccountId;
        partyBans = [];
        partyModerationError = null;
        _ = LoadPartyBans(session, ++partyBansGeneration);
    }
    private async Task LoadPartyBans(CharacterSession session, int generation)
    {
        partyModerationBusy = true;
        partyModerationBusyToken = session.Token;
        try
        {
            var bans = await PartyModerationClient.Bans(session.Token, session.AccountId, youtubeFeedToken).ConfigureAwait(false);
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (Volatile.Read(ref disposeStarted) == 0 && generation == partyBansGeneration &&
                    CurrentSession?.Token == session.Token && stream.Mode == StreamMode.Hosting)
                    partyBans = bans;
            });
        }
        catch (OperationCanceledException) when (youtubeFeedToken.IsCancellationRequested) { }
        catch
        {
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (Volatile.Read(ref disposeStarted) == 0 && generation == partyBansGeneration && CurrentSession?.Token == session.Token)
                    partyModerationError = "Couldn't load your bans. Please try again.";
            });
        }
        finally
        {
            await Plugin.Framework.RunOnFrameworkThread(() =>
            { if (Volatile.Read(ref disposeStarted) == 0 && generation == partyBansGeneration) partyModerationBusy = false; });
        }
    }
    private void ModeratePartyMember(string id, string name, string action)
    {
        if (partyModerationBusy || stream.Mode != StreamMode.Hosting || CurrentSession is not { } session ||
            id == session.AccountId || !Guid.TryParse(id, out _)) return;
        if (action != "unban" && !stream.Roster.Any(p => p.UserId == id)) return;
        partyModerationBusy = true;
        partyModerationBusyToken = session.Token;
        partyModerationError = null;
        var room = stream.HostingSessionId;
        _ = ModeratePartyMemberAsync(session, room, id, name, action);
    }
    private async Task ModeratePartyMemberAsync(CharacterSession session, string room, string id, string name, string action)
    {
        try
        {
            await PartyModerationClient.Moderate(session.Token, session.AccountId, id, name, action, youtubeFeedToken, room).ConfigureAwait(false);
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (Volatile.Read(ref disposeStarted) != 0 || CurrentSession?.Token != session.Token ||
                    stream.Mode != StreamMode.Hosting || stream.HostingSessionId != room) return;
                if (action == "unban") partyBans = partyBans.Where(b => b.AccountId != id).ToArray();
                else if (action == "ban" && partyBansOpen) partyBans = partyBans.Where(b => b.AccountId != id)
                    .Append(new PartyBan(id, name, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())).ToArray();
            });
        }
        catch (OperationCanceledException) when (youtubeFeedToken.IsCancellationRequested) { }
        catch
        {
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (Volatile.Read(ref disposeStarted) == 0 && CurrentSession?.Token == session.Token && stream.HostingSessionId == room)
                    partyModerationNotice = "Couldn't update party permissions. Please try again.";
            });
        }
        finally
        {
            await Plugin.Framework.RunOnFrameworkThread(() =>
            { if (Volatile.Read(ref disposeStarted) == 0 && CurrentSession?.Token == session.Token) partyModerationBusy = false; });
        }
    }
    private void DrawPartyModerationOverlays()
    {
        if (partyModerationBusy && partyModerationBusyToken != CurrentSession?.Token)
        { partyModerationBusy = false; partyBansOpen = false; partyBansGeneration++; }
        if (partyModerationNotice is { } notice)
        {
            DrawEventWindow("partyModerationNotice", "Watch Party", UiVec(450, 240),
                () => ImGui.TextWrapped(notice), () => partyModerationNotice = null,
                () => { if (AlphaChatButton("dismissPartyModeration", "Dismiss", FontAwesomeIcon.Check, Ui(110), primary: true)) partyModerationNotice = null; });
            return;
        }
        if (!partyBansOpen) return;
        if (stream.Mode != StreamMode.Hosting || CurrentSession?.AccountId != partyBansAccount)
        { partyBansOpen = false; return; }
        DrawEventWindow("partyBans", "Manage party bans", UiVec(540, 460), () =>
        {
            ImGui.TextWrapped("Bans apply to your current and future Watch Parties until you remove them.");
            ImGui.Spacing();
            if (partyModerationError is { } error)
            {
                ImGui.TextColored(Danger, error);
                if (ImGui.Button("Retry##partyBans")) OpenPartyBans();
            }
            if (partyModerationBusy) ImGui.TextColored(MutedText, "Updating…");
            else if (partyBans.Length == 0 && partyModerationError is null) ImGui.TextColored(MutedText, "You haven't banned anyone.");
            using var list = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##partyBanList", new Vector2(0, -1), false));
            if (!list) return;
            foreach (var ban in partyBans)
            {
                using var id = ImRaii.PushId(ban.AccountId);
                var origin = ImGui.GetCursorScreenPos();
                var width = ImGui.GetContentRegionAvail().X;
                DrawUsername(ban.AccountId, FitPartyTextToWidth(ban.Name, MathF.Max(1, width - Ui(105))), Vector4.One);
                ImGui.SetCursorScreenPos(origin + new Vector2(MathF.Max(0, width - Ui(92)), 0));
                using (UiDisabled.Scope(partyModerationBusy))
                    if (AlphaChatButton("unban", "Unban", FontAwesomeIcon.Unlock, Ui(90), Ui(30)))
                        ModeratePartyMember(ban.AccountId, ban.Name, "unban");
                ImGui.SetCursorScreenPos(origin + UiVec(0, 42));
            }
        }, () => { partyBansOpen = false; }, () =>
        {
            if (AlphaChatButton("closePartyBans", "Close", FontAwesomeIcon.Times, Ui(110)))
            { partyBansOpen = false; }
        });
    }
}
