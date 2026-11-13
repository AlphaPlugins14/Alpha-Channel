namespace AlphaChannel.Contracts;

public sealed record TwitchStreamDto(string ChannelName, string Title, string GameName, int ViewerCount, string ThumbnailUrl, string Url);
