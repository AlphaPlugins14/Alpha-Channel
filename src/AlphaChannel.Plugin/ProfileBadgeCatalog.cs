namespace AlphaChannel.Plugin;

// Keep design identities stable even when the display order changes.
internal enum ProfileBadgeDesign
{
    TvMechanic,
    CodeChip,
    PixelWorkshop,
    HeartTv,
    HeartTicket,
    PopcornPal,
    FirstBroadcast,
    LaunchTv,
    FoundersPin,
    PumpkinTv,
    MovieGhost,
    HauntedTape,
    SofaSquad,
    BigPremiere,
    ChatBuddies,
    ControllerCrunch,
    ArcadeBuddy,
    VinylPal,
}

internal sealed record ProfileBadgeDefinition(
    string Id, string Label, ProfileBadgeDesign Design, bool Reserved = false, bool PreviewOnly = false);

internal static class ProfileBadgeCatalog
{
    // Display order is independent of the artwork library. Reserved designs are
    // retained here but excluded from every available-badge listing.
    internal static readonly IReadOnlyList<ProfileBadgeDefinition> Designs =
    [
        new("pixel-workshop", "Dev Team", ProfileBadgeDesign.PixelWorkshop),
        new("launch-tv", "Founding Member", ProfileBadgeDesign.LaunchTv),
        new("heart-ticket", "Supporter", ProfileBadgeDesign.HeartTicket),
        new("haunted-tape", "Halloween 2026", ProfileBadgeDesign.HauntedTape),
        new("heart-tv", "Community Builder", ProfileBadgeDesign.HeartTv),
        new("popcorn-pal", "Movie Buff", ProfileBadgeDesign.PopcornPal),
        // Temporary universal artwork previews; excluded from assignment APIs and managers.
        new("sofa-squad", "Full House", ProfileBadgeDesign.SofaSquad),
        new("big-premiere", "Crowd Pleaser", ProfileBadgeDesign.BigPremiere),
        new("chat-buddies", "Socialite", ProfileBadgeDesign.ChatBuddies),
        new("controller-crunch", "Button Masher", ProfileBadgeDesign.ControllerCrunch),
        new("arcade-buddy", "Game On", ProfileBadgeDesign.ArcadeBuddy),
        new("vinyl-pal", "Beat Keeper", ProfileBadgeDesign.VinylPal),
        new("tv-mechanic", "TV mechanic", ProfileBadgeDesign.TvMechanic, Reserved: true),
        new("code-chip", "Code chip", ProfileBadgeDesign.CodeChip, Reserved: true),
        new("first-broadcast", "First broadcast", ProfileBadgeDesign.FirstBroadcast, Reserved: true),
        new("founders-pin", "Founders pin", ProfileBadgeDesign.FoundersPin, Reserved: true),
        new("pumpkin-tv", "Pumpkin TV", ProfileBadgeDesign.PumpkinTv, Reserved: true),
        new("movie-ghost", "Movie ghost", ProfileBadgeDesign.MovieGhost, Reserved: true),
    ];

    internal static readonly IReadOnlyList<ProfileBadgeDefinition> Available =
        Designs.Where(badge => !badge.Reserved && !badge.PreviewOnly).ToArray();

    // LINQ ordering is stable: keep the chosen catalogue order inside each group.
    internal static IReadOnlyList<ProfileBadgeDefinition> ForProfile(IReadOnlySet<string> unlockedIds) =>
        Designs.Where(badge => !badge.Reserved)
            .OrderByDescending(badge => !badge.PreviewOnly && unlockedIds.Contains(badge.Id)).ToArray();
}
