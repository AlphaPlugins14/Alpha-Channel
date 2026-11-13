namespace AlphaChannel.Plugin.Core;

// Written only during the initial plugin load. A clean load clears the record
// before ordinary feature startup, playback and shutdown can occur.
internal sealed class StartupRecoveryRecord(string directory, Version version)
{
    private string RecordPath => Path.Combine(directory, "startup-in-progress");

    internal bool WasInterrupted(bool updating)
    {
        if (updating) return false;
        try
        {
            return Version.TryParse(File.ReadAllText(RecordPath), out var previous) && previous == version;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    internal void Begin()
    {
        Directory.CreateDirectory(directory);
        var temporary = RecordPath + ".tmp";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            file.Write(System.Text.Encoding.UTF8.GetBytes(version.ToString()));
            file.Flush(flushToDisk: true);
        }
        File.Move(temporary, RecordPath, overwrite: true);
    }

    internal void Complete() => File.Delete(RecordPath);
}
