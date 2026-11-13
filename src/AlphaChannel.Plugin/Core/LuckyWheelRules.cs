namespace AlphaChannel.Plugin;

[Serializable]
internal sealed record LuckyWheelSegment(string Id, string Text, uint Colour, string? Icon = null);

[Serializable]
internal sealed class LuckyWheelDesign
{
    public string Title { get; set; } = "Lucky Wheel";
    public string Subtitle { get; set; } = "Spin and see where luck takes you";
    public uint Background { get; set; } = 0x141B2D;
    public uint TextColour { get; set; } = 0xFFFFFF;
    public uint BorderColour { get; set; } = 0xFFD166;
    public bool AutomaticLabelColour { get; set; } = true;
    public bool RemoveWinner { get; set; }
    public bool Sound { get; set; } = true;
    public bool EveryoneTakesTurn { get; set; }
    public bool Confetti { get; set; } = true;
    public LuckyWheelSegment[] Segments { get; set; } = Enumerable.Range(0, 6)
        .Select(i => new LuckyWheelSegment(Guid.NewGuid().ToString("N"), $"Option {i + 1}", LuckyWheelRules.Rainbow[i])).ToArray();
    public LuckyWheelDesign Copy() => new()
    {
        Title = Title, Subtitle = Subtitle, Background = Background, TextColour = TextColour,
        BorderColour = BorderColour, AutomaticLabelColour = AutomaticLabelColour,
        RemoveWinner = RemoveWinner, Sound = Sound, EveryoneTakesTurn = EveryoneTakesTurn, Confetti = Confetti, Segments = Segments.ToArray(),
    };
}

internal sealed record LuckyWheelPlayer(string Id, string Name);
internal sealed record LuckyWheelResult(int Spin, string Text, uint Colour);
internal sealed record LuckyWheelSnapshot(string SessionId, string CanvasId, int Revision,
    LuckyWheelDesign Design, string[] WheelIds, string[] RemainingIds, bool Spinning, int Spin,
    string? WinnerId, double StartAngle, double EndAngle, long StartedUnixMs, int DurationMs,
    LuckyWheelResult[] History)
{
    public LuckyWheelPlayer[] Players { get; init; } = [];
    public int TurnNumber { get; init; } = 1;
    public string? SpinnerId { get; init; }
}

internal static class LuckyWheelRules
{
    internal const int MaxSegments = 30;
    internal const int MaxLabelLength = 20;
    internal const int SpinDurationMs = 6500;
    internal const double Tau = Math.PI * 2;
    internal static readonly uint[] Rainbow = [0xEF476F, 0xFF9F1C, 0xFFD166, 0x06D6A0, 0x118AB2, 0x9065E8, 0xF15BB5];
    internal static double Normalize(double angle) => (angle % Tau + Tau) % Tau;
    internal static double FinishAngle(double start, int winner, int count, int turns) =>
        start + Math.Clamp(turns, 5, 8) * Tau + Normalize(-(winner + .5) * Tau / count - start);
    internal static double AngleAt(double start, double end, long started, int duration, long now)
    {
        var t = Math.Clamp((now - started) / (double)Math.Max(1, duration), 0, 1);
        return start + (end - start) * (1 - Math.Pow(1 - t, 4));
    }
    internal static int SegmentAtPointer(double angle, int count) =>
        Math.Min(count - 1, (int)(Normalize(-angle) / Tau * count));
    internal static uint LabelColour(uint colour) =>
        .2126 * ((colour >> 16) & 255) + .7152 * ((colour >> 8) & 255) + .0722 * (colour & 255) > 145 ? 0x111827u : 0xFFFFFFu;
    internal static LuckyWheelSnapshot BeginSpin(LuckyWheelSnapshot state, int winner, int turns, long started)
    {
        if (state.Spinning || winner < 0 || winner >= state.RemainingIds.Length)
            throw new InvalidOperationException("A wheel must be ready and have an eligible winner.");
        var ids = state.RemainingIds.ToArray();
        var start = Normalize(state.EndAngle);
        return state with { WheelIds = ids, WinnerId = ids[winner], Spinning = true, Spin = state.Spin + 1,
            Revision = state.Revision + 1, StartedUnixMs = started, StartAngle = start,
            EndAngle = FinishAngle(start, winner, ids.Length, turns) };
    }
    internal static LuckyWheelSnapshot CompleteSpin(LuckyWheelSnapshot state, long now)
    {
        if (!state.Spinning || now < state.StartedUnixMs + state.DurationMs) return state;
        var winner = state.Design.Segments.First(s => s.Id == state.WinnerId);
        var completed = state with { Spinning = false, Revision = state.Revision + 1,
            History = new[] { new LuckyWheelResult(state.Spin, winner.Text, winner.Colour) }.Concat(state.History).Take(20).ToArray(),
            RemainingIds = state.Design.RemoveWinner ? state.RemainingIds.Where(id => id != state.WinnerId).ToArray() : state.RemainingIds };
        return completed.Design.EveryoneTakesTurn ? AdvanceTurn(completed) : completed;
    }
    internal static LuckyWheelSnapshot ResetResults(LuckyWheelSnapshot state)
    {
        var ids = state.Design.Segments.Select(s => s.Id).ToArray();
        return state with { WheelIds = ids, RemainingIds = ids.ToArray(), Spin = 0, WinnerId = null,
            StartAngle = 0, EndAngle = 0, StartedUnixMs = 0, History = [], TurnNumber = state.TurnNumber + 1, Revision = state.Revision + 1 };
    }
    internal static LuckyWheelSnapshot ReconcilePlayers(LuckyWheelSnapshot state, IEnumerable<LuckyWheelPlayer> roster)
    {
        // Keep the current spinner until their animation finishes; apply departures afterwards.
        if (state.Spinning) return state;
        var available = roster.Where(p => Guid.TryParseExact(p.Id, "D", out _))
            .DistinctBy(p => p.Id).Take(65).ToArray();
        var existing = state.Players.Where(p => available.Any(a => a.Id == p.Id))
            .Select(p => available.First(a => a.Id == p.Id));
        var next = existing.Concat(available.Where(p => !state.Players.Any(a => a.Id == p.Id))).ToArray();
        if (state.Players.SequenceEqual(next)) return state;
        var changedTurn = state.Players.FirstOrDefault()?.Id != next.FirstOrDefault()?.Id;
        return state with { Players = next, Revision = state.Revision + 1,
            TurnNumber = state.TurnNumber + (changedTurn ? 1 : 0) };
    }
    internal static LuckyWheelSnapshot AdvanceTurn(LuckyWheelSnapshot state)
    {
        if (state.Spinning) return state;
        return state with { Players = state.Players.Skip(1).Concat(state.Players.Take(1)).ToArray(),
            TurnNumber = state.TurnNumber + 1, Revision = state.Revision + 1 };
    }
    internal static bool CanPlayerSpin(LuckyWheelSnapshot state, string player, string host) =>
        !state.Spinning && state.RemainingIds.Length > 0 &&
        (state.Design.EveryoneTakesTurn ? state.Players.FirstOrDefault()?.Id == player : host == player);
    internal static bool ValidSpinAction(string command, string session)
    {
        var parts = command.Split('|');
        return parts.Length == 3 && parts[0] == "P" && parts[1] == session &&
            int.TryParse(parts[2], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var turn) && turn > 0;
    }
    internal static bool AcceptsSpinAction(LuckyWheelSnapshot state, string player, string host, string command) =>
        ValidSpinAction(command, state.SessionId) && command.Split('|')[2] == state.TurnNumber.ToString(System.Globalization.CultureInfo.InvariantCulture) &&
        CanPlayerSpin(state, player, host);
    internal static bool ValidDesign(LuckyWheelDesign? design) => design is not null &&
        design.Title is { Length: > 0 and <= 60 } && !string.IsNullOrWhiteSpace(design.Title) &&
        design.Subtitle is { Length: <= 100 } && design.Background <= 0xFFFFFF &&
        design.TextColour <= 0xFFFFFF && design.BorderColour <= 0xFFFFFF &&
        design.Segments is { Length: >= 2 and <= MaxSegments } segments &&
        segments.All(s => s is not null && Guid.TryParseExact(s.Id, "N", out _) &&
            s.Text is { Length: > 0 and <= MaxLabelLength } && !string.IsNullOrWhiteSpace(s.Text) && s.Colour <= 0xFFFFFF && LuckyWheelIcons.Valid(s.Icon)) &&
        segments.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() == segments.Length;
    internal static bool ValidSnapshot(LuckyWheelSnapshot? state)
    {
        if (state is null || !ValidDesign(state.Design) || !Guid.TryParseExact(state.SessionId, "N", out _) ||
            !Guid.TryParseExact(state.CanvasId, "N", out _) || state.Revision < 1 || state.Spin < 0 ||
            state.WheelIds is not { Length: >= 1 and <= MaxSegments } || state.RemainingIds is not { Length: <= MaxSegments } ||
            state.Players is not { Length: <= 65 } || state.TurnNumber < 1 ||
            state.Players.Any(p => p is null || !Guid.TryParseExact(p.Id, "D", out _) || p.Name is not { Length: > 0 and <= 96 }) ||
            state.Players.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != state.Players.Length ||
            state.Design.EveryoneTakesTurn && state.Players.Length == 0 ||
            state.History is not { Length: <= 20 } || state.DurationMs != SpinDurationMs ||
            !double.IsFinite(state.StartAngle) || !double.IsFinite(state.EndAngle) ||
            state.StartAngle < 0 || state.StartAngle >= Tau || state.EndAngle < state.StartAngle || state.EndAngle > state.StartAngle + 9 * Tau ||
            state.StartedUnixMs < 0 || state.StartedUnixMs > 253402300799999) return false;
        var ids = state.Design.Segments.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        if (!ValidIds(state.WheelIds, ids) || !ValidIds(state.RemainingIds, ids) ||
            state.History.Any(r => r is null || r.Spin < 1 || r.Text is not { Length: > 0 and <= MaxLabelLength } || r.Colour > 0xFFFFFF)) return false;
        if (state.WinnerId is null) return !state.Spinning && state.Spin == 0;
        var winner = Array.IndexOf(state.WheelIds, state.WinnerId);
        return state.Spin > 0 && winner >= 0 && SegmentAtPointer(state.EndAngle, state.WheelIds.Length) == winner;
    }
    private static bool ValidIds(string[] selected, HashSet<string> ids) =>
        selected.All(id => id is not null && ids.Contains(id)) && selected.Distinct(StringComparer.Ordinal).Count() == selected.Length;
}
