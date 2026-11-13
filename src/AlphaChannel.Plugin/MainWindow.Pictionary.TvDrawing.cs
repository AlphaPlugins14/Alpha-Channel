using System.Numerics;
using AlphaChannel.Plugin.Video;

namespace AlphaChannel.Plugin;

// Direct-on-TV canvas drawing shared by Pictionary and standalone Whiteboard sessions.
// This file owns the TV toolbar and pointer path; the plugin-window canvas uses the same
// stroke batching and relay protocol in MainWindow.Whiteboard.cs.
internal sealed partial class MainWindow
{
    // One-line rollback switch for the direct-TV drawing trial.
    private const bool PictionaryTvDirectDrawingEnabled = true;
    private const int PictionaryTvDrawingTarget = 100;
    private const int PictionaryTvColourTargetStart = 200;
    private const int PictionaryTvBrushTargetStart = 300;
    private const int PictionaryTvUndoTarget = 400;
    private const int PictionaryTvClearTarget = 401;
    private const float PictionaryTvDrawingTop = 48f;
    private const float PictionaryTvDrawingBottom = 600f;
    private const float PictionaryTvToolbarTop = 610f;
    private const float PictionaryTvToolbarBottom = 704f;

    private bool pictionaryTvDrawingActive;

    internal bool PictionaryTvDrawingActive => pictionaryTvDrawingActive;

    private bool LocalCanDrawPictionaryOnTv =>
        PictionaryTvDirectDrawingEnabled &&
        (PictionaryActive
            ? pictionaryPhase == PictionaryPhase.Drawing && LocalIsPictionaryDrawer
            : PictionaryTeamsActive
                ? teamsPhase == TeamsPhase.Drawing && TeamsLocalDrawerColour is 0 or 1
            : IsStandaloneCanvasActivityActive &&
              (IsLocalActivityHost ||
               stream.Mode == StreamMode.Viewing && whiteboardMode == WhiteboardMode.Team && DrawingTransportReady));

    private bool TryGetPictionaryTvDrawingTarget(Vector2 uv, out int target)
    {
        target = -1;
        if (!LocalCanDrawPictionaryOnTv)
            return false;

        var x = uv.X * WhiteboardRenderer.Width;
        var y = uv.Y * WhiteboardRenderer.Height;
        if (y >= (PictionaryTeamsActive ? 60f : PictionaryTvDrawingTop) && y <= (PictionaryTeamsActive ? 530f : PictionaryTvDrawingBottom) &&
            (!PictionaryTeamsActive || (TeamsLocalDrawerColour == 0
                ? x < WhiteboardRenderer.Width / 2f : x > WhiteboardRenderer.Width / 2f)))
        {
            target = PictionaryTvDrawingTarget;
            return true;
        }

        if (y < PictionaryTvToolbarTop || y > PictionaryTvToolbarBottom)
            return false;

        for (var index = 0; index < WhiteboardColours.Length; index++)
        {
            if (PictionaryTvToolbarLayout.Colour(index).Contains(x, y))
            {
                target = PictionaryTvColourTargetStart + index;
                return true;
            }
        }
        for (var index = 0; index < WhiteboardBrushSizes.Length; index++)
        {
            if (PictionaryTvToolbarLayout.Brush(index).Contains(x, y))
            {
                target = PictionaryTvBrushTargetStart + index;
                return true;
            }
        }

        if (!PictionaryTeamsActive && x >= 1144f && x <= 1262f)
        {
            if (y >= 614f && y <= 642f)
                target = PictionaryTvUndoTarget;
            else if (y >= 648f && y <= 676f)
                target = PictionaryTvClearTarget;
            return target >= 0;
        }
        return false;
    }

    private bool SubmitPictionaryTvDrawingTarget(int target)
    {
        if (!LocalCanDrawPictionaryOnTv)
            return false;

        if (target >= PictionaryTvColourTargetStart &&
            target < PictionaryTvColourTargetStart + WhiteboardColours.Length)
        {
            whiteboardColourIndex = target - PictionaryTvColourTargetStart;
            whiteboardTvDirty = true;
            return true;
        }
        if (target >= PictionaryTvBrushTargetStart &&
            target < PictionaryTvBrushTargetStart + WhiteboardBrushSizes.Length)
        {
            whiteboardBrushWidth = WhiteboardBrushSizes[target - PictionaryTvBrushTargetStart];
            whiteboardTvDirty = true;
            return true;
        }
        if (target == PictionaryTvUndoTarget)
        {
            if (PictionaryTeamsActive) return false;
            var stroke = whiteboardStrokes.LastOrDefault();
            if (stroke is not null && whiteboardSessionId is not null)
                SendPictionaryTvCanvasAction($"U|{whiteboardSessionId}|{stroke.Id}");
            return true;
        }
        if (target == PictionaryTvClearTarget)
        {
            if (PictionaryTeamsActive) return false;
            if (whiteboardSessionId is not null)
                SendPictionaryTvCanvasAction($"C|{whiteboardSessionId}");
            return true;
        }
        return target == PictionaryTvDrawingTarget;
    }

    private void SendPictionaryTvCanvasAction(string command)
    {
        if (stream.Mode == StreamMode.Viewing &&
            (whiteboardMode == WhiteboardMode.Team || (PictionaryActive && LocalIsPictionaryDrawer)))
        {
            if (DrawingRelayGame is { } game && GameRelay(game) is not null) SendDrawingContribution("Q|" + command);
            else { ApplyWhiteboardCommand(command); SendDrawingContribution("Q|" + command); }
            return;
        }
        SendAndApplyWhiteboardCommand(command);
    }

    internal void UpdatePictionaryTvDrawingPointer(Vector2? uv, bool mouseDown, bool mousePressed)
    {
        if (!LocalCanDrawPictionaryOnTv)
        {
            CancelPictionaryTvDrawing();
            return;
        }

        if (!mouseDown)
        {
            if (pictionaryTvDrawingActive)
                FinishWhiteboardStroke();
            pictionaryTvDrawingActive = false;
            return;
        }

        if (uv is not { } point ||
            !TryGetPictionaryTvDrawingTarget(point, out var target) ||
            target != PictionaryTvDrawingTarget)
        {
            if (pictionaryTvDrawingActive) FinishWhiteboardStroke();
            pictionaryTvDrawingActive = false;
            return;
        }

        if (PictionaryTeamsActive)
            point = new Vector2(Math.Clamp(point.X, TeamsLocalDrawerColour == 0 ? 0.02f : 0.52f,
                TeamsLocalDrawerColour == 0 ? 0.48f : 0.98f), Math.Clamp(point.Y, 0.085f, 530f / 720f));

        if (mousePressed && !pictionaryTvDrawingActive)
        {
            pictionaryTvDrawingActive = true;
            BeginWhiteboardStroke(point);
            return;
        }

        if (!pictionaryTvDrawingActive || whiteboardDrawingStrokeId is null)
            return;

        ContinueWhiteboardStroke(point);
    }

    private void CancelPictionaryTvDrawing()
    {
        if (pictionaryTvDrawingActive)
            FinishWhiteboardStroke();
        pictionaryTvDrawingActive = false;
    }

    private void DrawPictionaryTvDrawingToolbar(byte[] pixels, int width, int height)
    {
        if (!LocalCanDrawPictionaryOnTv)
            return;

        FillPictionaryPixels(pixels, width, height, 0, 604, width, 108, 0x11172A);
        DrawPictionaryPixelText(pixels, width, height, 18, 620, "COLOUR", 2, 0xAAB2CA);
        for (var index = 0; index < WhiteboardColours.Length; index++)
        {
            var swatch = PictionaryTvToolbarLayout.Colour(index);
            var x = swatch.X;
            var selected = index == whiteboardColourIndex;
            if (selected)
                FillPictionaryPixels(pixels, width, height, x - 4, 626, 40, 40, 0xB77DFF);
            FillPictionaryPixels(pixels, width, height, swatch.X, swatch.Y, swatch.Width, swatch.Height, WhiteboardColours[index].Rgb);
            if (pictionaryTvHoveredTarget == PictionaryTvColourTargetStart + index)
                DrawPictionaryPixelRect(pixels, width, height, x - 2, 628, 36, 36, 0xF5F7FC, 2);
        }

        DrawPictionaryPixelText(pixels, width, height, 782, 620, "BRUSH", 2, 0xAAB2CA);
        for (var index = 0; index < WhiteboardBrushSizes.Length; index++)
        {
            var brush = PictionaryTvToolbarLayout.Brush(index);
            var x = brush.X;
            var selected = MathF.Abs(whiteboardBrushWidth - WhiteboardBrushSizes[index]) < 0.1f;
            FillPictionaryPixels(pixels, width, height, brush.X, brush.Y, brush.Width, brush.Height,
                selected ? 0x38235Fu : 0x252D48u);
            if (pictionaryTvHoveredTarget == PictionaryTvBrushTargetStart + index)
                DrawPictionaryPixelRect(pixels, width, height, brush.X, brush.Y, brush.Width, brush.Height, 0xF5F7FC, 2);
            var radius = 2 + index * 3;
            FillPictionaryPixels(pixels, width, height, x + 22 - radius, 645 - radius,
                radius * 2 + 1, radius * 2 + 1, selected ? WhiteboardColours[whiteboardColourIndex].Rgb : 0xAAB2CA);
        }

        if (!PictionaryTeamsActive)
        {
        FillQuizTvPanel(pixels, width, height, 1144, 614, 118, 28,
            0x252D48u, pictionaryTvHoveredTarget == PictionaryTvUndoTarget ? 0x512E8Fu : 0x38235Fu);
        DrawPictionaryPixelTextCentered(pixels, width, height, 1203, 621,
            "UNDO", 2, 0xF5F7FC);
        FillQuizTvPanel(pixels, width, height, 1144, 648, 118, 28,
            0x252D48u, pictionaryTvHoveredTarget == PictionaryTvClearTarget ? 0x512E8Fu : 0x38235Fu);
        DrawPictionaryPixelTextCentered(pixels, width, height, 1203, 655,
            "CLEAR", 2, 0xF5F7FC);
        }
        DrawPictionaryPixelTextCentered(pixels, width, height, width / 2, 682,
            PictionaryTeamsActive ? "DRAW ON YOUR TEAM'S HALF ONLY" :
            "DRAW DIRECTLY ON THE TV OR IN THE ALPHA CHANNEL ACTIVITIES PAGE", 2, 0xAAB2CA);
    }

    private static void DrawPictionaryPixelRect(byte[] pixels, int width, int height,
        int x, int y, int rectWidth, int rectHeight, uint colour, int thickness)
    {
        FillPictionaryPixels(pixels, width, height, x, y, rectWidth, thickness, colour);
        FillPictionaryPixels(pixels, width, height, x, y + rectHeight - thickness, rectWidth, thickness, colour);
        FillPictionaryPixels(pixels, width, height, x, y, thickness, rectHeight, colour);
        FillPictionaryPixels(pixels, width, height, x + rectWidth - thickness, y, thickness, rectHeight, colour);
    }
}
