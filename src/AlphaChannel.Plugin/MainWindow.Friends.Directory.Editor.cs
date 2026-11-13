using AlphaChannel.Plugin.Auth;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private bool directoryEditorOpen, directoryMutationBusy, directoryRemoveConfirm;
    private FriendDirectoryDraft directoryDraft = new();
    private string directoryCustomTag = "";
    private string? directoryEditorError;
    private FriendDirectoryPerson? directoryReportTarget;
    private string directoryReportNote = "";

    private async Task RefreshAndOpenDirectoryEditor(CharacterSession session)
    {
        var generation = directoryOwnGeneration + 1;
        await LoadDirectoryOwn(session).ConfigureAwait(false);
        await Plugin.Framework.RunOnFrameworkThread(() =>
        {
            if (DirectorySessionCurrent(session) && generation == directoryOwnGeneration && directoryOwnValid && currentPage == HomePage.FriendsDirectory)
                OpenDirectoryEditor();
        });
    }

    private void OpenDirectoryEditor()
    {
        directoryDraft = directoryOwn is not { } own ? new() : new()
        {
            Introduction = own.Introduction, LookingFor = own.LookingFor,
            Tags = own.Tags.ToArray(), Visible = !own.Suspended, ShowOnline = own.ShowOnline,
            ShowParty = own.ShowParty, Version = own.Version
        };
        directoryCustomTag = ""; directoryEditorError = null; directoryRemoveConfirm = false;
        directoryDraft.Visible = directoryOwn?.Suspended != true;
        directoryDraft.LookingFor = directoryDraft.LookingFor switch
        {
            "Watch parties" => "Watching together", "General chatting" => "Chatting",
            "Gaming friends" => "Gaming together", "Music friends" => "Shared Interests", _ => directoryDraft.LookingFor
        };
        directoryEditorOpen = true;
    }
    private void DrawFriendsDirectoryOverlays()
    {
        if (currentPage != HomePage.FriendsDirectory || CurrentSession is not { } session || directorySessionToken != session.Token)
        { directoryEditorOpen = false; directoryReportTarget = null; return; }
        if (directoryEditorOpen)
            DrawEventWindow("friendDirectoryEditor", "List my profile", UiVec(640, 690),
                () => DrawDirectoryEditor(session), () => { if (!directoryMutationBusy) directoryEditorOpen = false; },
                () => DrawDirectoryEditorFooter(session));
        else if (directoryReportTarget is { } person)
            DrawEventWindow("friendDirectoryReport", "Report directory listing", UiVec(530, 370),
                () =>
                {
                    ImGui.TextWrapped("Report " + person.DisplayName + "'s introduction or tags to the moderation team.");
                    ImGui.Spacing();
                    ImGui.InputTextMultiline("##directoryReportNote", ref directoryReportNote, 500, new Vector2(-1, Ui(100)));
                    if (directoryEditorError is { } error) ImGui.TextWrapped(error);
                }, () => { if (!directoryMutationBusy) directoryReportTarget = null; },
                () =>
                {
                    using var disabled = UiDisabled.Scope(directoryMutationBusy);
                    if (AlphaChatButton("sendDirectoryReport", directoryMutationBusy ? "Sending…" : "Send report", FontAwesomeIcon.Flag, Ui(160), primary: true))
                        _ = ReportDirectoryPerson(session, person);
                });
    }
    private void DrawDirectoryEditor(CharacterSession session)
    {
        using var disabled = UiDisabled.Scope(directoryMutationBusy);
        DrawSocialHelperWrapped("People signed in to Alpha Channel can view your profile and send a friend request.");
        if (directoryOwn?.Suspended == true) ImGui.TextWrapped("A moderator has hidden this listing. You can save it as hidden or remove it.");
        ImGui.TextUnformatted("Introduction (optional)");
        ImGui.InputTextMultiline("##directoryIntro", ref directoryDraft.Introduction, 240, new Vector2(-1, Ui(70)));
        DrawSocialHelper($"{directoryDraft.Introduction.Length}/240 · Tell people what you enjoy on Alpha Channel.");
        ImGui.TextUnformatted("Looking for");
        var looking = Math.Max(0, Array.IndexOf(DirectoryLooking, directoryDraft.LookingFor));
        ImGui.SetNextItemWidth(-1);
        if (DirectoryLookingCombo("##directoryDraftLooking", ref looking, DirectoryLooking)) directoryDraft.LookingFor = DirectoryLooking[looking];
        ImGui.Dummy(UiVec(0, 8));
        if (ImGui.CollapsingHeader("Suggested interests", ImGuiTreeNodeFlags.DefaultOpen))
        {
            foreach (var group in DirectorySuggestedTags)
            {
                ImGui.Dummy(UiVec(0, 4));
                ImGui.TextColored(MutedText, group.Category);
                DrawDirectoryInterestButtons(group.Tags, removable: false);
            }
        }
        ImGui.Dummy(UiVec(0, 8));
        ImGui.TextUnformatted($"Selected interests · {directoryDraft.Tags.Length}/8");
        if (directoryDraft.Tags.Length == 0) DrawSocialHelperWrapped("Choose suggested interests or add your own below.");
        else DrawDirectoryInterestButtons(directoryDraft.Tags.ToArray(), removable: true);
        ImGui.Dummy(UiVec(0, 2));
        ImGui.SetNextItemWidth(MathF.Max(Ui(90), ImGui.GetContentRegionAvail().X - Ui(80)));
        var enter = ImGui.InputTextWithHint("##directoryCustomTag", "Add a custom interest…", ref directoryCustomTag, 24, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine(); if (ImGui.Button("Add", UiVec(70, 0)) || enter)
        { if (TryAddDirectoryDraftTag(directoryCustomTag)) directoryCustomTag = ""; }
        ImGui.Spacing();
        ImGui.Checkbox("Show when I'm online", ref directoryDraft.ShowOnline);
        ImGui.Checkbox("Show when I'm hosting a public Watch Party", ref directoryDraft.ShowParty);
        DrawSocialHelperWrapped("Your current video, private parties and linked character are not shared. Save again within 90 days to keep your listing in discovery.");
    }
    private void DrawDirectoryInterestButtons(string[] tags, bool removable)
    {
        var right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        using var style = ImRaii.PushStyle(ImGuiStyleVar.FramePadding, UiVec(10, 5))
            .Push(ImGuiStyleVar.FrameRounding, Ui(8)).Push(ImGuiStyleVar.ItemSpacing, UiVec(7, 7));
        var first = true;
        foreach (var tag in tags)
        {
            var label = removable ? tag + " ×" : tag;
            var width = ImGui.CalcTextSize(label).X + Ui(20);
            if (!first && ImGui.GetItemRectMax().X + width + Ui(7) <= right) ImGui.SameLine();
            var selected = directoryDraft.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase);
            using (ImRaii.PushColor(ImGuiCol.Button, selected ? AccentInk(.4f) : FrameBg))
                if (ImGui.Button(label + (removable ? "##selectedInterest" : "##suggestedInterest"), new Vector2(width, Ui(28)))) ToggleDirectoryDraftTag(tag);
            first = false;
        }
    }
    private bool DirectoryLookingCombo(string id, ref int selected, string[] options)
    {
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, UiVec(8, 8))
            .Push(ImGuiStyleVar.ItemSpacing, UiVec(8, 5));
        var changed = false;
        if (ImGui.BeginCombo(id, options[Math.Clamp(selected, 0, options.Length - 1)], ImGuiComboFlags.HeightLarge))
        {
            ImGui.Dummy(UiVec(0, 4));
            for (var i = 0; i < options.Length; i++)
            {
                if (ImGui.Selectable(options[i], selected == i)) { selected = i; changed = true; }
                if (selected == i) ImGui.SetItemDefaultFocus();
            }
            ImGui.Dummy(UiVec(0, 4));
            ImGui.EndCombo();
        }
        return changed;
    }
    private void ToggleDirectoryDraftTag(string tag)
    {
        if (directoryDraft.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            directoryDraft.Tags = directoryDraft.Tags.Where(t => !string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)).ToArray();
        else TryAddDirectoryDraftTag(tag);
    }
    private bool TryAddDirectoryDraftTag(string raw)
    {
        var tag = string.Join(" ", raw.Trim().Normalize(System.Text.NormalizationForm.FormKC)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (tag.Length == 0 || tag.Length > 24 || tag.Any(char.IsControl))
        { directoryEditorError = "Use a tag between 1 and 24 characters."; return false; }
        if (directoryDraft.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) return true;
        if (directoryDraft.Tags.Length >= 8) { directoryEditorError = "Choose up to 8 interests."; return false; }
        directoryDraft.Tags = directoryDraft.Tags.Append(tag).ToArray(); directoryEditorError = null; return true;
    }
    private void DrawDirectoryEditorFooter(CharacterSession session)
    {
        if (directoryEditorError is { } error) ImGui.TextWrapped(error);
        using var disabled = UiDisabled.Scope(directoryMutationBusy);
        if (directoryOwn is not null)
        {
            if (directoryOwn.Visible)
            {
                if (AlphaChatButton("disableDirectoryListing", "Disable Listing", FontAwesomeIcon.Pause, Ui(150), Ui(30)))
                    _ = MutateDirectoryListing(session, remove: false, visible: false);
                ImGui.SameLine();
            }
            if (!directoryRemoveConfirm)
            {
                if (AlphaChatButton("removeDirectoryListing", "Remove listing", FontAwesomeIcon.Trash, Ui(150), Ui(30), danger: true))
                    directoryRemoveConfirm = true;
            }
            else
            {
                ImGui.TextUnformatted("Remove your listing entirely?");
                if (ImGui.SmallButton("Yes, remove")) _ = MutateDirectoryListing(session, remove: true);
                ImGui.SameLine(); if (ImGui.SmallButton("Keep listing")) directoryRemoveConfirm = false;
            }
        }
        if (AlphaChatButton("saveDirectoryListing", directoryMutationBusy ? "Saving…" : "Save listing", FontAwesomeIcon.Check, Ui(160), primary: true))
        {
            if (directoryCustomTag.Length > 0) directoryEditorError = "Add your custom interest or clear the tag field before saving.";
            else _ = MutateDirectoryListing(session, remove: false);
        }
    }
    private async Task MutateDirectoryListing(CharacterSession session, bool remove, bool? visible = null)
    {
        directoryMutationBusy = true; directoryEditorError = null;
        var draft = new FriendDirectoryDraft
        {
            Introduction = directoryDraft.Introduction, LookingFor = directoryDraft.LookingFor,
            Tags = directoryDraft.Tags.ToArray(), Visible = visible ?? directoryDraft.Visible, ShowOnline = directoryDraft.ShowOnline,
            ShowParty = directoryDraft.ShowParty, Version = directoryDraft.Version
        };
        if (visible == false && directoryOwn is { } own)
            draft = new FriendDirectoryDraft
            {
                Introduction = own.Introduction, LookingFor = own.LookingFor,
                Tags = own.Tags.ToArray(), Visible = false, ShowOnline = own.ShowOnline,
                ShowParty = own.ShowParty, Version = own.Version
            };
        try
        {
            FriendDirectoryPerson? saved = null;
            if (remove) await friendsDirectoryApi.Remove(session.Token, draft.Version, youtubeFeedToken).ConfigureAwait(false);
            else saved = (await friendsDirectoryApi.Save(session.Token, draft, youtubeFeedToken).ConfigureAwait(false)).Listing;
            await Plugin.Framework.RunOnFrameworkThread(() =>
            {
                if (!DirectorySessionCurrent(session)) return;
                directoryOwn = saved; directoryOwnValid = directoryOwnLoaded = true;
                directoryEditorOpen = false; MarkDirectoryDirty();
            });
        }
        catch (Exception ex)
        { await Plugin.Framework.RunOnFrameworkThread(() => { if (DirectorySessionCurrent(session)) directoryEditorError = DirectoryFailure(ex); }); }
        finally
        { await Plugin.Framework.RunOnFrameworkThread(() => { if (DirectorySessionCurrent(session)) directoryMutationBusy = false; }); }
    }
    private async Task ReportDirectoryPerson(CharacterSession session, FriendDirectoryPerson person)
    {
        directoryMutationBusy = true; directoryEditorError = null;
        var note = "Find Friends listing: " + person.DisplayName + "\nIntroduction: " + person.Introduction
            + "\nInterests: " + string.Join(", ", person.Tags) + "\nReason: " + directoryReportNote;
        var sent = false;
        try { sent = await reportClient.SubmitAsync(session.Token, "inappropriate_content", note, person.AccountId, null, null, null).ConfigureAwait(false); }
        catch { }
        await Plugin.Framework.RunOnFrameworkThread(() =>
        {
            if (!DirectorySessionCurrent(session)) return;
            directoryMutationBusy = false;
            if (sent) { directoryReportTarget = null; directoryError = "Report sent to the moderation team."; }
            else directoryEditorError = "Your report could not be sent. Please try again.";
        });
    }
}
