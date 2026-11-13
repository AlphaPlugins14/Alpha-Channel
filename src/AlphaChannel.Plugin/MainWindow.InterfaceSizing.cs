using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private const string InterfaceSizingChoiceHint = "Try both and choose the text size and spacing you prefer.";
    private const string InterfaceSizingDragHint = "You can drag this popup around to view different areas";
    private bool interfaceSizingPrepared;
    private bool showInterfaceSizingComparison;
    private Vector2 interfaceSizingDisplay;
    private bool interfaceSizingRecommendsNative;
    private float interfaceSizingSplashScale = 1f;
    private Vector2? interfaceSizingPopupOffset;
    private bool interfaceSizingPopupDragging;
    private Vector2 interfaceSizingPopupDragOffset;
    private Vector2 interfaceSizingFormMinimum;
    private Vector2 interfaceSizingFormMaximum;

    private void PrepareInterfaceSizingSetup()
    {
        if (interfaceSizingPrepared) return;
        interfaceSizingPrepared = true;
        if (Plugin.Cfg.HasCompletedInterfaceSizingSetup) return;

        // Choose a readable starting mode before the first window is rendered.
        // Existing users only need the chooser; new users finish onboarding first.
        DetectInterfaceSizingDisplay();
        if (!Plugin.Cfg.HasCompletedInterfaceSizingSetup)
            Plugin.Cfg.NativeUiSizing = interfaceSizingRecommendsNative;
        if (!showingFirstLaunch)
            OpenInterfaceSizingComparison();
    }

    private void DetectInterfaceSizingDisplay()
    {
        interfaceSizingDisplay = ImGui.GetIO().DisplaySize;
        interfaceSizingRecommendsNative = NativeUiSizingMetrics.RecommendsNative(
            interfaceSizingDisplay.X, interfaceSizingDisplay.Y);
        interfaceSizingSplashScale = Math.Clamp(interfaceSizingDisplay.X / 1920f, 1f, 1.5f);
    }

    private void OpenInterfaceSizingComparison()
    {
        DetectInterfaceSizingDisplay();
        currentPage = HomePage.Home;
        interfaceSizingPopupOffset = null;
        interfaceSizingPopupDragging = false;
        showInterfaceSizingComparison = true;
    }

    private Vector2 InterfaceSizingSplashSize()
    {
        var available = ImGui.GetMainViewport().WorkSize - new Vector2(40f);
        return Vector2.Min(FirstLaunchWindowSize * interfaceSizingSplashScale,
            Vector2.Max(new Vector2(320f, 240f), available));
    }

    private void DrawInterfaceSizingLiveOverlay()
    {
        var position = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        var previousScale = activeLayoutScale;
        var previousNative = activeNativeUiSizing;
        // Inherit the main window's selected sizing mode and font settings.
        // Applying another Native font scope here would normalize Standard too.
        try
        {
            ImGui.SetNextWindowPos(position + new Vector2(6f), ImGuiCond.Always);
            ImGui.SetNextWindowSize(size - new Vector2(12f), ImGuiCond.Always);
            ImGui.SetNextWindowBgAlpha(0f);
            using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            var visible = BeginWindowOverlay("##interfaceSizingLiveOverlay",
                ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
                ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings |
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
                ImGuiWindowFlags.NoDocking);
            try
            {
                if (!visible) return;
                // Clearly signal that Home is inactive, while keeping it visible.
                var draw = ImGui.GetWindowDrawList();
                draw.AddRectFilled(position, position + size,
                    ImGui.GetColorU32(new Vector4(.01f, .02f, .05f, .42f)));
                // A faint diagonal veil helps distinguish covered content from Home.
                var textureColour = ImGui.GetColorU32(new Vector4(.65f, .73f, .86f, .025f));
                var textureStep = MathF.Max(8f, Ui(10f));
                for (var x = -size.Y; x < size.X; x += textureStep)
                {
                    var startY = MathF.Max(0f, -x);
                    var endY = MathF.Min(size.Y, size.X - x);
                    draw.AddLine(position + new Vector2(x + startY, startY),
                        position + new Vector2(x + endY, endY), textureColour);
                }
                var formWidth = MathF.Max(Ui(410f),
                    ImGui.CalcTextSize("Choose your interface size").X * 1.25f + Ui(66f));
                formWidth = MathF.Min(formWidth, size.X - Ui(24f));
                var textWidth = formWidth - Ui(36f);
                var formHeight = Ui(32f + 32f + 42f + 12f) + InterfaceSizingChoiceHeight() +
                    ImGui.GetFontSize() * 2.85f + ImGui.GetStyle().ItemSpacing.Y * 6f +
                    ImGui.CalcTextSize(InterfaceSizingChoiceHint, false, textWidth).Y +
                    ImGui.CalcTextSize(InterfaceSizingDragHint, false, textWidth).Y;
                var formSize = Vector2.Min(new Vector2(formWidth, formHeight), size - UiVec(24f, 24f));
                var initialOffset = (size - formSize) * .5f;
                if (interfaceSizingPopupDragging && ImGui.IsMouseDown(ImGuiMouseButton.Left))
                    interfaceSizingPopupOffset = ImGui.GetMousePos() - position - interfaceSizingPopupDragOffset;
                if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) interfaceSizingPopupDragging = false;
                var offset = interfaceSizingPopupOffset ?? initialOffset;
                offset = Vector2.Clamp(offset, UiVec(10f, 10f),
                    Vector2.Max(UiVec(10f, 10f), size - formSize - UiVec(10f, 10f)));
                if (interfaceSizingPopupOffset.HasValue) interfaceSizingPopupOffset = offset;
                interfaceSizingFormMinimum = position + offset;
                interfaceSizingFormMaximum = interfaceSizingFormMinimum + formSize;
                DrawInterfaceSizingPopup(formSize);
                // Leave a narrow edge free for the main window's resize grips.
                DrawInterfaceSizingDragAreas(position + new Vector2(6f), size - new Vector2(12f), position);
            }
            finally { ImGui.End(); }
        }
        finally
        {
            activeLayoutScale = previousScale;
            activeNativeUiSizing = previousNative;
        }
    }

    private void DrawInterfaceSizingPopup(Vector2 formSize)
    {
        ImGui.SetCursorScreenPos(interfaceSizingFormMinimum);
        using var rounding = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(10f))
            .Push(ImGuiStyleVar.WindowPadding, UiVec(18f, 16f));
        using var colours = ImRaii.PushColor(ImGuiCol.ChildBg, CardBg)
            .Push(ImGuiCol.Border, Accent);
        using var form = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##interfaceSizingLiveChooser", formSize, true,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse));
        if (!form) return;
        var header = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##moveSizingPopup", new Vector2(ImGui.GetContentRegionAvail().X, Ui(28f)));
        if (ImGui.IsItemClicked())
        {
            interfaceSizingPopupDragging = true;
            interfaceSizingPopupDragOffset = ImGui.GetMousePos() - interfaceSizingFormMinimum;
        }
        using (UiFonts.PushIcon())
            UiFonts.DrawText(ImGui.GetWindowDrawList(), Ui(20f), header, ImGui.GetColorU32(AccentHover), FontAwesomeIcon.Desktop.ToIconString());
        ImGui.SetCursorScreenPos(header + UiVec(30f, 0f));
        using var uiFontScale7975=UiFonts.PushRelative(1.25f);
        ImGui.TextUnformatted("Choose your interface size");
        uiFontScale7975.Dispose();
        ImGui.SetCursorScreenPos(header + UiVec(0f, 32f));
        ImGui.TextColored(MutedText, $"Your display: {interfaceSizingDisplay.X:0} × {interfaceSizingDisplay.Y:0}");
        var recommendation = interfaceSizingRecommendsNative ? "Native" : "Standard";
        ImGui.TextColored(AccentHover, $"Suggested for your display: {recommendation}");
        var width = (ImGui.GetContentRegionAvail().X - Ui(10f)) * .5f;
        if (DrawInterfaceSizingChoice("Standard", !Plugin.Cfg.NativeUiSizing, width))
            Plugin.Cfg.NativeUiSizing = false;
        ImGui.SameLine(0f, Ui(10f));
        if (DrawInterfaceSizingChoice("Native", Plugin.Cfg.NativeUiSizing, width))
            Plugin.Cfg.NativeUiSizing = true;
        ImGui.TextWrapped(InterfaceSizingChoiceHint);
        using (ImRaii.PushColor(ImGuiCol.Text, MutedText))
            ImGui.TextWrapped(InterfaceSizingDragHint);
        ImGui.Dummy(UiVec(0f, 12f));
        if (ImGui.Button("Use this layout", new Vector2(ImGui.GetContentRegionAvail().X, Ui(42f))))
        {
            // Enable the dock only on initial completion. Reopening the chooser
            // must preserve a user's later decision to hide it.
            if (!Plugin.Cfg.HasCompletedInterfaceSizingSetup)
                Plugin.Cfg.MiniDockDisplay = MiniDockVisibility.AlwaysDisplay;
            Plugin.Cfg.HasCompletedInterfaceSizingSetup = true;
            Plugin.Cfg.Save();
            showInterfaceSizingComparison = false;
        }
        using var uiFontScale9574=UiFonts.PushRelative(.85f);
        ImGui.TextColored(MutedText, "Change anytime in Settings → Appearance.");
        uiFontScale9574.Dispose();
        // Empty space inside the popup is also a drag handle; controls retain clicks.
        if (ImGui.IsWindowHovered() && !ImGui.IsAnyItemHovered() &&
            ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            interfaceSizingPopupDragging = true;
            interfaceSizingPopupDragOffset = ImGui.GetMousePos() - interfaceSizingFormMinimum;
        }
    }

    private float InterfaceSizingChoiceHeight() => MathF.Max(Ui(52f), ImGui.GetFontSize() + Ui(24f));

    private bool DrawInterfaceSizingChoice(string label, bool selected, float width)
    {
        var minimum = ImGui.GetCursorScreenPos();
        var size = new Vector2(width, InterfaceSizingChoiceHeight());
        var clicked = ImGui.InvisibleButton("##sizingChoice" + label, size);
        var hovered = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();
        var fill = selected ? new Vector4(Accent.X, Accent.Y, Accent.Z, hovered ? .30f : .20f) : FrameBg;
        draw.AddRectFilled(minimum, minimum + size, ImGui.GetColorU32(fill), Ui(8f));
        var border = selected || hovered ? AccentHover : new Vector4(MutedText.X, MutedText.Y, MutedText.Z, .4f);
        draw.AddRect(minimum, minimum + size, ImGui.GetColorU32(border), Ui(8f));
        var iconSize = Ui(22f);
        using (UiFonts.PushIcon())
            UiFonts.DrawText(draw, iconSize, minimum + new Vector2(Ui(12f), (size.Y - iconSize) * .5f), ImGui.GetColorU32(selected ? AccentHover : MutedText), FontAwesomeIcon.Desktop.ToIconString());
        var textSize = ImGui.CalcTextSize(label);
        draw.AddText(minimum + new Vector2(Ui(44f), (size.Y - textSize.Y) * .5f),
            ImGui.GetColorU32(ImGuiCol.Text), label);
        var centre = minimum + new Vector2(size.X - Ui(17f), size.Y * .5f);
        draw.AddCircle(centre, Ui(7f), ImGui.GetColorU32(selected ? AccentHover : MutedText), 24, Ui(1f));
        if (selected)
            draw.AddCircleFilled(centre, Ui(4f), ImGui.GetColorU32(AccentHover), 24);
        return clicked;
    }

    private void DrawInterfaceSizingDragAreas(Vector2 position, Vector2 size, Vector2 mainPosition)
    {
        var maximum = position + size;
        DrawFirstLaunchDragBlocker("##sizingDragTop", position,
            new Vector2(maximum.X, interfaceSizingFormMinimum.Y), mainPosition);
        DrawFirstLaunchDragBlocker("##sizingDragBottom",
            new Vector2(position.X, interfaceSizingFormMaximum.Y), maximum, mainPosition);
        DrawFirstLaunchDragBlocker("##sizingDragLeft",
            new Vector2(position.X, interfaceSizingFormMinimum.Y),
            new Vector2(interfaceSizingFormMinimum.X, interfaceSizingFormMaximum.Y), mainPosition);
        DrawFirstLaunchDragBlocker("##sizingDragRight",
            new Vector2(interfaceSizingFormMaximum.X, interfaceSizingFormMinimum.Y),
            new Vector2(maximum.X, interfaceSizingFormMaximum.Y), mainPosition);
    }
}
