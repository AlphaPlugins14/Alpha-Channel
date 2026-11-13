namespace AlphaChannel.Plugin;

internal enum BingoPattern { SingleLine, TwoLines, ThreeLines, Cross, X, FourCorners, FullHouse }

internal static class BingoRules
{
    internal static readonly BingoPattern[] Rotation = Enum.GetValues<BingoPattern>();
    private static readonly int[][] Lines = Enumerable.Range(0, 5)
        .Select(row => Enumerable.Range(0, 5).Select(col => row * 5 + col).ToArray())
        .Concat(Enumerable.Range(0, 5).Select(col => Enumerable.Range(0, 5).Select(row => row * 5 + col).ToArray()))
        .Concat(new[] { Enumerable.Range(0, 5).Select(i => i * 6).ToArray(),
            Enumerable.Range(0, 5).Select(i => (i + 1) * 4).ToArray() }).ToArray();

    internal static string Name(BingoPattern pattern) => pattern switch
    {
        BingoPattern.TwoLines => "Two lines", BingoPattern.ThreeLines => "Three lines",
        BingoPattern.Cross => "Cross", BingoPattern.X => "X shape",
        BingoPattern.FourCorners => "Four corners", BingoPattern.FullHouse => "Full house", _ => "One line",
    };
    internal static string Description(BingoPattern pattern) => pattern switch
    {
        BingoPattern.TwoLines => "Complete any two distinct rows, columns or diagonals.",
        BingoPattern.ThreeLines => "Complete any three distinct rows, columns or diagonals.",
        BingoPattern.Cross => "Complete the middle row and middle column (+).",
        BingoPattern.X => "Complete both diagonals (X).",
        BingoPattern.FourCorners => "Mark all four corners of the card.",
        BingoPattern.FullHouse => "Mark every number on the card.",
        _ => "Complete any row, column or diagonal.",
    };
    internal static bool ValidCard(int[]? card)
    {
        if (card is not { Length: 25 } || card[12] != 0) return false;
        for (var col = 0; col < 5; col++)
        {
            var seen = new HashSet<int>();
            for (var row = 0; row < 5; row++)
            {
                var index = row * 5 + col;
                if (index == 12) continue;
                if (card[index] < col * 15 + 1 || card[index] > col * 15 + 15 || !seen.Add(card[index])) return false;
            }
        }
        return true;
    }
    private static int LineCount(BingoPattern pattern) => pattern switch
    { BingoPattern.SingleLine => 1, BingoPattern.TwoLines => 2, BingoPattern.ThreeLines => 3, _ => 0 };

    // Representative preview for flexible line patterns; exact target cells for fixed patterns.
    internal static int[] PatternCells(BingoPattern pattern) => pattern switch
    {
        BingoPattern.Cross => Lines[2].Concat(Lines[7]).Distinct().ToArray(),
        BingoPattern.X => Lines[10].Concat(Lines[11]).Distinct().ToArray(),
        BingoPattern.FourCorners => [0, 4, 20, 24],
        BingoPattern.FullHouse => Enumerable.Range(0, 25).ToArray(),
        BingoPattern.TwoLines => Lines[0].Concat(Lines[4]).ToArray(),
        BingoPattern.ThreeLines => Lines[0].Concat(Lines[2]).Concat(Lines[4]).ToArray(),
        _ => Lines[2],
    };

    private static readonly Dictionary<BingoPattern, int[][]> Previews = Rotation.ToDictionary(
        pattern => pattern, BuildPreviews);

    private static int[][] BuildPreviews(BingoPattern pattern)
    {
        var required = LineCount(pattern);
        if (required == 0) return [PatternCells(pattern)];
        var masks = new HashSet<uint>();
        var previews = new List<int[]>();
        void Combine(int start, int remaining, uint mask)
        {
            if (remaining == 0)
            {
                if (masks.Add(mask))
                    previews.Add(Enumerable.Range(0, 25).Where(cell => (mask & (1u << cell)) != 0).ToArray());
                return;
            }
            for (var index = start; index <= Lines.Length - remaining; index++)
            {
                var combined = Lines[index].Aggregate(mask, (value, cell) => value | (1u << cell));
                Combine(index + 1, remaining - 1, combined);
            }
        }
        Combine(0, required, 0);
        return previews.ToArray();
    }

    internal static int PreviewCount(BingoPattern pattern) => Previews[pattern].Length;
    internal static int[] PreviewCells(BingoPattern pattern, long step) =>
        Previews[pattern][(int)(Math.Max(0, step) % Previews[pattern].Length)];

    internal static (int Complete, int Required) Progress(int[] card, IReadOnlyCollection<int> called, BingoPattern pattern)
    {
        if (!ValidCard(card) || !Enum.IsDefined(pattern)) return (0, 1);
        bool Marked(int index) => index == 12 || called.Contains(card[index]);
        var lines = LineCount(pattern);
        if (lines > 0) return (Lines.Count(line => line.All(Marked)), lines);
        var cells = PatternCells(pattern);
        return (cells.Count(Marked), cells.Length);
    }
    internal static bool ValidMarks(int[] card, IReadOnlyCollection<int> called, int[]? marks) =>
        marks is { Length: <= 24 } && marks.Distinct().Count() == marks.Length &&
        marks.All(n => n > 0 && card.Contains(n) && called.Contains(n));

    internal static bool CanMark(int[] card, IReadOnlyCollection<int> called, int number) =>
        number > 0 && card.Contains(number) && called.Contains(number);

    internal static bool CanClaim(int[] card, IReadOnlyCollection<int> called, int[] marks, BingoPattern pattern) =>
        ValidMarks(card, called, marks) && Wins(card, marks, pattern);

    internal static DateTime ReadyDeadline(DateTime deadline, DateTime now, bool allLocked) =>
        allLocked && deadline > now.AddSeconds(5) ? now.AddSeconds(5) : deadline;

    internal static bool Wins(int[] card, IReadOnlyCollection<int> called, BingoPattern pattern)
    {
        var progress = Progress(card, called, pattern);
        return progress.Complete >= progress.Required;
    }
    internal static HashSet<int> WinningCells(int[] card, IReadOnlyCollection<int> called, BingoPattern pattern)
    {
        if (!Wins(card, called, pattern)) return [];
        bool Marked(int index) => index == 12 || called.Contains(card[index]);
        return LineCount(pattern) > 0
            ? Lines.Where(line => line.All(Marked)).SelectMany(line => line).ToHashSet()
            : PatternCells(pattern).ToHashSet();
    }
}
