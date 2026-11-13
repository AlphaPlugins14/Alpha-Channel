namespace AlphaChannel.WebViewHost;

internal sealed class CaptureHealth
{
    private DateTime? failureStarted;
    private bool reported;
    internal int Failures { get; private set; }
    internal bool Failed(DateTime now)
    {
        failureStarted ??= now;
        Failures++;
        if (reported || now - failureStarted.Value < TimeSpan.FromSeconds(2)) return false;
        reported = true;
        return true;
    }
    internal bool Recovered()
    {
        var clearError = reported;
        failureStarted = null; reported = false; Failures = 0;
        return clearError;
    }
}
