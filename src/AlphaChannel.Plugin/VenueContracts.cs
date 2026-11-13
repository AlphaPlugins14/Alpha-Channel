namespace AlphaChannel.Contracts;

// A named, saved screen placement, anchored to the zone it was recorded in - see
// AlphaChannel.Server.Data.Venue's doc comment for why TerritoryTypeId matters (world-space
// coordinates only make sense within the zone they came from).
public sealed record VenueDto(
    string Id, string Name, int TerritoryTypeId,
    float ScreenX, float ScreenY, float ScreenZ, float ScreenYaw, float ScreenScale, long CreatedAtUnix);

public sealed record CreateVenueRequest(
    string Name, int TerritoryTypeId,
    float ScreenX, float ScreenY, float ScreenZ, float ScreenYaw, float ScreenScale);
