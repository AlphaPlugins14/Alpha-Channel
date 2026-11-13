using AlphaChannel.Plugin.Core;
using AlphaChannel.Plugin.Auth;
using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private readonly ChatLinkPreviewCache alphaChatLinkPreviews = new();
    private readonly ChatLinkedImageCache chatLinkedImages = new();
    private string? chatLinkedImageViewer;
    private readonly ThumbnailCache alphaChatLinkImages = new(new HttpClient(new ChatPreviewHttpHandler())
        { Timeout = TimeSpan.FromSeconds(12) });

    private void DrawAlphaChatLinks(string text, float availableWidth, bool imagesOnly = false)
    {
        var links = ChatLinkPreview.Extract(text);
        for (var i = 0; i < links.Length; i++)
        {
            using var id = ImRaii.PushId("linkPreview" + i);
            var link = links[i];
            // Old event-page URLs must not create previews or browser actions.
            if(Uri.TryCreate(link.Url,UriKind.Absolute,out var oldEventUrl)&&
                oldEventUrl.Host.Equals(new Uri(CharacterDirectoryClient.WorkerUrl).Host,StringComparison.OrdinalIgnoreCase)&&
                (oldEventUrl.AbsolutePath=="/events"||oldEventUrl.AbsolutePath.StartsWith("/events/",StringComparison.OrdinalIgnoreCase)))continue;
            if (ChatLinkPreview.IsDirectImage(link.Url))
            {
                DrawChatLinkedImage(link.Url, availableWidth);
                continue;
            }
            if (imagesOnly) continue;
            var width = MathF.Min(availableWidth, Ui(620f));
            var height = Ui(170f);
            var start = ImGui.GetCursorScreenPos();
            // Offscreen messages reserve identical space without starting network work.
            if (!ImGui.IsRectVisible(start, start + new Vector2(width, height)))
            {
                ImGui.Dummy(new Vector2(width, height));
                continue;
            }
            var result = alphaChatLinkPreviews.Get(link, (url, token) => searchResolver.GetVideoEntryAsync(url, token,
                section: YouTube.YouTubeFetchSection.LinkPreview));
            var isVideo = link.YouTubeId is not null;
            var page = result.Page;
            var videoEntry = result.Video;
            var title = videoEntry?.Title ?? page?.Title ?? (result.Loading ? "Loading preview…" : isVideo ? "YouTube video" : new Uri(link.Url).IdnHost);
            var site = isVideo ? "YouTube" : page?.Site ?? new Uri(link.Url).IdnHost;
            using var colors = ImRaii.PushColor(ImGuiCol.ChildBg, FrameBg).Push(ImGuiCol.Border, BorderSubtle);
            using var padding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, UiVec(12f, 12f))
                .Push(ImGuiStyleVar.ChildRounding, Ui(10f));
            using var card = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##alphaChatLink", new Vector2(width, height), true,
                ImGuiWindowFlags.AlwaysUseWindowPadding | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse));
            if (!card) continue;
            var origin = ImGui.GetCursorScreenPos();
            var inner = ImGui.GetContentRegionAvail().X;
            var imageWidth = MathF.Min(Ui(188f), inner * .36f);
            var imageSize = new Vector2(imageWidth, imageWidth * 9f / 16f);
            var imagePos = origin + new Vector2(0f, (height - Ui(24f) - imageSize.Y) * .5f);
            var draw = ImGui.GetWindowDrawList();
            // Generated YouTube thumbnail URLs also work while full metadata loads.
            var imageUrl = isVideo ? $"https://i.ytimg.com/vi/{link.YouTubeId}/hqdefault.jpg" : page?.ImageUrl;
            var image = alphaChatLinkImages.Get(imageUrl);
            if (image is not null)
            {
                var (uv0, uv1) = CoverUvs(image.Width, image.Height, imageSize.X, imageSize.Y);
                draw.AddImageRounded(image.Handle, imagePos, imagePos + imageSize, uv0, uv1, uint.MaxValue, Ui(8f));
            }
            else
            {
                draw.AddRectFilled(imagePos, imagePos + imageSize, ImGui.GetColorU32(CardBg), Ui(8f));
                var icon = (isVideo ? FontAwesomeIcon.Film : FontAwesomeIcon.Globe).ToIconString();
                Vector2 iconSize;
                using (UiFonts.PushIcon()) iconSize = ImGui.CalcTextSize(icon);
                UiFonts.DrawIcon(draw, ImGui.GetFontSize(), imagePos + (imageSize - iconSize) * .5f, ImGui.GetColorU32(MutedText), icon);
            }
            if (isVideo)
            {
                var centre = imagePos + imageSize * .5f;
                draw.AddCircleFilled(centre, Ui(19f), ImGui.GetColorU32(new Vector4(.12f, .38f, .8f, .92f)));
                var play = FontAwesomeIcon.Play.ToIconString();
                Vector2 playSize;
                using (UiFonts.PushIcon()) playSize = ImGui.CalcTextSize(play);
                UiFonts.DrawIcon(draw, ImGui.GetFontSize(), centre - playSize * .5f + UiVec(1f, 0f), uint.MaxValue, play);
            }
            ImGui.SetCursorScreenPos(imagePos);
            var clicked = ImGui.InvisibleButton("##previewImage", imageSize);
            if (ImGui.IsItemHovered()) { ImGui.SetMouseCursor(ImGuiMouseCursor.Hand); UiFonts.SetTooltip(isVideo ? "Play video" : "Open webpage"); }

            var body = origin + new Vector2(imageWidth + Ui(14f), 0f);
            var textWidth = MathF.Max(Ui(30f), inner - imageWidth - Ui(14f));
            ImGui.SetCursorScreenPos(body);
            ImGui.TextColored(AccentHover, FitPartyTextToWidth(site, textWidth));
            ImGui.SetCursorScreenPos(body + UiVec(0f, 24f));
            draw.PushClipRect(body + UiVec(0f, 24f), body + new Vector2(textWidth, Ui(64f)), true);
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + textWidth);
            ImGui.TextUnformatted(title);
            ImGui.PopTextWrapPos();
            draw.PopClipRect();
            if (ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(body + UiVec(0f, 24f), body + new Vector2(textWidth, Ui(64f)))) UiFonts.SetTooltip(title);
            var detail = isVideo ? videoEntry?.ChannelName ?? "Video details unavailable" : page?.Description ?? "";
            if (isVideo && result.Loading) detail = "Fetching video details…";
            ImGui.SetCursorScreenPos(body + UiVec(0f, 68f));
            using (currentPage is HomePage.WatchAlong or HomePage.Messages or HomePage.AlphaChat ? UiFonts.PushRelative(.90f) : UiFonts.Empty)
                ImGui.TextColored(MutedText, FitPartyTextToWidth(detail, textWidth));
            if (detail.Length > 0 && ImGui.IsItemHovered()) UiFonts.SetTooltip(detail);
            var stats = videoEntry is null ? "" : string.Join(" · ", new[]
            {
                videoEntry.Duration is { } duration ? FormatTime((float)duration.TotalSeconds) : null,
                videoEntry.ViewCount is { } views ? FormatViewCount(views) : null
            }.Where(s => s is not null));
            ImGui.SetCursorScreenPos(body + UiVec(0f, 90f));
            using (currentPage is HomePage.Messages or HomePage.AlphaChat ? UiFonts.PushRelative(.90f) : UiFonts.Empty)
            ImGui.TextColored(MutedText, FitPartyTextToWidth(isVideo ? stats : link.Url, textWidth));
            if (!isVideo && ImGui.IsItemHovered()) UiFonts.SetTooltip(link.Url);
            ImGui.SetCursorScreenPos(body + UiVec(0f, 114f));
            if (isVideo)
            {
                if (AlphaChatButton("playLinkVideo", "Play", FontAwesomeIcon.Play, MathF.Min(Ui(86f), textWidth), Ui(30f), primary: true)) clicked = true;
                if (textWidth >= Ui(125f))
                {
                    ImGui.SameLine(0f, Ui(6f));
                    if (AlphaChatButton("openLinkVideo", "", FontAwesomeIcon.ExternalLinkAlt, Ui(30f), Ui(30f)))
                        Dalamud.Utility.Util.OpenLink(link.Url);
                    if (ImGui.IsItemHovered()) UiFonts.SetTooltip("Open on YouTube");
                }
            }
            else if (AlphaChatButton("openLinkPage", "Open link", FontAwesomeIcon.ExternalLinkAlt,
                         MathF.Min(Ui(126f), textWidth), Ui(30f))) clicked = true;
            if (clicked)
            {
                if (isVideo) HandlePlayNow(new VideoQueueEntry(link.Url, videoEntry?.Title ?? "YouTube video",
                    videoEntry?.ChannelName ?? "YouTube", videoEntry?.Duration, imageUrl));
                else Dalamud.Utility.Util.OpenLink(link.Url);
            }
        }
    }

    private void DrawChatLinkedImage(string url, float availableWidth)
    {
        var width = MathF.Max(1f, MathF.Min(availableWidth, Ui(420f)));
        var height = Ui(240f);
        var start = ImGui.GetCursorScreenPos();
        // Keep layout stable while loading, including when scrolling past media.
        var total = new Vector2(width, height + Ui(26f));
        if (!ImGui.IsRectVisible(start, start + total)) { ImGui.Dummy(total); return; }
        var result = chatLinkedImages.Get(url);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(start, start + new Vector2(width, height), ImGui.GetColorU32(FrameBg), Ui(8f));
        draw.AddRect(start, start + new Vector2(width, height), ImGui.GetColorU32(BorderSubtle), Ui(8f));
        if (result.Texture is { } image)
        {
            var factor = MathF.Min((width - Ui(8f)) / image.Width, (height - Ui(8f)) / image.Height);
            var size = new Vector2(image.Width, image.Height) * factor;
            var position = start + (new Vector2(width, height) - size) * .5f;
            draw.AddImage(image.Handle, position, position + size);
        }
        else
        {
            var label = result.State == ImagePreviewState.Failed ? "Image unavailable · click to retry" : "Loading linked image…";
            var size = ImGui.CalcTextSize(label);
            draw.AddText(start + (new Vector2(width, height) - size) * .5f, ImGui.GetColorU32(MutedText), label);
        }
        if (ImGui.InvisibleButton("##linkedImage", new Vector2(width, height)))
        {
            if (result.Texture is not null) chatLinkedImageViewer = url;
            else if (result.State == ImagePreviewState.Failed) chatLinkedImages.Invalidate(url);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            UiFonts.SetTooltip(result.Error ?? "Click to enlarge · image hosted externally");
        }
        ImGui.SetCursorScreenPos(start + new Vector2(0f, height + Ui(4f)));
        using (UiFonts.PushRelative(.90f))
        {
            var label = FitPartyTextToWidth("Linked image · " + new Uri(url).IdnHost, width);
            ImGui.TextColored(AccentHover, label);
            if (ImGui.IsItemClicked()) Dalamud.Utility.Util.OpenLink(url);
            if (ImGui.IsItemHovered()) { ImGui.SetMouseCursor(ImGuiMouseCursor.Hand); UiFonts.SetTooltip(url + "\nHosted externally; not an encrypted image upload."); }
        }
        ImGui.SetCursorScreenPos(start);
        ImGui.Dummy(total);
    }

    private void DrawChatLinkedImageViewer()
    {
        if (chatLinkedImageViewer is not { } url) return;
        var result = chatLinkedImages.Get(url);
        if (result.Texture is not { } image) { chatLinkedImageViewer = null; return; }
        var root = ImGuiP.GetCurrentWindow().RootWindow;
        var position = root.Pos;
        var size = root.Size;
        ImGui.SetNextWindowPos(position); ImGui.SetNextWindowSize(size); ImGui.SetNextWindowBgAlpha(0);
        if (!BeginWindowOverlay("##chatLinkedImageViewer", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDocking |
            ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoScrollbar)) { ImGui.End(); return; }
        try
        {
            ImGui.GetWindowDrawList().AddRectFilled(position, position + size, ImGui.GetColorU32(new Vector4(0, 0, 0, .85f)));
            if (ImGui.IsKeyPressed(ImGuiKey.Escape)) chatLinkedImageViewer = null;
            ImGui.SetCursorScreenPos(position + UiVec(22, 18));
            if (ImGui.Button("Close")) chatLinkedImageViewer = null;
            ImGui.SameLine();
            if (ImGui.Button("Open original link")) Dalamud.Utility.Util.OpenLink(url);
            var available = Vector2.Max(Vector2.One, size - UiVec(48, 100));
            var factor = MathF.Min(available.X / image.Width, available.Y / image.Height);
            var imageSize = new Vector2(image.Width, image.Height) * factor;
            ImGui.SetCursorScreenPos(position + new Vector2((size.X - imageSize.X) * .5f, Ui(70) + (available.Y - imageSize.Y) * .5f));
            ImGui.Image(image.Handle, imageSize);
        }
        finally { ImGui.End(); }
    }
}
