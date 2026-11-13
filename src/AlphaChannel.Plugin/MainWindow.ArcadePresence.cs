namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private ArcadePresenceClient? arcadePresence;

    internal void TickArcadePresence()
    {
        if(Volatile.Read(ref disposeStarted)!=0)return;
        arcadePresence??=new ArcadePresenceClient(Plugin.Cfg);
        var players=ArcadePresenceClient.Contribution(Plugin.ClientState.IsLoggedIn,IsArcadeGameActive,
            stream.Mode==StreamMode.Viewing,stream.Mode==StreamMode.Hosting && stream.IsConnected,stream.Roster.Length);
        arcadePresence.Tick(DateTime.UtcNow,players,IsOpen && !windowMinimized && currentPage==HomePage.Arcade);
    }
}
