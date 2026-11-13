using System.Collections.Concurrent;
using AlphaChannel.Plugin.Auth;
using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private static readonly string[] NotificationSoundNames =
        ["Bright chime", "Short click", "Thin message click", "Classic bell"];
    private static readonly string[] NotificationSoundFiles =
        ["notification-bright-chime.mp3", "notification-short-click.mp3",
         "notification-thin-click.mp3", "notification-classic-bell.mp3"];
    private sealed record PendingSound(CharacterSession Session, string? ConversationId, bool PartyChat, DateTime ReceivedUtc,
        PartyChatItemKind? PartyKind, bool AlphaChat);
    private readonly ConcurrentQueue<PendingSound> notificationSounds = new();
    private readonly NotificationSoundCooldown notificationSoundCooldown = new();
    private readonly NotificationSoundPlayer notificationSoundPlayer = new();
    private DateTime notificationDmLastDrawUtc = DateTime.MinValue;
    private DateTime notificationMiniChatLastDrawUtc = DateTime.MinValue;
    private DateTime notificationPreviewLastUtc = DateTime.MinValue;

    private void QueueNotificationSound(string? conversationId = null, bool partyChat = false,
        PartyChatItemKind? partyKind = null, bool alphaChat = false)
    {
        if (Volatile.Read(ref disposeStarted) == 0 && Plugin.Cfg.NotificationSoundsEnabled && CurrentSession is { } session)
            notificationSounds.Enqueue(new(session, conversationId, partyChat, DateTime.UtcNow, partyKind, alphaChat));
    }

    private void UpdateNotificationSounds()
    {
        var now = DateTime.UtcNow;
        while (notificationSounds.TryDequeue(out var pending))
        {
            if (Volatile.Read(ref disposeStarted) != 0 || !ReferenceEquals(CurrentSession, pending.Session) ||
                now - pending.ReceivedUtc > TimeSpan.FromSeconds(5)) continue;
            if (pending.PartyKind is { } kind && !PartyChatNotificationEnabled(kind)) continue;
            var chatVisible = pending.AlphaChat
                ? IsOpen && !windowMinimized && currentPage == HomePage.AlphaChat
                : pending.PartyChat
                ? (IsOpen && !windowMinimized && currentPage == HomePage.WatchAlong &&
                   partyPanelTab == PartyPanelTab.Chat && now - partyChatLastDrawUtc < TimeSpan.FromSeconds(1)) ||
                  now - notificationMiniChatLastDrawUtc < TimeSpan.FromSeconds(1)
                : pending.ConversationId is { } conversationId &&
                  IsDisplayingConversation(pending.Session, conversationId) &&
                  now - notificationDmLastDrawUtc < TimeSpan.FromSeconds(1);
            if (notificationSoundCooldown.TryAcquire(TimeSpan.FromMilliseconds(Environment.TickCount64),
                    Plugin.Cfg.NotificationSoundsEnabled, chatVisible,
                    Plugin.Cfg.LimitNotificationSoundsToTwoMinutes))
                PlaySelectedNotificationSound();
        }
    }

    private void ClearPartyNotificationSounds()
    {
        var retained = new List<PendingSound>();
        while (notificationSounds.TryDequeue(out var pending))
            if (!pending.PartyChat) retained.Add(pending);
        foreach (var pending in retained) notificationSounds.Enqueue(pending);
    }

    private void PlaySelectedNotificationSound()
    {
        var index = Math.Clamp(Plugin.Cfg.NotificationSoundIndex, 0, NotificationSoundFiles.Length - 1);
        notificationSoundPlayer.Play(Path.Combine(
            Plugin.PluginInterface.AssemblyLocation.DirectoryName ?? string.Empty, "Assets", NotificationSoundFiles[index]));
    }

    private void DrawAlphaChatSettings()
    {
        DrawOtherSettingsPanel("##alphaChatSettings", 90f, () =>
        {
            DrawSocialHeading("Alpha Chat", 1.1f);
            ImGui.Spacing();
            var autoJoinChat = Plugin.Cfg.AutoJoinAlphaChat;
            if (DrawSettingsToggle("##autoJoinAlphaChat", "Join Alpha Chat in the background on login", ref autoJoinChat))
            {
                Plugin.Cfg.AutoJoinAlphaChat = autoJoinChat;
                if (autoJoinChat) { alphaChatAutoJoinSuppressed = false; StartAlphaChatPresence(); }
                else LeaveAlphaChatForLogin();
                Plugin.Cfg.Save();
            }
        }, autoHeight: true);
    }

    private void DrawNotificationSoundSettings()
    {
        DrawOtherSettingsPanel("##notificationSounds", 230f, () =>
        {
            SettingsSection("Notifications", "Choose which Watch Party alerts count as notifications. Messages remain visible in chat.");
            var presence = Plugin.Cfg.PartyPresenceNotificationsEnabled;
            if (DrawSettingsToggle("##partyPresenceNotifications", "Show notification for join/leave party alerts", ref presence))
            {
                Plugin.Cfg.PartyPresenceNotificationsEnabled = presence;
                RefreshPartyNotificationPreferences();
                Plugin.Cfg.Save();
            }
            var reactions = Plugin.Cfg.ReactLiveNotificationsEnabled;
            if (DrawSettingsToggle("##reactLiveNotifications", "Show notification for React Live alerts", ref reactions))
            {
                Plugin.Cfg.ReactLiveNotificationsEnabled = reactions;
                RefreshPartyNotificationPreferences();
                Plugin.Cfg.Save();
            }
            ImGui.Dummy(UiVec(0f, 8f));
            var enabled = Plugin.Cfg.NotificationSoundsEnabled;
            if (DrawSettingsToggle("##notificationSoundsEnabled", "Play notification sounds", ref enabled))
            {
                Plugin.Cfg.NotificationSoundsEnabled = enabled;
                Plugin.Cfg.Save();
            }
            ImGui.Dummy(UiVec(0f, 8f));
            using (UiDisabled.Scope(!enabled))
            {
                var limitSounds = Plugin.Cfg.LimitNotificationSoundsToTwoMinutes;
                if (DrawSettingsToggle("##limitNotificationSounds", "Limit notification sounds to once every 2 minutes", ref limitSounds))
                {
                    Plugin.Cfg.LimitNotificationSoundsToTwoMinutes = limitSounds;
                    Plugin.Cfg.Save();
                }
                ImGui.Dummy(UiVec(0f, 8f));
                var index = Math.Clamp(Plugin.Cfg.NotificationSoundIndex, 0, NotificationSoundNames.Length - 1);
                ImGui.SetNextItemWidth(MathF.Min(Ui(230f), ImGui.GetContentRegionAvail().X - Ui(100f)));
                if (ImGui.Combo("##notificationSoundChoice", ref index, NotificationSoundNames, NotificationSoundNames.Length))
                {
                    Plugin.Cfg.NotificationSoundIndex = index;
                    Plugin.Cfg.Save();
                }
                ImGui.SameLine();
                if (ImGui.Button("Preview##notificationSound") && DateTime.UtcNow - notificationPreviewLastUtc >= TimeSpan.FromSeconds(1))
                {
                    notificationPreviewLastUtc = DateTime.UtcNow;
                    PlaySelectedNotificationSound();
                }
            }
            ImGui.Dummy(UiVec(0f, 8f));
            DrawSettingsExplanation("Plays for new notifications. Messages are silent while their chat is visible, including Watch Party Mini Chat.");
            ImGui.Dummy(UiVec(0f, 6f));
            DrawSettingsHelper("Sound effects obtained from https://www.zapsplat.com");
            if (ImGui.IsItemClicked()) Dalamud.Utility.Util.OpenLink("https://www.zapsplat.com/");
            if (ImGui.IsItemHovered()) UiFonts.SetTooltip("Visit ZapSplat");
        }, autoHeight: true);
    }

    private bool PartyChatNotificationEnabled(PartyChatItemKind kind) => kind switch
    {
        PartyChatItemKind.RoomPresence => Plugin.Cfg.PartyPresenceNotificationsEnabled,
        PartyChatItemKind.Reaction => Plugin.Cfg.ReactLiveNotificationsEnabled,
        _ => true,
    };

    private void RefreshPartyNotificationPreferences()
    {
        var eligibleIds = partyChatItems.Where(item => PartyChatNotificationEnabled(item.Kind)).Select(item => item.Id).ToHashSet();
        partyChatUnreadIds.IntersectWith(eligibleIds);
        if (partyChatFirstUnreadId is { } first && !partyChatUnreadIds.Contains(first))
        {
            partyChatFirstUnreadId = partyChatItems.FirstOrDefault(item => partyChatUnreadIds.Contains(item.Id))?.Id;
            partyChatUnreadDividerShownUtc = null;
        }
        partyChatStickToBottom = partyChatWasAtBottom && partyChatFirstUnreadId is null;
    }
}
