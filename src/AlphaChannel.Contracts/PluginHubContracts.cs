namespace AlphaChannel.Contracts;

// Previous plugin code, likely needs removing with future updates
public sealed record InstalledPluginDto(string InternalName, string Name, string Version);

// Wholesale replace, not a diff - see PluginHubService.SyncAsync for why.
public sealed record SyncInstalledPluginsRequest(InstalledPluginDto[] Plugins);
