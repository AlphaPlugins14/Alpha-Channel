using System.Security.Cryptography;
using AlphaChannel.BroadcastStudio;

namespace AlphaChannel.Plugin.Video;

internal sealed class MusicLibraryStorage(string folder)
{
    internal static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".wav", ".flac", ".ogg", ".opus", ".m4a", ".aac", ".aiff", ".wma" };
    internal string Root { get; } = Path.GetFullPath(folder);
    internal List<StudioTrack> Scan(IEnumerable<StudioTrack> known)
    {
        Directory.CreateDirectory(Root);
        var previous = known.ToList();
        return Directory.EnumerateFiles(Root).Where(p => Extensions.Contains(Path.GetExtension(p)) &&
                (File.GetAttributes(p) & FileAttributes.ReparsePoint) == 0).OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Select(p => previous.FirstOrDefault(t => SamePath(t.Path, p)) ??
                new StudioTrack(Guid.NewGuid().ToString("N"), p, Path.GetFileNameWithoutExtension(p))).ToList();
    }
    internal StudioTrack Import(StudioTrack source)
    {
        var path = Path.GetFullPath(source.Path);
        if (!File.Exists(path) || !Extensions.Contains(Path.GetExtension(path))) throw new IOException("Song is missing or unsupported.");
        Directory.CreateDirectory(Root);
        if (SamePath(Path.GetDirectoryName(path)!, Root) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
            return source with { Path = path };
        var name = Path.GetFileNameWithoutExtension(path); var extension = Path.GetExtension(path);
        for (var suffix = 1; ; suffix++)
        {
            var destination = Path.Combine(Root, name + (suffix == 1 ? "" : $" ({suffix})") + extension);
            if (File.Exists(destination))
            {
                if ((File.GetAttributes(destination) & FileAttributes.ReparsePoint) == 0 && EqualContents(path, destination))
                    return source with { Path = destination };
                continue;
            }
            var temporary = Path.Combine(Root, ".import-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.Copy(path, temporary, false);
                try { File.Move(temporary, destination, false); }
                catch (IOException) when (File.Exists(destination)) { continue; }
                return source with { Path = destination };
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
    internal static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    private static bool EqualContents(string a, string b)
    {
        if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
        using var left = File.OpenRead(a); using var right = File.OpenRead(b);
        return SHA256.HashData(left).AsSpan().SequenceEqual(SHA256.HashData(right));
    }
}
