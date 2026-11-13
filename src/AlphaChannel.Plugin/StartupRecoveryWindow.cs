using System.Numerics;
using AlphaChannel.Plugin.Core;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace AlphaChannel.Plugin;

internal sealed class StartupRecoveryWindow : Window, IDisposable
{
    private readonly StartupRecoveryRecord record;
    private readonly WindowSystem windows = new("AlphaChannelStartupRecovery");
    private readonly object lifetime = new();
    private readonly bool commandRegistered;
    private bool disposed;
    private bool showNotice = true;
    private bool standardSelected;
    private bool saveFailed;

    internal StartupRecoveryWindow(StartupRecoveryRecord record) : base("Alpha Channel###AlphaChannelRecovery")
    {
        this.record = record;
        Size = new Vector2(580, 380);
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(430, 340),
            MaximumSize = new Vector2(850, 650)
        };
        SizeCondition = ImGuiCond.FirstUseEver;
        Flags = ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoCollapse;
        IsOpen = true;
        windows.AddWindow(this);
        Plugin.PluginInterface.UiBuilder.Draw += DrawWindows;
        Plugin.PluginInterface.UiBuilder.OpenMainUi += Open;
        Plugin.PluginInterface.UiBuilder.OpenConfigUi += Open;
        commandRegistered = Plugin.CommandManager.AddHandler("/alpha", new CommandInfo((_, _) => Open())
        {
            HelpMessage = "Open Alpha Channel recovery."
        });
    }

    private void Open()
    {
        lock (lifetime)
        {
            if (disposed) return;
            IsOpen = true;
            showNotice = !standardSelected;
        }
    }

    private void DrawWindows()
    {
        lock (lifetime)
        {
            if (!disposed) windows.Draw();
        }
    }

    public override void Draw()
    {
        var card = new Vector4(.07f, .10f, .17f, 1f);
        var accent = new Vector4(.23f, .49f, .93f, 1f);
        var muted = new Vector4(.65f, .71f, .82f, 1f);
        ImGui.TextUnformatted(standardSelected ? "Standard mode selected" : "Recovery mode");
        ImGui.Spacing();
        if (standardSelected)
        {
            ImGui.TextWrapped("Disable and enable Alpha Channel in Dalamud, or restart the game, to start in standard mode.");
            return;
        }

        ImGui.TextWrapped("You can update or disable Alpha Channel in the Dalamud plugin installer.");
        if (!showNotice)
        {
            if (ImGui.Button("View recovery options")) showNotice = true;
            return;
        }

        // Keep the overlay inside Alpha Channel so the Dalamud installer
        // remains available while recovery options are open.
        var parent = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        ImGui.GetWindowDrawList().AddRectFilled(parent, parent + size,
            ImGui.GetColorU32(new Vector4(0, 0, 0, .58f)));
        var panel = Vector2.Min(size - new Vector2(28, 48), new Vector2(520, 300));
        ImGui.SetCursorScreenPos(parent + new Vector2((size.X - panel.X) * .5f, (size.Y - panel.Y) * .5f + 8f));
        using var colors = ImRaii.PushColor(ImGuiCol.ChildBg, card)
            .Push(ImGuiCol.Border, accent).Push(ImGuiCol.Button, accent);
        using var styles = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, 14f)
            .Push(ImGuiStyleVar.ChildBorderSize, 1f).Push(ImGuiStyleVar.WindowPadding, new Vector2(22, 18))
            .Push(ImGuiStyleVar.FrameRounding, 8f).Push(ImGuiStyleVar.FramePadding, new Vector2(12, 8));
        using var child = ImRaii.Child("##StartupRecoveryPanel", panel, true, ImGuiWindowFlags.AlwaysUseWindowPadding);
        if (!child) return;

        using (UiFonts.PushRelative(1.15f, emphasis: true)) ImGui.TextUnformatted("Startup recovery");
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextWrapped("Alpha Channel detected that its last startup did not finish. This can happen for reasons outside Alpha Channel, so we’ve opened in recovery mode as a precaution.");
        ImGui.Spacing();
        using (ImRaii.PushColor(ImGuiCol.Text, muted))
            ImGui.TextWrapped("If you’re having trouble, stay in recovery mode and update Alpha Channel in Dalamud. The updated plugin will start in standard mode.");
        ImGui.Spacing();
        ImGui.TextWrapped("You can also choose standard mode for the next time the plugin loads.");
        ImGui.Spacing();
        if (saveFailed) ImGui.TextWrapped("Couldn’t save your choice. You can still update or disable the plugin in Dalamud.");
        if (ImGui.Button("Stay in recovery mode")) showNotice = false;
        if (ImGui.GetContentRegionAvail().X >= ImGui.CalcTextSize("Stay in recovery mode   Return to standard mode").X + 56)
            ImGui.SameLine();
        if (ImGui.Button("Return to standard mode"))
        {
            try
            {
                record.Complete();
                standardSelected = true;
                showNotice = false;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                saveFailed = true;
            }
        }
    }

    public void Dispose()
    {
        lock (lifetime)
        {
            if (disposed) return;
            disposed = true;
            Plugin.PluginInterface.UiBuilder.Draw -= DrawWindows;
            Plugin.PluginInterface.UiBuilder.OpenMainUi -= Open;
            Plugin.PluginInterface.UiBuilder.OpenConfigUi -= Open;
            if (commandRegistered) Plugin.CommandManager.RemoveHandler("/alpha");
            windows.RemoveAllWindows();
        }
    }
}
