using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private int HomeSearchDropdownRows(bool url) => url && HomeSearchHasParty ? 3 : 2;
    private float HomeSearchDropdownHeight(bool url) => Ui(16 + HomeSearchDropdownRows(url) * (url ? 44 : 38)
        + (HomeSearchDropdownRows(url) - 1) * 4);

    // A real window owns input outside the fixed header's clip rectangle.
    // Keep it unfocused while typing; its buttons take focus only on a click.
    private int? DrawHomeSearchDropdown(Vector2 position, float width, string query, bool url)
    {
        var rowHeight = Ui(url ? 44f : 38f);
        var gap = Ui(4f);
        var padding = Ui(8f);
        ImGui.SetNextWindowPos(position, ImGuiCond.Always);
        var rows = HomeSearchDropdownRows(url);
        ImGui.SetNextWindowSize(new Vector2(width, HomeSearchDropdownHeight(url)), ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(padding));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, Ui(10f));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, gap));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, CardBg);
        try
        {
            var visible = BeginWindowOverlay("##homeSearchSuggestions", ImGuiWindowFlags.NoTitleBar |
                ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoSavedSettings |
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoDocking);
            try
            {
                if (!visible) return null;
                int? selected = null;
                for (var i = 0; i < rows; i++)
                {
                    var origin = ImGui.GetCursorScreenPos();
                    var size = new Vector2(width - padding * 2, rowHeight);
                    if (ImGui.InvisibleButton("##homeSearchAction" + i, size)) selected = i;
                    var hovered = ImGui.IsItemHovered();
                    if (hovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    var draw = ImGui.GetWindowDrawList();
                    draw.AddRectFilled(origin, origin + size, ImGui.GetColorU32(hovered
                        ? new Vector4(Accent.X, Accent.Y, Accent.Z, .18f)
                        : new Vector4(.12f, .15f, .22f, 1f)), Ui(6f));
                    var label = url ? i == 0 ? "▶  Play video" : i == 1 ? "+  Add to your queue" : "+  Add to watch party queue"
                        : i == 0 ? $"▶  Search for \"{query}\" videos" : $"▶  Find Twitch channel \"{query}\"";
                    draw.PushClipRect(origin, origin + size, true);
                    draw.AddText(origin + new Vector2(Ui(12f), Ui(8f)), ImGui.GetColorU32(Vector4.One), label);
                    if (url) draw.AddText(origin + new Vector2(Ui(12f), Ui(24f)), ImGui.GetColorU32(MutedText), query);
                    draw.PopClipRect();
                }
                return selected;
            }
            finally { ImGui.End(); }
        }
        finally { ImGui.PopStyleColor(); ImGui.PopStyleVar(3); }
    }
}
