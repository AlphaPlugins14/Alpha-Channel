namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private GuestPresenceClient? guestPresence;
    private int signedInUsersOnlineCount;
    private bool hasRealtimeOnlineCount;
    private int usersOnlineCount => (int)Math.Min(int.MaxValue,
        (long)Math.Max(0, stream.IsConnected ? signedInUsersOnlineCount : guestPresence?.SignedInCount(DateTime.UtcNow) ?? 0) +
        (guestPresence?.GuestCount(DateTime.UtcNow) ?? 0));

    internal void TickGuestPresence()
    {
        guestPresence ??= new GuestPresenceClient(Plugin.Cfg);
        if (!stream.IsConnected) hasRealtimeOnlineCount = false;
        guestPresence.Tick(DateTime.UtcNow, Plugin.ClientState.IsLoggedIn, CurrentSession is not null,
            CurrentSession is not null && stream.IsConnected && hasRealtimeOnlineCount ? signedInUsersOnlineCount : null);
    }
}
