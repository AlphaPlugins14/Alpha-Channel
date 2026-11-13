namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private string? alphaChatLoginAccount;
    private string? alphaChatLoginToken;
    private bool alphaChatAutoJoinSuppressed;
    private bool alphaChatPageWasVisible;
    private bool alphaChatStartPending;

    private void StartAlphaChatPresence()
    {
        if (Volatile.Read(ref disposeStarted) != 0 || CurrentSession is not { } session) return;
        if (alphaChat.Active && (alphaChat.AccountId != session.AccountId || alphaChat.Token != session.Token))
        { alphaChatStartPending = true; return; }
        alphaChat.Join(session.Token, session.AccountId,
            Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "AlphaChatHistory"), AepLog.Warning,
            presenceOnly: true, nameColourHex: SavedAlphaChatUsernameColour(session.AccountId), nameEffect: SavedAlphaChatUsernameEffect(session.AccountId), styleManaged: true);
    }

    private void UpdateAlphaChatPresence()
    {
        if (Volatile.Read(ref disposeStarted) != 0) return;
        var account = CurrentSession?.AccountId; var token = CurrentSession?.Token;
        if (alphaChatLoginAccount != account || alphaChatLoginToken != token)
        {
            var accountChanged = alphaChatLoginAccount != account;
            alphaChat.Leave();
            alphaChatLoginAccount = account; alphaChatLoginToken = token;
            if (accountChanged) alphaChatAutoJoinSuppressed = false;
            alphaChatPageWasVisible = false;
            if (accountChanged) UpdateAlphaChat();
            alphaChatStartPending = account is not null && Plugin.Cfg.AutoJoinAlphaChat && !alphaChatAutoJoinSuppressed;
        }
        if (alphaChatStartPending && !alphaChat.Active && account is not null)
        {
            alphaChatStartPending = false; StartAlphaChatPresence();
            alphaChatPageWasVisible = false;
        }
        var visible = IsOpen && !windowMinimized && currentPage == HomePage.AlphaChat;
        if (visible && !alphaChatPageWasVisible && alphaChat.Active) alphaChat.OpenChat();
        if (!visible && alphaChatPageWasVisible && alphaChat.Active) alphaChat.Background();
        alphaChatPageWasVisible = visible;
        alphaChat.SetRosterWatching(alphaChatRosterOpen || visible && !string.IsNullOrEmpty(alphaChatDraft) && alphaChatDraft.Contains('@'));
        if (account is not null && alphaChat.Connected && alphaChat.AccountId == account && DateTime.UtcNow >= alphaChatNextColourSaveUtc)
        {
            var effect = SavedAlphaChatUsernameEffect(account);
            if (alphaChat.RequestedUsernameEffect != effect)
            { alphaChat.SetUsernameStyle(SavedAlphaChatUsernameColour(account), effect, publish: false); alphaChatNextColourSaveUtc = DateTime.UtcNow.AddSeconds(1.1); }
        }
    }

    private void LeaveAlphaChatForLogin()
    {
        alphaChatAutoJoinSuppressed = true; alphaChatStartPending = false;
        alphaChat.Leave(); alphaChatUnread.Clear(); alphaChatUnreadMentions.Clear();
    }
}
