namespace AlphaChannel.Plugin.Core;

// Only fixed, user-safe messages belong here; never forward arbitrary response bodies.
internal sealed class PartyQueueRequestException(string message) : InvalidOperationException(message);
