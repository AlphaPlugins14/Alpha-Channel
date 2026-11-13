using AlphaChannel.Contracts;

namespace AlphaChannel.Plugin;

// Admission happens before allocating JSON or waiting for the socket send lock.
// These are safeguards for ordinary clients, not server-side enforcement.
internal sealed class RealtimeOutgoingPolicy(Func<long>? clock = null)
{
    internal const int MaxPending = 64;
    internal const int ReservedControls = 8;
    internal const int MaxRetainedCharacters = 2 * 1024 * 1024;
    private readonly object gate = new();
    private readonly Func<long> now = clock ?? (() => Environment.TickCount64);
    private int pending, batches, retained;
    private double chatTokens = 8, reactionTokens = 6;
    private long chatAt, reactionAt;
    private long nextWarning;
    private StreamControl? lastJoin, lastHello;
    private long joinAt, helloAt;

    internal static bool Validate(StreamControl message, out int characters)
    {
        characters = 0;
        if (!Add(message.Type, 64, ref characters) ||
            !Add(message.HostId, 512, ref characters) || !Add(message.UserId, 512, ref characters) ||
            !Add(message.TargetUserId, 512, ref characters) || !Add(message.DisplayName, 256, ref characters) ||
            !Add(message.Reason, 4096, ref characters) || !Add(message.Reaction, 131072, ref characters) ||
            !Add(message.ChatText, 32768, ref characters) || !Add(message.Url, 16384, ref characters) ||
            !Add(message.MediaTitle, 2048, ref characters) || !Add(message.MediaThumbnailUrl, 16384, ref characters) ||
            !Add(message.SubtitleLanguage, 128, ref characters) || !Add(message.SubtitleName, 1024, ref characters) ||
            !Add(message.Description, 4096, ref characters) || !Add(message.Location, 1024, ref characters) ||
            !Add(message.Password, 1024, ref characters)) return false;
        if (message.Participants is { } people)
        {
            if (people.Length > 512) return false;
            foreach (var person in people)
                if (person is null || !Add(person.UserId, 512, ref characters) ||
                    !Add(person.DisplayName, 256, ref characters)) return false;
        }
        return true;
    }
    private static bool Add(string? value, int maximum, ref int total)
    {
        var length = value?.Length ?? 0;
        if (length > maximum || total > 160000 - length) return false;
        total += length;
        return true;
    }

    internal IDisposable? TryAcquire(StreamControl message, out string reason, bool handoverState = false)
    {
        if (!Validate(message, out var characters)) { reason = "oversized fields or collection"; return null; }
        var control = message.Type is SignalType.StreamJoin or SignalType.StreamLeave or
            SignalType.StreamTransferHost or SignalType.StreamHello or SignalType.StreamResume or SignalType.StreamRecoveryDecline ||
            (handoverState && message.Type == SignalType.StreamState);
        lock (gate)
        {
            if (pending >= (control ? MaxPending : MaxPending - ReservedControls) ||
                retained > MaxRetainedCharacters + (control ? 262144 : 0) - characters)
            { reason = "outgoing backlog limit"; return null; }
            var time = now();
            if (message.Type == SignalType.StreamJoin && lastJoin == message && time - joinAt < 500 ||
                message.Type == SignalType.StreamHello && lastHello == message && time - helloAt < 500)
            { reason = "repeated connection action"; return null; }
            var chat = message.Type == SignalType.StreamChat && message.ChatText is { } text &&
                (!text.StartsWith("[[", StringComparison.Ordinal) || text.StartsWith("[[AC_MSG_1]]", StringComparison.Ordinal) ||
                 text.StartsWith("[[AC_MSG_REACT_1]]", StringComparison.Ordinal) || text.StartsWith("[[AC_MSG_DELETE_1]]", StringComparison.Ordinal));
            var reaction = message.Type == SignalType.StreamReaction && message.Reaction is { } glyph &&
                !glyph.StartsWith("[[", StringComparison.Ordinal);
            if (chat && !Take(ref chatTokens, ref chatAt, time, 8, 2) ||
                reaction && !Take(ref reactionTokens, ref reactionAt, time, 6, 2))
            { reason = "chat/reaction burst limit"; return null; }
            if (message.Type == SignalType.StreamJoin) { lastJoin = message; joinAt = time; }
            if (message.Type == SignalType.StreamHello) { lastHello = message; helloAt = time; }
            pending++; retained += characters;
            reason = string.Empty;
            return new Admission(() => { lock (gate) { pending--; retained -= characters; } });
        }
    }
    internal IDisposable? TryAcquireBatch()
    {
        lock (gate)
        {
            if (batches >= 8) return null;
            batches++;
            return new Admission(() => { lock (gate) batches--; });
        }
    }
    internal bool ShouldWarn()
    {
        lock (gate)
        {
            var time = now();
            if (time < nextWarning) return false;
            nextWarning = time + 10000;
            return true;
        }
    }
    private static bool Take(ref double tokens, ref long last, long time, int capacity, double rate)
    {
        tokens = Math.Min(capacity, tokens + Math.Max(0, time - last) * rate / 1000);
        last = time;
        if (tokens < 1) return false;
        tokens--; return true;
    }
    private sealed class Admission(Action release) : IDisposable
    {
        private Action? cleanup = release;
        public void Dispose() => Interlocked.Exchange(ref cleanup, null)?.Invoke();
    }
}
