namespace AlphaChannel.Plugin.Lighting;

internal sealed unsafe partial class WorldLightingController
{
    private nint CopyDetour(nint destination,nint source)
    {
        Interlocked.Increment(ref copyCallbacks);
        try
        {
        lock(gate)
        {
            if(outdoor) { copyCalls++; lastDestinationOffset = (long)(destination - environmentAddress); }
            // Only touch the live environment block, not arbitrary temporary copies.
            var target = outdoor && !disposed && WatchPartyTvLocation.IsLocalPlayerReady() && EnvironmentMatches() &&
                destination == environmentAddress + 0x58;
            var lighting = (SceneLightingValues*)(destination + 0x20);
            // Avoid accumulating darkening if the engine copies an already-scaled block onto itself.
            if(target && appliedOutdoor && lighting->SameColors(applied)) WriteColors(lighting,natural);
            if(target && atmosphereApplied && ReadAtmosphere((byte*)destination)==appliedAtmosphere)
                WriteAtmosphere((byte*)destination,naturalAtmosphere);
            var result = copyOriginal!(destination,source);
            if(target)
            {
                var fresh = *lighting;
                if(appliedOutdoor && fresh.SameColors(applied)) fresh = natural;
                if(fresh.Valid) { ApplyOutdoor(lighting,fresh); if(dimAtmosphere) ApplyAtmosphere((byte*)destination); outdoorUpdates++; }
                else { outdoor = false; status = "Lighting validation failed; outdoor override stopped."; }
            }
            return result;
        }
        }
        finally { Interlocked.Decrement(ref copyCallbacks); }
    }
    public void Dispose()
    {
        lock(gate) { if(disposed) return; DisposeTime(); ResetCore(); disposed = true; }
        Plugin.Framework.Update -= Update;
        var hook = outdoorHook;
        if (hook is null) return;
        // Disable first, then drain outside gate. A callback waiting for gate
        // still owns the original trampoline until it returns.
        _ = Task.Run(() =>
        {
            SpinWait.SpinUntil(() => Volatile.Read(ref copyCallbacks) == 0);
            try { hook.Dispose(); }
            catch { AepLog.Warning("[Lighting] Disabled environment hook could not be released."); }
            GC.KeepAlive(this);
        });
    }
}
