using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;

namespace AlphaChannel.Plugin;

// Every browse entry point uses the same non-modal manager and the shared,
// plugin-sized backdrop. Keep completion/cancellation independent of pages.
internal sealed class PluginFilePicker
{
    private readonly FileDialogManager manager = new()
    {
        AddedWindowFlags = ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoSavedSettings,
    };
    private Action? cancel;
    private int generation;
    internal bool IsOpen { get; private set; }

    private Action<bool, T> Begin<T>(Action<bool, T> callback, T empty)
    {
        Reset();
        IsOpen = true;
        var current = ++generation;
        void Complete(bool success, T value)
        {
            if (!IsOpen || generation != current) return;
            IsOpen = false; cancel = null;
            callback(success, value);
        }
        cancel = () => Complete(false, empty);
        return Complete;
    }

    internal void OpenFileDialog(string title, string filters, Action<bool, string> callback)
    {
        var complete = Begin(callback, "");
        manager.OpenFileDialog(title, filters, (success, paths) =>
            complete(success && paths.Count > 0, paths.FirstOrDefault() ?? ""), 1, null!, false);
    }

    internal void OpenFileDialog(string title, string filters, Action<bool, List<string>> callback,
        int selectionCountMax, string? startPath = null)
        => manager.OpenFileDialog(title, filters, Begin(callback, new List<string>()), selectionCountMax, startPath!, false);

    internal void OpenFolderDialog(string title, Action<bool, string> callback, string? startPath = null)
        => manager.OpenFolderDialog(title, Begin(callback, ""), startPath!, false);

    internal void SaveFileDialog(string title, string filters, string filename, string extension, Action<bool, string> callback)
        => manager.SaveFileDialog(title, filters, filename, extension, Begin(callback, ""), null!, false);

    internal void Draw() => manager.Draw();
    internal void Reset()
    {
        manager.Reset();
        // Escape must also restore a suspended music-library dialog, just as
        // Cancel does. Complete before invoking callbacks so they may reopen.
        var complete = cancel;
        complete?.Invoke();
    }
}
