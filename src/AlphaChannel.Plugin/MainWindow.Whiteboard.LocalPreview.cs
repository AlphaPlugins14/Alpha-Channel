namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    internal void RefreshArcadeCanvasFrame() => RenderWhiteboardTvIfNeeded();

    // Local ink is a visual preview only. The host still validates every batch,
    // and its snapshot remains the authority for all other participants.
    private readonly Dictionary<string, WhiteboardStroke> whiteboardLocalInk = new(StringComparer.Ordinal);
    private string? whiteboardPreviewEpoch;
    private string WhiteboardPreviewEpoch => $"{whiteboardSessionId}|{DrawingRelayGame}|{DrawingRelayRound}";
    private IEnumerable<WhiteboardStroke> VisibleWhiteboardStrokes => whiteboardStrokes
        .Where(stroke => !whiteboardLocalInk.ContainsKey(stroke.Id)).Concat(whiteboardLocalInk.Values);

    // Every drawing surface samples and batches through the same path. Local
    // preview points are captured immediately, independently of host snapshots.
    private void BeginWhiteboardStroke(Vector2 point)
    {
        whiteboardDrawingStrokeId = Guid.NewGuid().ToString("N");
        whiteboardDrawingSequence = 0;
        whiteboardPendingPoints.Clear();
        whiteboardPendingPoints.Add(point);
        PreviewWhiteboardPoint(point);
        whiteboardLastBatchUtc = DateTime.UtcNow;
    }

    private void ContinueWhiteboardStroke(Vector2 point)
    {
        var last = whiteboardPendingPoints.Count > 0 ? whiteboardPendingPoints[^1] : point;
        if (whiteboardPendingPoints.Count < DrawingBatchSize && Vector2.DistanceSquared(last, point) >= 0.000006f)
        {
            whiteboardPendingPoints.Add(point);
            PreviewWhiteboardPoint(point);
        }
        if (whiteboardPendingPoints.Count >= DrawingBatchSize ||
            DateTime.UtcNow - whiteboardLastBatchUtc >= DrawingBatchInterval)
            FlushWhiteboardStrokeBatch(false);
    }

    private void PreviewWhiteboardPoint(Vector2 point)
    {
        if (stream.Mode != StreamMode.Viewing || whiteboardDrawingStrokeId is not { } id) return;
        if (whiteboardPreviewEpoch != WhiteboardPreviewEpoch)
        {
            whiteboardLocalInk.Clear();
            whiteboardPreviewEpoch = WhiteboardPreviewEpoch;
        }
        if (!whiteboardLocalInk.TryGetValue(id, out var stroke))
        {
            if (whiteboardLocalInk.Count >= 128) return;
            stroke = new() { Id = id, ColourIndex = whiteboardColourIndex, Width = whiteboardBrushWidth };
            whiteboardLocalInk[id] = stroke;
        }
        if (whiteboardLocalInk.Values.Sum(ink => ink.Points.Count) < 16384 && (stroke.Points.Count == 0 || stroke.Points[^1] != point))
            stroke.Points.Add(point);
        whiteboardTvDirty = true;
    }

    private void ConfirmWhiteboardPreviews()
    {
        if (whiteboardPreviewEpoch != WhiteboardPreviewEpoch ||
            (PictionaryActive || PictionaryTeamsActive) && !LocalCanDrawPictionaryOnTv)
        {
            whiteboardLocalInk.Clear();
            return;
        }
        foreach (var (id, preview) in whiteboardLocalInk.ToArray())
            if (id != whiteboardDrawingStrokeId && whiteboardStrokeById.TryGetValue(id, out var confirmed) &&
                confirmed.Complete && confirmed.LastSequence >= preview.LastSequence)
                whiteboardLocalInk.Remove(id);
    }

    private void RejectWhiteboardPreview(string command)
    {
        var parts = command.Split('|');
        if (parts.Length != 12 || parts[0] != "Q" || parts[3] != "Q" || parts[4] != "P") return;
        whiteboardLocalInk.Remove(parts[6]);
        if (whiteboardDrawingStrokeId == parts[6])
        {
            whiteboardDrawingStrokeId = null;
            whiteboardPendingPoints.Clear();
            pictionaryTvDrawingActive = false;
        }
        whiteboardTvDirty = true;
    }
}
