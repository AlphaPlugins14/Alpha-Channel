namespace AlphaChannel.Plugin;

// Public configuration state only; each identity's credentials stay in its own
// encrypted vault. Selection changes take effect at startup, never mid-session.
internal sealed class DebugAccountTestState
{
    public bool Requested { get; set; }
    public bool Loaded { get; set; }
    public bool[] NormalOnboarding { get; set; } = [];
    public bool[] TestOnboarding { get; set; } = [];
    public Dictionary<ulong, string> NormalDisplayNames { get; set; } = new();
    public Dictionary<ulong, string> TestDisplayNames { get; set; } = new();

    internal void Apply(Configuration configuration, bool allowTest)
    {
        if (!allowTest) Requested = false;
        if (Loaded == Requested) return;
        var current = new[] { configuration.HasCompletedFirstLaunch,
            configuration.HasCompletedInterfaceSizingSetup, configuration.GuestAccessNoticeSeen };
        if (Loaded)
        {
            TestOnboarding = current;
            TestDisplayNames = new(configuration.CharacterDisplayNames);
        }
        else
        {
            NormalOnboarding = current;
            NormalDisplayNames = new(configuration.CharacterDisplayNames);
        }
        var target = Requested ? TestOnboarding : NormalOnboarding;
        configuration.HasCompletedFirstLaunch = target.ElementAtOrDefault(0);
        configuration.HasCompletedInterfaceSizingSetup = target.ElementAtOrDefault(1);
        configuration.GuestAccessNoticeSeen = target.ElementAtOrDefault(2);
        configuration.CharacterDisplayNames = new(Requested ? TestDisplayNames : NormalDisplayNames);
        Loaded = Requested;
    }
}
