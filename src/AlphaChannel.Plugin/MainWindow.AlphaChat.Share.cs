using AlphaChannel.Contracts;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private bool alphaChatShareOpen;
    private string? alphaChatShareHost;
    private string? alphaChatShareAccount;
    private string? alphaChatShareError;

    private string? CurrentAlphaChatShareHost => stream.Mode == StreamMode.Hosting
        ? CurrentSession?.AccountId : stream.Mode == StreamMode.Viewing ? stream.HostId : null;

    private void RequestAlphaChatShare()
    {
        alphaChatShareHost = CurrentAlphaChatShareHost;
        alphaChatShareAccount = CurrentSession?.AccountId;
        alphaChatShareError = stream.RoomKind == RoomKind.Locked ? "Locked parties can not be shared in chat" : null;
        alphaChatShareOpen = alphaChatShareHost is not null;
    }

    private async void ConfirmAlphaChatShare()
    {
        var host = alphaChatShareHost;
        alphaChatShareOpen = false;
        if (host is null) return;
        var shared = await alphaChat.ShareAsync(host);
        if (!shared && alphaChat.Status == "Locked parties can not be shared in chat")
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (Volatile.Read(ref disposeStarted) != 0 || CurrentSession?.AccountId != alphaChatShareAccount) return;
                alphaChatShareError = "Locked parties can not be shared in chat";
                alphaChatShareOpen = true;
            });
    }

    private void DrawAlphaChatShareOverlay()
    {
        if (!alphaChatShareOpen) return;
        if (currentPage != HomePage.AlphaChat || !alphaChat.ChatSubscribed || CurrentSession?.AccountId != alphaChatShareAccount)
        { alphaChatShareOpen = false; return; }
        if (alphaChatShareError is null)
        {
            if (CurrentAlphaChatShareHost != alphaChatShareHost)
                alphaChatShareError = "Your watch party has changed. Close this window and try sharing again.";
            else if (stream.RoomKind == RoomKind.Locked)
                alphaChatShareError = "Locked parties can not be shared in chat";
        }
        var parent = ImGui.GetWindowPos(); var parentSize = ImGui.GetWindowSize();
        var size = new Vector2(MathF.Min(Ui(500f), parentSize.X - Ui(32f)), MathF.Min(Ui(250f), parentSize.Y - Ui(32f)));
        var position = parent + (parentSize - size) * .5f;
        ImGui.SetNextWindowPos(parent, ImGuiCond.Always);
        ImGui.SetNextWindowSize(parentSize, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoBackground;
        if (!BeginWindowOverlay("##alphaChatShareOverlay", flags)) { ImGui.End(); return; }
        ImGui.GetWindowDrawList().AddRectFilled(parent, parent + parentSize, ImGui.GetColorU32(new Vector4(0f, 0f, 0f, .54f)));
        ImGui.SetCursorScreenPos(position);
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(14f))
            .Push(ImGuiStyleVar.ChildBorderSize, Ui(1f)).Push(ImGuiStyleVar.WindowPadding, UiVec(20f, 18f)))
        using (ImRaii.PushColor(ImGuiCol.ChildBg, CardBg)
            .Push(ImGuiCol.Border, new Vector4(Accent.X, Accent.Y, Accent.Z, .55f)))
        using (var panel = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##alphaChatSharePanel", size, true, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)))
        {
            if (panel)
            {
                using (UiFonts.PushIcon())
                    ImGui.TextColored(AccentHover, (alphaChatShareError is null ? FontAwesomeIcon.BroadcastTower : FontAwesomeIcon.Lock).ToIconString());
                ImGui.SameLine(); ImGui.TextUnformatted(alphaChatShareError is null ? "Share Watch Party?" : "Unable to share Watch Party");
                ImGui.SetCursorScreenPos(position + new Vector2(size.X - Ui(48f), Ui(14f)));
                if (AlphaChatButton("closeAlphaChatShare", "", FontAwesomeIcon.Times, Ui(28f), Ui(28f))) alphaChatShareOpen = false;
                ImGui.SetCursorScreenPos(position + UiVec(20f, 64f));
                using (var body = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##alphaChatShareBody", new Vector2(size.X - Ui(40f), size.Y - Ui(128f)), false)))
                    if (body)
                    {
                        ImGui.PushTextWrapPos(ImGui.GetWindowContentRegionMax().X);
                        ImGui.TextColored(MutedText, alphaChatShareError ??
                            "This will post your current watch party's status, details and content to Alpha Chat, allowing other users to join your watch party.");
                        ImGui.PopTextWrapPos();
                    }
                ImGui.SetCursorScreenPos(position + new Vector2(MathF.Max(Ui(20f), size.X - Ui(alphaChatShareError is null ? 290f : 140f)), size.Y - Ui(54f)));
                if (alphaChatShareError is null)
                {
                    if (ActivePartyButton("Cancel##alphaChatShare", UiVec(120f, 36f))) alphaChatShareOpen = false;
                    ImGui.SameLine();
                    using (UiDisabled.Scope(!alphaChat.EncryptionHealthy))
                        if (ActivePartyButton("Share Watch Party##confirmAlphaChatShare", UiVec(142f, 36f), primary: true)) ConfirmAlphaChatShare();
                }
                else if (ActivePartyButton("Close##alphaChatShare", UiVec(120f, 36f), primary: true)) alphaChatShareOpen = false;
            }
        }
        if (ImGui.IsKeyPressed(ImGuiKey.Escape)) alphaChatShareOpen = false;
        ImGui.End();
    }
}
