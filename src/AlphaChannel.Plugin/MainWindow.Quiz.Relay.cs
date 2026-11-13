namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private void DisposeQuizRelay() => DisposeActivityRelay("quiz");
    private void UpdateQuizRelay() => UpdateActivityRelay("quiz");
    private bool TrySendQuizThroughCloudflare(string command) => TrySendActivityThroughCloudflare("quiz", command);
}
