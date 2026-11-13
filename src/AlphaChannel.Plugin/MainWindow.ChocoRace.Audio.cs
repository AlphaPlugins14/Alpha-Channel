using AlphaChannel.Plugin.Video;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private const double ChocoRaceStartAlertLeadSeconds = 3d;
    private readonly ChocoRaceAudioPlayer chocoRaceThemePlayer = new();
    private readonly ChocoRaceAudioPlayer chocoRaceStartPlayer = new();
    private readonly ChocoRaceAudioPlayer chocoRaceMeteorPlayer = new();
    private readonly ChocoRaceAudioPlayer chocoRaceFanfarePlayer = new();
    private bool chocoRaceThemeStarted;
    private bool chocoRaceStartPlayedThisRound;
    private bool chocoRaceStartPlaying;
    private bool chocoRaceMeteorPlayedThisRound;
    private bool chocoRaceMeteorPlaying;
    private bool chocoRaceFanfarePlayedThisRound;
    private bool chocoRaceFanfarePlaying;

    private static string ChocoRaceAudioPath(string fileName) => Path.Combine(
        Plugin.PluginInterface.AssemblyLocation.DirectoryName ?? string.Empty,
        "Assets", fileName);

    private void UpdateChocoRaceAudio()
    {
        var volume = Plugin.Cfg.Muted ? 0 : Plugin.Cfg.Volume;
        if (!Plugin.Cfg.ChocoRaceMusicEnabled && chocoRaceThemeStarted)
        {
            chocoRaceThemePlayer.Stop();
            chocoRaceThemeStarted = false;
        }
        else if (Plugin.Cfg.ChocoRaceMusicEnabled && !chocoRaceThemeStarted)
        {
            chocoRaceThemeStarted = true;
            chocoRaceThemePlayer.Play(ChocoRaceAudioPath("chocorace-theme.mp3"), 0d, volume, loop: true);
        }

        var remaining = (chocoRacePhaseEndsUtc - ChocoRaceNowUtc).TotalSeconds;
        if (!chocoRaceStartPlayedThisRound && chocoRacePhase == ChocoRacePhase.Betting &&
            remaining is > 0d and <= ChocoRaceStartAlertLeadSeconds)
        {
            chocoRaceStartPlayedThisRound = true;
            if (Plugin.Cfg.ChocoRaceSoundEffectsEnabled)
            {
                chocoRaceStartPlaying = true;
                chocoRaceStartPlayer.Play(ChocoRaceAudioPath("chocorace-start.mp3"),
                    ChocoRaceStartAlertLeadSeconds - remaining, volume);
            }
        }
        if (chocoRaceStartPlaying && (!Plugin.Cfg.ChocoRaceSoundEffectsEnabled ||
                                      chocoRacePhase != ChocoRacePhase.Betting ||
                                      chocoRaceStartPlayer.Finished || chocoRaceStartPlayer.Failed))
        {
            chocoRaceStartPlayer.Stop();
            chocoRaceStartPlaying = false;
        }

        if (!chocoRaceMeteorPlayedThisRound && chocoRacePhase == ChocoRacePhase.Racing)
        {
            var chest = chocoRaceObstacles.FirstOrDefault(obstacle => obstacle.IsChest);
            if (chest is not null)
            {
                var sinceCollection = (ChocoRaceNowUtc - chocoRaceStartedUtc).TotalSeconds -
                    chest.HitSeconds;
                if (sinceCollection is >= 0d and < 1d)
                {
                    chocoRaceMeteorPlayedThisRound = true;
                    if (Plugin.Cfg.ChocoRaceSoundEffectsEnabled)
                    {
                        chocoRaceMeteorPlaying = true;
                        chocoRaceMeteorPlayer.Play(ChocoRaceAudioPath("chocorace-meteor.mp3"),
                            sinceCollection, volume);
                    }
                }
                else if (sinceCollection >= 1d)
                    chocoRaceMeteorPlayedThisRound = true;
            }
        }
        if (chocoRaceMeteorPlaying && (!Plugin.Cfg.ChocoRaceSoundEffectsEnabled ||
                                       chocoRacePhase != ChocoRacePhase.Racing ||
                                       chocoRaceMeteorPlayer.Finished ||
                                       chocoRaceMeteorPlayer.Failed))
        {
            chocoRaceMeteorPlayer.Stop();
            chocoRaceMeteorPlaying = false;
        }

        if (!chocoRaceFanfarePlayedThisRound && chocoRacePhase == ChocoRacePhase.Results)
        {
            chocoRaceFanfarePlayedThisRound = true;
            if (Plugin.Cfg.ChocoRaceSoundEffectsEnabled)
            {
                chocoRaceFanfarePlaying = true;
                chocoRaceFanfarePlayer.Play(ChocoRaceAudioPath("chocorace-fanfare.mp3"), 0d, volume);
            }
        }
        if (chocoRaceFanfarePlaying && (!Plugin.Cfg.ChocoRaceSoundEffectsEnabled ||
                                        chocoRacePhase != ChocoRacePhase.Results ||
                                        chocoRaceFanfarePlayer.Finished || chocoRaceFanfarePlayer.Failed))
        {
            chocoRaceFanfarePlayer.Stop();
            chocoRaceFanfarePlaying = false;
        }

        // Only duck the theme for the race-start alert; other effects mix at full music volume.
        chocoRaceThemePlayer.SetVolume(chocoRaceStartPlaying
            ? (int)Math.Round(volume * 0.4d) : volume);
        chocoRaceStartPlayer.SetVolume(volume);
        chocoRaceMeteorPlayer.SetVolume(volume);
        chocoRaceFanfarePlayer.SetVolume(volume);
    }

    private void ResetChocoRaceAudio()
    {
        chocoRaceThemePlayer.Stop();
        chocoRaceStartPlayer.Stop();
        chocoRaceMeteorPlayer.Stop();
        chocoRaceFanfarePlayer.Stop();
        chocoRaceThemeStarted = false;
        chocoRaceStartPlayedThisRound = false;
        chocoRaceStartPlaying = false;
        chocoRaceMeteorPlayedThisRound = false;
        chocoRaceMeteorPlaying = false;
        chocoRaceFanfarePlayedThisRound = false;
        chocoRaceFanfarePlaying = false;
    }
}
