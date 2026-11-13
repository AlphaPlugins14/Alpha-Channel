using System.Text;
using AlphaChannel.Plugin.Auth;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private readonly HashSet<long> alphaChatUnreadMentions = [];
    private int alphaChatComposerRevision;
    private bool alphaChatFocusComposer;
    private int alphaChatCaretBytes;
    private int? alphaChatPendingCaretBytes;
    private bool alphaChatCompleteWithTab;
    private int alphaChatMentionSelection;
    private string alphaChatMentionQuery = "";
    private string? alphaChatDismissedMentionQuery;
    private bool alphaChatMentionSuggestionsVisible;
    private Vector2 alphaChatMentionSuggestionsMin;
    private Vector2 alphaChatMentionSuggestionsMax;

    private int AlphaChatComposerCallback(ref ImGuiInputTextCallbackData data)
    {
        if (alphaChatPendingCaretBytes is { } caret)
        {
            data.CursorPos = data.SelectionStart = data.SelectionEnd = Math.Min(caret, data.BufTextLen);
            alphaChatPendingCaretBytes = null;
        }
        alphaChatCaretBytes = data.CursorPos;
        if (data.EventFlag == ImGuiInputTextFlags.CallbackCompletion) alphaChatCompleteWithTab = true;
        if (data.EventFlag == ImGuiInputTextFlags.CallbackHistory)
            alphaChatMentionSelection += data.EventKey == ImGuiKey.DownArrow ? 1 : -1;
        return 0;
    }

    private void SetAlphaChatMentionDraft(string text, int caret)
    {
        alphaChatDraft = text;
        alphaChatComposerRevision++;
        alphaChatPendingCaretBytes = Encoding.UTF8.GetByteCount(text.AsSpan(0, caret));
        alphaChatFocusComposer = true;
        alphaChatDismissedMentionQuery = null;
    }

    private void MentionAlphaChatUser(AlphaChatUser user)
    {
        var prefix = alphaChatDraft + (alphaChatDraft.Length > 0 && !char.IsWhiteSpace(alphaChatDraft[^1]) ? " " : "");
        if (AlphaChatMentions.Complete(prefix, prefix.Length, prefix.Length, user.DisplayName, out var caret) is { } text)
            SetAlphaChatMentionDraft(text, caret);
        alphaChatRosterOpen = false;
    }

    private bool DrawAlphaChatMentionSuggestions(bool inputActive, Vector2 inputMin, Vector2 inputMax, bool enter)
    {
        // Do not keep an autocomplete window above an emoji/reaction picker.
        if (ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel))
        {
            alphaChatMentionSuggestionsVisible = false;
            alphaChatCompleteWithTab = false;
            return false;
        }
        // A mouse press in the list deactivates InputText before Selectable handles
        // its release. Retain the list across that focus change to finish selection.
        var interactingWithList = alphaChatMentionSuggestionsVisible &&
            ImGui.IsMouseHoveringRect(alphaChatMentionSuggestionsMin, alphaChatMentionSuggestionsMax, false);
        alphaChatMentionSuggestionsVisible = false;
        var tab = alphaChatCompleteWithTab; alphaChatCompleteWithTab = false;
        var bytes = Encoding.UTF8.GetBytes(alphaChatDraft);
        var caret = Encoding.UTF8.GetCharCount(bytes.AsSpan(0, Math.Clamp(alphaChatCaretBytes, 0, bytes.Length)));
        if (!AlphaChatMentions.TryCompletion(alphaChatDraft, caret, out var start, out var end, out var query))
        {
            alphaChatMentionSelection = 0;
            alphaChatMentionQuery = ""; alphaChatDismissedMentionQuery = null;
            return false;
        }
        var key = start + ":" + query;
        if (key != alphaChatMentionQuery)
        {
            alphaChatMentionSelection = 0; alphaChatMentionQuery = key; alphaChatDismissedMentionQuery = null;
        }
        if (alphaChatDismissedMentionQuery == key || !(inputActive || enter || tab || interactingWithList)) return false;
        var names = alphaChatUsers.Where(u => !AlphaChatBlocked(u.AccountId)).Select(u => u.DisplayName)
            .Where(n => !string.IsNullOrWhiteSpace(n) && n.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(8).ToArray();
        if (names.Length == 0) return false;
        if (ImGui.IsKeyPressed(ImGuiKey.Escape)) { alphaChatDismissedMentionQuery = key; return enter; }
        alphaChatMentionSelection = (alphaChatMentionSelection % names.Length + names.Length) % names.Length;
        var chosen = enter || tab ? names[alphaChatMentionSelection] : null;
        // A non-modal window keeps the composer editable as suggestions change.
        ImGui.SetNextWindowPos(new Vector2(inputMin.X, inputMin.Y - Ui(6f)), ImGuiCond.Always, new Vector2(0f, 1f));
        ImGui.SetNextWindowSize(new Vector2(MathF.Min(Ui(300f), inputMax.X - inputMin.X), 0f));
        using (ImRaii.PushColor(ImGuiCol.WindowBg, CardBg).Push(ImGuiCol.Border, BorderSubtle))
        using (ImRaii.PushStyle(ImGuiStyleVar.WindowBorderSize, Ui(1f)))
        {
            if (BeginWindowOverlay("##alphaChatMentionSuggestions", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoMove |
                ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNavFocus |
                ImGuiWindowFlags.AlwaysAutoResize))
            {
                alphaChatMentionSuggestionsVisible = true;
                alphaChatMentionSuggestionsMin = ImGui.GetWindowPos();
                alphaChatMentionSuggestionsMax = alphaChatMentionSuggestionsMin + ImGui.GetWindowSize();
                ImGui.TextColored(MutedText, "Mention someone · Enter / Tab to select");
                for (var i = 0; i < names.Length; i++)
                    if (ImGui.Selectable(names[i], i == alphaChatMentionSelection)) chosen = names[i];
            }
            ImGui.End();
        }
        if (chosen is not null && AlphaChatMentions.Complete(alphaChatDraft, start, end, chosen, out var position) is { } completed)
        {
            alphaChatMentionSuggestionsVisible = false;
            SetAlphaChatMentionDraft(completed, position);
        }
        return enter || tab;
    }

    private void RegisterAlphaChatMention(AlphaChatMessage message)
    {
        if (message.IsHistory || message.Kind != "text" || message.Sender.AccountId == CurrentSession?.AccountId ||
            AlphaChatBlocked(message.Sender.AccountId) ||
            message.MentionAccountIds is { } targets && !targets.Contains(CurrentSession?.AccountId, StringComparer.OrdinalIgnoreCase)) return;
        var name = alphaChatUsers.FirstOrDefault(u => u.AccountId == CurrentSession?.AccountId)?.DisplayName ??
            CurrentSession?.DisplayName ?? CurrentDisplayName;
        if (!AlphaChatMentions.Contains(message.Content, name, alphaChatUsers.Select(u => u.DisplayName))) return;
        if (!(IsOpen && !windowMinimized && currentPage == HomePage.AlphaChat && alphaChatWasAtBottom))
            alphaChatUnreadMentions.Add(message.Sequence);
        QueueNotificationSound(alphaChat: true);
    }
}
