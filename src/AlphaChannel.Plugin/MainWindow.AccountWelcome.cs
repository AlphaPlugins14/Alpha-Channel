using System.Numerics;
using AlphaChannel.Contracts;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private void DrawAccountWelcomeSplash(Vector2 windowPosition, Vector2 windowSize)
    {
        var draw = ImGui.GetWindowDrawList();
        var stacked = windowSize.X < Ui(720);
        var canvas = stacked ? new Vector2(620, 880) : new Vector2(1100, 760);
        var scale = MathF.Min((windowSize.X - 24) / canvas.X, (windowSize.Y - 24) / canvas.Y);
        var origin = windowPosition + (windowSize - canvas * scale) / 2;
        Vector2 P(float x, float y) => origin + new Vector2(x, y) * scale;
        uint C(uint rgb, float alpha = 1) => ImGui.GetColorU32(new Vector4(
            ((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, alpha * firstLaunchFadeAlpha));

        void Text(string text, float x, float y, float size, uint rgb, bool centered = true)
        {
            using var font = UiFonts.PushRelative(size * scale / ImGui.GetFontSize());
            var position = P(x, y);
            if (centered) position.X -= ImGui.CalcTextSize(text).X / 2;
            UiFonts.DrawText(draw, ImGui.GetFontSize(), position, C(rgb), text);
        }

        bool Button(string label, string id, float x, float y, float width, bool primary)
        {
            using var font = UiFonts.PushRelative(18 * scale / ImGui.GetFontSize());
            using var style = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 9 * scale)
                .Push(ImGuiStyleVar.FrameBorderSize, primary ? 0 : scale);
            using var colours = ImRaii.PushColor(ImGuiCol.Button, primary ? Hex(0x347FF1) : Hex(0x0D192C))
                .Push(ImGuiCol.ButtonHovered, primary ? Hex(0x5095FF) : Hex(0x172D4A))
                .Push(ImGuiCol.ButtonActive, Hex(0x2865C4))
                .Push(ImGuiCol.Border, Hex(0x408DDA)).Push(ImGuiCol.Text, Vector4.One);
            ImGui.SetCursorScreenPos(P(x, y));
            return ImGui.Button(label + "##" + id, new Vector2(width, 48) * scale);
        }

        bool BackLink(float left)
        {
            ImGui.SetCursorScreenPos(P(left, 414));
            var clicked = ImGui.InvisibleButton("##welcomeBack", new Vector2(70, 26) * scale);
            var hovered = ImGui.IsItemHovered();
            if (hovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            var ink = hovered ? 0xDCEAFFu : 0x93A9C8u;
            UiFonts.DrawCenteredIcon(draw, 13 * scale, P(left + 7, 427), C(ink), FontAwesomeIcon.ArrowLeft.ToIconString());
            Text("Back", left + 21, 417, 16, ink, centered: false);
            return clicked;
        }

        // Only this top strip drags the underlying window; it does not overlap
        // any account action or the close control.
        DrawFirstLaunchDragBlocker("##accountWelcomeDrag", windowPosition,
            windowPosition + new Vector2(Math.Max(1, windowSize.X - Ui(55)), Ui(42)), windowPosition);
        // DrawSplashCloseButton supplies the existing topmost close control.
        draw.AddRect(windowPosition + Vector2.One, windowPosition + windowSize - Vector2.One,
            C(0x285991, .7f), Ui(10));

        var cx = canvas.X / 2;
        Text("ALPHA CHANNEL", cx, 32, 21, 0xBECBE4);
        DrawAccountWelcomeMediaTiles(draw, P(cx, 207), scale, firstLaunchFadeAlpha, stacked);
        var choosingCreation = firstLaunchStep == FirstLaunchStep.CreateAccountChoice;
        var choosingUsername = firstLaunchStep == FirstLaunchStep.Username;
        var choosingTopics = firstLaunchStep == FirstLaunchStep.Topics;
        Text(choosingTopics ? "What are you into?" : choosingUsername ? "What should we call you?" : choosingCreation ? "Make yourself at home." : "Watch. Play. Together.", cx, 325, stacked ? 34 : 42, 0xF5F7FF);
        Text(choosingTopics ? "Pick at least 3 topics to personalise your video recommendations." :
            choosingUsername ? "Choose a username for your profile, friends and Watch Parties." :
            choosingCreation ? "Choose how to create your account." : "Videos, music and games with friends in Eorzea.",
            cx, 381, stacked ? 17 : 20, 0xA9B6CE);

        if (choosingTopics)
        {
            var width = MathF.Min(780, canvas.X - 48);
            using (UiDisabled.Scope(guestRegistrationBusy))
            {
                if (!firstLaunchUsingAccount && CurrentSession is null && BackLink(cx - width / 2))
                {
                    firstLaunchStep = FirstLaunchStep.Username;
                    return;
                }
                DrawWelcomeTopicPicker(P(cx - width / 2, 448), new Vector2(width, 312) * scale, scale);
            }
            return;
        }

        if (choosingUsername)
        {
            var panelWidth = MathF.Min(560, canvas.X - 48);
            var left = cx - panelWidth / 2;
            using (UiDisabled.Scope(firstLaunchNameSubmitting))
                if (!firstLaunchUsingAccount && BackLink(left))
                {
                    // Keep the entered name when returning to the signup choice.
                    firstLaunchNameError = null;
                    firstLaunchStep = FirstLaunchStep.CreateAccountChoice;
                    return;
                }

            draw.AddRectFilled(P(left, 448), P(left + panelWidth, 744), C(0x0D1729), 12 * scale);
            draw.AddRect(P(left, 448), P(left + panelWidth, 744), C(0x2B527C), 12 * scale);
            Text("Username", left + 24, 466, 19, 0xE2E9F6, centered: false);
            var enterPressed = false;
            ImGui.SetCursorScreenPos(P(left + 24, 497));
            using (UiFonts.PushRelative(18 * scale / ImGui.GetFontSize()))
            using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(14 * scale, MathF.Max(1, (46 * scale - ImGui.GetFontSize()) / 2)))
                .Push(ImGuiStyleVar.FrameRounding, 8 * scale).Push(ImGuiStyleVar.FrameBorderSize, scale))
            using (ImRaii.PushColor(ImGuiCol.FrameBg, Hex(0x0A1323))
                .Push(ImGuiCol.Border, firstLaunchNameError is { Length: > 0 } ? Danger : Hex(0x5476A0)))
            using (UiDisabled.Scope(firstLaunchNameSubmitting))
            {
                ImGui.SetNextItemWidth((panelWidth - 48) * scale);
                var previousInput = namePromptInput;
                if (ImGui.InputTextWithHint("##firstLaunchUsernameInput", "Choose your username", ref namePromptInput,
                        DisplayNameRules.MaxLength, ImGuiInputTextFlags.EnterReturnsTrue)) enterPressed = true;
                if (!string.Equals(previousInput, namePromptInput, StringComparison.Ordinal)) firstLaunchNameError = null;
            }

            var helper = firstLaunchNameError is { Length: > 0 } error ? error :
                $"{DisplayNameRules.MinLength}-{DisplayNameRules.MaxLength} characters. Letters, numbers, single spaces, _ or -.";
            float helperHeight;
            using (UiFonts.PushRelative(14 * scale / ImGui.GetFontSize()))
            {
                var wrapWidth = (panelWidth - 48) * scale;
                helperHeight = ImGui.CalcTextSize(helper, false, wrapWidth).Y / scale;
                ImGui.SetCursorScreenPos(P(left + 24, 555));
                using (ImRaii.TextWrapPos(ImGui.GetCursorPosX() + wrapWidth))
                    ImGui.TextColored(firstLaunchNameError is { Length: > 0 } ? Danger : MutedText, helper);
            }
            var buttonY = MathF.Max(599, 555 + helperHeight + 16);
            var validName = DisplayNameRules.IsValid(namePromptInput);
            using (UiDisabled.Scope(firstLaunchNameSubmitting || !validName))
            {
                var clicked = Button(firstLaunchNameSubmitting ? "Saving..." : "Continue", "welcomeUsernameContinue",
                    left + 24, buttonY, panelWidth - 48, true);
                if (!firstLaunchNameSubmitting && validName && (clicked || enterPressed)) SubmitFirstLaunchUsername();
            }
            if (!firstLaunchUsingAccount)
            {
                Text("Your login is saved on this device. Verify with XIVAuth later", left + 24, buttonY + 69, 14, 0x9DACC5, centered: false);
                Text("to recover access elsewhere.", left + 24, buttonY + 89, 14, 0x9DACC5, centered: false);
            }
            return;
        }

        if (choosingCreation)
        {
            var panelWidth = MathF.Min(560, canvas.X - 48);
            var left = cx - panelWidth / 2;
            // Quiet navigation sits outside the signup panel, away from the
            // primary actions. Keep the arrow in the existing icon font.
            if (BackLink(left))
            {
                firstLaunchStep = FirstLaunchStep.SignInChoice;
                return;
            }

            draw.AddRectFilled(P(left, 448), P(left + panelWidth, 744), C(0x0D1729), 12 * scale);
            draw.AddRect(P(left, 448), P(left + panelWidth, 744), C(0x2B527C), 12 * scale);
            Text("No email or password needed.", cx, 466, 19, 0xD3DBE9);
            if (Button("Choose a username", "welcomeUsername", left + 24, 497, panelWidth - 48, true))
            {
                firstLaunchUsingAccount = false;
                firstLaunchNameError = null;
                firstLaunchStep = FirstLaunchStep.Username;
            }
            Text("You can verify with XIVAuth later.", cx, 557, 15, 0x9DACC5);

            const string verifiedDivider = "Or create a verified account";
            using (UiFonts.PushRelative(18 * scale / ImGui.GetFontSize(), emphasis: true))
            {
                var textWidth = ImGui.CalcTextSize(verifiedDivider).X;
                var middle = P(cx, 606);
                var gap = 14 * scale;
                draw.AddLine(P(left + 24, 606), new Vector2(middle.X - textWidth / 2 - gap, middle.Y), C(0x506684, .65f), scale);
                draw.AddLine(new Vector2(middle.X + textWidth / 2 + gap, middle.Y), P(left + panelWidth - 24, 606), C(0x506684, .65f), scale);
                UiFonts.DrawText(draw, ImGui.GetFontSize(), P(cx, 596) - new Vector2(textWidth / 2, 0), C(0xD3DFF2), verifiedDivider);
            }
            if (Button("Create with XIVAuth", "welcomeVerify", left + 24, 630, panelWidth - 48, false))
            {
                firstLaunchUsingAccount = true;
                StartSignIn(null, fromFirstLaunch: true);
            }
            Text("Unlock additional features and sign back in if you lose your", cx, 693, 15, 0x9DACC5);
            Text("plugin files or change devices.", cx, 713, 15, 0x9DACC5);
            return;
        }

        for (var i = 0; i < 2; i++)
        {
            var width = stacked ? 560 : 480;
            var left = stacked ? 30 : 56 + i * 508;
            var top = stacked ? 432 + i * 214 : 450;
            var height = stacked ? 198 : 250;
            draw.AddRectFilled(P(left, top), P(left + width, top + height), C(0x0D1729), 13 * scale);
            draw.AddRect(P(left, top), P(left + width, top + height), C(i == 0 ? 0x347BC4u : 0x315578u), 13 * scale);
            var cardCenter = left + width / 2;
            if (!stacked)
                UiFonts.DrawIcon(draw, 32 * scale, P(cardCenter - 16, top + 23),
                    C(i == 0 ? 0x5BA6FFu : 0xB088EBu),
                    (i == 0 ? FontAwesomeIcon.UserPlus : FontAwesomeIcon.UserCheck).ToIconString());
            Text(i == 0 ? "New to Alpha Channel?" : "Already have an account?", cardCenter, top + (stacked ? 18 : 70), 23, 0xF0F4FF);
            Text(i == 0 ? "Choose a username and get started." : "Welcome back. Pick up where you left off.",
                cardCenter, top + (stacked ? 53 : 109), 17, 0xACB9D0);
            if (Button(i == 0 ? "Create an account" : "Sign in with XIVAuth", "welcomeAccount" + i,
                    left + 26, top + (stacked ? 93 : 155), width - 52, i == 0))
            {
                if (i == 0) firstLaunchStep = FirstLaunchStep.CreateAccountChoice;
                else { firstLaunchUsingAccount = true; StartSignIn(null, fromFirstLaunch: true); }
            }
            Text(i == 0 ? "No email or password needed." : "For accounts linked to a verified character.",
                cardCenter, top + (stacked ? 158 : 217), 15, 0x94A4C0);
        }
    }

}
