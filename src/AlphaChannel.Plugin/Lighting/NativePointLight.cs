using AlphaChannel.Plugin.Lighting;
using AlphaChannel.Plugin.Core;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Scene = FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Render = FFXIVClientStructs.FFXIV.Client.Graphics.Render;

namespace AlphaChannel.Plugin.Lighting;

// One game-native light, updated on the framework thread. Creation defaults
// follow Brio's LightingService; native ownership is ours, not a GPose slot.
// https://github.com/Etheirys/Brio/blob/main/Brio/Game/World/LightingService.cs
internal sealed unsafe class NativePointLight : IDisposable
{
    private readonly WorldLightingController lighting = Plugin.LightingControls;
    private NativeLightPose? desired;
    private Scene.Light* light;
    private nint renderAddress;
    private delegate nint DestroyLight(Scene.Light* self, byte free);
    private static readonly object hookGate=new();
    private static readonly Dictionary<nint,NativePointLight> owners=[];
    private static Hook<DestroyLight>? sharedDestructorHook;
    private static int controllerCount;
    private NativeLightPose? applied;
    private bool failed, disposed;
    private readonly string label;
    private readonly bool autoUpdate;
    private readonly string activeStatus,offStatus;
    internal bool Failed => failed;
    // Beat-gated shows reuse an existing native light at zero intensity between
    // flashes. A null desired pose (Stop), zoning and Dispose still destroy it.
    internal bool RetainAtZero { get; set; }
    internal string Status { get; private set; } = "Orange glow ready · exempt from scene-light dimming.";

    internal NativePointLight(string label = "Orange glow", bool autoUpdate = true)
    {
        this.autoUpdate=autoUpdate;
        this.label=label; activeStatus=label+" active · exempt from scene-light dimming.";
        offStatus=label+" off · remains exempt whenever active.";
        Status=label+" ready · exempt from scene-light dimming.";
        lock(hookGate) controllerCount++;
        if(autoUpdate) Plugin.Framework.Update += Update;
    }
    internal void SetDesired(NativeLightPose? pose) => Volatile.Write(ref desired, pose);
    internal void Stop() { SetDesired(null); failed = false; }

    internal void UpdateNow() => Update(Plugin.Framework);

    private void Update(IFramework framework)
    {
        var pose = Volatile.Read(ref desired);
        // Most controllers are idle for substantial parts of the song. Avoid
        // area lookups, allocations and native calls when there is no light.
        if(light==null&&(pose is null||pose.Intensity<=.0001f))
        { if(!failed) Status=offStatus;return; }
        var here = WatchPartyTvLocation.TryReadCurrent(out var current);
        if (disposed || pose is null || pose.Intensity <= .0001f && !RetainAtZero || !here)
        {
            Destroy();
            if (!failed) Status = offStatus;
            return;
        }
        if (failed) return;
        try
        {
            if (light == null && !Create()) return;
            var render = light->RenderLight;
            if (render == null) { Status = label+" has no render resource."; Destroy(); failed = true; return; }
            if (renderAddress != (nint)render)
            {
                lighting.SetSceneLightExempt((nint)light, renderAddress, false);
                renderAddress = (nint)render;
                lighting.SetSceneLightExempt((nint)light, renderAddress, true);
                applied=null;
            }
            if(pose==applied) return;
            var moved=applied is null||pose.Position!=applied.Position||pose.Range!=applied.Range;
            if(moved) {
                light->Position = new(pose.Position.X, pose.Position.Y, pose.Position.Z);
                light->IsTransformChanged = true;
            }
            var color=pose.Color??new Vector3(20,4.4f,.25f);
            render->Color = new(color.X,color.Y,color.Z);
            render->Intensity = pose.Intensity;
            render->Range = pose.Range;
            if(moved) { light->UpdateTransforms(true);light->UpdateCulling(); }
            light->UpdateMaterials();
            applied=pose;
            Status = activeStatus;
        }
        catch (Exception ex)
        {
            failed = true;
            Status = label+" unavailable. Restart the preview to retry.";
            AepLog.Warning("[Scene Mapper] Pumpkin light failed: " + ex.GetType().Name);
            Destroy();
        }
    }

    private bool Create()
    {
        light = Scene.Light.Create(Render.LightShape.PointLight, (byte*)null);
        if (light == null) { failed = true; Status = "The game could not create "+label+"."; return false; }
        // Observe native teardown too, so zone unloading cannot leave a stale
        // pointer or an exemption that accidentally applies to a reused address.
        lock(hookGate)
        {
            if(sharedDestructorHook is null) {
                var destructor=**(nint**)light;
                sharedDestructorHook=Plugin.InteropProvider.HookFromAddress<DestroyLight>(destructor,OnDestroyed);
                sharedDestructorHook.Enable();
            }
            owners[(nint)light]=this;
        }
        var render = light->RenderLight;
        if (render == null) { Destroy(); failed = true; Status = "The game could not create "+label+"'s render resource."; return false; }
        renderAddress = (nint)render;
        lighting.SetSceneLightExempt((nint)light, renderAddress, true);
        light->Rotation = new(0, 0, 0, 1);
        light->Scale = new(1, 1, 1);
        render->Transform = (FFXIVClientStructs.FFXIV.Client.Graphics.Transform*)(&light->Position);
        render->LightFlags = Render.LightFlags.SpecularHighlights;
        render->LightShape = Render.LightShape.PointLight;
        render->Intensity = 0;
        render->FalloffType = Render.LightFalloffType.Quadratic;
        render->FalloffFactor = 1;
        render->ShadowPlaneNear = .01f;
        render->ShadowPlaneFar = 30;
        render->CharacterShadowRange = 60;
        render->FlatLightSkewAngleDegrees = new(0, 0);
        return true;
    }

    private static nint OnDestroyed(Scene.Light* self, byte free)
    {
        DestroyLight original;
        lock(hookGate)
        {
            original=sharedDestructorHook!.Original;
            if(owners.Remove((nint)self,out var owner)) {
                owner.lighting.SetSceneLightExempt((nint)self,owner.renderAddress,false);
                owner.light=null;owner.renderAddress=0;owner.applied=null;
            }
        }
        return original(self, free);
    }

    private void Destroy()
    {
        if (light == null) return;
        var owned = light;
        lock(hookGate) owners.Remove((nint)owned);
        lighting.SetSceneLightExempt((nint)owned, renderAddress, false);
        light = null; renderAddress = 0; applied=null;
        owned->CleanupRender();
        owned->Dtor(1);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        SetDesired(null);
        if(autoUpdate) Plugin.Framework.Update -= Update;
        // RunOnFrameworkThread executes immediately when already on that thread.
        void Cleanup()
        {
            try { Destroy(); }
            finally {
                lock(hookGate) {
                    if(--controllerCount==0) {
                        sharedDestructorHook?.Dispose();sharedDestructorHook=null;owners.Clear();
                    }
                }
            }
        }
        if (Plugin.Framework.IsFrameworkUnloading) Cleanup();
        else Plugin.Framework.RunOnFrameworkThread(Cleanup).GetAwaiter().GetResult();
    }
}
