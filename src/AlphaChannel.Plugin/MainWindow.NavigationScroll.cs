using Dalamud.Bindings.ImGui;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private readonly Core.PageNavigationScroll navigationScroll = new();

    private object NavigationScrollKey => (currentPage, currentPage switch
    {
        HomePage.Music => (object)musicTab,
        HomePage.Screen => (object)screenTab,
        HomePage.Player => (activePlayerDrawer, playerSourceTab),
        // Feed tabs own separate scroll panels. Resetting their outer page mid-draw
        // moves the whole YouTube layout; only channel navigation needs that reset.
        HomePage.VideoGrid => viewedChannelId ?? "",
        HomePage.WatchAlong => (object)partyPanelTab,
        HomePage.Settings => (object)settingsTab,
        HomePage.Help => helpCategory,
        HomePage.Moderator => (moderatorTab, moderatorSelectedReport ?? ""),
        HomePage.Messages => openConversationId ?? "",
        HomePage.Profile => profilePageAccountId ?? "",
        HomePage.Arcade => (object)arcadeFilter,
        HomePage.PlaySnes => (object)selectedGameSystem,
        HomePage.InternetArchive => internetArchiveViewCategoryKey ?? "",
        HomePage.LuckyWheel => (object)luckyWheelEditorTab,
#if DEBUG
#endif
        _ => ""
    });

    private void ResetPageScrollOnNavigation()
    {
        navigationScroll.ResetIfChanged(NavigationScrollKey);
    }
}
