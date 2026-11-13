using AlphaChannel.Contracts;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private bool CanAdminEvents => false;
    internal bool HalloweenActivityActive => false;
    internal bool HalloweenCountdownActive => false;
    internal string? HalloweenCountdownUrl => null;
    internal bool HalloweenOwnsTv => false;
    private bool HalloweenCountdownSuppressed => false;
    private bool halloweenStartingMedia => false;
    private bool halloweenBadgePopupOpen;
    internal void TickHalloween() { }
    internal void UpdatePumpkinVideoPreview() { }
    internal void EndHalloween(bool publish = true, bool completed = false) { }
    internal bool ObserveHalloweenState(StreamControl state) => false;
}
