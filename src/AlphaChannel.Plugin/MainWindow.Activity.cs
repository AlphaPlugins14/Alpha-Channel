using AlphaChannel.Contracts;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

// Activity Channel: a friends-only feed of "X started watching", "X joined a watch-along", "X
// accepted your friend request" - refreshed on open and again whenever StreamClient's
// OnActivityNew ping fires (see AlphaChannel.Contracts.SocialSignalType, the feed itself is always
// fetched via REST, the socket only ever says "something changed, refetch").
//
// Layout identity: vertical timeline rail — not card tiles.
internal sealed partial class MainWindow
{
    private bool activityDirty = true;
    private bool activityLoading;
    private DateTime activityNextRetryUtc = DateTime.MinValue;
    private int activityRequestGeneration;
    private string? activityAccountId;
    private string? activitySessionToken;
    private ActivityEventDto[] activityItems = [];
    private string? activityNextCursor;

}
