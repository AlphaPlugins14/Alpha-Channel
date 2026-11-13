using AlphaChannel.Plugin.BroadcastAllowance;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private bool allowanceCalculatorInitialized;
    private int allowanceCalculatorTier;
    private int allowanceCalculatorViewers = 1;
    private int allowanceCalculatorMethod;
    private int allowanceCalculatorQuality = 2;

    private static readonly string[] CalculatorMemberships = ["Free", "Patreon Tier 1", "Patreon Tier 2", "Patreon Tier 3", "Patreon Tier 4", "Patreon Tier 5 or higher"];
    private static readonly string[] CalculatorMethods = ["Broadcast Studio", "DJ Live (external app)", "Retro games", "Browser", "Local video", "PC live stream"];
    private sealed record CalculatorQuality(string Label, BroadcastAllowanceQuality Quality = BroadcastAllowanceQuality.DataSaver, int AudioBitrate = 128, double PcBitrate = 0d, int RequiredTier = 0);
    private static readonly CalculatorQuality[] CalculatorStudioQualities =
    [
        new("Data Saver", AudioBitrate: 96),
        new("Standard", AudioBitrate: 128),
        new("Recommended", AudioBitrate: 192),
        new("High Quality", AudioBitrate: 256, RequiredTier: 2),
        new("Highest Quality", AudioBitrate: 320, RequiredTier: 3),
    ];
    private static readonly CalculatorQuality[] CalculatorVideoQualities =
    [
        new("Data Saver"),
        new("Standard Quality", BroadcastAllowanceQuality.Standard, RequiredTier: 2),
        new("High Quality", BroadcastAllowanceQuality.High, RequiredTier: 3),
    ];
    private static readonly CalculatorQuality[] CalculatorGameQualities =
    [
        new("Data Saver"),
        new("Standard Native Quality", BroadcastAllowanceQuality.Standard, RequiredTier: 2),
    ];
    private static readonly CalculatorQuality[] CalculatorPcQualities =
    [
        new("Data Saver", PcBitrate: 1.25d),
        new("Standard", PcBitrate: 2.5d),
        new("Recommended", PcBitrate: 4.16d),
        new("High Quality", PcBitrate: 6d),
        new("Highest Quality", PcBitrate: 8d),
    ];

    private CalculatorQuality[] AllowanceCalculatorQualities() => allowanceCalculatorMethod switch
    {
        0 => CalculatorStudioQualities,
        2 => CalculatorGameQualities,
        3 or 4 => CalculatorVideoQualities,
        5 => CalculatorPcQualities,
        _ => [new CalculatorQuality("Standard")],
    };

    private CalculatorQuality AllowedCalculatorQuality()
    {
        var qualities = AllowanceCalculatorQualities();
        allowanceCalculatorQuality = Math.Clamp(allowanceCalculatorQuality, 0, qualities.Length - 1);
        while (allowanceCalculatorQuality > 0 && qualities[allowanceCalculatorQuality].RequiredTier > allowanceCalculatorTier)
            allowanceCalculatorQuality--;
        return qualities[allowanceCalculatorQuality];
    }

    private void DrawCalculatorQualityChoice()
    {
        var selected = AllowedCalculatorQuality();
        if (!UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginCombo("##calculatorQuality", selected.Label))) return;
        var qualities = AllowanceCalculatorQualities();
        for (var index = 0; index < qualities.Length; index++)
        {
            var quality = qualities[index];
            var locked = quality.RequiredTier > allowanceCalculatorTier;
            using (UiDisabled.Scope(locked))
            {
                var label = locked ? $"{quality.Label} (Patreon Tier {quality.RequiredTier})" : quality.Label;
                if (ImGui.Selectable(label, allowanceCalculatorQuality == index) && !locked)
                    allowanceCalculatorQuality = index;
            }
        }
        ImGui.EndCombo();
    }

    private void DrawHelpAllowanceCalculator()
    {
        if (!allowanceCalculatorInitialized)
        {
            allowanceCalculatorTier = Math.Clamp(ConfiguredPatreonTier(), 0, 5);
            allowanceCalculatorInitialized = true;
        }

        using (ImRaii.PushColor(ImGuiCol.FrameBg, FrameBg).Push(ImGuiCol.FrameBgHovered, CardBgHover).Push(ImGuiCol.Border, BorderSubtle))
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, Ui(8f)).Push(ImGuiStyleVar.FramePadding, UiVec(12f, 8f)).Push(ImGuiStyleVar.FrameBorderSize, Ui(1f)))
        {
            var columns = ImGui.GetContentRegionAvail().X >= Ui(420f) ? 2 : 1;
            if (ImGui.BeginTable("##allowanceCalculatorInputs", columns, ImGuiTableFlags.SizingStretchSame))
            {
                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding(); ImGui.TextUnformatted("Membership");
                ImGui.SetNextItemWidth(-1f);
                DrawCalculatorChoice("##calculatorMembership", CalculatorMemberships, ref allowanceCalculatorTier);

                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding(); ImGui.TextUnformatted("Number of viewers");
                ImGui.SetNextItemWidth(-1f);
                ImGui.InputInt("##calculatorViewers", ref allowanceCalculatorViewers, 1, 5);
                allowanceCalculatorViewers = Math.Clamp(allowanceCalculatorViewers, 0, 10000);
                DrawSocialHelper("Excludes you, the host.");

                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding(); ImGui.TextUnformatted("Stream method");
                ImGui.SetNextItemWidth(-1f);
                if (DrawCalculatorChoice("##calculatorMethod", CalculatorMethods, ref allowanceCalculatorMethod))
                    allowanceCalculatorQuality = allowanceCalculatorMethod is 0 or 5 ? 2 : 0;

                AllowedCalculatorQuality();
                if (allowanceCalculatorMethod != 1)
                {
                    ImGui.TableNextColumn();
                    ImGui.AlignTextToFramePadding(); ImGui.TextUnformatted("Quality");
                    ImGui.SetNextItemWidth(-1f);
                    DrawCalculatorQualityChoice();
                }
                ImGui.EndTable();
            }
        }

        var selectedQuality = AllowedCalculatorQuality();
        var method = allowanceCalculatorMethod switch
        {
            0 or 1 => BroadcastAllowanceMethod.DjLive,
            2 => BroadcastAllowanceMethod.RetroGame,
            3 => BroadcastAllowanceMethod.Browser,
            4 => BroadcastAllowanceMethod.LocalVideo,
            _ => BroadcastAllowanceMethod.Obs,
        };
        var usage = new BroadcastAllowanceUsage(method, selectedQuality.PcBitrate,
            BroadcastAllowanceService.ViewerConsumptionMultiplier(allowanceCalculatorTier, allowanceCalculatorViewers, 0),
            selectedQuality.Quality, selectedQuality.AudioBitrate);
        var creditsPerMinute = BroadcastAllowanceService.TokensPerMinute(usage);
        var weeklyCredits = BroadcastAllowanceService.WeeklyTokensForTier(allowanceCalculatorTier);
        var rateText = $"About {creditsPerMinute:N1} credits per minute";
        var hoursText = creditsPerMinute > 0d
            ? $"About {weeklyCredits / (creditsPerMinute * 60d):N1} hours with your full weekly allowance."
            : "No allowance is used when there are no viewers.";
        var limitText = $"Weekly allowance: {weeklyCredits:N0} credits";
        var resultWrapWidth = Math.Max(Ui(60f), ImGui.GetContentRegionAvail().X - Ui(36f));
        var resultHeight = Math.Max(Ui(116f),
            ImGui.CalcTextSize(rateText, false, resultWrapWidth).Y +
            ImGui.CalcTextSize(hoursText, false, resultWrapWidth).Y +
            ImGui.CalcTextSize(limitText, false, resultWrapWidth).Y + Ui(46f) + ImGui.GetStyle().ItemSpacing.Y * 3f);

        ImGui.Dummy(UiVec(0f, 10f));
        using (ImRaii.PushColor(ImGuiCol.ChildBg, FrameBg).Push(ImGuiCol.Border, BorderSubtle))
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(10f)).Push(ImGuiStyleVar.WindowPadding, UiVec(16f, 14f)))
        using (var result = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##allowanceCalculatorResult", new Vector2(0f, resultHeight), true, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)))
        {
            if (result)
            {
                using var wrap = ImRaii.TextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
                ImGui.TextColored(Accent, rateText);
                ImGui.Spacing();
                ImGui.TextWrapped(hoursText);
                ImGui.Spacing();
                ImGui.TextColored(MutedText, limitText);
            }
        }
        ImGui.Dummy(UiVec(0f, 6f));
        DrawSocialHelperWrapped("Based on the selected membership and full weekly allowance, before credits already used.");
        if (allowanceCalculatorMethod == 5)
        {
            ImGui.Spacing();
            DrawSocialHelperWrapped("PC stream estimates use typical quality settings. Your streaming app settings affect actual usage.");
        }
        ImGui.Spacing();
        const string savingsText = "Patreon hosts can save up to 50% when viewers with Patreon memberships join their room. This extra saving is not included in the estimate.";
        var noteHeight = ImGui.CalcTextSize(savingsText, false, resultWrapWidth).Y + ImGui.GetTextLineHeight() + Ui(38f) + ImGui.GetStyle().ItemSpacing.Y;
        using (ImRaii.PushColor(ImGuiCol.ChildBg, FrameBg).Push(ImGuiCol.Border, Accent))
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, Ui(10f)).Push(ImGuiStyleVar.WindowPadding, UiVec(16f, 14f)))
        using (var note = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##allowanceCalculatorSavings", new Vector2(0f, noteHeight), true, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)))
        {
            if (note)
            {
                ImGui.TextColored(AccentHover, "Patreon viewer savings");
                ImGui.Spacing();
                DrawSocialHelperWrapped(savingsText);
            }
        }
    }

    private static bool DrawCalculatorChoice(string id, string[] labels, ref int selected)
    {
        var changed = false;
        if (UiFonts.Window(ImGui.GetFontSize(), ImGui.BeginCombo(id, labels[selected])))
        {
            for (var index = 0; index < labels.Length; index++)
            {
                if (ImGui.Selectable(labels[index], selected == index))
                {
                    selected = index;
                    changed = true;
                }
            }
            ImGui.EndCombo();
        }
        return changed;
    }
}
