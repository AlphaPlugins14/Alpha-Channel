using AlphaChannel.Plugin.Lighting;
using GameFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace AlphaChannel.Plugin.Lighting;

internal sealed unsafe partial class WorldLightingController
{
    private readonly SceneTimeOverride timeRender = new();
    private SceneTimeControl? timeControl;
    private SceneTimeControl TimeControl => timeControl ??= new(timeRender.Start, () => timeRender.Active, timeRender.Dispose);
    private static string? TimeScope => WatchPartyTvLocation.TryReadCurrent(out var here) ? here.Encode() : null;

    internal bool CustomTimeActive { get { lock(gate) return timeControl?.Minutes.HasValue == true; } }
    internal bool CanAdjustTime { get { lock(gate) return !disposed && WatchPartyTvLocation.IsLocalPlayerReady() &&
        timeControl?.ShowActive != true && timeRender.Available; } }
    internal string TimeStatus { get { lock(gate) return timeControl?.ShowActive == true
        ? "The Halloween show is controlling time of day."
        : !WatchPartyTvLocation.IsLocalPlayerReady() ? "Time adjustment is unavailable while changing areas."
        : "Time adjustment unavailable: another time override is active or the game version is unsupported."; } }
    internal int TimeMinutes
    {
        get
        {
            lock(gate)
            {
                if (timeControl?.ShowActive == true) return 0;
                if (timeControl?.Minutes is { } minutes) return minutes;
                var framework = GameFramework.Instance();
                var seconds = framework == null ? 0 : framework->ClientTime.EorzeaTime;
                return (int)((seconds % 86400 + 86400) % 86400) / 60;
            }
        }
    }
    internal bool SetTimeMinutes(int minutes)
    {
        lock(gate) return !disposed && TimeScope is { } scope && TimeControl.Set(minutes, scope);
    }
    internal void ResetTime()
    {
        lock(gate) timeControl?.ResetManual();
    }
    internal bool BeginShowTime()
    {
        lock(gate) return !disposed && TimeScope is { } scope && TimeControl.BeginShow(scope);
    }
    internal void EndShowTime()
    {
        lock(gate) timeControl?.EndShow(TimeScope);
    }
    private void UpdateTime() { if (timeControl?.Active == true) timeControl.Update(TimeScope); }
    private void DisposeTime()
    {
        timeControl?.Dispose();
        timeRender.Dispose();
    }
}
