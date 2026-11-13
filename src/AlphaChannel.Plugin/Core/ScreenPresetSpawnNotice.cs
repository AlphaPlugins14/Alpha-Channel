namespace AlphaChannel.Plugin;

// Session-only notification state. Despawning does not change the active preset
// context; edits, manual placement, preset switches and area changes do.
internal sealed class ScreenPresetSpawnNotice
{
    private sealed record Snapshot(ScreenPositionPreset Preset, string? Location, string Name,
        string? SavedLocation, bool AutoApply, DateTime SavedUtc, float X, float Y, float Z,
        float Yaw, float Scale, bool IndependentScale, float? Width, float? Height);

    private Snapshot? previous;
    private bool announced;

    internal void Reset() { previous = null; announced = false; }

    internal void ObserveLocation(string location)
    {
        if (previous is not null && previous.Location != location) Reset();
    }

    internal void Invalidate(ScreenPositionPreset preset)
    {
        if (ReferenceEquals(previous?.Preset, preset)) Reset();
    }

    internal void Observe(ScreenPositionPreset preset, string? location)
    {
        var snapshot = new Snapshot(preset, location, preset.Name, preset.LocationKey, preset.AutoApply,
            preset.SavedUtc, preset.X, preset.Y, preset.Z, preset.Yaw, preset.Scale,
            preset.DisableFixedScaleRatio, preset.WidthScale, preset.HeightScale);
        if (snapshot == previous) return;
        previous = snapshot;
        announced = false;
    }

    internal bool TryAnnounce(ScreenPositionPreset preset, string? location)
    {
        Observe(preset, location);
        if (announced) return false;
        announced = true;
        return true;
    }
}
