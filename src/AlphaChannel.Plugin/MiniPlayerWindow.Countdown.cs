using AlphaChannel.Plugin.Core;
using Dalamud.Bindings.ImGui;
using System.Numerics;

namespace AlphaChannel.Plugin;

internal sealed partial class MiniPlayerWindow
{
    private static void DrawCountdownPreview(ImDrawListPtr draw, Vector2 origin, Vector2 size, PartyCountdownView countdown)
    {
        draw.PushClipRect(origin, origin + size, true);
        draw.AddRectFilled(origin, origin + size, countdown.Halloween ? 0xff240b18 : 0xff27140c, 7);
        var center = origin + size * .5f; var radius = size.Y * .32f;
        draw.AddCircle(center, radius, countdown.Halloween ? 0xff702f69 : 0xff50311d, 64, 3);
        draw.PathArcTo(center, radius, -MathF.PI / 2, -MathF.PI / 2 + MathF.Tau * MathF.Max(.001f, countdown.Progress), 64);
        draw.PathStroke(countdown.Halloween ? 0xff2384ff : 0xffff9c46, ImDrawFlags.None, 3);
        var fontSize = size.Y * .23f;
        var textSize = ImGui.CalcTextSize(countdown.Clock) * (fontSize / ImGui.GetFontSize());
        UiFonts.DrawText(draw, fontSize, center - textSize * .5f, uint.MaxValue, countdown.Clock);
        var label = countdown.Halloween ? "TRICK OR TREAT" : "Starting soon";
        draw.AddText(new(center.X - ImGui.CalcTextSize(label).X / 2, origin.Y + size.Y * .87f), 0xffeec6a7, label);
        draw.PopClipRect();
    }
}
