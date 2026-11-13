using System.Text.Json;

namespace AlphaChannel.Plugin.Core;

internal enum PartyQueueAdding { HostOnly, Requests, Open }
internal sealed record PartyQueueItem(Guid Id, string Url, string Title, string Source, double? DurationSeconds,
    string? ThumbnailUrl, string AddedById, string AddedByName, string? Status = null);
internal sealed record PartyQueueSnapshot(Guid PartyId, long Revision, PartyQueueAdding Adding, bool AutoPlay,
    PartyQueueItem[] Upcoming, PartyQueueItem[] Requests, PartyQueueItem[] Played, PartyQueueItem? Current);
internal sealed record PartyQueueCommand(string Kind, Guid PartyId, Guid ActionId, PartyQueueItem? Item = null,
    Guid? ItemId = null, PartyQueueSnapshot? Snapshot = null);
internal sealed record PartyQueueRecovery(string AccountId, DateTime SavedUtc, PartyQueueSnapshot Snapshot);

internal sealed class PartyQueueState
{
    internal const int Limit = 100;
    internal Guid PartyId { get; private set; } = Guid.NewGuid();
    internal long Revision { get; private set; }
    internal PartyQueueAdding Adding { get; set; } = PartyQueueAdding.Requests;
    internal bool AutoPlay { get; set; } = true;
    internal List<PartyQueueItem> Upcoming { get; } = new();
    internal List<PartyQueueItem> Requests { get; } = new();
    internal List<PartyQueueItem> Played { get; } = new();
    internal PartyQueueItem? Current { get; set; }
    private readonly HashSet<Guid> actions = new();
    private readonly Queue<Guid> actionOrder = new();
    internal void Changed() => Revision++;
    internal PartyQueueSnapshot Snapshot() => new(PartyId, Revision, Adding, AutoPlay,
        Upcoming.ToArray(), Requests.ToArray(), Played.ToArray(), Current);

    internal bool ApplySnapshot(PartyQueueSnapshot snapshot, bool allowNewParty)
    {
        if (!Valid(snapshot) || !allowNewParty && snapshot.PartyId != PartyId ||
            snapshot.PartyId == PartyId && snapshot.Revision < Revision) return false;
        PartyId = snapshot.PartyId; Revision = snapshot.Revision; Adding = snapshot.Adding; AutoPlay = snapshot.AutoPlay;
        Upcoming.Clear(); Upcoming.AddRange(snapshot.Upcoming); Requests.Clear(); Requests.AddRange(snapshot.Requests);
        Played.Clear(); Played.AddRange(snapshot.Played); Current = snapshot.Current;
        return true;
    }

    internal string? Add(PartyQueueItem item, bool host, string senderId, string senderName, bool allowDuplicate = false)
    {
        if (!ValidItem(item)) return "That link cannot be added to the video queue.";
        if (!host && Adding == PartyQueueAdding.HostOnly) return "Only the host can add videos.";
        if (!allowDuplicate && Upcoming.Concat(Requests).Any(x => SameUrl(x.Url, item.Url))) return "That video is already queued or requested.";
        if (!host && Requests.Count(x => x.AddedById == senderId) + Upcoming.Count(x => x.AddedById == senderId) >= 10)
            return "You already have 10 videos in this party queue.";
        while (Count >= Limit && Played.Count > 0) Played.RemoveAt(0);
        if (Count >= Limit) return "The party queue is full (100 items including requests and history).";
        item = item with { Id = Guid.NewGuid(), AddedById = senderId, AddedByName = Clip(senderName, 64), Status = null };
        if (!host && Adding == PartyQueueAdding.Requests) Requests.Add(item); else Upcoming.Add(item);
        Changed(); return null;
    }

    internal bool HandleViewer(PartyQueueCommand command, string senderId, string senderName, out string? error)
    {
        error = null;
        if (command.PartyId != PartyId || command.ActionId == Guid.Empty || !actions.Add(command.ActionId)) return false;
        actionOrder.Enqueue(command.ActionId);
        while (actionOrder.Count > 512) actions.Remove(actionOrder.Dequeue());
        if (command.Kind == "add" && command.Item is { } item)
        { error = Add(item, false, senderId, senderName); return error is null; }
        if (command.Kind == "withdraw" && command.ItemId is { } id)
        {
            var removed = Requests.RemoveAll(x => x.Id == id && x.AddedById == senderId);
            if (Adding == PartyQueueAdding.Open) removed += Upcoming.RemoveAll(x => x.Id == id && x.AddedById == senderId);
            if (removed > 0) { Changed(); return true; }
        }
        return false;
    }

    internal void RecordPlayed(PartyQueueItem item, string? status = null)
    {
        Played.RemoveAll(x => x.Id == item.Id);
        Played.Add(item with { Status = status });
        while (Played.Count > 40 || Count > Limit) Played.RemoveAt(0);
        Changed();
    }
    internal void ReleaseCurrent(bool keptForLater, string? status = null)
    {
        var previous = Current;
        Current = null;
        if (previous is not null && !keptForLater) RecordPlayed(previous, status);
    }
    private int Count => Upcoming.Count + Requests.Count + Played.Count + (Current is null ? 0 : 1);
    internal static bool SameUrl(string a, string b) => string.Equals(VideoKey(a), VideoKey(b), StringComparison.Ordinal);
    private static string VideoKey(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return value;
        var host = uri.Host.ToLowerInvariant();
        if (host == "youtu.be") return "youtube:" + uri.AbsolutePath.Trim('/');
        if (host is "youtube.com" or "www.youtube.com" or "m.youtube.com" or "www.youtube-nocookie.com")
        {
            foreach (var part in uri.Query.TrimStart('?').Split('&'))
                if (part.StartsWith("v=", StringComparison.Ordinal)) return "youtube:" + Uri.UnescapeDataString(part[2..]);
            var path = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (path.Length == 2 && path[0] is "shorts" or "embed" or "live") return "youtube:" + path[1];
        }
        return uri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped).TrimEnd('/');
    }
    internal static string Clip(string? value, int length) => (value ?? "").Length > length ? value![..length] : value ?? "";
    internal static bool ValidItem(PartyQueueItem item) => item.Id != Guid.Empty && item.Url is { Length: <= 2048 } &&
        Uri.TryCreate(item.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" &&
        item.Title is { Length: <= 240 } && item.Source is { Length: <= 128 } && (item.ThumbnailUrl is null ||
            item.ThumbnailUrl.Length <= 512 && Uri.TryCreate(item.ThumbnailUrl, UriKind.Absolute, out var thumb) && thumb.Scheme is "http" or "https") &&
        item.AddedById is { Length: <= 128 } && item.AddedByName is { Length: <= 64 } &&
        (item.Status?.Length ?? 0) <= 160 && (item.DurationSeconds is null || double.IsFinite(item.DurationSeconds.Value) && item.DurationSeconds >= 0 && item.DurationSeconds <= 604800);
    internal static bool Valid(PartyQueueSnapshot snapshot)
    {
        if (snapshot.PartyId == Guid.Empty || snapshot.Revision < 0 || !Enum.IsDefined(snapshot.Adding) ||
            snapshot.Upcoming is null || snapshot.Requests is null || snapshot.Played is null) return false;
        var items = snapshot.Upcoming.Concat(snapshot.Requests).Concat(snapshot.Played).Concat(snapshot.Current is { } current ? [current] : Array.Empty<PartyQueueItem>()).ToArray();
        return items.Length <= Limit && items.All(x => x is not null && ValidItem(x)) && items.Select(x => x.Id).Distinct().Count() == items.Length;
    }
}
