using System.Numerics;

namespace AlphaChannel.Plugin;

// Only apply when the eligible preset changes, rather than resetting manual
// adjustments on every framework tick. Respawning starts a fresh selection.
internal sealed class ScreenPresetAutoApply
{
    private ScreenPositionPreset? previous;
    private DateTime previousSavedUtc;

    internal void Reset() => previous = null;

    internal ScreenPositionPreset? Observe(IReadOnlyList<ScreenPositionPreset> presets,
        string locationKey, Vector3 playerPosition, bool residentialInterior = false)
    {
        var selected = Select(presets, locationKey, playerPosition, residentialInterior);
        var changed = !ReferenceEquals(previous, selected) || previousSavedUtc != selected?.SavedUtc;
        previous = selected;
        previousSavedUtc = selected?.SavedUtc ?? default;
        return changed ? selected : null;
    }

    internal static ScreenPositionPreset? Select(IReadOnlyList<ScreenPositionPreset> presets,
        string locationKey, Vector3 playerPosition, bool residentialInterior = false)
    {
        ScreenPositionPreset? newest = null;
        foreach (var preset in presets)
        {
            if (!preset.AutoApply || string.IsNullOrEmpty(preset.LocationKey) ||
                preset.LocationKey != locationKey) continue;
            var distance = Vector3.DistanceSquared(playerPosition, new(preset.X, preset.Y, preset.Z));
            if (!float.IsFinite(distance) || (!residentialInterior && distance > 60f * 60f)) continue;
            // Later list entries win ties, including legacy timestamps.
            if (newest is null || preset.SavedUtc >= newest.SavedUtc) newest = preset;
        }
        return newest;
    }
}
