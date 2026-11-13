namespace AlphaChannel.Plugin;

[Serializable]
internal sealed class ScreenPositionPreset
{
    public string Name { get; set; } = "";
    public string? LocationKey { get; set; }
    public string? LocationName { get; set; }
    public bool AutoApply { get; set; }
    public DateTime SavedUtc { get; set; }

    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Yaw { get; set; }

    //
    // Retained for presets created before independent width/height
    // scaling was introduced.
    //
    public float Scale { get; set; } = 1.0f;

    public bool DisableFixedScaleRatio { get; set; }

    //
    // Nullable so an old preset without these JSON properties can fall
    // back to Scale instead of deserializing them as zero.
    //
    public float? WidthScale { get; set; }
    public float? HeightScale { get; set; }
}


