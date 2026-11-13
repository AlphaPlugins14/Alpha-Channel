using System.Text.Json;

namespace AlphaChannel.Plugin;

internal sealed class PixelsBoard
{
    internal const int Width = 128, Height = 72;
    internal static readonly uint[] Palette = [0xFFFFFF,0xC2C7D0,0x777D8C,0x242836,0x000000,0x6D001A,0xBE0039,0xFF4500,0xFFA800,0xFFD635,0xFFF8B8,0x00A368,0x00CC78,0x7EED56,0x00756F,0x009EAA,0x00CCC0,0x2450A4,0x3690EA,0x51E9F4,0x811E9F,0xB44AC0,0xFF99AA,0x9C6926];
    internal readonly int[] Colours = Enumerable.Repeat(-1, Width * Height).ToArray();
    internal readonly string?[] Owners = new string?[Width * Height];
    internal readonly long[] PlacedAt = new long[Width * Height];
    internal readonly Dictionary<string, string> Names = new();
    internal long Revision, NextAt, ServerOffset;
    internal bool Ready, Blocked;
    internal long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + ServerOffset;
    internal int RemainingSeconds => (int)Math.Max(0, Math.Ceiling((NextAt - Now) / 1000d));
    internal string Timer => Blocked ? "Contributions blocked" : !Ready ? "Connecting…" : RemainingSeconds == 0 ? "Your pixel is ready" : $"Your next pixel · {RemainingSeconds / 60}:{RemainingSeconds % 60:00}";
    internal (string Id, string Name, int Count)[] Rankings() => Owners.Where(id => id is not null).GroupBy(id => id!).Select(g => (g.Key, Names.GetValueOrDefault(g.Key, "Player"), g.Count())).OrderByDescending(p => p.Item3).ThenBy(p => p.Item2, StringComparer.OrdinalIgnoreCase).ToArray();
    // False means an ordered update was missed: reconnect for one fresh snapshot.
    internal bool Apply(JsonElement e)
    {
        var type = e.GetProperty("type").GetString();
        if (e.TryGetProperty("serverTime", out var clock)) ServerOffset = clock.GetInt64() - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (type is "snapshot" or "status") { NextAt = e.GetProperty("nextAt").GetInt64(); Blocked = e.GetProperty("blocked").GetBoolean(); }
        if (type == "snapshot")
        {
            if (e.GetProperty("width").GetInt32() != Width || e.GetProperty("height").GetInt32() != Height) throw new InvalidOperationException("Board version is incompatible.");
            Array.Fill(Colours, -1); Array.Clear(Owners); Array.Clear(PlacedAt); Names.Clear();
            foreach (var p in e.GetProperty("people").EnumerateArray()) Names[p.GetProperty("id").GetString()!] = p.GetProperty("name").GetString() ?? "Player";
            foreach (var c in e.GetProperty("cells").EnumerateArray())
            {
                var i = c[0].GetInt32(); var colour = c[1].GetInt32();
                if (i < 0 || i >= Colours.Length || colour < 0 || colour >= Palette.Length) throw new InvalidOperationException("Invalid board snapshot.");
                Colours[i] = colour; Owners[i] = c[2].GetString(); PlacedAt[i] = c[3].GetInt64();
            }
            Revision = e.GetProperty("revision").GetInt64(); Ready = true;
        }
        else if (type is "pixel" or "wipe" or "revision")
        {
            var revision = e.GetProperty("revision").GetInt64();
            if (revision <= Revision) return true;
            if (!Ready || revision != Revision + 1) return false;
            if (type == "pixel")
            {
                var i = e.GetProperty("index").GetInt32(); var c = e.GetProperty("color").GetInt32();
                if (i < 0 || i >= Colours.Length || c < 0 || c >= Palette.Length) return false;
                var owner = e.GetProperty("owner").GetString()!;
                Colours[i] = c; Owners[i] = owner; PlacedAt[i] = e.GetProperty("placedAt").GetInt64(); Names[owner] = e.GetProperty("name").GetString() ?? "Player";
            }
            else if (type == "wipe")
            {
                var id = e.GetProperty("accountId").GetString();
                for (var i = 0; i < Owners.Length; i++) if (Owners[i] == id) { Owners[i] = null; Colours[i] = -1; PlacedAt[i] = 0; }
            }
            Revision = revision;
        }
        return true;
    }
    internal static int SquareAt(float u, float v)
    {
        var x = u * 1280 - 128; var y = v * 720 - 64;
        return float.IsFinite(x) && float.IsFinite(y) && x >= 0 && y >= 0 && x < 1024 && y < 576 ? (int)(y / 8) * Width + (int)(x / 8) : -1;
    }
}
