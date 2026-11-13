using System.Threading;

namespace AlphaChannel.Plugin.Core;

internal sealed class RoomCloseReconnectState
{
    internal const string Message = "Please wait.. closing previous room connection";
    private int closingGeneration = -1;
    internal bool Pending => Volatile.Read(ref closingGeneration) >= 0;
    internal void Begin(int generation) => Volatile.Write(ref closingGeneration, generation);
    internal void Reset() => Volatile.Write(ref closingGeneration, -1);
    internal void Confirm(int generation)
    {
        if (generation > Volatile.Read(ref closingGeneration)) Reset();
    }
    internal void ConnectionFailed(int generation, bool openedSocket)
    {
        // Failure on the intentionally closed socket is expected. Failure of
        // the replacement connection must keep its normal error explanation.
        if (!openedSocket || generation != Volatile.Read(ref closingGeneration)) Reset();
    }
}
