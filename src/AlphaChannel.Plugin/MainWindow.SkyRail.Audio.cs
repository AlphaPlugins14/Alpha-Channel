using AlphaChannel.Plugin.Video;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    // Independent effects playback: never replaces the TV media or music player.
    private readonly ChocoRaceAudioPlayer skyRailThemePlayer = new();
    private bool skyRailThemeStarted;
    private readonly ChocoRaceAudioPlayer skyRailBossAlertPlayer = new();
    private (string? Session, int Round, int Wave)? skyRailBossAlertPlayed;

    private static string SkyRailAudioPath(string fileName) => Path.Combine(
        Plugin.PluginInterface.AssemblyLocation.DirectoryName ?? string.Empty, "Assets", fileName);

    private void UpdateSkyRailAudio(DateTime now)
    {
        var volume = Plugin.Cfg.Muted ? 0 : Plugin.Cfg.Volume;
        volume = Plugin.Cfg.SkyRailMusicEnabled ? (int)Math.Round(volume * Math.Clamp(Plugin.Cfg.SkyRailMusicVolume, 0, 130) / 100d) : 0;
        if (!Plugin.Cfg.SkyRailMusicEnabled && skyRailThemeStarted)
        {
            skyRailThemePlayer.Stop();
            skyRailThemeStarted = false;
        }
        if (Plugin.Cfg.SkyRailMusicEnabled && !skyRailThemeStarted)
        {
            skyRailThemeStarted = true;
            skyRailThemePlayer.Play(SkyRailAudioPath("skyrail-theme.mp3"), 0, volume, loop: true);
        }
        // Leave space for the boss warning while the background track keeps running.
        skyRailThemePlayer.SetVolume(SkyRailBossWarning(now) ? (int)(volume * 0.4) : volume);
        UpdateSkyRailBossAlert(now);
    }

    private void ResetSkyRailAudio()
    {
        skyRailThemePlayer.Stop();
        skyRailThemeStarted = false;
        ResetSkyRailBossAlert();
    }

    private void UpdateSkyRailBossAlert(DateTime now)
    {
        // Balance this louder recording while retaining the TV volume and mute controls.
        var volume = Plugin.Cfg.Muted || !Plugin.Cfg.SkyRailSoundEffectsEnabled ? 0 :
            (int)Math.Round(Plugin.Cfg.Volume * 0.7 * Math.Clamp(Plugin.Cfg.SkyRailSoundEffectsVolume, 0, 130) / 100d);
        skyRailBossAlertPlayer.SetVolume(volume);
        if (!SkyRailBossWarning(now))
        {
            skyRailBossAlertPlayer.Stop();
            return;
        }
        var encounter = (skyRailSessionId, skyRailRound, skyRailWave);
        if (skyRailBossAlertPlayed == encounter) return;
        skyRailBossAlertPlayed = encounter;
        var path = Path.Combine(Plugin.PluginInterface.AssemblyLocation.DirectoryName ?? string.Empty,
            "Assets", "skyrail-boss-alert.mp3");
        // Viewers receiving the snapshot late join the same point in the alert.
        skyRailBossAlertPlayer.Play(path, Math.Max(0, (now - skyRailWaveStartsUtc).TotalSeconds), volume);
    }

    private void ResetSkyRailBossAlert()
    {
        skyRailBossAlertPlayer.Stop();
        skyRailBossAlertPlayed = null;
        skyRailWarningBackdrop = null;
    }
}
