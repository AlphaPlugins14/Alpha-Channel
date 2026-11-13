using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Graphics.Environment;

namespace AlphaChannel.Plugin.Lighting;

// Shared local native lighting and render-time controls. No server commands, game-clock/weather changes or post-process overlay.
internal sealed unsafe partial class WorldLightingController : IDisposable
{
    private readonly object gate = new();
    private Hook<CopyEnvironment>? outdoorHook;
    private CopyEnvironment? copyOriginal;
    private int copyCallbacks;
    private delegate nint CopyEnvironment(nint destination, nint source);
    private const string CopySignature = "0F 10 42 08 0F 11 41 08 F2 0F 10 4A 18";
    private bool disposed, indoor, outdoor;
    private WatchPartyTvLocation location;
    private nint indoorAddress, environmentAddress, environmentScene;
    private float originalIndoor, indoorLevel = 1, outdoorLevel = 1;
    private SceneLightingValues natural, applied;
    private bool appliedOutdoor;
    private long outdoorUpdates, copyCalls, lastDestinationOffset;
    private string status = "Lighting tests are off.";

    internal WorldLightingController() => Plugin.Framework.Update += Update;
    internal bool IndoorActive { get { lock(gate) return indoor; } }
    internal bool OutdoorActive { get { lock(gate) return outdoor; } }
    internal bool HasCustomAdjustments { get { lock(gate) return
        (indoor && indoorLevel<.9999f) || (outdoor && outdoorLevel<.9999f) || timeControl?.Active == true; } }
    internal float IndoorLevel { get { lock(gate) return indoorLevel; } }
    internal float OutdoorLevel { get { lock(gate) return outdoorLevel; } }
    internal void ResetAll()
    {
        lock(gate) { ResetTime(); ResetBrightness(); }
    }
    internal void ResetBrightness()
    {
        lock(gate)
        {
            var hadIndoorAdjustment=indoor;
            ResetCore();
            // Recover a room left dark by an earlier plugin reload when no active snapshot survives.
            var area=CurrentIndoor();
            if(!hadIndoorAdjustment && area!=null && area->SavedInvertedBrightness<=5)
                RestoreIndoor(area,1-area->SavedInvertedBrightness*.2f);
            dimAtmosphere=false; dimSceneLights=false; indoorLevel=outdoorLevel=1; status="Normal lighting restored.";
        }
    }
    internal string Status { get { lock(gate) return outdoor && outdoorUpdates == 0
        ? "Outdoor test armed; waiting for a validated environment update. If nothing changes, stop and copy diagnostics." : status; } }
    internal string Diagnostics { get { lock(gate) return $"Indoor: {indoor}, Outdoor: {outdoor}\nEnvironment updates applied: {outdoorUpdates}\n" +
        $"Environment copy callbacks: {copyCalls}\nLast destination offset from manager: 0x{lastDestinationOffset:X}\n" +
        $"Natural sun: {natural.Sun}\nNatural moon: {natural.Moon}\nNatural ambient: {natural.Ambient}\n" +
        $"Applied sun: {applied.Sun}\nApplied moon: {applied.Moon}\nApplied ambient: {applied.Ambient}\n" +
        ExtraDiagnostics + IndoorDiagnostics() + $"Outdoor brightness: {outdoorLevel:P0}\n{Status}"; } }

    private string IndoorDiagnostics()
    {
        var area=CurrentIndoor();
        return area==null?"Indoor room: unavailable\n":
            $"Indoor brightness: {indoorLevel:P0}, original: {originalIndoor}\n" +
            $"Indoor current: {area->BrightnessCurrent}, target: {area->BrightnessTarget}, speed: {area->BrightnessTransitionSpeed}, transitioning: {area->IsBrightnessTransitioning}\n" +
            $"Saved inverted room brightness: {area->SavedInvertedBrightness}\n";
    }
    private static IndoorTerritory* CurrentIndoor()
    {
        if (!WatchPartyTvLocation.IsLocalPlayerReady()) return null;
        var housing = HousingManager.Instance();
        return housing != null && housing->IndoorTerritory != null &&
            (void*)housing->CurrentTerritory == (void*)housing->IndoorTerritory ? housing->IndoorTerritory : null;
    }
    internal static bool CanUseIndoor => CurrentIndoor() != null;
    internal static bool CanUseOutdoor => WatchPartyTvLocation.IsLocalPlayerReady() && CurrentIndoor() == null;

    internal void StartIndoor(float level)
    {
        lock(gate)
        {
            if(disposed) return;
            ResetCore();
            var area = CurrentIndoor();
            if(area == null || !WatchPartyTvLocation.TryReadCurrent(out location) ||
                !float.IsFinite(area->BrightnessTarget) || area->BrightnessTarget < 0 || area->BrightnessTarget > 1)
            { status = "Indoor brightness is unavailable here. Enter a housing interior."; return; }
            indoorAddress = (nint)area;
            originalIndoor = area->BrightnessTarget;
            // Recover a room left at zero by a reload of the broken direct-write build.
            if(originalIndoor==0 && area->SavedInvertedBrightness<5)
                originalIndoor=1-area->SavedInvertedBrightness*.2f;
            indoor = true;
            SetIndoorLevel(level);
            status = "Indoor native lighting test active.";
        }
    }
    internal void SetIndoorLevel(float level)
    {
        lock(gate)
        {
            indoorLevel = Math.Clamp(float.IsFinite(level) ? level : 1,0,1);
            if(indoor && ScopeMatches() && (nint)CurrentIndoor() == indoorAddress)
                Transition(CurrentIndoor(),originalIndoor*indoorLevel);
        }
    }
    private static void Transition(IndoorTerritory* area, float target)
    {
        if(area == null) return;
        area->BrightnessTarget = target;
        // Let the native transition update the room's lighting resources, not just the stored float.
        area->BrightnessTransitionSpeed = target-area->BrightnessCurrent;
        area->IsBrightnessTransitioning = true;
    }
    private static void RestoreIndoor(IndoorTerritory* area,float target)
    {
        if(area==null) return;
        // The native restore path uses the opposite direction from the slider's update path.
        area->BrightnessTarget=target;
        area->BrightnessTransitionSpeed=MathF.Sign(area->BrightnessCurrent-target)*.02f;
        area->IsBrightnessTransitioning=true;
    }

    internal void StartOutdoor(float level)
    {
        lock(gate)
        {
            if(disposed) return;
            ResetCore();
            if(!CanUseOutdoor || !WatchPartyTvLocation.TryReadCurrent(out location))
            { status = "Outdoor lighting requires a loaded area outside housing interiors."; return; }
            var manager = EnvManager.Instance();
            if(manager == null || manager->EnvScene == null)
            { status = "The game's environment manager is unavailable."; return; }
            environmentAddress = (nint)manager;
            environmentScene = (nint)manager->EnvScene;
            var values = (SceneLightingValues*)((byte*)manager + 0x58 + 0x20);
            if(!values->Valid)
            { status = "Environment lighting layout could not be validated; no lighting changed."; return; }
            try
            {
                // Lazy hook creation: missing signatures only disable this test.
                outdoorHook ??= Plugin.InteropProvider.HookFromSignature<CopyEnvironment>(CopySignature,CopyDetour);
                copyOriginal = outdoorHook.Original;
                outdoorLevel = Math.Clamp(float.IsFinite(level) ? level : 1,0,1);
                outdoorUpdates = copyCalls = lastDestinationOffset = 0; appliedOutdoor = false; outdoor = true;
                outdoorHook.Enable();
                status = "Outdoor native lighting test active.";
            }
            catch(Exception e)
            { outdoor = false; status = "Outdoor test unavailable: " + e.Message; }
        }
    }
    internal void SetOutdoorLevel(float level)
    {
        lock(gate)
        {
            outdoorLevel = Math.Clamp(float.IsFinite(level) ? level : 1,0,1);
            if(outdoor && appliedOutdoor && ScopeMatches() && EnvironmentMatches())
            {
                ApplyOutdoor((SceneLightingValues*)((byte*)environmentAddress + 0x78),natural);
                if(dimAtmosphere) ApplyAtmosphere((byte*)environmentAddress+0x58);
                // Scene lights are coalesced by the framework update.
            }
        }
    }

    private void ApplyOutdoor(SceneLightingValues* lighting,SceneLightingValues fresh)
    {
        natural = fresh; applied = fresh.Scaled(outdoorLevel);
        WriteColors(lighting,applied); appliedOutdoor = true;
    }
    private static void WriteColors(SceneLightingValues* lighting,SceneLightingValues values)
    { lighting->Sun = values.Sun; lighting->Moon = values.Moon; lighting->Ambient = values.Ambient; }
    private bool EnvironmentMatches()
    {
        var manager = EnvManager.Instance();
        return manager != null && (nint)manager == environmentAddress &&
            (nint)manager->EnvScene == environmentScene;
    }
    private bool ScopeMatches() => WatchPartyTvLocation.TryReadCurrent(out var here) &&
        here.World == location.World && here.Territory == location.Territory && here.Instance == location.Instance &&
        here.Ward == location.Ward && (!indoor || (here.Plot == location.Plot && here.Room == location.Room));

    private void Update(IFramework framework)
    {
        lock(gate)
        {
            if(disposed) return;
            UpdateTime();
            if(!indoor && !outdoor)
            {
                if(appliedOutdoor || atmosphereApplied || sceneLightValues.Count>0) ResetCore();
                return;
            }
            if(!ScopeMatches() || (indoor && (nint)CurrentIndoor() != indoorAddress) ||
                (outdoor && (!CanUseOutdoor || !EnvironmentMatches())))
            { ResetAll(); status = "Normal lighting restored after leaving the area."; }
            else if(outdoor && dimSceneLights) UpdateSceneLights();
        }
    }
    internal void Reset()
    {
        ResetAll();
    }
    private void ResetCore()
    {
        ResetExtraControls();
        // Never dereference a stored pointer after the game has replaced/unloaded its scene.
        if(indoor && ScopeMatches() && (nint)CurrentIndoor() == indoorAddress)
            RestoreIndoor(CurrentIndoor(),originalIndoor);
        if(appliedOutdoor && ScopeMatches() && EnvironmentMatches())
        {
            var lighting = (SceneLightingValues*)((byte*)environmentAddress + 0x78);
            if(lighting->SameColors(applied)) WriteColors(lighting,natural);
        }
        indoor = false; outdoor = false; appliedOutdoor = false;
        outdoorHook?.Disable();
        indoorAddress = environmentAddress = environmentScene = 0;
    }
}
