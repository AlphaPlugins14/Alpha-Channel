using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Auth;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Gui.ContextMenu;

namespace AlphaChannel.Plugin;

public sealed partial class Plugin
{
    private readonly Dictionary<string, (AccountProfileDto? Profile, DateTime Expires)> characterMenuMatches = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> characterMenuPending = new(StringComparer.OrdinalIgnoreCase);
    private string? characterMenuSessionToken;
    // AgentContext also backs shop/item menus and can retain a previous player's
    // name and world. Only accept world targets and known character-list addons.
    private static readonly HashSet<string> CharacterMenuAddons = new(StringComparer.Ordinal)
    {
        "ChatLog", "_PartyList", "PartyMemberList", "FriendList", "FreeCompany",
        "FreeCompanyMember", "SocialList", "ContactList", "BlackList", "MuteList",
        "LinkShell", "CrossWorldLinkshell", "LookingForGroup", "LookingForGroupDetail",
        "LookingForGroupSearch", "ContentMemberList", "SearchResult", "CharaSearch"
    };
    private static string CharacterMenuKey(string name, string world) => name.Trim() + "\n" + world.Trim();

    private void TickCharacterMenus()
    {
        var session = mainWindow.CurrentSession;
        if (characterMenuSessionToken != session?.Token)
        {
            characterMenuSessionToken = session?.Token;
            characterMenuMatches.Clear(); characterMenuPending.Clear();
        }
    }

    private async Task FetchCharacterMenuMatchesAsync(CharacterSession session, CharacterDirectoryTarget[] targets)
    {
        AccountProfileDto[] matches = [];
        try { matches = await mainWindow.CharacterDirectory.LookupAsync(session.Token, targets, sessionValidationLifetime.Token).ConfigureAwait(false); }
        catch (Exception) {
            frameworkActions.Enqueue(() => {
                if (mainWindow.CurrentSession?.Token == session.Token)
                    foreach (var target in targets) characterMenuPending.Remove(CharacterMenuKey(target.Name, target.World));
            });
            return;
        }
        frameworkActions.Enqueue(() =>
        {
            if (mainWindow.CurrentSession?.Token != session.Token) return;
            foreach (var target in targets)
            {
                var key = CharacterMenuKey(target.Name, target.World);
                characterMenuPending.Remove(key);
                var profile = matches.FirstOrDefault(p => string.Equals(p.CharacterName, target.Name, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(p.CharacterWorld, target.World, StringComparison.OrdinalIgnoreCase));
                characterMenuMatches[key] = (profile, DateTime.UtcNow.AddMinutes(profile is null ? 15 : 30));
            }
        });
    }

    private void OnCharacterMenuOpened(IMenuOpenedArgs args)
    {
        if (args.MenuType != ContextMenuType.Default ||
            args.AddonName is { Length: > 0 } addon && !CharacterMenuAddons.Contains(addon)) return;
        if (args.Target is not MenuTargetDefault { TargetName.Length: > 0 } target) return;
        var targetObject = target.TargetObject;
        if (targetObject is not null && targetObject is not IPlayerCharacter) return;
        if (targetObject is null && target.TargetCharacter is null &&
            (target.TargetContentId is 0 or ulong.MaxValue) && string.IsNullOrEmpty(args.AddonName)) return;
        var session = mainWindow.CurrentSession;
        string world;
        try { world = target.TargetHomeWorld.IsValid ? target.TargetHomeWorld.Value.Name.ToString() : ""; }
        catch { return; }
        if (world.Length == 0) return;
        var name = target.TargetName;
        var key = CharacterMenuKey(name, world);
        if (characterMenuMatches.Count >= 512)
        {
            foreach (var expired in characterMenuMatches.Where(x => x.Value.Expires <= DateTime.UtcNow).Select(x => x.Key).ToArray()) characterMenuMatches.Remove(expired);
            if (characterMenuMatches.Count >= 512) characterMenuMatches.Remove(characterMenuMatches.Keys.First());
        }
        if (session is not null && (!characterMenuMatches.TryGetValue(key, out var cached) || cached.Expires <= DateTime.UtcNow))
        {
            if (characterMenuPending.Add(key)) _ = FetchCharacterMenuMatchesAsync(session, [new(name, world)]);
        }
        args.AddMenuItem(new MenuItem
        {
            Name = characterMenuPending.Contains(key) ? "Alpha Channel (Loading…)" : "Alpha Channel",
            PrefixChar = 'A', PrefixColor = 1, IsSubmenu = true, IsEnabled = true,
            // Nonnegative priority keeps plugin actions below FFXIV's native actions.
            Priority = 0,
            OnClicked = clicked => OpenCharacterSubmenu(clicked, session, name, world, key)
        });
    }

    private void OpenCharacterSubmenu(IMenuItemClickedArgs clicked, CharacterSession? session,
        string name, string world, string key)
    {
        if (session is null || mainWindow.CurrentSession?.Token != session.Token)
        {
            clicked.OpenSubmenu([new MenuItem { Name = "Sign in to Alpha Channel first", IsEnabled = false }]);
            return;
        }
        // A lookup may finish between opening the native menu and its
        // submenu. Use the latest match, never a captured stale result.
        var profile = characterMenuMatches.TryGetValue(key, out var match) && match.Expires > DateTime.UtcNow &&
            match.Profile is { } found && mainWindow.CanUseCharacterMenuTarget(found.AccountId) ? found : null;
        if (profile is null)
        {
            var pending = characterMenuPending.Contains(key);
            var known = characterMenuMatches.TryGetValue(key, out var resolved) && resolved.Expires > DateTime.UtcNow;
            if (!known && !pending && characterMenuPending.Add(key))
            {
                _ = FetchCharacterMenuMatchesAsync(session, [new(name, world)]);
                pending = true;
            }
            clicked.OpenSubmenu([new MenuItem
            {
                Name = pending ? "Loading… click to check again" : "No Alpha Channel account found",
                IsEnabled = pending, IsSubmenu = pending,
                OnClicked = retry => OpenCharacterSubmenu(retry, session, name, world, key)
            }]);
            return;
        }
        clicked.OpenSubmenu([
            CharacterMenuItem("Add to friends", "friend", session, profile, name, world),
            CharacterMenuItem("View Profile", "profile", session, profile, name, world),
            CharacterMenuItem("Join Watch Party", "join", session, profile, name, world),
            CharacterMenuItem("Invite to Watch Party", "invite", session, profile, name, world)
        ]);
    }

    private MenuItem CharacterMenuItem(string label, string action, CharacterSession? session, AccountProfileDto? profile, string name, string world) => new()
    {
        Name = label, IsEnabled = session is not null && profile is not null,
        OnClicked = clicked => { if (session is not null && profile is not null) _ = RecheckCharacterMenuActionAsync(session, profile.AccountId, name, world, action); }
    };

    private async Task RecheckCharacterMenuActionAsync(CharacterSession session, string expectedAccount, string name, string world, string action)
    {
        try
        {
            var matches = await mainWindow.CharacterDirectory.LookupAsync(session.Token, [new(name, world)], sessionValidationLifetime.Token).ConfigureAwait(false);
            frameworkActions.Enqueue(() =>
            {
                if (mainWindow.CurrentSession?.Token != session.Token) return;
                var profile = matches.FirstOrDefault(p => p.AccountId == expectedAccount);
                if (profile is not null) mainWindow.RunCharacterMenuAction(profile, action);
                else { characterMenuMatches.Remove(CharacterMenuKey(name, world)); ChatGui.Print("[AlphaChannel] This character is no longer available through the character directory."); }
            });
        }
        catch (Exception) { frameworkActions.Enqueue(() => ChatGui.Print("[AlphaChannel] Character directory unavailable. Please try again.")); }
    }
}
