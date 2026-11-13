using System.Numerics;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    // Allow the full sidebar, including Media Player's lower divider, to sit
    // above the fixed utility/support footer at the smallest normal height.
    private const float MinimumWindowHeight = 833f + SidebarRowHeight;

    private Vector2 CurrentMinimumWindowSize =>
        new Vector2(MinimumWindowSize.X - CollapsedSidebarSavedWidth, MinimumWindowSize.Y) *
        (Plugin.Cfg.WindowSizePreset == UiWindowSizePreset.Compact1080 ? .5f : 1f);

    private static UiWindowSizePreset NormalizeWindowSizePreset(UiWindowSizePreset preset) => preset switch
    {
        UiWindowSizePreset.Uhd => preset,
        UiWindowSizePreset.Compact1080 => preset,
        _ => UiWindowSizePreset.Design,
    };

    private static Vector2 NamedWindowSize(UiWindowSizePreset preset) =>
        preset==UiWindowSizePreset.Uhd?WindowSize*1.5f:WindowSize;

    private Vector2 CurrentMaximumWindowSize => Plugin.Cfg.WindowSizePreset switch
    {
        UiWindowSizePreset.Uhd => UhdMaximumWindowSize,
        _ => MaximumWindowSize,
    };

    private Vector2 ClampWindowSize(Vector2 size)
    {
        var min=CurrentMinimumWindowSize;
        var max=CurrentMaximumWindowSize;
        return new(Math.Clamp(float.IsFinite(size.X)?size.X:WindowSize.X,min.X,max.X),
            Math.Clamp(float.IsFinite(size.Y)?size.Y:WindowSize.Y,min.Y,max.Y));
    }

    private void LoadWindowSizeFromConfig()
    {
        var cfg=Plugin.Cfg;
        var previousPreset=cfg.WindowSizePreset;
        // Retired 1080p/1440p and historical Custom values migrate to Design.
        // Retain saved dimensions rather than resizing them during migration.
        cfg.WindowSizePreset=NormalizeWindowSizePreset(previousPreset);
        var saved=new Vector2(cfg.WindowWidth,cfg.WindowHeight);
        var compact = cfg.WindowSizePreset == UiWindowSizePreset.Compact1080;
        var valid=float.IsFinite(saved.X) && float.IsFinite(saved.Y) &&
            saved.X >= (compact ? CurrentMinimumWindowSize.X : 650f) &&
            saved.Y >= (compact ? CurrentMinimumWindowSize.Y : PreviousMinimumWindowHeight);
        userWindowSize=ClampWindowSize(valid?saved:NamedWindowSize(cfg.WindowSizePreset));
        if(previousPreset!=cfg.WindowSizePreset || saved!=userWindowSize)
            SaveNormalWindowSize();
        userResized=true;
    }

    // Preserve each dimension's fraction of the maximum, matching 500/1000
    // becoming 1000/2000. Minimum bounds constrain the result, not its origin.
    internal static Vector2 ProportionalWindowSize(Vector2 size,Vector2 oldMaximum,
        Vector2 newMinimum,Vector2 newMaximum)
    {
        static float Scale(float value,float oldMax,float min,float max)
        {
            var ratio=float.IsFinite(value) && oldMax>0?Math.Clamp(value/oldMax,0f,1f):1f;
            return Math.Clamp(ratio*max,min,max);
        }
        return new(Scale(size.X,oldMaximum.X,newMinimum.X,newMaximum.X),
            Scale(size.Y,oldMaximum.Y,newMinimum.Y,newMaximum.Y));
    }

    private void ApplyWindowSizePreset(UiWindowSizePreset preset)
    {
        var current=ClampWindowSize(userWindowSize);
        var previousMaximum=CurrentMaximumWindowSize;
        Plugin.Cfg.WindowSizePreset=NormalizeWindowSizePreset(preset);
        RememberNormalWindowSize(ProportionalWindowSize(current,previousMaximum,
            CurrentMinimumWindowSize,CurrentMaximumWindowSize));
        userResized=true;
        SaveNormalWindowSize();
    }

    private void RememberNormalWindowSize(Vector2 size)
    {
        userWindowSize=ClampWindowSize(size);
        Plugin.Cfg.WindowWidth=userWindowSize.X;
        Plugin.Cfg.WindowHeight=userWindowSize.Y;
        windowSizeSavePending=true;
    }

    private void SaveNormalWindowSize()
    {
        // Saving geometry must never change the selected size level or turn a
        // 4K window into Custom (which would restore using Design's limits).
        Plugin.Cfg.WindowWidth=userWindowSize.X;
        Plugin.Cfg.WindowHeight=userWindowSize.Y;
        Plugin.Cfg.Save();
        windowSizeSavePending=false;
    }

    internal void PersistPositions()
    {
        var cfg=Plugin.Cfg;
        if(cfg.MaximizedPosition==maximizedPosition && cfg.MinimizedPosition==minimizedPosition &&
            !windowSizeSavePending)return;
        cfg.MaximizedPosition=maximizedPosition;
        cfg.MinimizedPosition=minimizedPosition;
        SaveNormalWindowSize();
    }

    private void SetMinimized(bool minimized,Vector2? normalWindowSize=null)
    {
        if(windowMinimized==minimized)return;
        if(minimized && normalWindowSize is { } capturedSize)
        {
            // Capture the full panel before switching to the compact capsule.
            RememberNormalWindowSize(capturedSize);
            SaveNormalWindowSize();
        }
        windowMinimized=minimized;
        if(minimized) youtubeVisibleMetadata.Reset();
        else userResized=true;
        RequestPosition(minimized?minimizedPosition:maximizedPosition);
    }
}
