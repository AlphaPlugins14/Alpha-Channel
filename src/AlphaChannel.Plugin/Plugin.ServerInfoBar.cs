using System.Globalization;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;

namespace AlphaChannel.Plugin;

public sealed partial class Plugin
{
    private IDtrBarEntry? serverInfoEntry;
    private DateTime nextServerInfoUpdateUtc;
    private (int Count, bool Playing)? lastServerInfoDisplay;
    private string? lastServerInfoTooltip;

    private void InitializeServerInfoBar()
    {
        try
        {
            serverInfoEntry = DtrBar.Get("Alpha Channel");
        }
        catch (ArgumentException)
        {
            // An optional bar entry must not fail plugin startup after a reload.
            AepLog.Warning("[Server info bar] An existing entry prevented registration; the plugin will continue without this entry.");
            return;
        }
        // OnClick covers this entry's whole text: both the cutscene icon and
        // its notification number. This is an open action, never a toggle.
        serverInfoEntry.OnClick = _ =>
        {
            if (Volatile.Read(ref disposeStarted) == 0)
                frameworkActions.Enqueue(mainWindow.OpenUi);
        };
        UpdateServerInfoBar();
    }

    private void UpdateServerInfoBar()
    {
        if (serverInfoEntry is null) return;
        serverInfoEntry.Shown = ClientState.IsLoggedIn;
        var now = DateTime.UtcNow;
        if (now < nextServerInfoUpdateUtc) return;
        nextServerInfoUpdateUtc = now.AddMilliseconds(500);
        var count = ClientState.IsLoggedIn ? mainWindow.GetPlaybackHudNotificationCount() : 0;
        var activity = mainWindow.GetServerInfoActivity();
        var display = (count, activity.Playing);
        if (lastServerInfoDisplay != display)
        {
            var text = new SeStringBuilder();
            if (activity.Playing) text.AddUiForeground(500);
            text.AddIcon(BitmapFontIcon.WatchingCutscene)
                .AddText(" " + count.ToString(CultureInfo.InvariantCulture));
            if (activity.Playing) text.AddUiForegroundOff();
            serverInfoEntry.Text = text.Build();
            lastServerInfoDisplay = display;
        }

        var status = stream.Mode switch
        {
            StreamMode.Hosting => "Hosting",
            StreamMode.Viewing => "Viewer",
            _ => "Solo",
        };
        var viewers = stream.Mode == StreamMode.None ? 0 : stream.Roster.Length;
        var tooltip = $"Alpha Channel\nStatus: {status}\nViewers: {viewers}\n{activity.Status}";
        if (!string.IsNullOrWhiteSpace(activity.Title)) tooltip += "\nCurrent: " + activity.Title;
        tooltip += $"\nNotifications: {count}\nClick to open Alpha Channel";
        if (tooltip != lastServerInfoTooltip)
        {
            serverInfoEntry.Tooltip = new SeStringBuilder().AddText(tooltip).Build();
            lastServerInfoTooltip = tooltip;
        }
    }
}
