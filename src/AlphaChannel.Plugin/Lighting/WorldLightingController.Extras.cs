using System.Numerics;
using Scene = FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Render = FFXIVClientStructs.FFXIV.Client.Graphics.Render;

namespace AlphaChannel.Plugin.Lighting;

internal sealed unsafe partial class WorldLightingController
{
    private bool dimAtmosphere, dimSceneLights, atmosphereApplied;
    private SceneAtmosphereValues naturalAtmosphere, appliedAtmosphere;
    private readonly Dictionary<nint,LightSnapshot> sceneLightValues = [];
    private readonly SceneLightExemptions sceneLightExemptions = new();
    internal void SetSceneLightExempt(nint scene, nint render, bool exempt)
    {
        lock(gate)
        {
            if (!exempt) { sceneLightExemptions.Remove(scene, render); return; }
            sceneLightExemptions.Add(scene, render);
            // If a light is opted out after an earlier dimming pass, undo only
            // our own applied value and stop tracking it for restoration.
            if (sceneLightValues.TryGetValue(scene, out var saved) && saved.Render == render)
            {
                if (((Render.Light*)render)->Intensity == saved.Applied) ((Render.Light*)render)->Intensity = saved.Natural;
                sceneLightValues.Remove(scene);
            }
        }
    }
    private readonly record struct LightSnapshot(nint Render,float Natural,float Applied);
    private int sceneLightsFound, sceneLightsDimmed;
    private bool sceneTraversalLimited;
    private readonly Stack<nint> sceneStackScratch = new();
    private readonly HashSet<nint> sceneVisitedScratch = [];
    private readonly HashSet<nint> sceneLiveScratch = [];
    private readonly List<nint> sceneRemovedScratch = [];
    internal bool DimAtmosphere { get { lock(gate) return dimAtmosphere; } }
    internal bool DimSceneLights { get { lock(gate) return dimSceneLights; } }

    internal void SetExtraControls(bool atmosphere,bool lights)
    {
        lock(gate)
        {
            if(disposed) return;
            var enablingSceneLights = lights && !dimSceneLights;
            if(dimAtmosphere && !atmosphere) RestoreAtmosphere();
            if(dimSceneLights && !lights) RestoreSceneLights();
            dimAtmosphere=atmosphere; dimSceneLights=lights;
            if(outdoor && ScopeMatches() && EnvironmentMatches())
            {
                if(dimAtmosphere) ApplyAtmosphere((byte*)environmentAddress+0x58);
                if(enablingSceneLights) UpdateSceneLights();
            }
        }
    }
    private static SceneAtmosphereValues ReadAtmosphere(byte* state) =>
        new(*(Vector3*)(state+0xc0),*(Vector3*)(state+0x148),*(Vector3*)(state+0x154),*(Vector3*)(state+0xac));
    private static void WriteAtmosphere(byte* state,SceneAtmosphereValues value)
    {
        *(Vector3*)(state+0xc0)=value.Fog;
        *(Vector3*)(state+0x148)=value.CloudA;
        *(Vector3*)(state+0x154)=value.CloudB;
        *(Vector3*)(state+0xac)=value.Moon;
    }
    private void ApplyAtmosphere(byte* state)
    {
        var fresh=ReadAtmosphere(state);
        if(atmosphereApplied && fresh==appliedAtmosphere) fresh=naturalAtmosphere;
        if(!fresh.Valid) { dimAtmosphere=false; status="Atmosphere channels could not be validated; atmosphere dimming stopped."; return; }
        naturalAtmosphere=fresh; appliedAtmosphere=fresh.Scaled(outdoorLevel);
        WriteAtmosphere(state,appliedAtmosphere); atmosphereApplied=true;
    }
    private void RestoreAtmosphere()
    {
        if(atmosphereApplied && ScopeMatches() && EnvironmentMatches())
        {
            var state=(byte*)environmentAddress+0x58;
            if(ReadAtmosphere(state)==appliedAtmosphere) WriteAtmosphere(state,naturalAtmosphere);
        }
        atmosphereApplied=false;
    }
    // Discover and process each live light in the same traversal. Never use a
    // cached native pointer to access a light that may have been removed.
    private void VisitSceneLights(bool restoring)
    {
        sceneTraversalLimited=false;
        if(!WatchPartyTvLocation.IsLocalPlayerReady()) return;
        var world=Scene.World.Instance();
        if(world==null) return;
        var stack=sceneStackScratch; stack.Clear();
        var visited=sceneVisitedScratch; visited.Clear();
        if(world->ChildObject!=null) stack.Push((nint)world->ChildObject);
        while(stack.Count>0 && (restoring || visited.Count<8192))
        {
            var address=stack.Pop();
            if(!visited.Add(address)) continue;
            var obj=(Scene.Object*)address;
            if(obj->NextSiblingObject!=null) stack.Push((nint)obj->NextSiblingObject);
            if(obj->ChildObject!=null) stack.Push((nint)obj->ChildObject);
            if(obj->GetObjectType()!=Scene.ObjectType.Light) continue;
            var sceneLight=(Scene.Light*)obj;
            if(sceneLight->RenderLight==null) continue;
            var render=(nint)sceneLight->RenderLight;
            if(!restoring) { sceneLightsFound++; sceneLiveScratch.Add(address); }
            if(sceneLightExemptions.Contains(address,render)) continue;
            var light=(Render.Light*)render;
            if(restoring)
            {
                if(sceneLightValues.TryGetValue(address,out var saved) && saved.Render==render && light->Intensity==saved.Applied)
                    light->Intensity=saved.Natural;
                continue;
            }
            var fresh=light->Intensity;
            if(!float.IsFinite(fresh) || fresh<0 || fresh>100000) continue;
            if(sceneLightValues.TryGetValue(address,out var previous) && previous.Render==render && fresh==previous.Applied)
                fresh=previous.Natural;
            var scaled=fresh*outdoorLevel;
            light->Intensity=scaled;
            sceneLightValues[address]=new(render,fresh,scaled);
            sceneLightsDimmed++;
        }
        sceneTraversalLimited=stack.Count>0;
    }
    private void UpdateSceneLights()
    {
        // At normal brightness restore once, then skip discovery altogether.
        // Any dimming, including fades just below 100%, still runs every frame:
        // the game rewrites intensity each frame and throttling causes flashing.
        if(outdoorLevel>=1f) { RestoreSceneLights(); return; }
        sceneLightsFound=sceneLightsDimmed=0;
        var live=sceneLiveScratch; live.Clear();
        VisitSceneLights(restoring: false);
        // An incomplete scan cannot prove that an unseen light was destroyed.
        if (!sceneTraversalLimited)
        {
            sceneRemovedScratch.Clear();
            foreach (var old in sceneLightValues.Keys) if (!live.Contains(old)) sceneRemovedScratch.Add(old);
            foreach (var old in sceneRemovedScratch) sceneLightValues.Remove(old);
        }
    }
    private void RestoreSceneLights()
    {
        if(sceneLightValues.Count>0 && ScopeMatches() && EnvironmentMatches())
            VisitSceneLights(restoring: true);
        sceneLightValues.Clear(); sceneLightsFound=sceneLightsDimmed=0;
        sceneTraversalLimited=false;
    }
    private void ResetExtraControls() { RestoreAtmosphere(); RestoreSceneLights(); }
    private string ExtraDiagnostics => $"Dim atmosphere: {dimAtmosphere}, Dim scene lights: {dimSceneLights}\n" +
        $"Scene lights found: {sceneLightsFound}, dimmed: {sceneLightsDimmed}, traversal limited: {sceneTraversalLimited}\n" +
        $"Natural fog: {naturalAtmosphere.Fog}\nNatural clouds: {naturalAtmosphere.CloudA} / {naturalAtmosphere.CloudB}\n";
}
