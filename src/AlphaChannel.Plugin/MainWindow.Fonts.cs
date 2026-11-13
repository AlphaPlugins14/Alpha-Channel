using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private static IDisposable BeginUiFonts()=>UiFonts.Active?UiFonts.Empty:UiFonts.Begin();
    private static IDisposable PushUiFont(float relative=1,bool emphasis=false)=>UiFonts.PushRelative(relative,emphasis);

    private static void DrawUiIcon(FontAwesomeIcon icon,float pixels,Vector2 position,Vector4 colour)=>
        UiFonts.DrawIcon(ImGui.GetWindowDrawList(),pixels,position,ImGui.GetColorU32(colour),icon.ToIconString());

    private static Vector2 MeasureUiIcon(FontAwesomeIcon icon,float pixels)
    {
        using var font=UiFonts.PushIcon(pixels);
        return ImGui.CalcTextSize(icon.ToIconString());
    }

    private static void DrawUiAvatarIcon(Vector2 center,float diameter,string? name)
    {
        var icon=Enum.TryParse<FontAwesomeIcon>(name,out var parsed)&&Enum.IsDefined(parsed)?parsed:FontAwesomeIcon.User;
        UiFonts.DrawCenteredIcon(ImGui.GetWindowDrawList(),diameter*.55f,center,
            ImGui.GetColorU32(Vector4.One),icon.ToIconString());
    }
}
