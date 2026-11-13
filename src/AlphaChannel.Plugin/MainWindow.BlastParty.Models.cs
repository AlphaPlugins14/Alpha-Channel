namespace AlphaChannel.Plugin;
internal sealed partial class MainWindow
{
    private sealed record BlastPlayer(string Id, string Name, bool Connected, bool Ready, bool Playing, int Score,
        int X, int Y, bool Alive, long Respawn, long Immune, int Ammo, int Capacity, int Range, long Refill, int[]? Target);
    private sealed record BlastPickup(int Cell, string Kind, long Available);
    private sealed record BlastBomb(int Id, string Owner, int X, int Y, int Range, long At);
    private sealed record BlastFlame(int X, int Y, string Owner, long Until);
    private sealed record BlastDrop(int Cell, long At);
    private sealed record BlastLava(int Side, long At, string Owner);
    private sealed record BlastState(string Phase, long Now, long End, int Revision, string Host, int Width, int Height,
        BlastPlayer[] Players, int[] Crates, BlastPickup[] Pickups, BlastBomb[] Bombs, BlastFlame[] Flames, BlastDrop[] Drops, BlastLava? Lava, bool Closed);
}
