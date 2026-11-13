namespace AlphaChannel.Plugin.Core;

internal static class WatchPartyJoinPolicy
{
    internal static string? BlockReason(bool hosting,bool viewing,bool joining,bool leaving) =>
        hosting ? "End your current Watch Party before joining another one." :
        viewing ? "Leave your current Watch Party before joining another one." :
        leaving ? "Wait for your current Watch Party to finish closing before joining another one." :
        joining ? "A Watch Party join is already in progress." : null;
}
