using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private DateTime signInLinkCopiedUntil, signInCodeCopiedUntil;
    private string? signInBrowserOpenError;

    private void DrawXivAuthBrowserPrompt()
    {
        var draw = ImGui.GetWindowDrawList();
        using (ImRaii.PushColor(ImGuiCol.Text, MutedText))
            ImGui.TextWrapped("Continue in your browser to verify your character.");
        ImGui.Dummy(UiVec(0, 10));
        if (GameLayoutButton("Open XIVAuth", FontAwesomeIcon.ExternalLinkAlt,
                ImGui.GetContentRegionAvail().X, primary: true, height: 46))
        {
            try
            {
                if (!Uri.TryCreate(signInVerificationUri, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                    throw new InvalidOperationException("Invalid sign-in link.");
                Dalamud.Utility.Util.OpenLink(uri.AbsoluteUri);
                signInBrowserOpenError = null;
            }
            catch (Exception)
            {
                signInBrowserOpenError = "Couldn't open your browser. Copy the sign-in link to open it manually.";
            }
        }

        var linkText = DateTime.UtcNow < signInLinkCopiedUntil ? "Link copied" : "Copy sign-in link";
        var linkSize = ImGui.CalcTextSize(linkText);
        var linkLeft = ImGui.GetCursorPosX() + MathF.Max(0, (ImGui.GetContentRegionAvail().X - linkSize.X - Ui(12)) / 2);
        ImGui.SetCursorPosX(linkLeft);
        var linkOrigin = ImGui.GetCursorScreenPos();
        if (ImGui.InvisibleButton("##copyXivAuthSignInLink", new Vector2(linkSize.X + Ui(12), Ui(26))))
        {
            ImGui.SetClipboardText(signInVerificationUri);
            signInLinkCopiedUntil = DateTime.UtcNow.AddSeconds(2);
        }
        var linkHovered = ImGui.IsItemHovered();
        if (linkHovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        UiFonts.DrawText(draw, ImGui.GetFontSize(), linkOrigin + UiVec(6, 4),
            ImGui.GetColorU32(linkHovered ? AccentHover : Accent), linkText);
        // Reset X after the centered text action so following rows remain aligned.
        ImGui.SetCursorPosX(ImGui.GetStyle().WindowPadding.X);
        if (signInBrowserOpenError is not null) ImGui.TextWrapped(signInBrowserOpenError);
        ImGui.Dummy(UiVec(0, 6));

        using (ImRaii.PushColor(ImGuiCol.Text, MutedText)) ImGui.TextUnformatted("Need to enter a code?");
        var row = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var height = Ui(56);
        draw.AddRectFilled(row, row + new Vector2(width, height), ImGui.GetColorU32(FrameBg), Ui(8));
        draw.AddRect(row, row + new Vector2(width, height), ImGui.GetColorU32(BorderSubtle), Ui(8));
        draw.PushClipRect(row + UiVec(12, 0), row + new Vector2(MathF.Max(Ui(12), width - Ui(54)), height), true);
        using (UiFonts.PushRelative(1.50f, emphasis: true))
        {
            var code = signInUserCode.Length == 8 ? signInUserCode.Insert(4, " ") : signInUserCode;
            UiFonts.DrawText(draw, ImGui.GetFontSize(), row + new Vector2(Ui(16), (height - ImGui.GetTextLineHeight()) / 2),
                ImGui.GetColorU32(Vector4.One), code);
        }
        draw.PopClipRect();

        var copySize = UiVec(36, 36);
        ImGui.SetCursorScreenPos(row + new Vector2(MathF.Max(0, width - Ui(46)), Ui(10)));
        var copyOrigin = ImGui.GetCursorScreenPos();
        if (ImGui.InvisibleButton("##copyXivAuthUserCode", copySize))
        {
            // Display grouping is cosmetic; the clipboard always receives the
            // exact device code returned by XIVAuth.
            ImGui.SetClipboardText(signInUserCode);
            signInCodeCopiedUntil = DateTime.UtcNow.AddSeconds(2);
        }
        var copyHovered = ImGui.IsItemHovered();
        var copied = DateTime.UtcNow < signInCodeCopiedUntil;
        if (copyHovered)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            draw.AddRectFilled(copyOrigin, copyOrigin + copySize, ImGui.GetColorU32(CardBgHover), Ui(6));
            UiFonts.SetTooltip(copied ? "Code copied" : "Copy code");
        }
        UiFonts.DrawCenteredIcon(draw, Ui(19), copyOrigin + copySize / 2,
            ImGui.GetColorU32(copied ? Good : copyHovered ? AccentHover : MutedText),
            (copied ? FontAwesomeIcon.Check : FontAwesomeIcon.Clipboard).ToIconString());
        ImGui.SetCursorScreenPos(row);
        ImGui.Dummy(new Vector2(width, height));
        ImGui.Dummy(UiVec(0, 6));

        var statusOrigin = ImGui.GetCursorScreenPos();
        var spinnerCenter = statusOrigin + UiVec(11, 11);
        var phase = (float)ImGui.GetTime() * 3;
        for (var point = 0; point <= 24; point++)
        {
            var angle = phase + point / 24f * MathF.PI * 1.5f;
            draw.PathLineTo(spinnerCenter + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * Ui(9));
        }
        draw.PathStroke(ImGui.GetColorU32(Accent), ImDrawFlags.None, Ui(2));
        ImGui.Dummy(UiVec(22, 22));
        ImGui.SameLine(0, Ui(10));
        using (ImRaii.PushColor(ImGuiCol.Text, MutedText))
            ImGui.TextUnformatted("Waiting for browser confirmation...");
    }
}
