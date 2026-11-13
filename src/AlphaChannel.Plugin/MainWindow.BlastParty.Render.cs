namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private static readonly uint[] BlastColours = [0x60CFFF, 0xFF75AD, 0xFFCE66, 0x80E5A0, 0xBA92FF, 0xFF9970, 0x91E5DD, 0xF1E8FF];
    private static bool BlastLavaAt(int x, int y, BlastState s, long now)
    {
        if (s.Lava is not { } lava || now < lava.At) return false;
        var depth = Math.Min(lava.Side % 2 == 0 ? 4 : 6, 1 + (now - lava.At) / 300);
        return lava.Side switch { 0 => y <= depth, 1 => x <= depth, 2 => y >= 10 - depth, _ => x >= 18 - depth };
    }
    private void RenderBlastParty(byte[] pixels)
    {
        void Rect(int x, int y, int w, int h, uint c) => FillPictionaryPixels(pixels, 1280, 720, x, y, w, h, c);
        void Text(int x, int y, string t, int scale = 2, uint c = 0xEDF2FF) => DrawPictionaryPixelText(pixels, 1280, 720, x, y, t, scale, c);
        void Center(int x, int y, string t, int scale = 2, uint c = 0xEDF2FF) => DrawPictionaryPixelTextCentered(pixels, 1280, 720, x, y, t, scale, c);
        void Circle(int x, int y, int r, uint c)
        {
            for (var dy = -r; dy <= r; dy++) { var half = (int)MathF.Sqrt(r * r - dy * dy); Rect(x - half, y + dy, half * 2 + 1, 1, c); }
        }
        static string Name(string n, int count = 16) => new(n.Take(count).Select(c => c is >= ' ' and <= '~' ? c : '?').ToArray());
        Rect(0, 0, 1280, 720, 0x080E1C);
        var s = blastState;
        for (var y = 0; y < 11; y++) for (var x = 0; x < 19; x++)
        {
            var px = 70 + x * 60; var py = 30 + y * 60;
            var wall = x is 0 or 18 || y is 0 or 10 || x % 2 == 0 && y % 2 == 0;
            Rect(px + 1, py + 1, 58, 58, wall ? 0x27364D : (x + y) % 2 == 0 ? 0x152535u : 0x122130u);
            if (wall) { Rect(px + 3, py + 3, 54, 4, 0x3A4F69); Rect(px + 3, py + 53, 54, 4, 0x1C263A); }
            else if (s is not null && BlastLavaAt(x, y, s, BlastNow))
            { Rect(px + 1, py + 1, 58, 58, 0xE64C26); Rect(px + 7, py + 15, 24, 5, 0xFFA342); Rect(px + 25, py + 38, 28, 5, 0xFFBD50); }
        }
        Text(92, 48, "BLAST PARTY", 2, 0xA9C6FF);
        if (s is null) { Rect(295, 240, 690, 200, 0x101C30); Center(640, 315, "CONNECTING TO BLAST PARTY", 3); return; }
        var now = BlastNow;
        if (s.Phase == "playing") Text(290, 48, $"{Math.Max(0, (s.End - now + 999) / 1000)}s", 2, 0xFFCF79);
        var leader = s.Players.Where(p => p.Playing).OrderByDescending(p => p.Score).ThenBy(p => p.Name).FirstOrDefault();
        if (leader is not null) Text(690, 48, $"LEADER {Name(leader.Name)}  {leader.Score}", 2, 0xFFCF79);
        foreach (var cell in s.Crates)
        {
            var x = 70 + cell % 19 * 60; var y = 30 + cell / 19 * 60;
            Rect(x + 5, y + 5, 50, 50, 0x80583B); Rect(x + 9, y + 9, 42, 4, 0xC39865); Rect(x + 9, y + 47, 42, 4, 0xBC8858);
            Rect(x + 12, y + 16, 4, 28, 0xA7774B); Rect(x + 44, y + 16, 4, 28, 0xA7774B);
        }
        foreach (var d in s.Drops)
        {
            var x = 70 + d.Cell % 19 * 60; var y = 30 + d.Cell / 19 * 60;
            if (now / 150 % 2 == 0) { Rect(x + 5, y + 5, 50, 4, 0xFFCA63); Rect(x + 5, y + 51, 50, 4, 0xFFCA63); Text(x + 23, y + 22, "!", 3, 0xFFCA63); }
        }
        foreach (var p in s.Pickups.Where(p => p.Available <= now))
        {
            var x = 100 + p.Cell % 19 * 60; var y = 60 + p.Cell / 19 * 60;
            var c = p.Kind switch { "bomb" => 0x72BFFFu, "range" => 0xFF9F5Fu, "speed" => 0x81E6A2u, "lava" => 0xFF5E42u, _ => 0xDDAD71u };
            Circle(x, y, 18, c); Center(x, y - 7, p.Kind switch { "bomb" => "+", "range" => "R", "speed" => "S", "lava" => "L", _ => "C" }, 2, 0x101A29);
        }
        foreach (var b in s.Bombs)
        {
            var x = 100 + b.X * 60; var y = 60 + b.Y * 60;
            Circle(x, y + 3, 19, now / 150 % 2 == 0 && b.At - now < 800 ? 0xE95C49u : 0x050A13u);
            Rect(x + 1, y - 24, 5, 10, 0x9CB2C7); Circle(x + 6, y - 23, 3, 0xFFE37A); Circle(x - 6, y - 4, 4, 0x60748C);
        }
        foreach (var f in s.Flames.Where(f => f.Until > now))
        {
            var x = 70 + f.X * 60; var y = 30 + f.Y * 60;
            Rect(x + 3, y + 17, 54, 26, 0xFF913A); Rect(x + 17, y + 3, 26, 54, 0xFFAD49); Rect(x + 21, y + 21, 18, 18, 0xFFF1A2);
        }
        var index = 0;
        foreach (var p in s.Players.Where(p => p.Playing))
        {
            var c = BlastColours[index++ % BlastColours.Length];
            if (!p.Alive || now < p.Immune && now / 150 % 2 == 0) continue;
            var position = new Vector2(p.X, p.Y);
            var old = blastPrevious?.Players.FirstOrDefault(v => v.Id == p.Id);
            if (old is { Alive: true } && Math.Abs(old.X - p.X) + Math.Abs(old.Y - p.Y) == 1)
                position = Vector2.Lerp(new Vector2(old.X, old.Y), position, Math.Clamp((float)(DateTime.UtcNow - blastReceivedUtc).TotalMilliseconds / 100, 0, 1));
            var x = 100 + (int)(position.X * 60); var y = 60 + (int)(position.Y * 60);
            if (p.Id == blastAccount) { Circle(x, y, 23, 0xF0F6FF); if (p.Target is [var tx, var ty]) { Rect(70 + tx * 60 + 24, 30 + ty * 60 + 24, 12, 12, c); } }
            Circle(x, y, 19, c); Rect(x - 10, y - 7, 20, 12, 0x101D2D); Rect(x - 6, y - 4, 4, 4, 0xFFFFFF); Rect(x + 3, y - 4, 4, 4, 0xFFFFFF);
            Center(x, y + 21, Name(p.Name, 7), 1, c);
        }
        if (s.Lava is { } lava && now < lava.At)
        {
            if (now / 200 % 2 == 0) switch (lava.Side)
            { case 0: Rect(70, 90, 1140, 6, 0xFF4444); break; case 1: Rect(128, 90, 6, 540, 0xFF4444); break; case 2: Rect(70, 624, 1140, 6, 0xFF4444); break; default: Rect(1144, 90, 6, 540, 0xFF4444); break; }
            Center(640, 16, $"LAVA SURGE IN {Math.Max(1, (lava.At - now + 999) / 1000)}", 2, 0xFF6D64);
        }
        var me = LocalBlastPlayer;
        Rect(82, 638, 300, 57, 0x101B2E); Text(98, 657, me?.Playing == true ? $"YOUR SCORE  {me.Score}" : "SPECTATING", 2, 0xBFCEEB);
        if (me?.Playing == true)
        {
            Rect(1030, 638, 180, 57, 0x284C7B); Circle(1060, 667, 18, 0x101D31);
            var fill = me.Ammo >= me.Capacity ? 1f : Math.Clamp(1f - (me.Refill - now) / 15000f, 0, 1);
            // Clip the bomb silhouette vertically to make refill progress visible without a large bar.
            for (var dy = 18 - (int)(fill * 36); dy <= 18; dy++) { var half = (int)MathF.Sqrt(Math.Max(0, 18 * 18 - dy * dy)); Rect(1060 - half, 667 + dy, half * 2 + 1, 1, 0xC8D9FF); }
            Rect(1059, 643, 4, 8, 0xFFCE6B); Text(1088, 650, "BOMB", 2); Text(1088, 675, $"{me.Ammo}/{me.Capacity}", 1);
            if (!me.Alive && s.Phase == "playing") { Rect(420, 330, 440, 54, 0x101B2E); Center(640, 347, $"RESPAWN IN {Math.Max(1, (me.Respawn - now + 999) / 1000)}", 2); }
        }
        if (s.Phase is "lobby" or "results" || s.Closed)
        {
            Rect(280, 140, 720, 492, 0x101B2E); Rect(280, 140, 720, 4, 0x769FF4);
            Center(640, 165, s.Closed ? "GAME ENDED" : s.Phase == "results" ? "ROUND RESULTS" : "BLAST PARTY", 4, 0xC6D6FF);
            Center(640, 219, "2-8 PLAYERS  /  120 SECONDS", 2, 0x95ABC9);
            var row = 260;
            foreach (var p in s.Players.Where(p => s.Phase == "results" ? p.Playing : p.Connected).OrderByDescending(p => p.Score).Take(8))
            { Text(340, row, Name(p.Name, 22), 2); Text(765, row, s.Phase == "results" ? $"{p.Score} PTS" : p.Ready ? "READY" : "WAITING", 2, p.Ready ? 0x80DFA2u : 0xA3B4CDu); row += 27; }
            if (!s.Closed)
            {
                Rect(380, 500, 520, 50, me?.Ready == true ? 0x285D4Du : 0x345B98u); Center(640, 516, me?.Ready == true ? "READY - CLICK TO CANCEL" : "READY UP", 2);
                if (blastHostMode) { Rect(380, 565, 520, 50, s.Players.Count(p => p.Ready && p.Connected) >= 2 ? 0x6151A9u : 0x26354Cu); Center(640, 581, "START ROUND", 2); }
                else Center(640, 584, "HOST STARTS WHEN PLAYERS ARE READY", 2, 0x95ABC9);
            }
        }
        if (s.Phase == "countdown") { Rect(480, 285, 320, 120, 0x101B2E); Center(640, 315, Math.Max(1, (s.End - now + 999) / 1000).ToString(), 8, 0xFFCE79); }
        if (blastClient?.Connected != true) { Rect(320, 695, 640, 25, 0x712D3F); Center(640, 701, "RECONNECTING - CONTROLS PAUSED", 1); }
    }
}
