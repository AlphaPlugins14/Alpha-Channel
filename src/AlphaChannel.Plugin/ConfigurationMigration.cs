using Dalamud.Plugin;
using Newtonsoft.Json;

namespace AlphaChannel.Plugin;

/// <summary>
/// Loads configuration before any plugin subsystem can consume it and owns
/// every persisted schema transition from the version-2 baseline onward.
/// </summary>
internal static class ConfigurationMigration
{
    internal const int BaselineVersion = 2;
    internal const int CurrentVersion = 5;

    // Add each future migration here. A migration changes only one schema
    // version and must set Configuration.Version to its target version.
    private static readonly IReadOnlyDictionary<int, Action<Configuration>>
        MigrationSteps =
            new Dictionary<int, Action<Configuration>>
            {
                [2] = MigrateVersion2To3,
                [3] = MigrateVersion3To4,
                [4] = MigrateVersion4To5,
            };

    private static void MigrateVersion2To3(
        Configuration configuration)
    {
        // Earlier experimental builds could retain paths to external browser
        // profiles or manually exported cookie files. Version 3 starts from
        // explicit consent to Alpha Channel's embedded-browser session.
        configuration.YouTubeEmbeddedBrowserSessionEnabled =
            false;
        configuration.Version =
            3;
    }

    private static void MigrateVersion3To4(Configuration configuration)
    {
        // Enter commonly opens other plugins' chat input, even while we clear
        // the game-facing key state. Replace the previous default once.
        if (configuration.SnesKeyStart == (int)Dalamud.Game.ClientState.Keys.VirtualKey.RETURN)
            configuration.SnesKeyStart = (int)Dalamud.Game.ClientState.Keys.VirtualKey.T;
        configuration.Version = 4;
    }

    private static void MigrateVersion4To5(Configuration configuration)
    {
        configuration.MiniDockDisplay=MiniDockVisibilityPolicy.FromLegacy(configuration.HideMiniDock);
        configuration.Version=5;
    }

    internal static Configuration Load(
        IDalamudPluginInterface pluginInterface,
        out bool baselineReset)
    {
        baselineReset = false;

        var loaded =
            pluginInterface.GetPluginConfig() as Configuration;

        if (loaded is null)
        {
            var created = CreateCurrentConfiguration();
            created.Initialize(pluginInterface);
            created.Save();
            return created;
        }

        if (loaded.Version < BaselineVersion)
        {
            var backupPath =
                ArchiveConfiguration(
                    pluginInterface,
                    loaded,
                    $"pre-v{BaselineVersion}");

            var reset = CreateCurrentConfiguration();
            // A preferences reset must preserve cryptographic identity and recovery keys.
            reset.DmPrivateKeys = new(loaded.DmPrivateKeys);
            reset.DmPrivateKeyHistory = loaded.DmPrivateKeyHistory.ToDictionary(
                pair => pair.Key, pair => new List<string>(pair.Value));
            reset.Initialize(pluginInterface);
            reset.Save();

            baselineReset = true;

            

            return reset;
        }

        if (loaded.Version > CurrentVersion)
        {
            AepLog.Error(
                "[Configuration] Configuration version [redacted] is newer than supported version [redacted]. Using temporary defaults without overwriting the newer configuration.");

            var temporary = CreateCurrentConfiguration();
            temporary.Initialize(
                pluginInterface,
                enablePersistence: false);
            return temporary;
        }

        string? migrationBackupPath = null;
        if (loaded.Version < CurrentVersion)
        {
            migrationBackupPath =
                ArchiveConfiguration(
                    pluginInterface,
                    loaded,
                    $"v{loaded.Version}-before-v{CurrentVersion}");
        }

        var migrated = MigrateToCurrent(loaded);

        loaded.Initialize(pluginInterface);

        if (migrated)
        {
            loaded.Save();

            
        }

        return loaded;
    }

    private static Configuration CreateCurrentConfiguration() =>
        new()
        {
            Version = CurrentVersion,
            // Fresh installations use the measured two-column Friends layout.
            WindowSizePreset = UiWindowSizePreset.Design,
            WindowWidth = 1197f,
            WindowHeight = 812f,
            // Apply this scene only when creating a configuration. Existing
            // users keep their saved scene and legacy effect toggles.
            AudioVisualizerMode = Video.AudioVisualizerMode.NeonWave,
            TvAudioEffectsEnabled = true,
            TvAudioHaloEnabled = true,
            TvAudioLedStripsEnabled = true,
            TvAudioLightRingsEnabled = true,
            TvAudioSparklesEnabled = true,
            CurrentMusicScene = new Video.SavedMusicScene
            {
                Name = "Default scene",
                Presentation = Video.MusicSceneEffects.AddToUrl(
                    Video.AudioVisualizerSelection.AddToUrl("music-scene://preset",
                        Video.AudioVisualizerMode.NeonWave, Video.AudioVisualizerTheme.Rainbow),
                    Video.MusicSceneEffects.Default with
                    {
                        Mask = 63, Background = 0x000000,
                        Rings = 0x00DDFF, Sparkles = 0xFF1493,
                    }),
            },
        };

    private static bool MigrateToCurrent(
        Configuration configuration)
    {
        var changed = false;

        while (configuration.Version < CurrentVersion)
        {
            var previousVersion =
                configuration.Version;

            if (!MigrationSteps.TryGetValue(
                    previousVersion,
                    out var migrate))
            {
                throw new InvalidOperationException(
                    $"No configuration migration exists from version {previousVersion}.");
            }

            migrate(configuration);

            if (configuration.Version <= previousVersion)
            {
                throw new InvalidOperationException(
                    $"Configuration migration from version {previousVersion} did not advance the version.");
            }

            changed = true;
        }

        return changed;
    }

    private static string ArchiveConfiguration(
        IDalamudPluginInterface pluginInterface,
        Configuration configuration,
        string label)
    {
        Directory.CreateDirectory(
            pluginInterface.ConfigDirectory.FullName);

        var source =
            pluginInterface.ConfigFile.FullName;

        var originalName =
            Path.GetFileNameWithoutExtension(
                pluginInterface.ConfigFile.Name);

        var backupPath =
            Path.Combine(
                pluginInterface.ConfigDirectory.FullName,
                $"{originalName}.{label}.{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.json");

        if (File.Exists(source))
        {
            File.Copy(
                source,
                backupPath,
                overwrite: false);
        }
        else
        {
            // This is only a fallback for unusual hosts where a configuration
            // object was supplied without its backing file being present.
            File.WriteAllText(
                backupPath,
                JsonConvert.SerializeObject(
                    configuration,
                    Formatting.Indented));
        }

        return backupPath;
    }
}
