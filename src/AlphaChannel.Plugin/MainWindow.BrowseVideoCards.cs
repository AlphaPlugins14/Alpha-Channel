using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private float BrowseVideoCardHeight => Ui(266f);
    private float BrowseVideoThumbnailHeight => Ui(122f);
    private int BrowseVideoGridColumns(float width) => width >= Ui(930) ? 5 : width >= Ui(720) ? 4 :
        width >= Ui(520) ? 3 : width >= Ui(340) ? 2 : 1;

    private void DrawBrowseVideoCardPanel(Vector2 origin, Vector2 size) =>
        DrawArcadeGradientPanel(ImGui.GetWindowDrawList(), origin, size,
            HubSurface(0x122038), HubSurface(0x080F1D), HubTint(0x24456B), 10);

    private bool DrawBrowseVideoFooter(Vector2 origin, float width, float height,
        string label, FontAwesomeIcon icon, Action play, Func<bool> options)
    {
        using var footerId = ImRaii.PushId("browseVideoFooter");
        var saved = ImGui.GetCursorScreenPos();
        var clicked = false;
        ImGui.SetCursorScreenPos(origin + new Vector2(Ui(8), height - Ui(42)));
        if (GameLayoutButton(label, icon, width - Ui(63), true, height: 34))
        { play(); clicked = true; }
        ImGui.SameLine(0, Ui(7));
        clicked |= options();
        ImGui.SetCursorScreenPos(saved);
        return clicked;
    }

    // Shares YouTube's frame, dimensions and footer, with provider-specific actions.
    private void DrawSourceBrowseVideoCard(UnifiedSearchResult result, float width)
    {
        var origin = ImGui.GetCursorScreenPos();
        var size = new Vector2(width, BrowseVideoCardHeight);
        if (!ImGui.IsRectVisible(origin, origin + size)) { ImGui.Dummy(size); return; }
        using var id = ImRaii.PushId($"browse_{result.Provider}_{result.ExactKey}");
        DrawBrowseVideoCardPanel(origin, size);
        var draw = ImGui.GetWindowDrawList();
        var imageOrigin = origin + UiVec(6, 6);
        var imageSize = new Vector2(width - Ui(12), BrowseVideoThumbnailHeight);
        ImGui.SetCursorScreenPos(imageOrigin);
        if (ImGui.InvisibleButton("##thumbnail", imageSize)) PlayUnifiedResult(result);
        var thumbnail = thumbnails.Get(result.ThumbnailUrl);
        if (thumbnail is not null)
            draw.AddImageRounded(thumbnail.Handle, imageOrigin, imageOrigin + imageSize,
                Vector2.Zero, Vector2.One, uint.MaxValue, 9f);
        else
        {
            draw.AddRectFilled(imageOrigin, imageOrigin + imageSize, ImGui.GetColorU32(CardBg), 9f);
            if (result.ThumbnailUrl is null) DrawArchiveMissingThumbnail(imageOrigin, imageSize, draw);
        }
        draw.AddRectFilled(imageOrigin + new Vector2(0, imageSize.Y - Ui(22)), imageOrigin + imageSize,
            ImGui.GetColorU32(new Vector4(0, 0, 0, .18f)));
        // Archive's release year is not an upload date: keep its meaning explicit.
        var date = result.Archive?.Year is { } year ? year.ToString() :
            result.Video?.UploadDate is { } upload ? FormatRelativeUploadDate(upload) : null;
        using (UiFonts.PushRelative(.90f))
        {
            if (result.Duration is { } duration)
            {
                var text = FormatTime((float)duration.TotalSeconds);
                var textSize = ImGui.CalcTextSize(text);
                var min = imageOrigin + imageSize - new Vector2(textSize.X + Ui(10), Ui(20));
                var max = imageOrigin + imageSize - UiVec(4, 4);
                draw.AddRectFilled(min, max, ImGui.GetColorU32(new Vector4(0, 0, 0, .82f)), 4f);
                draw.AddText(min + new Vector2(Ui(3), (max.Y - min.Y - textSize.Y) * .5f), uint.MaxValue, text);
            }
            if (date is not null)
            {
                var min = imageOrigin + UiVec(7, 6);
                draw.AddRectFilled(min, min + ImGui.CalcTextSize(date) + UiVec(10, 5),
                    ImGui.GetColorU32(new Vector4(0, 0, 0, .75f)), 5f);
                draw.AddText(min + UiVec(5, 2), uint.MaxValue, date);
            }
        }
        if (queue.Current is { } playing &&
            (result.Video?.Url == playing.Url || result.Archive?.Episodes.Any(e => e.VideoUrl == playing.Url) == true))
        {
            var min = imageOrigin + UiVec(7, date is null ? 6 : 30);
            const string text = "NOW PLAYING";
            draw.AddRectFilled(min, min + ImGui.CalcTextSize(text) + UiVec(10, 5), ImGui.GetColorU32(Accent), 5f);
            draw.AddText(min + UiVec(5, 2), uint.MaxValue, text);
        }
        // The same two-line title and fixed metadata rows used by YouTube.
        var textOrigin = imageOrigin + new Vector2(2, imageSize.Y + Ui(10));
        var textWidth = MathF.Max(1, imageSize.X - 4);
        float lineHeight;
        bool truncated;
        using (UiFonts.PushRelative(1f, emphasis: true))
        {
            lineHeight = ImGui.GetTextLineHeight();
            DrawWrappedLines(draw, textOrigin, textWidth, lineHeight, 2,
                ImGui.GetColorU32(Hex(0xE2E7EF)), result.Title, out truncated);
            ImGui.SetCursorScreenPos(textOrigin);
            if (ImGui.InvisibleButton("##title", new Vector2(textWidth, lineHeight * 2))) PlayUnifiedResult(result);
            if (truncated && ImGui.IsItemHovered()) UiFonts.SetTooltip(result.Title);
        }
        var creator = string.IsNullOrWhiteSpace(result.Creator) ? UnifiedProviderLabels[(int)result.Provider + 1] : result.Creator;
        var creatorOrigin = textOrigin + new Vector2(0, lineHeight * 2 + Ui(5));
        var glyph = FontAwesomeIcon.User.ToIconString();
        float iconWidth;
        using (UiFonts.PushIcon())
        {
            iconWidth = ImGui.CalcTextSize(glyph).X;
            draw.AddText(creatorOrigin, ImGui.GetColorU32(AccentHover with { W = .78f }), glyph);
        }
        var creatorText = TruncateToWidth(creator, MathF.Max(1, textWidth - iconWidth - Ui(5)));
        draw.AddText(creatorOrigin + new Vector2(iconWidth + Ui(5), 0),
            ImGui.GetColorU32(MutedText with { W = .82f }), creatorText);
        var views = result.Archive is { } archive ? $"{FormatArchiveDownloads(Math.Max(0, archive.Downloads))} views" :
            result.Video?.ViewCount is { } count ? count == 1 ? "1 view" : FormatViewCount(count) : null;
        if (views is not null)
            draw.AddText(creatorOrigin + new Vector2(0, lineHeight + Ui(3)),
                ImGui.GetColorU32(MutedText with { W = .58f }), views);
        var collection = result.Archive is { Episodes.Count: > 1 };
        DrawBrowseVideoFooter(imageOrigin, imageSize.X, size.Y - Ui(12),
            collection ? $"{result.Archive!.Episodes.Count} episodes" : "Play",
            collection ? FontAwesomeIcon.List : FontAwesomeIcon.Play,
            () => PlayUnifiedResult(result), () =>
            {
                if (result.Archive is { } item)
                    DrawInternetArchiveQueueOptions(item, collection ? null : item.Episodes.FirstOrDefault(), 34);
                else if (result.Video is { } video) DrawUnifiedVideoOptions(video);
                return false;
            });
        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(size);
    }
}
