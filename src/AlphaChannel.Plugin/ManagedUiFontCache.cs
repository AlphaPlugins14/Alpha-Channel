using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Utility.Raii;
using System.Diagnostics;

namespace AlphaChannel.Plugin;

// Accessed on the draw thread. The isolated atlas never rebuilds Dalamud's
// fonts or changes fonts used by other plugins.
internal sealed class ManagedUiFontCache : IDisposable
{
    private const int MaximumEntries = 64;
    private readonly IFontAtlas atlas;
    private readonly string directory;
    private readonly Dictionary<(UiFontFace Face,int Pixels),Entry> entries = [];
    private long changedAt;
    private long dirtySince;
    private bool dirty;
    private Task? build;
    private bool disposed;

    private sealed class Entry(IFontHandle handle)
    {
        internal readonly IFontHandle Handle = handle;
        internal readonly RetainedUiFont Font = new(handle);
        internal int LastFrame;
    }

    internal ManagedUiFontCache(IUiBuilder builder, string fontDirectory)
    {
        directory = fontDirectory;
        atlas = builder.CreateFontAtlas(FontAtlasAutoRebuildMode.Disable,false,"Alpha Channel UI sizes");
        atlas.RebuildRecommend += RecommendBuild;
    }

    private void RecommendBuild()
    {
        changedAt=Stopwatch.GetTimestamp();
        if(!dirty)dirtySince=changedAt;
        dirty=true;
    }

    internal void Tick()
    {
        if (disposed) return;
        if (build is { IsCompleted:true })
        {
            if (build.IsFaulted) AepLog.Error("[UI fonts] Font atlas build failed.");
            build=null;
        }
        // Resolve existing entries before drawing any window. Refresh unused
        // sizes too so an off-page font cannot pin an old whole-atlas texture
        // after a newer generation is ready. ForFrame is idempotent when mini
        // windows enter this cache again during the same ImGui frame.
        var frame=ImGui.GetFrameCount();
        foreach (var entry in entries.Values) entry.Font.ForFrame(frame);
        if (!dirty || build is not null ||
            (Stopwatch.GetElapsedTime(changedAt).TotalMilliseconds < 120&&Stopwatch.GetElapsedTime(dirtySince).TotalMilliseconds < 600)) return;
        dirty=false;
        build=atlas.BuildFontsAsync();
    }

    private Entry Get(UiFontFace face,int pixels)
    {
        var key=(face,pixels);
        if (!entries.TryGetValue(key,out var entry))
        {
            // Do not dispose handles used in this or the preceding draw frame.
            if (entries.Count >= MaximumEntries)
            {
                var oldest=entries.Where(pair=>pair.Value.LastFrame<ImGui.GetFrameCount()-1)
                    .OrderBy(pair=>pair.Value.LastFrame).FirstOrDefault();
                if (oldest.Value is not null) { oldest.Value.Font.Dispose(); oldest.Value.Handle.Dispose(); entries.Remove(oldest.Key); }
                else
                {
                    // A pathological frame can request more than the entire
                    // cache. Temporarily scale a nearby same-face font instead.
                    var closest=entries.Where(pair=>pair.Key.Face==face)
                        .MinBy(pair=>Math.Abs(pair.Key.Pixels-pixels)).Value;
                    closest??=entries.Values.First();
                    closest.LastFrame=ImGui.GetFrameCount();
                    return closest;
                }
            }
            var handle=atlas.NewDelegateFontHandle(step=>step.OnPreBuild(toolkit=>
            {
                var config=new SafeFontConfig { SizePx=pixels };
                if (face==UiFontFace.Icon) toolkit.Font=toolkit.AddFontAwesomeIconFont(config);
                else
                {
                    // Include punctuation used throughout Events (ellipsis,
                    // em dash, bullets) as well as Latin/Greek/Cyrillic names.
                    config.GlyphRanges=[0x20,0x024f,0x0370,0x052f,0x1e00,0x1eff,0x2000,0x206f,0x20a0,0x20cf,0x2100,0x214f,0x2190,0x21ff,0x2500,0x27bf,0];
                    config.MergeFont=toolkit.AddFontFromFile(Path.Combine(directory,
                        face==UiFontFace.Emphasis?"Inter-SemiBold.ttf":"Inter-Regular.ttf"),config);
                    toolkit.Font=config.MergeFont;
                    // Some controls mix icon glyphs and words in one label.
                    toolkit.AddFontAwesomeIconFont(config);
                    toolkit.AddGameSymbol(config);
                    // Names, worlds and media titles may be Korean or Japanese
                    // even on an English client. Merge at the same baked size;
                    // Inter remains the primary face for existing UI glyphs.
                    var cjk = new SafeFontConfig
                    {
                        SizePx = pixels,
                        MergeFont = toolkit.Font,
                        GlyphRanges = UiInternationalGlyphRanges.Cjk,
                    };
                    toolkit.AddDalamudAssetFont(Dalamud.DalamudAsset.NotoSansCjkMedium, cjk);
                    toolkit.AttachExtraGlyphsForDalamudLanguage(config);
                }
            }));
            entries[key]=entry=new Entry(handle);
            RecommendBuild();
        }
        entry.LastFrame=ImGui.GetFrameCount();
        return entry;
    }

    internal void PreloadHeadings(float basePixels)
    {
        if(disposed)return;
        // These are the three baked design groups used by the hub banners.
        // Touch them while the main window is open so the LRU cannot evict an
        // off-page heading. Size changes still use the normal asynchronous build.
        Get(UiFontFace.Emphasis,UiFontSizing.Pixels(basePixels,1.80f));
        Get(UiFontFace.Emphasis,UiFontSizing.Pixels(basePixels,2.05f));
        Get(UiFontFace.Emphasis,UiFontSizing.Pixels(basePixels,2.60f));
    }

    internal IDisposable Push(UiFontFace face,float pixels)
    {
        var size=UiFontSizing.RoundPixels(pixels);
        var font=Get(face,size).Font.ForFrame(ImGui.GetFrameCount());
        return new UiFontRenderScope(font,face,size);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed=true;
        atlas.RebuildRecommend-=RecommendBuild;
        foreach (var entry in entries.Values) { entry.Font.Dispose(); entry.Handle.Dispose(); }
        entries.Clear();
        atlas.Dispose();
    }
}
