#if DEBUG
using Dalamud.Bindings.ImGui;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private string? debugAccountTestError;

    private void DrawDebugAccountTestSettings()
    {
        var cfg = Plugin.Cfg;
        var active = cfg.DebugAccountTestActive;
        ImGui.TextUnformatted("Account signup testing (Debug)");
        ImGui.TextWrapped(active
            ? "Your test account stays active across reloads. Your normal login is preserved separately."
            : "Try new-user signup with a separate login. Your normal XIVAuth credentials remain preserved.");
        if (cfg.DebugAccountTest is { } state && state.Requested != active)
        {
            ImGui.TextWrapped("Reload Alpha Channel in the plugin installer to apply the account switch.");
            if (ImGui.Button("Cancel pending account switch"))
            { state.Requested = active; cfg.Save(); }
        }
        else if (ImGui.Button(active ? "Restore normal account on next reload" : "Start account signup test on next reload"))
        {
            // Persist the current vault before requesting a different one. No
            // sign-out, token revocation, character link or server mutation occurs.
            if (!cfg.SaveAccountCredentials())
                debugAccountTestError = "Could not preserve your current login securely. The account switch was not requested.";
            else
            {
                cfg.DebugAccountTest ??= new DebugAccountTestState();
                cfg.DebugAccountTest.Requested = !active;
                cfg.Save();
                debugAccountTestError = null;
            }
        }
        if (debugAccountTestError is not null) ImGui.TextWrapped(debugAccountTestError);
        ImGui.Separator();
        ImGui.Spacing();
    }
}
#endif
