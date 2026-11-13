using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using System.Numerics;

namespace AlphaChannel.Plugin;

// All state is scoped to our draw calls. The cache is shared by the main UI,
// mini windows and dock, but never installed into Dalamud's global font atlas.
internal static class UiFonts
{
    private static ManagedUiFontCache? cache;
    [ThreadStatic] private static Context? current;
    internal static bool Active => current is not null;
    internal static IDisposable Empty { get; } = new EmptyScope();
    private sealed class Context(float pixels)
    {
        internal readonly float BasePixels=pixels;
        internal UiFontFace Face=UiFontFace.Text;
    }

    internal static IDisposable Begin(bool preloadHeadings=false)
    {
        cache??=new ManagedUiFontCache(Plugin.PluginInterface.UiBuilder,
            Path.Combine(Plugin.PluginInterface.AssemblyLocation.DirectoryName!,"Fonts"));
        RefreshMetrics();
        if(preloadHeadings)cache.PreloadHeadings(ImGui.GetFontSize());
        cache.Tick();
        var previous=current;
        current=new Context(ImGui.GetFontSize());
        return new ContextScope(previous,cache.Push(UiFontFace.Text,current.BasePixels));
    }

    internal static IDisposable PushRelative(float relative,bool emphasis=false)
    {
        if(current is null)return new LegacyScope(relative);
        var face=current.Face==UiFontFace.Icon?UiFontFace.Icon:
            emphasis||relative>1?UiFontFace.Emphasis:UiFontFace.Text;
        return Push(face,UiFontSizing.Pixels(current.BasePixels,relative));
    }

    internal static IDisposable PushRegularRelative(float relative) => current is null
        ? new LegacyScope(relative)
        : Push(UiFontFace.Text, UiFontSizing.Pixels(current.BasePixels, relative));

    internal static IDisposable PushIcon() => current is null?ImRaii.PushFont(UiBuilder.IconFont):Push(UiFontFace.Icon,ImGui.GetFontSize());
    internal static IDisposable PushIcon(float pixels) => current is null?ImRaii.PushFont(UiBuilder.IconFont):Push(UiFontFace.Icon,pixels);

    private static IDisposable Push(UiFontFace face,float pixels)
    {
        var context=current!;var previous=context.Face;
        var font=cache!.Push(face,pixels);
        context.Face=face;
        return new FaceScope(context,previous,font);
    }

    // Orphan resets existed at several page boundaries. Our body font already
    // has its final pixel size; setting the window scale to 1 would reapply DPI.
    internal static void ResetRelativeScale(){if(current is null)ImGui.SetWindowFontScale(1);}

    // Begin calls may select another viewport/DPI or start a fresh window scale.
    // Keep the inherited physical text size; the atlas has already baked it.
    internal static bool Window(float pixels,bool visible)
    {if(visible)NormalizeWindow(pixels);return visible;}
    internal static ImRaii.ChildDisposable Window(float pixels,ImRaii.ChildDisposable child)
    {if(child.Success)NormalizeWindow(pixels);return child;}
    internal static ImRaii.PopupDisposable Window(float pixels,ImRaii.PopupDisposable popup)
    {if(popup.Success)NormalizeWindow(pixels);return popup;}
    internal static ImRaii.TooltipDisposable Window(float pixels,ImRaii.TooltipDisposable tooltip)
    {NormalizeWindow(pixels);return tooltip;}
    private static void NormalizeWindow(float pixels)
    {
        if(!Active)return;
        ImGui.SetWindowFontScale(1);
        ImGui.SetWindowFontScale(pixels/MathF.Max(1,ImGui.GetFontSize()));
    }

    internal static void SetTooltip(string? text)
    {
        if(string.IsNullOrEmpty(text))return;
        if(!Active){ImGui.SetTooltip(text);return;}
        using var font=Push(UiFontFace.Text,current!.BasePixels);
        BeginTooltip();ImGui.TextUnformatted(text);ImGui.EndTooltip();
    }
    internal static void BeginTooltip()
    {var pixels=ImGui.GetFontSize();ImGui.BeginTooltip();NormalizeWindow(pixels);}

    internal static void DrawIcon(ImDrawListPtr draw,float pixels,Vector2 p,uint ink,string text,float wrap=0)
    {
        if(current is null){draw.AddText(UiBuilder.IconFont,pixels,p,ink,text,wrap);return;}
        using var font=Push(UiFontFace.Icon,pixels);
        draw.AddText(ImGui.GetFont(),ImGui.GetFontSize(),Vector2.Round(p),ink,text,wrap);
    }

    internal static unsafe void DrawCenteredIcon(ImDrawListPtr draw,float pixels,Vector2 center,uint ink,string text)
    {
        using var scope=PushIcon(pixels);
        var font=ImGui.GetFont();
        var drawPixels=Active ? ImGui.GetFontSize() : pixels;
        var glyph=text.Length==1 ? font.FindGlyphNoFallback(text[0]) : null;
        Vector2 offset;
        if(glyph!=null)
            offset=new Vector2((glyph->X0+glyph->X1)*.5f,(glyph->Y0+glyph->Y1)*.5f)*(drawPixels/font.FontSize);
        else
            offset=ImGui.CalcTextSize(text)*(.5f*drawPixels/ImGui.GetFontSize());
        draw.AddText(font,drawPixels,Vector2.Round(center-offset),ink,text);
    }

    internal static void DrawText(ImDrawListPtr draw,float pixels,Vector2 p,uint ink,string text,float wrap=0)
    {
        if(current is null){draw.AddText(ImGui.GetFont(),pixels,p,ink,text,wrap);return;}
        if(MathF.Abs(pixels-ImGui.GetFontSize())<.01f)
        {draw.AddText(ImGui.GetFont(),ImGui.GetFontSize(),Vector2.Round(p),ink,text,wrap);return;}
        using var font=Push(current.Face,pixels);
        draw.AddText(ImGui.GetFont(),ImGui.GetFontSize(),Vector2.Round(p),ink,text,wrap);
    }

    // Compact controls deliberately shrink their whole label to fit. Preserve
    // that exact fit instead of rounding upward or snapping to a design group.
    internal static void DrawFittedText(ImDrawListPtr draw,float pixels,Vector2 p,uint ink,string text)=>
        draw.AddText(ImGui.GetFont(),pixels,p,ink,text);

    internal static void Dispose(){cache?.Dispose();cache=null;}
    internal static void RefreshMetrics(){ImGui.PushFont(ImGui.GetFont());ImGui.PopFont();}

    private sealed class ContextScope(Context? previous,IDisposable font):IDisposable
    {
        private bool disposed;
        public void Dispose(){if(disposed)return;disposed=true;font.Dispose();current=previous;}
    }
    private sealed class EmptyScope:IDisposable {public void Dispose(){}}
    private sealed class FaceScope(Context context,UiFontFace previous,IDisposable font):IDisposable
    {
        private bool disposed;
        public void Dispose(){if(disposed)return;disposed=true;font.Dispose();context.Face=previous;}
    }
    private sealed class LegacyScope:IDisposable
    {
        private readonly float previous=ImGuiP.GetCurrentWindow().FontWindowScale;
        private bool disposed;
        internal LegacyScope(float scale){ImGui.SetWindowFontScale(scale);}
        public void Dispose(){if(disposed)return;disposed=true;ImGui.SetWindowFontScale(previous);}
    }
}
