namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private bool usernameStyleSavePending;
    private long usernameStyleSaveAfter;

    private void QueueUsernameStyleSave()
    {
        if (!usernameStyleSavePending) usernameStyleSaveAfter = Environment.TickCount64 + 2000;
        usernameStyleSavePending = true;
    }

    private void FlushUsernameStyleSave()
    {
        if (Volatile.Read(ref disposeStarted) != 0 || !usernameStyleSavePending ||
            Environment.TickCount64 < usernameStyleSaveAfter) return;
        usernameStyleSavePending = false;
        Plugin.Cfg.SavePublicStateDeferred();
    }
}
