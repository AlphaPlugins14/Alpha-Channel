using System.Text.Json;
using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

// Screen is a calibration tool — transform + clearer presets (Venues deferred from launch nav).
internal sealed partial class MainWindow
{
    private string presetNameInput =
    string.Empty;
    private bool presetAutoApplyInput;

    private string screenClipboardStatus =
        string.Empty;

    private bool screenClipboardStatusIsError;

    private sealed class ScreenClipboardPayload
    {
        public string? Type { get; set; }

        public int Version { get; set; }

        public float? X { get; set; }

        public float? Y { get; set; }

        public float? Z { get; set; }

        public float? Yaw { get; set; }

        public bool DisableFixedScaleRatio { get; set; }

        public float? WidthScale { get; set; }

        public float? HeightScale { get; set; }
    }

    private void ExportScreenSettingsToClipboard(
    VideoEngine engine)
    {
        var position =
            engine.ScreenPosition;

        var payload =
            new ScreenClipboardPayload
            {
                Type =
                    "AlphaChannelScreen",

                Version =
                    1,

                X =
                    position.X,

                Y =
                    position.Y,

                Z =
                    position.Z,

                Yaw =
                    engine.ScreenYaw,

                DisableFixedScaleRatio =
                    engine.DisableFixedScreenScaleRatio,

                WidthScale =
                    engine.ScreenWidthScale,

                HeightScale =
                    engine.ScreenHeightScale,
            };

        var json =
            JsonSerializer.Serialize(
                payload);

        ImGui.SetClipboardText(
            json);

        screenClipboardStatus =
            "Screen settings copied to clipboard.";

        screenClipboardStatusIsError =
            false;
    }

    private void ImportScreenSettingsFromClipboard(
        VideoEngine engine)
    {
        try
        {
            var clipboardText =
                ImGui.GetClipboardText();

            if (string.IsNullOrWhiteSpace(
                    clipboardText))
            {
                SetScreenImportError(
                    "The clipboard is empty.");

                return;
            }

            var payload =
                JsonSerializer.Deserialize<ScreenClipboardPayload>(
                    clipboardText);

            if (payload is null ||
                !string.Equals(
                    payload.Type,
                    "AlphaChannelScreen",
                    StringComparison.Ordinal) ||
                payload.Version != 1 ||
                payload.X is not { } x ||
                payload.Y is not { } y ||
                payload.Z is not { } z ||
                payload.Yaw is not { } yaw ||
                payload.WidthScale is not { } widthScale ||
                payload.HeightScale is not { } heightScale ||
                !float.IsFinite(x) ||
                !float.IsFinite(y) ||
                !float.IsFinite(z) ||
                !float.IsFinite(yaw) ||
                !float.IsFinite(widthScale) ||
                !float.IsFinite(heightScale) ||
                widthScale <
                VideoEngine.MinScreenScale ||
                widthScale >
                VideoEngine.MaxScreenScale ||
                heightScale <
                VideoEngine.MinScreenScale ||
                heightScale >
                VideoEngine.MaxScreenScale)
            {
                SetScreenImportError(
                    "The clipboard does not contain valid AlphaChannel screen settings.");

                return;
            }

            if (!payload.DisableFixedScaleRatio)
            {
                heightScale =
                    widthScale;
            }

            var position =
                new Vector3(
                    x,
                    y,
                    z);

            engine.SetScreenTransform(
                position,
                yaw,
                payload.DisableFixedScaleRatio,
                widthScale,
                heightScale, manualAdjustment: true);

            Plugin.Cfg.ScreenPosition =
                position;

            Plugin.Cfg.ScreenYaw =
                yaw;

            Plugin.Cfg.ScreenScale =
                widthScale;

            Plugin.Cfg.DisableFixedScreenScaleRatio =
                payload.DisableFixedScaleRatio;

            Plugin.Cfg.ScreenWidthScale =
                widthScale;

            Plugin.Cfg.ScreenHeightScale =
                heightScale;

            Plugin.Cfg.Save();

            screenClipboardStatus =
                "Screen settings imported successfully.";

            screenClipboardStatusIsError =
                false;
        }
        catch (JsonException)
        {
            SetScreenImportError(
                "The clipboard does not contain valid AlphaChannel screen settings.");
        }
        catch (Exception)
        {
            AepLog.Warning(
                "[Screen] Import failed.");

            SetScreenImportError(
                "Screen settings could not be imported.");
        }
    }

    private void SetScreenImportError(
        string message)
    {
        screenClipboardStatus =
            message;

        screenClipboardStatusIsError =
            true;
    }

}

