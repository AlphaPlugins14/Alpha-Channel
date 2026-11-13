using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Auth;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

// Venues: persistent hangout spaces - a named, saved screen placement (see Venue's server-side doc
// comment) shareable with friends. The purely-local equivalent (MainWindow.Screen.cs's presets) is
// still there for a quick personal "remember this spot" with no server round-trip; a Venue is the
// same idea made visible to friends, anchored to the zone it was recorded in via TerritoryTypeId.
internal sealed partial class MainWindow
{
    private bool myVenuesDirty = true;
    private bool myVenuesLoading;
    private VenueDto[] myVenues = [];
    private string venueNameInput = string.Empty;
    private bool venueSaving;
    private string? venueError;

    private string? selectedVenueFriendAccountId;
    private string? selectedVenueFriendDisplayName;
    private VenueDto[] friendVenues = [];
    private bool friendVenuesLoading;
    private string? friendVenuesError;

    private void DrawVenueRow(VenueDto venue, int currentZone, bool isMine, CharacterSession session)
    {
        ImGui.PushID(venue.Id);
        ImGui.Text(venue.Name);

        var sameZone = venue.TerritoryTypeId == currentZone;
        if (!sameZone)
        {
            ImGui.SameLine();
            ImGui.TextColored(MutedText, "(different zone)");
        }

        ImGui.SameLine();
        using (UiDisabled.Scope(!sameZone))
        {
            if (ImGui.SmallButton("Load"))
            {
                screenController.Engine.SetScreenTransform(new Vector3(venue.ScreenX, venue.ScreenY, venue.ScreenZ), venue.ScreenYaw, venue.ScreenScale);
            }
        }

        if (isMine)
        {
            ImGui.SameLine();
            using (ImRaii.PushColor(ImGuiCol.Text, Danger))
            {
                if (ImGui.SmallButton("Delete"))
                {
                    var token = session.Token;
                    var venueId = venue.Id;
                    _ = Task.Run(async () => { await venuesClient.DeleteAsync(token, venueId); myVenuesDirty = true; });
                    myVenues = myVenues.Where(v => v.Id != venue.Id).ToArray();
                }
            }
        }

        ImGui.PopID();
    }

    private void SaveVenue(CharacterSession session, int territoryTypeId)
    {
        var engine = screenController.Engine;
        var name = venueNameInput.Trim();
        var token = session.Token;
        var request = new CreateVenueRequest(name, territoryTypeId,
            engine.ScreenPosition.X, engine.ScreenPosition.Y, engine.ScreenPosition.Z, engine.ScreenYaw, engine.ScreenScale);

        venueSaving = true;
        venueError = null;
        _ = Task.Run(async () =>
        {
            var created = await venuesClient.CreateAsync(token, request);
            venueSaving = false;
            if (created is null)
            {
                venueError = "Couldn't save that venue - you may have hit the 50-venue limit.";
                return;
            }

            venueNameInput = string.Empty;
            myVenuesDirty = true;
        });
    }

    private void RefreshMyVenues(string bearerToken)
    {
        myVenuesDirty = false;
        myVenuesLoading = true;
        _ = Task.Run(async () =>
        {
            try
            {
                myVenues = await venuesClient.GetMineAsync(bearerToken) ?? [];
            }
            finally
            {
                myVenuesLoading = false;
            }
        });
    }

    private void SelectFriendForVenues(CharacterSession session, string accountId, string displayName)
    {
        selectedVenueFriendAccountId = accountId;
        selectedVenueFriendDisplayName = displayName;
        friendVenues = [];
        friendVenuesLoading = true;
        friendVenuesError = null;
        var token = session.Token;
        _ = Task.Run(async () =>
        {
            var result = await venuesClient.GetFriendVenuesAsync(token, accountId);
            friendVenues = result ?? [];
            friendVenuesError = result is null ? "Couldn't load their venues." : null;
            friendVenuesLoading = false;
        });
    }
}
