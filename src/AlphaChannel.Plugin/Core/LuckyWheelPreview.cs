using System.Text.Json;

namespace AlphaChannel.Plugin;

// Local editor simulation: contains no transport and always owns a copy of the draft.
internal sealed class LuckyWheelPreview
{
    private string? designKey;
    internal LuckyWheelSnapshot? State { get; private set; }
    internal bool SetDesign(LuckyWheelDesign design)
    {
        var key = JsonSerializer.Serialize(design);
        if (key == designKey) return false;
        if (!LuckyWheelRules.ValidDesign(design)) return false;
        designKey = key;
        var ids = design.Segments.Select(s => s.Id).ToArray();
        State = new(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), 1, design.Copy(), ids, ids.ToArray(),
            false, 0, null, 0, 0, 0, LuckyWheelRules.SpinDurationMs, []);
        return true;
    }
    internal void Spin(int winner, int turns, long now)
    {
        if (State is not { Spinning: false } state || state.RemainingIds.Length == 0) return;
        State = LuckyWheelRules.BeginSpin(state, winner, turns, now);
    }
    internal void Advance(long now)
    {
        if (State is { Spinning: true } state) State = LuckyWheelRules.CompleteSpin(state, now);
    }
    internal void Reset()
    {
        if (State is { } state) State = LuckyWheelRules.ResetResults(state with { Spinning = false });
    }
    internal void Clear() { designKey = null; State = null; }
}
