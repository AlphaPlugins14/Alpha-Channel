using System.Security.Cryptography;
using System.Text;

namespace AlphaChannel.Plugin;

internal sealed class RealtimeAccountInUseException : IOException;

// The server stores one socket per account. An exclusive OS file handle also
// coordinates plugin reloads and multiple game processes on this computer.
internal sealed class RealtimeAccountLease : IDisposable
{
    private readonly FileStream marker;
    private int disposed;
    internal TimeSpan Cooldown { get; }
    internal static RealtimeAccountLease Acquire(string server, string account)
    {
        var directory = Path.Combine(Path.GetTempPath(), "AlphaChannel", "Realtime");
        Directory.CreateDirectory(directory);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(server.TrimEnd('/').ToLowerInvariant() + "\n" + account.ToLowerInvariant())));
        FileStream file;
        try { file = new FileStream(Path.Combine(directory, key + ".lease"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1); }
        catch (IOException exception) when ((exception.HResult & 0xffff) is 32 or 33)
        { throw new RealtimeAccountInUseException(); }
        try { return new RealtimeAccountLease(file); }
        catch { file.Dispose(); throw; }
    }
    private RealtimeAccountLease(FileStream file)
    {
        marker = file;
        Span<byte> bytes = stackalloc byte[8];
        var read = RandomAccess.Read(marker.SafeFileHandle, bytes, 0);
        var ticks = read == 8 ? BitConverter.ToInt64(bytes) : 0;
        var now = DateTime.UtcNow.Ticks;
        var remaining = ticks > now ? ticks - now : 0;
        Cooldown = TimeSpan.FromTicks(Math.Clamp(remaining, 0, TimeSpan.FromSeconds(30).Ticks));
        WriteCooldown(TimeSpan.FromSeconds(30));
    }
    internal void Refresh() => WriteCooldown(TimeSpan.FromSeconds(10));
    internal void StartConnecting() => WriteCooldown(TimeSpan.FromSeconds(30));
    private void WriteCooldown(TimeSpan delay)
    {
        Span<byte> bytes = stackalloc byte[8];
        BitConverter.TryWriteBytes(bytes, DateTime.UtcNow.Add(delay).Ticks);
        RandomAccess.Write(marker.SafeFileHandle, bytes, 0);
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { WriteCooldown(TimeSpan.FromSeconds(10)); }
        catch (IOException) { }
        finally { marker.Dispose(); }
    }
}
