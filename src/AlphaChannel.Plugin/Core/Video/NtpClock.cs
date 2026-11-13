using System.Net;
using System.Net.Sockets;

namespace AlphaChannel.Plugin.Video;

internal static class NtpClock
{
    internal static async Task<long> GetUtcAsync(string server, CancellationToken shutdownToken, int port = 123, TimeSpan? timeout = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(5));
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(server, deadline.Token).ConfigureAwait(false);
            foreach (var address in addresses.Take(4))
            {
                if (deadline.IsCancellationRequested) break;
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                attempt.CancelAfter(TimeSpan.FromSeconds(1));
                try
                {
                    var packet = new byte[48]; packet[0] = 0x23; // NTPv4 client.
                    using var socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                    await socket.ConnectAsync(new IPEndPoint(address, port), attempt.Token).ConfigureAwait(false);
                    await socket.SendAsync(packet, SocketFlags.None, attempt.Token).ConfigureAwait(false);
                    var length = await socket.ReceiveAsync(packet, SocketFlags.None, attempt.Token).ConfigureAwait(false);
                    // Reject truncated replies, unsynchronised servers and kiss-of-death replies.
                    if (length < 48 || (packet[0] & 7) != 4 || (packet[0] >> 6) == 3 || packet[1] is 0 or > 15) continue;
                    ulong seconds = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(40));
                    ulong fraction = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(44));
                    var utc = new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero)
                        .AddMilliseconds((long)(seconds * 1000 + fraction * 1000 / 0x100000000UL));
                    if (Math.Abs((utc - DateTimeOffset.UtcNow).TotalDays) <= 1) return utc.ToUnixTimeMilliseconds();
                }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
                catch (SocketException) { }
            }
        }
        catch { /* Offline/timeout: retain the local clock fallback. */ }
        return 0;
    }
}
