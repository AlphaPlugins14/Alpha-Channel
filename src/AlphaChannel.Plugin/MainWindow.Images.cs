using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private enum ImageDisplayMode
    {
        StillImage,
        Slideshow
    }

    private const int MaxSlideshowImages =
        5;

    private ImageDisplayMode imageDisplayMode =
        ImageDisplayMode.Slideshow;

    private readonly string[] slideshowImageUrls =
        new string[MaxSlideshowImages];

    private string imageUrlInput =
        string.Empty;

    private int selectedSlideshowImage;

    private int slideshowSeconds =
        5;

    private int slideshowTransition;

    private bool slideshowLoop =
        true;

    private string? imageMediaError;

    private readonly string[] slideshowTransitions =
    [
        "Instant",
    "Fade"
    ];

    private enum ImageGalleryContext
    {
        None,
        StillImage,
        Slideshow
    }

    private const int MaximumSavedImages =
        100;

    private Guid? pendingImageGalleryDeleteId;

    private ImageGalleryContext imageGalleryContext =
        ImageGalleryContext.None;

    private readonly List<Guid> selectedGalleryImageIds =
        new();

    private string imageGallerySearch =
        string.Empty;

    private string imageGalleryUrlInput =
        string.Empty;

    private string? imageGalleryError;

    private bool imageGalleryAddUrlOpen;

    private bool imageHostingGuideOpen;

    private int imageGallerySort;

    private readonly string[] imageGallerySortOptions =
    [
        "Newest first",
    "Oldest first",
    "Name"
    ];

    private void OpenImageGallery(
    ImageGalleryContext context)
    {
        imageGalleryContext =
            context;

        selectedGalleryImageIds.Clear();

        imageGalleryError =
            null;

        imageGalleryAddUrlOpen =
            false;
    }

    private void CloseImageGallery()
    {
        imageGalleryContext =
            ImageGalleryContext.None;

        selectedGalleryImageIds.Clear();

        imageGalleryError =
            null;

        imageGalleryAddUrlOpen =
            false;

        imageGalleryUrlInput =
            string.Empty;

        pendingImageGalleryDeleteId =
    null;
    }

    private bool IsImageSavedToGallery(
        string? url)
    {
        if (string.IsNullOrWhiteSpace(
                url))
        {
            return false;
        }

        return Plugin.Cfg.SavedImages.Any(
            savedImage =>
                string.Equals(
                    savedImage.Url,
                    url.Trim(),
                    StringComparison.OrdinalIgnoreCase));
    }

    private bool SaveImageToGallery(
        string? url)
    {
        imageGalleryError =
            null;

        if (string.IsNullOrWhiteSpace(
                url))
        {
            imageGalleryError =
                "Enter an image URL first.";

            return false;
        }

        var normalizedUrl =
            url.Trim();

        if (IsImageSavedToGallery(
                normalizedUrl))
        {
            imageGalleryError =
                "That image is already in your gallery.";

            return false;
        }

        if (Plugin.Cfg.SavedImages.Count >=
            MaximumSavedImages)
        {
            imageGalleryError =
                $"Your gallery can contain up to {MaximumSavedImages} images.";

            return false;
        }

        Plugin.Cfg.SavedImages.Add(
            new SavedImageRecord
            {
                Name =
                    CreateSavedImageName(
                        normalizedUrl),

                Url =
                    normalizedUrl,

                SavedAtUtc =
                    DateTime.UtcNow
            });

        Plugin.Cfg.Save();

        return true;
    }

    private static string CreateSavedImageName(
        string url)
    {
        if (!Uri.TryCreate(
                url,
                UriKind.Absolute,
                out var uri))
        {
            return "Saved image";
        }

        var finalSegment =
            uri.AbsolutePath.Split(
                    '/',
                    StringSplitOptions.RemoveEmptyEntries |
                    StringSplitOptions.TrimEntries)
                .LastOrDefault();

        if (string.IsNullOrWhiteSpace(
                finalSegment))
        {
            return uri.Host;
        }

        finalSegment =
            Uri.UnescapeDataString(
                finalSegment);

        var extensionPosition =
            finalSegment.LastIndexOf(
                '.');

        if (extensionPosition > 0)
        {
            finalSegment =
                finalSegment[..extensionPosition];
        }

        finalSegment =
            finalSegment
                .Replace(
                    '-',
                    ' ')
                .Replace(
                    '_',
                    ' ')
                .Trim();

        if (string.IsNullOrWhiteSpace(
                finalSegment))
        {
            return uri.Host;
        }

        const int maximumNameLength =
            42;

        return finalSegment.Length >
               maximumNameLength
            ? $"{finalSegment[..(maximumNameLength - 1)]}…"
            : finalSegment;
    }

    private void RemoveImageFromGallery(
        Guid imageId)
    {
        Plugin.Cfg.SavedImages.RemoveAll(
            savedImage =>
                savedImage.Id ==
                imageId);

        selectedGalleryImageIds.Remove(
            imageId);

        pendingImageGalleryDeleteId =
            null;

        Plugin.Cfg.Save();
    }

    private int GetRemainingSlideshowSpaces()
    {
        return slideshowImageUrls.Count(
            string.IsNullOrWhiteSpace);
    }

    private void ToggleGalleryImageSelection(
        Guid imageId)
    {
        if (selectedGalleryImageIds.Remove(
                imageId))
        {
            return;
        }

        var maximumSelection =
            imageGalleryContext ==
            ImageGalleryContext.Slideshow
                ? GetRemainingSlideshowSpaces()
                : 1;

        if (maximumSelection <= 0)
        {
            imageGalleryError =
                "Your slideshow already contains 5 images.";

            return;
        }

        if (imageGalleryContext ==
            ImageGalleryContext.StillImage)
        {
            selectedGalleryImageIds.Clear();
        }
        else if (selectedGalleryImageIds.Count >=
                 maximumSelection)
        {
            imageGalleryError =
                maximumSelection == 1
                    ? "Only one slideshow space remains."
                    : $"Only {maximumSelection} slideshow spaces remain.";

            return;
        }

        selectedGalleryImageIds.Add(
            imageId);

        imageGalleryError =
            null;
    }

    private void UseSelectedGalleryImages()
    {
        var selectedImages =
            selectedGalleryImageIds
                .Select(
                    selectedId =>
                        Plugin.Cfg.SavedImages.FirstOrDefault(
                            savedImage =>
                                savedImage.Id ==
                                selectedId))
                .Where(
                    savedImage =>
                        savedImage is not null)
                .Cast<SavedImageRecord>()
                .ToList();

        if (selectedImages.Count == 0)
        {
            imageGalleryError =
                "Select an image first.";

            return;
        }

        //
        // Validate the complete selection before changing the still image or
        // slideshow. This makes gallery restoration atomic: if one selected
        // image is unavailable, none of the selection is added.
        //

        foreach (var savedImage in
                 selectedImages)
        {
            var preview =
                imagePreviews.Get(
                    savedImage.Url);

            if (preview.State ==
                ImagePreviewState.Loading)
            {
                imageGalleryError =
                    $"“{savedImage.Name}” is still loading. Try again shortly.";

                return;
            }

            if (preview.State !=
                ImagePreviewState.Ready)
            {
                imageGalleryError =
                    $"“{savedImage.Name}” is temporarily unavailable. " +
                    "Retry the image before using it.";

                return;
            }
        }

        imageGalleryError =
            null;

        if (imageGalleryContext ==
            ImageGalleryContext.StillImage)
        {
            imageUrlInput =
                selectedImages[0].Url;

            CloseImageGallery();

            return;
        }

        var firstAddedIndex =
            -1;

        foreach (var savedImage in
                 selectedImages)
        {
            var emptyIndex =
                Array.FindIndex(
                    slideshowImageUrls,
                    string.IsNullOrWhiteSpace);

            if (emptyIndex < 0)
            {
                break;
            }

            slideshowImageUrls[emptyIndex] =
                savedImage.Url;

            if (firstAddedIndex < 0)
            {
                firstAddedIndex =
                    emptyIndex;
            }
        }

        if (firstAddedIndex >= 0)
        {
            selectedSlideshowImage =
                firstAddedIndex;
        }

        CloseImageGallery();
    }

    private void DrawImageHostingGuideOverlay()
    {
        if (!imageHostingGuideOpen)
        {
            return;
        }

        var parentPosition =
            ImGui.GetWindowPos();

        var parentSize =
            ImGui.GetWindowSize();

        var panelWidth =
            MathF.Min(
                Ui(690f),
                parentSize.X -
                Ui(40f));

        var panelHeight =
            MathF.Min(
                Ui(650f),
                parentSize.Y -
                Ui(40f));

        var panelPosition =
            parentPosition +
            new Vector2(
                (parentSize.X -
                 panelWidth) *
                0.5f,

                (parentSize.Y -
                 panelHeight) *
                0.5f);

        ImGui.SetNextWindowPos(
            parentPosition,
            ImGuiCond.Always);

        ImGui.SetNextWindowSize(
            parentSize,
            ImGuiCond.Always);

        ImGui.SetNextWindowBgAlpha(
            0f);

        const ImGuiWindowFlags overlayFlags =
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoDocking |
            ImGuiWindowFlags.NoBackground;

        if (!BeginWindowOverlay(
                "##imageHostingGuideOverlay",
                overlayFlags))
        {
            ImGui.End();

            return;
        }

        var overlayDrawList =
            ImGui.GetWindowDrawList();

        overlayDrawList.AddRectFilled(
            parentPosition,
            parentPosition +
            parentSize,
            ImGui.GetColorU32(
                new Vector4(
                    0f,
                    0f,
                    0f,
                    0.58f)));

        ImGui.SetCursorScreenPos(
            panelPosition);

        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.ChildRounding,
                Ui(14f)))
        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.ChildBorderSize,
                Ui(1f)))
        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.WindowPadding,
                UiVec(
                    22f,
                    20f)))
        using (
            ImRaii.PushColor(
                ImGuiCol.ChildBg,
                CardBg))
        using (
            ImRaii.PushColor(
                ImGuiCol.Border,
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.82f)))
        using (
            var panel =
                UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(
                    "##imageHostingGuidePanel",
                    new Vector2(
                        panelWidth,
                        panelHeight),
                    true,
                    ImGuiWindowFlags.NoScrollbar |
                    ImGuiWindowFlags.NoScrollWithMouse)))
        {
            if (panel)
            {
                DrawImageHostingGuideContents(
                    panelPosition,
                    panelWidth,
                    panelHeight);
            }
        }

        ImGui.End();
    }

    private void DrawImageHostingGuideContents(Vector2 panelPosition, float panelWidth, float panelHeight)
    {
        var origin = ImGui.GetCursorScreenPos();
        var closeSize = UiVec(32f, 32f);
        ImGui.SetCursorScreenPos(new Vector2(panelPosition.X + panelWidth - Ui(54f), panelPosition.Y + Ui(16f)));
        if (ImGui.Button("×##closeImageHostingGuide", closeSize)) imageHostingGuideOpen = false;

        DrawUiIcon(FontAwesomeIcon.BookOpen, Ui(26f), origin, Accent);
        ImGui.SetCursorScreenPos(origin + UiVec(48f, 0f));
        using (UiFonts.PushRelative(1.25f)) ImGui.TextColored(Hex(0xE2E7EF), "Image Hosting Guide");
        ImGui.SetCursorScreenPos(origin + UiVec(48f, 29f));
        using (UiFonts.PushRelative(.82f)) ImGui.TextColored(MutedText, "Where to upload images and how to use their direct links.");
        ImGui.SetCursorScreenPos(origin + UiVec(0f, 70f));
        ImGui.Separator();

        // Keep all help text in a scrollable region above a fixed footer.
        var footerTop = panelPosition.Y + panelHeight - Ui(60f);
        var bodyHeight = MathF.Max(Ui(40f), footerTop - ImGui.GetCursorScreenPos().Y - Ui(12f));
        using (var body = UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child("##imageHostingGuideBody",
                   new Vector2(-1f, bodyHeight), false, ImGuiWindowFlags.AlwaysVerticalScrollbar)))
        {
            if (body)
            {
                using (UiFonts.PushRelative(1.05f)) ImGui.TextColored(Hex(0xE2E7EF), "Supported image hosts");
                using (UiFonts.PushRelative(.82f)) ImGui.TextColored(MutedText, "Open one of these websites to upload your image.");
                ImGui.Dummy(UiVec(0f, 10f));
                var gap = Ui(8f);
                var buttonWidth = (ImGui.GetContentRegionAvail().X - gap) / 2f;
                DrawDjActionButton("##openMyImgs", FontAwesomeIcon.ExternalLinkAlt, "MyImgs", new Vector2(buttonWidth, Ui(38f)), false,
                    () => Dalamud.Utility.Util.OpenLink("https://myimgs.org/"));
                ImGui.SameLine(0f, gap);
                DrawDjActionButton("##openImgPile", FontAwesomeIcon.ExternalLinkAlt, "ImgPile", new Vector2(buttonWidth, Ui(38f)), false,
                    () => Dalamud.Utility.Util.OpenLink("https://imgpile.com/"));
                ImGui.Dummy(UiVec(0f, 8f));
                DrawDjActionButton("##openImgur", FontAwesomeIcon.ExternalLinkAlt, "Imgur", new Vector2(buttonWidth, Ui(38f)), false,
                    () => Dalamud.Utility.Util.OpenLink("https://imgur.com/upload"));
                if (ImGui.IsItemHovered()) UiFonts.SetTooltip("Imgur is blocked in the UK, so only use it if all watch party members are outside of the UK.");
                ImGui.SameLine(0f, gap);
                DrawDjActionButton("##openPostimages", FontAwesomeIcon.ExternalLinkAlt, "Postimages", new Vector2(buttonWidth, Ui(38f)), false,
                    () => Dalamud.Utility.Util.OpenLink("https://postimages.org/"));
                ImGui.Dummy(UiVec(0f, 18f));
                ImGui.TextColored(Accent, "Use the direct image URL");
                ImGui.Dummy(UiVec(0f, 5f));
                using (UiFonts.PushRelative(.90f))
                using (ImRaii.PushColor(ImGuiCol.Text, Hex(0xE2E7EF)))
                {
                    ImGui.TextWrapped("After uploading, copy the direct link to the image itself. Direct image links usually end in .jpg, .jpeg, .png, .webp or .gif.");
                    ImGui.Dummy(UiVec(0f, 5f));
                    using (ImRaii.PushColor(ImGuiCol.Text, MutedText))
                        ImGui.TextWrapped("Links to gallery pages, albums and image-viewing pages will not work.");
                    ImGui.Dummy(UiVec(0f, 13f));
                    using (ImRaii.PushColor(ImGuiCol.Text, new Vector4(1f, .72f, .30f, 1f)))
                        ImGui.TextWrapped("Images from any other website will be rejected.");
                    ImGui.Dummy(UiVec(0f, 8f));
                    ImGui.TextWrapped("If you have trouble submitting an image, or would like another image-hosting website to be supported, reach out to the Alpha Channel team on Discord.");
                }
                ImGui.Dummy(UiVec(0f, 12f));
            }
        }
        ImGui.SetCursorScreenPos(new Vector2(panelPosition.X + panelWidth - Ui(142f), footerTop));
        DrawDjActionButton("##doneImageHostingGuide", FontAwesomeIcon.Times, "Close", UiVec(120f, 40f), false,
            () => imageHostingGuideOpen = false, true);
    }

    private void DrawImageGalleryOverlay()
    {
        if (imageGalleryContext ==
            ImageGalleryContext.None)
        {
            return;
        }

        var parentPosition =
            ImGui.GetWindowPos();

        var parentSize =
            ImGui.GetWindowSize();

        var panelWidth =
            MathF.Min(
                Ui(800f),
                parentSize.X -
                Ui(40f));

        var galleryRowCount =
            Math.Clamp(
                (Plugin.Cfg.SavedImages.Count + 2) /
                3,
                1,
                3);

        var desiredPanelHeight =
            Ui(
                290f +
                galleryRowCount *
                198f +
(imageGalleryAddUrlOpen
    ? 125f
    : 0f));

        var panelHeight =
            MathF.Min(
                MathF.Min(
                    desiredPanelHeight,
                    Ui(680f)),
                parentSize.Y -
                Ui(40f));

        var panelPosition =
            parentPosition +
            new Vector2(
                (parentSize.X -
                 panelWidth) *
                0.5f,

                (parentSize.Y -
                 panelHeight) *
                0.5f);

        ImGui.SetNextWindowPos(
            parentPosition,
            ImGuiCond.Always);

        ImGui.SetNextWindowSize(
            parentSize,
            ImGuiCond.Always);

        ImGui.SetNextWindowBgAlpha(
            0f);

        const ImGuiWindowFlags overlayFlags =
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoDocking |
            ImGuiWindowFlags.NoBackground;

        if (!BeginWindowOverlay(
                "##imageGalleryOverlay",
                overlayFlags))
        {
            ImGui.End();

            return;
        }

        var overlayDrawList =
            ImGui.GetWindowDrawList();

        overlayDrawList.AddRectFilled(
            parentPosition,
            parentPosition +
            parentSize,
            ImGui.GetColorU32(
                new Vector4(
                    0f,
                    0f,
                    0f,
                    0.58f)));

        ImGui.SetCursorScreenPos(
            panelPosition);

        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.ChildRounding,
                Ui(14f)))
        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.ChildBorderSize,
                Ui(1f)))
        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.WindowPadding,
                UiVec(
                    22f,
                    20f)))
        using (
            ImRaii.PushColor(
                ImGuiCol.ChildBg,
                CardBg))
        using (
            ImRaii.PushColor(
                ImGuiCol.Border,
                new Vector4(
                    Accent.X,
                    Accent.Y,
                    Accent.Z,
                    0.82f)))
        using (
            var panel =
                UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(
                    "##imageGalleryPanel",
                    new Vector2(
                        panelWidth,
                        panelHeight),
                    true,
                    ImGuiWindowFlags.NoScrollbar |
                    ImGuiWindowFlags.NoScrollWithMouse)))
        {
            if (panel)
            {
                DrawImageGalleryContents(
                    panelPosition,
                    panelWidth,
                    panelHeight);
            }
        }

        ImGui.End();
    }

    private void DrawImageGalleryContents(
        Vector2 panelPosition,
        float panelWidth,
        float panelHeight)
    {
        var contentOrigin =
            ImGui.GetCursorScreenPos();

        var closeSize =
            new Vector2(
                Ui(32f),
                Ui(32f));

        ImGui.SetCursorScreenPos(
            new Vector2(
                panelPosition.X +
                panelWidth -
                Ui(22f) -
                closeSize.X,
                panelPosition.Y +
                Ui(16f)));

        var galleryCloseOrigin =
       ImGui.GetCursorScreenPos();

        var galleryCloseClicked =
            ImGui.InvisibleButton(
                "##closeImageGallery",
                closeSize);

        var galleryCloseHovered =
            ImGui.IsItemHovered();

        var galleryCloseDrawList =
            ImGui.GetWindowDrawList();

        galleryCloseDrawList.AddCircleFilled(
            galleryCloseOrigin +
            closeSize *
            0.5f,
            closeSize.X *
            0.5f,
            ImGui.GetColorU32(
                galleryCloseHovered
                    ? Accent
                    : CardBg));

        using (
            UiFonts.PushIcon())
        {
            var galleryCloseIcon =
                FontAwesomeIcon.Times
                    .ToIconString();

            var galleryCloseIconSize =
                ImGui.CalcTextSize(
                    galleryCloseIcon);

            ImGui.SetCursorScreenPos(
                galleryCloseOrigin +
                new Vector2(
                    (closeSize.X -
                     galleryCloseIconSize.X) *
                    0.5f,

                    (closeSize.Y -
                     galleryCloseIconSize.Y) *
                    0.5f -
                    Ui(1f)));

            ImGui.TextColored(
                Vector4.One,
                galleryCloseIcon);
        }

        if (galleryCloseClicked)
        {
            CloseImageGallery();
        }

        ImGui.SetCursorScreenPos(
            contentOrigin);

        DrawImageGalleryHeading();

        ImGui.Dummy(
            new Vector2(
                0f,
                Ui(14f)));

        DrawImageGalleryToolbar();

        if (imageGalleryAddUrlOpen)
        {
            ImGui.Dummy(
                new Vector2(
                    0f,
                    Ui(10f)));

            DrawImageGalleryUrlEntry();
        }

        ImGui.Dummy(
            new Vector2(
                0f,
                Ui(10f)));

        DrawImageGalleryStatusLine();

        ImGui.Dummy(
            new Vector2(
                0f,
                Ui(8f)));

        var gridOrigin =
    ImGui.GetCursorScreenPos();

        var footerButtonHeight =
            Ui(40f);

        var footerBottomPadding =
            Ui(18f);

        var footerTop =
            panelPosition.Y +
            panelHeight -
            footerBottomPadding -
            footerButtonHeight;

        var separatorY =
            footerTop -
            Ui(14f);

        var gridHeight =
            separatorY -
            gridOrigin.Y -
            Ui(8f);

        DrawImageGalleryGrid(
            MathF.Max(
                Ui(90f),
                gridHeight));

        //
        // Position the separator and footer explicitly so expanding the
        // Add-by-URL form can shrink the grid but never push actions outside
        // the popup.
        //

        ImGui.SetCursorScreenPos(
            new Vector2(
                contentOrigin.X,
                separatorY));

        ImGui.Separator();

        ImGui.SetCursorScreenPos(
            new Vector2(
                contentOrigin.X,
                footerTop));

        DrawImageGalleryFooter();
    }

    private void DrawImageGalleryHeading()
    {
        var origin =
            ImGui.GetCursorScreenPos();

        var icon =
            FontAwesomeIcon.Images
                .ToIconString();

        using (
            UiFonts.PushIcon())
        {
            using var uiFontScale31754=UiFonts.PushRelative(1.55f);

            ImGui.TextColored(
                Accent,
                icon);

            uiFontScale31754.Dispose();
        }

        ImGui.SetCursorScreenPos(
            origin +
            new Vector2(
                Ui(48f),
                0f));

        using var uiFontScale32087=UiFonts.PushRelative(1.25f);

        ImGui.TextColored(
            Hex(0xE2E7EF),
            "Image Gallery");

        uiFontScale32087.Dispose();

        ImGui.SetCursorScreenPos(
            origin +
            new Vector2(
                Ui(48f),
                Ui(29f)));

        using var uiFontScale32406=UiFonts.PushRelative(0.82f);

        ImGui.TextColored(
            MutedText,
            imageGalleryContext ==
            ImageGalleryContext.Slideshow
                ? "Choose images to add to your slideshow."
                : "Save images for quick use in still images and slideshows.");

        uiFontScale32406.Dispose();

        ImGui.SetCursorScreenPos(
            origin +
            new Vector2(
                0f,
                Ui(50f)));
    }

    private void DrawImageGalleryToolbar()
    {
        var availableWidth =
            ImGui.GetContentRegionAvail().X;

        var gap = NativeUi(8f);

        var sortWidth =
            Ui(142f);

        var addWidth =
            Ui(132f);

        var searchWidth =
            availableWidth -
            sortWidth -
            addWidth -
            gap *
            2f;

        ImGui.SetNextItemWidth(
            searchWidth);

        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                Ui(7f)))
        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.FramePadding,
                UiVec(
                    10f,
                    9f)))
        using (
            ImRaii.PushColor(
                ImGuiCol.FrameBg,
                FrameBg))
        {
            ImGui.InputTextWithHint(
                "##imageGallerySearch",
                "Search saved images...",
                ref imageGallerySearch,
                200);
        }

        ImGui.SameLine(
            0f,
            gap);

        ImGui.SetNextItemWidth(
            sortWidth);

        ImGui.Combo(
            "##imageGallerySort",
            ref imageGallerySort,
            imageGallerySortOptions,
            imageGallerySortOptions.Length);

        ImGui.SameLine(
            0f,
            gap);

        DrawDjActionButton(
            "##toggleGalleryUrlEntry",
            FontAwesomeIcon.Plus,
            "Add by URL",
            new Vector2(
                addWidth,
                Ui(36f)),
            false,
            () =>
            {
                imageGalleryAddUrlOpen =
                    !imageGalleryAddUrlOpen;

                imageGalleryError =
                    null;
            },
            true);
    }

    private void DrawImageGalleryUrlEntry()
    {
        using var uiFontScale34874=UiFonts.PushRelative(0.75f);

        ImGui.TextColored(
            MutedText,
            "Add an image URL");

        uiFontScale34874.Dispose();

        ImGui.Dummy(
            new Vector2(
                0f,
                Ui(4f)));

        var validation =
            DrawImageUrlInput(
                "##imageGalleryAddUrl",
                ref imageGalleryUrlInput);

        ImGui.Dummy(
            new Vector2(
                0f,
                Ui(7f)));

        var alreadySaved =
            IsImageSavedToGallery(
                imageGalleryUrlInput);

        DrawDjActionButton(
            "##saveGalleryUrl",
            FontAwesomeIcon.Save,
            alreadySaved
                ? "Already saved"
                : "Save Image",
           new Vector2(
    ImGui.GetContentRegionAvail().X,
    Ui(36f)),
            validation.State !=
                ImagePreviewState.Ready ||
            alreadySaved,
            () =>
            {
                if (SaveImageToGallery(
                        imageGalleryUrlInput))
                {
                    imageGalleryUrlInput =
                        string.Empty;

                    imageGalleryAddUrlOpen =
                        false;
                }
            },
            true);
    }

    private void DrawImageGalleryStatusLine()
    {
        if (!string.IsNullOrWhiteSpace(
                imageGalleryError))
        {
            using var uiFontScale36391=UiFonts.PushRelative(0.75f);

            ImGui.TextColored(
                new Vector4(
                    1f,
                    0.52f,
                    0.52f,
                    1f),
                imageGalleryError);

            uiFontScale36391.Dispose();

            return;
        }

        if (imageGalleryContext ==
            ImageGalleryContext.Slideshow)
        {
            var remainingSpaces =
                GetRemainingSlideshowSpaces();

            using var uiFontScale36918=UiFonts.PushRelative(0.75f);

            ImGui.TextColored(
                MutedText,
                remainingSpaces == 1
                    ? "1 slideshow space remaining"
                    : $"{remainingSpaces} slideshow spaces remaining");

            uiFontScale36918.Dispose();

            return;
        }

        using var uiFontScale37283=UiFonts.PushRelative(0.75f);

        ImGui.TextColored(
            MutedText,
            Plugin.Cfg.SavedImages.Count == 1 ? "1 saved image" : $"{Plugin.Cfg.SavedImages.Count} saved images");

        uiFontScale37283.Dispose();
    }

    private void DrawImageGalleryGrid(
        float height)
    {
        using (
            var grid =
                UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(
                    "##imageGalleryGrid",
                    new Vector2(
                        -1f,
                        height),
                    false,
                    ImGuiWindowFlags.None)))
        {
            if (!grid)
            {
                return;
            }

            var query =
                imageGallerySearch.Trim();

            IEnumerable<SavedImageRecord> images =
                Plugin.Cfg.SavedImages;

            if (!string.IsNullOrWhiteSpace(
                    query))
            {
                images =
                    images.Where(
                        savedImage =>
                            savedImage.Name.Contains(
                                query,
                                StringComparison.OrdinalIgnoreCase) ||
                            savedImage.Url.Contains(
                                query,
                                StringComparison.OrdinalIgnoreCase) ||
                            GetSavedImageHost(
                                    savedImage.Url)
                                .Contains(
                                    query,
                                    StringComparison.OrdinalIgnoreCase));
            }

            images =
                imageGallerySort switch
                {
                    1 =>
                        images.OrderBy(
                            savedImage =>
                                savedImage.SavedAtUtc),

                    2 =>
                        images.OrderBy(
                            savedImage =>
                                savedImage.Name,
                            StringComparer.OrdinalIgnoreCase),

                    _ =>
                        images.OrderByDescending(
                            savedImage =>
                                savedImage.SavedAtUtc)
                };

            var visibleImages =
                images.ToList();

            if (visibleImages.Count == 0)
            {
                DrawEmptyImageGallery();

                return;
            }

            const int columnCount =
                3;

            var gap = NativeUi(10f);

            var availableWidth =
                ImGui.GetContentRegionAvail().X -
                Ui(12f);

            var cardWidth =
                (availableWidth -
                 gap *
                 (columnCount - 1)) /
                columnCount;

            for (var index = 0;
                 index < visibleImages.Count;
                 index++)
            {
                DrawImageGalleryCard(
                    visibleImages[index],
                    cardWidth);

                if (index %
                    columnCount !=
                    columnCount - 1 &&
                    index <
                    visibleImages.Count - 1)
                {
                    ImGui.SameLine(
                        0f,
                        gap);
                }
                else
                {
                    ImGui.Dummy(
                        new Vector2(
                            0f,
                            Ui(8f)));
                }
            }
        }
    }

    private void DrawEmptyImageGallery()
    {
        var available =
            ImGui.GetContentRegionAvail();

        var origin =
            ImGui.GetCursorScreenPos();

        var center =
            origin +
            available *
            0.5f;

        var icon =
            FontAwesomeIcon.Images
                .ToIconString();

        Vector2 iconSize;

        using (
            UiFonts.PushIcon())
        {
            using var uiFontScale41483=UiFonts.PushRelative(1.8f);

            iconSize =
                ImGui.CalcTextSize(
                    icon);

            UiFonts.DrawText(ImGui.GetWindowDrawList(), ImGui.GetFontSize(), new Vector2(
                        center.X -
                        iconSize.X *
                        0.5f,
                        center.Y -
                        Ui(45f)), ImGui.GetColorU32(
                        Accent), icon);

            uiFontScale41483.Dispose();
        }

        const string heading =
            "Your gallery is empty";

        var headingSize =
            ImGui.CalcTextSize(
                heading);

        UiFonts.DrawText(ImGui.GetWindowDrawList(), ImGui.GetFontSize(), new Vector2(
                    center.X -
                    headingSize.X *
                    0.5f,
                    center.Y), ImGui.GetColorU32(
                    Hex(0xE2E7EF)), heading);

        const string description =
            "Save an image URL to use it again later.";

        var descriptionSize =
            ImGui.CalcTextSize(
                description);

        UiFonts.DrawText(ImGui.GetWindowDrawList(), ImGui.GetFontSize(), new Vector2(
                    center.X -
                    descriptionSize.X *
                    0.5f,
                    center.Y +
                    Ui(25f)), ImGui.GetColorU32(
                    MutedText), description);

        ImGui.Dummy(
            available);
    }

    private void DrawUnavailableGalleryThumbnail(
    Vector2 requestedSize)
    {
        var width =
            requestedSize.X <= 0f
                ? ImGui.GetContentRegionAvail().X
                : requestedSize.X;

        var size =
            new Vector2(
                width,
                requestedSize.Y);

        var origin =
            ImGui.GetCursorScreenPos();

        var drawList =
            ImGui.GetWindowDrawList();

        drawList.AddRectFilled(
            origin,
            origin +
            size,
            ImGui.GetColorU32(
                CardBg),
            Ui(7f));

        drawList.AddRect(
            origin,
            origin +
            size,
            ImGui.GetColorU32(
                new Vector4(
                    MutedText.X,
                    MutedText.Y,
                    MutedText.Z,
                    0.18f)),
            Ui(7f));

        var icon =
            FontAwesomeIcon.ExclamationTriangle
                .ToIconString();

        Vector2 iconSize;

        using (
            UiFonts.PushIcon())
        {
            using var uiFontScale44448=UiFonts.PushRelative(1.35f);

            iconSize =
                ImGui.CalcTextSize(
                    icon);

            UiFonts.DrawText(drawList, ImGui.GetFontSize(), new Vector2(
                    origin.X +
                    (size.X -
                     iconSize.X) *
                    0.5f,

                    origin.Y +
                    Ui(28f)), ImGui.GetColorU32(
                    new Vector4(
                        MutedText.X,
                        MutedText.Y,
                        MutedText.Z,
                        0.85f)), icon);

            uiFontScale44448.Dispose();
        }

        const string message =
            "Image unavailable";

        var messageSize =
            ImGui.CalcTextSize(
                message);

        UiFonts.DrawText(drawList, ImGui.GetFontSize(), new Vector2(
                origin.X +
                (size.X -
                 messageSize.X) *
                0.5f,

                origin.Y +
                Ui(72f)), ImGui.GetColorU32(
                MutedText), message);

        ImGui.Dummy(
            size);
    }

    private void DrawImageGalleryCard(
     SavedImageRecord savedImage,
     float width)
    {
        var selectedIndex =
            selectedGalleryImageIds.IndexOf(
                savedImage.Id);

        var selected =
            selectedIndex >= 0;

        var cardOrigin =
            ImGui.GetCursorScreenPos();

        var cardSize =
            new Vector2(
                width,
                Ui(190f));

        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.WindowPadding,
                UiVec(
                    7f,
                    7f)))
        using (
     ImRaii.PushStyle(
         ImGuiStyleVar.ChildRounding,
         Ui(8f)))
        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.ChildBorderSize,
                selected
                    ? Ui(2f)
                    : Ui(1f)))
        using (
            ImRaii.PushColor(
                ImGuiCol.ChildBg,
                CardBg))
        using (
            ImRaii.PushColor(
                ImGuiCol.Border,
                selected
                    ? Accent
                    : new Vector4(
                        MutedText.X,
                        MutedText.Y,
                        MutedText.Z,
                        0.16f)))
        using (
            var card =
                UiFonts.Window(ImGui.GetFontSize(), ImRaii.Child(
                    $"##savedImageCard{savedImage.Id}",
                    cardSize,
                    true,
                    ImGuiWindowFlags.NoScrollbar |
                    ImGuiWindowFlags.NoScrollWithMouse)))
        {
            if (!card)
            {
                return;
            }

            var preview =
                imagePreviews.Get(
                    savedImage.Url);

            if (preview.State ==
                ImagePreviewState.Failed)
            {
                DrawUnavailableGalleryThumbnail(
                    new Vector2(
                        -1f,
                        Ui(118f)));
            }
            else
            {
                DrawImagePreviewSurface(
                    savedImage.Url,
                    new Vector2(
                        -1f,
                        Ui(118f)));
            }

            if (preview.State ==
                    ImagePreviewState.Ready &&
                ImGui.IsItemClicked(
                    ImGuiMouseButton.Left))
            {
                ToggleGalleryImageSelection(
                    savedImage.Id);

                selectedIndex =
                    selectedGalleryImageIds.IndexOf(
                        savedImage.Id);

                selected =
                    selectedIndex >= 0;
            }

            ImGui.Dummy(
                new Vector2(
                    0f,
                    Ui(5f)));

            var reservedActionsWidth = Ui(preview.State == ImagePreviewState.Failed ? 62f : 31f);
            var nameWidth = MathF.Max(1f, ImGui.GetContentRegionAvail().X - reservedActionsWidth);
            ImGui.TextColored(Hex(0xE2E7EF), TruncateToWidth(savedImage.Name, nameWidth));
            if (ImGui.IsItemHovered()) UiFonts.SetTooltip(savedImage.Name);

            using var uiFontScale48814=UiFonts.PushRelative(0.70f);

            ImGui.TextColored(
                MutedText,
                TruncateImageUrl(
                    GetSavedImageHost(
                        savedImage.Url),
                    28));

            uiFontScale48814.Dispose();

            var smallActionSize =
                new Vector2(
                    Ui(26f),
                    Ui(26f));

            var deleteX =
                cardOrigin.X +
                cardSize.X -
                Ui(9f) -
                smallActionSize.X;

            var actionY =
                cardOrigin.Y +
                cardSize.Y -
                Ui(8f) -
                smallActionSize.Y;

            if (preview.State ==
                ImagePreviewState.Failed)
            {
                ImGui.SetCursorScreenPos(
                    new Vector2(
                        deleteX -
                        smallActionSize.X -
                        Ui(5f),
                        actionY));

                DrawDjActionButton(
                    $"##retrySavedImage{savedImage.Id}",
                    FontAwesomeIcon.SyncAlt,
                    string.Empty,
                    smallActionSize,
                    false,
                    () =>
                    {
                        imageGalleryError =
                            null;

                        imagePreviews.Invalidate(
                            savedImage.Url);

                        imagePreviews.Get(
                            savedImage.Url);
                    });

                if (ImGui.IsItemHovered())
                {
                    UiFonts.SetTooltip(
                        "Retry image");
                }
            }

            ImGui.SetCursorScreenPos(
                new Vector2(
                    deleteX,
                    actionY));

            DrawDjActionButton(
                $"##deleteSavedImage{savedImage.Id}",
                FontAwesomeIcon.Trash,
                string.Empty,
                smallActionSize,
                false,
                () =>
                {
                    pendingImageGalleryDeleteId =
                        savedImage.Id;

                    imageGalleryError =
                        null;
                });

            if (ImGui.IsItemHovered())
            {
                UiFonts.SetTooltip(
                    "Remove from gallery");
            }

            //
            // Draw selection decoration from the card child's own draw list.
            // This places it above the child background and thumbnail.
            //

            if (selected)
            {
                var drawList =
                    ImGui.GetWindowDrawList();

            

                var badgeCenter =
                    cardOrigin +
                    UiVec(
                        22f,
                        22f);

                drawList.AddCircleFilled(
                    badgeCenter,
                    Ui(15f),
                    ImGui.GetColorU32(
                        Accent));

                var badgeText =
                    (selectedIndex + 1)
                    .ToString();

                var badgeTextSize =
                    ImGui.CalcTextSize(
                        badgeText);

                UiFonts.DrawText(drawList, ImGui.GetFontSize(), badgeCenter -
                    badgeTextSize *
                    0.5f, ImGui.GetColorU32(
                        Vector4.One), badgeText);
            }
        }
    }

    private void DrawImageGalleryDeleteConfirmation(
     SavedImageRecord savedImage)
    {
        var origin =
            ImGui.GetCursorScreenPos();

        var availableWidth =
            ImGui.GetContentRegionAvail().X;

        var gap = NativeUi(8f);

        var cancelWidth =
            Ui(104f);

        var removeWidth =
            Ui(112f);

        var actionWidth =
            cancelWidth +
            removeWidth +
            gap;

        var questionWidth =
            MathF.Max(
                Ui(120f),
                availableWidth -
                actionWidth -
                Ui(18f));

        var question =
            $"Remove “{savedImage.Name}” from the gallery?";

        ImGui.SetCursorScreenPos(
            new Vector2(
                origin.X,
                origin.Y +
                Ui(11f)));

        using var uiFontScale53483=UiFonts.PushRelative(0.80f);

        ImGui.TextColored(
            Vector4.One,
            TruncateImageUrl(
                question,
                Math.Max(
                    18,
                    (int)(
                        questionWidth /
                        MathF.Max(
                            1f,
                            ImGui.CalcTextSize(
                                "M").X)))));

        uiFontScale53483.Dispose();

        ImGui.SetCursorScreenPos(
            new Vector2(
                origin.X +
                availableWidth -
                actionWidth,
                origin.Y));

        DrawDjActionButton(
            "##cancelGalleryImageRemoval",
            FontAwesomeIcon.Times,
            "Cancel",
            new Vector2(
                cancelWidth,
                Ui(40f)),
            false,
            () =>
                pendingImageGalleryDeleteId =
                    null);

        ImGui.SameLine(
            0f,
            gap);

        DrawDjActionButton(
            "##confirmGalleryImageRemoval",
            FontAwesomeIcon.Trash,
            "Remove",
            new Vector2(
                removeWidth,
                Ui(40f)),
            false,
            () =>
                RemoveImageFromGallery(
                    savedImage.Id),
            false,
            new Vector4(
                1f,
                0.46f,
                0.50f,
                1f));
    }

    private void DrawImageGalleryFooter()
    {
        if (pendingImageGalleryDeleteId is
            { } pendingDeleteId)
        {
            var pendingImage =
                Plugin.Cfg.SavedImages.FirstOrDefault(
                    savedImage =>
                        savedImage.Id ==
                        pendingDeleteId);

            if (pendingImage is null)
            {
                pendingImageGalleryDeleteId =
                    null;
            }
            else
            {
                DrawImageGalleryDeleteConfirmation(
                    pendingImage);

                return;
            }
        }

        var availableWidth =
            ImGui.GetContentRegionAvail().X;

        var gap = NativeUi(8f);

        var cancelWidth =
            Ui(112f);

        var actionWidth =
            Ui(170f);

        ImGui.SetCursorPosX(
            ImGui.GetCursorPosX() +
            MathF.Max(
                0f,
                availableWidth -
                cancelWidth -
                actionWidth -
                gap));

        DrawDjActionButton(
            "##cancelImageGallery",
            FontAwesomeIcon.Times,
            "Cancel",
            new Vector2(
                cancelWidth,
                Ui(40f)),
            false,
            CloseImageGallery);

        ImGui.SameLine(
            0f,
            gap);

        var selectionCount =
            selectedGalleryImageIds.Count;

        var actionLabel =
            imageGalleryContext ==
            ImageGalleryContext.Slideshow
                ? selectionCount == 0
                    ? "Add Selected"
                    : $"Add Selected ({selectionCount})"
                : "Use Image";

        DrawDjActionButton(
            "##useSelectedGalleryImages",
            imageGalleryContext ==
            ImageGalleryContext.Slideshow
                ? FontAwesomeIcon.Plus
                : FontAwesomeIcon.Image,
            actionLabel,
            new Vector2(
                actionWidth,
                Ui(40f)),
            selectionCount == 0,
            UseSelectedGalleryImages,
            true);
    }

    private static string GetSavedImageHost(
        string url)
    {
        return Uri.TryCreate(
            url,
            UriKind.Absolute,
            out var uri)
            ? uri.Host
            : url;
    }

    private static bool DrawSlideshowRowIconButton(
    string id,
    FontAwesomeIcon icon,
    Vector2 position,
    float size,
    bool disabled)
    {
        ImGui.SetCursorScreenPos(
            position);

        bool clicked;
        Vector2 buttonMin;
        Vector2 iconSize;

        var iconText =
            icon.ToIconString();

        using (
            UiDisabled.Scope(
                disabled))
        using (
            UiFonts.PushIcon())
        {
            //
            // The button label contains only its hidden ID. Drawing the icon
            // ourselves avoids FontAwesome's uneven glyph bearings affecting
            // ImGui's normal label alignment.
            //
            clicked =
                ImGui.Button(
                    id,
                    new Vector2(
                        size,
                        size));

            buttonMin =
                ImGui.GetItemRectMin();

            iconSize =
                ImGui.CalcTextSize(
                    iconText);

            var iconPosition =
                new Vector2(
                    buttonMin.X +
                        (size -
                         iconSize.X) /
                        2f,
                    buttonMin.Y +
                        (size -
                         iconSize.Y) /
                        2f +
                        Ui(1.5f));

            var iconColor =
                disabled
                    ? ImGui.GetStyle()
                        .Colors[
                            (int)ImGuiCol.TextDisabled]
                    : ImGui.GetStyle()
                        .Colors[
                            (int)ImGuiCol.Text];

            UiFonts.DrawText(ImGui.GetWindowDrawList(), ImGui.GetFontSize(), iconPosition, ImGui.GetColorU32(
                        iconColor), iconText);
        }

        return clicked &&
               !disabled;
    }

    private void DrawEmptyImageThumbnail(
        Vector2 min,
        Vector2 max,
        int number)
    {
        var drawList =
            ImGui.GetWindowDrawList();

        drawList.AddRectFilled(
            min,
            max,
            ImGui.GetColorU32(
                FrameBg),
            6f);

        drawList.AddRect(
            min,
            max,
            ImGui.GetColorU32(
                new Vector4(
                    MutedText.X,
                    MutedText.Y,
                    MutedText.Z,
                    0.10f)),
            6f);

        var icon =
            FontAwesomeIcon.Image
                .ToIconString();

        Vector2 iconSize;

        using (
            UiFonts.PushIcon())
        {
            iconSize =
                ImGui.CalcTextSize(
                    icon);

            UiFonts.DrawText(drawList, ImGui.GetFontSize(), new Vector2(
                    min.X +
                    ((max.X - min.X) -
                     iconSize.X) /
                    2f,
                    min.Y +
                    ((max.Y - min.Y) -
                     iconSize.Y) /
                    2f), ImGui.GetColorU32(
                    new Vector4(
                        MutedText.X,
                        MutedText.Y,
                        MutedText.Z,
                        0.40f)), icon);
        }
    }


    private void DrawImagePreviewSurface(
        string url,
        Vector2 requestedSize)
    {
        var width =
            requestedSize.X <= 0f
                ? ImGui.GetContentRegionAvail().X
                : requestedSize.X;

        var height =
            requestedSize.Y;

        var origin =
            ImGui.GetCursorScreenPos();

        var size =
            new Vector2(
                width,
                height);

        var drawList =
            ImGui.GetWindowDrawList();

        drawList.AddRectFilled(
            origin,
            origin + size,
            ImGui.GetColorU32(
                WindowBg),
            8f);

        drawList.AddRect(
            origin,
            origin + size,
            ImGui.GetColorU32(
                new Vector4(
                    MutedText.X,
                    MutedText.Y,
                    MutedText.Z,
                    0.13f)),
            8f);

        if (!string.IsNullOrWhiteSpace(
                url))
        {
            var preview =
                imagePreviews.Get(
                    url);

            if (preview.State ==
                    ImagePreviewState.Ready &&
                preview.Texture is not null)
            {
                DrawImagesHubContainedTexture(preview.Texture.Handle, preview.Texture.Width, preview.Texture.Height,
                    origin + UiVec(5, 5), size - UiVec(10, 10));
            }
            else if (preview.State ==
                     ImagePreviewState.Failed)
            {
                DrawImagePreviewPlaceholder(
                    origin,
                    size,
                    preview.Error ??
                    "The image could not be loaded.");
            }
            else
            {
                DrawImagePreviewPlaceholder(
                    origin,
                    size,
                    "Loading image...");
            }
        }
        else
        {
            DrawImagePreviewPlaceholder(
                origin,
                size,
                "Select an image to preview");
        }

        ImGui.Dummy(
            size);
    }


    private void DrawImagePreviewPlaceholder(
        Vector2 origin,
        Vector2 size,
        string message)
    {
        var drawList =
            ImGui.GetWindowDrawList();

        var icon =
            FontAwesomeIcon.Image
                .ToIconString();

        Vector2 iconSize;

        using (
            UiFonts.PushIcon())
        {
            iconSize =
                ImGui.CalcTextSize(
                    icon);

            UiFonts.DrawText(drawList, ImGui.GetFontSize(), new Vector2(
                    origin.X +
                    (size.X -
                     iconSize.X) *
                    0.5f,
                    origin.Y +
                    size.Y *
                    0.5f -
                    Ui(27f)), ImGui.GetColorU32(
                    new Vector4(
                        Accent.X,
                        Accent.Y,
                        Accent.Z,
                        0.70f)), icon);
        }

        var messageSize =
            ImGui.CalcTextSize(
                message);

        UiFonts.DrawText(drawList, ImGui.GetFontSize(), new Vector2(
                origin.X +
                (size.X -
                 messageSize.X) *
                0.5f,
                origin.Y +
                size.Y *
                0.5f +
                Ui(7f)), ImGui.GetColorU32(
                MutedText), message);
    }


    private ImagePreviewResult DrawImageUrlInput(
     string id,
     ref string value)
    {
        //
        // Reserve room for the validation icon, spacing and paste button.
        //
        ImGui.SetNextItemWidth(
            -Ui(82f));

        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                7f)
                .Push(
                    ImGuiStyleVar.FramePadding,
                    UiVec(10f, 9f)))
        using (
            ImRaii.PushColor(
                ImGuiCol.FrameBg,
                FrameBg)
                .Push(
                    ImGuiCol.FrameBgHovered,
                    FrameBgHover)
                .Push(
                    ImGuiCol.FrameBgActive,
                    AccentFieldActive))
        {
            ImGui.InputTextWithHint(
                id,
                "Paste an image link…",
                ref value,
                2000);
            if (ImGui.IsItemHovered())
                UiFonts.SetTooltip("Use a direct image link (.jpg, .jpeg, .png, .webp or .gif).");
        }

        //
        // Calling Get starts validation asynchronously. Editing the URL creates
        // a different cache key, so a previous failure does not remain attached
        // to the new value.
        //
        var preview =
            imagePreviews.Get(
                value);

        ImGui.SameLine(
            0f,
            7f);

        var statusIcon =
            preview.State switch
            {
                ImagePreviewState.Loading =>
                    FontAwesomeIcon.CircleNotch,

                ImagePreviewState.Ready =>
                    FontAwesomeIcon.CheckCircle,

                ImagePreviewState.Failed =>
                    FontAwesomeIcon.ExclamationTriangle,

                _ =>
                    FontAwesomeIcon.Circle
            };

        var statusColor =
            preview.State switch
            {
                ImagePreviewState.Loading =>
                    Accent,

                ImagePreviewState.Ready =>
                    Good,

                ImagePreviewState.Failed =>
                    Danger,

                _ =>
                    new Vector4(
                        MutedText.X,
                        MutedText.Y,
                        MutedText.Z,
                        0.25f)
            };

        using (
            UiFonts.PushIcon())
        {
            ImGui.TextColored(
                statusColor,
                statusIcon.ToIconString());
        }

        if (ImGui.IsItemHovered())
        {
            var tooltip =
                preview.State switch
                {
                    ImagePreviewState.Loading =>
                        "Checking the image URL...",

                    ImagePreviewState.Ready =>
                        "This image passed all safety checks.",

                    ImagePreviewState.Failed =>
                        preview.Error ??
                        "This image does not meet the requirements.",

                    _ =>
                        "Enter an image URL."
                };

            UiFonts.SetTooltip(
                tooltip);
        }

        ImGui.SameLine(
            0f,
            7f);

        using (
            ImRaii.PushStyle(
                ImGuiStyleVar.FrameRounding,
                7f))
        using (
            ImRaii.PushColor(
                ImGuiCol.Button,
                FrameBg)
                .Push(
                    ImGuiCol.ButtonHovered,
                    FrameBgHover)
                .Push(
                    ImGuiCol.ButtonActive,
                    AccentFieldActive))
        using (
            UiFonts.PushIcon())
        {
            if (ImGui.Button(
                    $"{FontAwesomeIcon.Clipboard.ToIconString()}##paste{id}",
                    UiVec(44f, 0f)))
            {
                var clipboard =
                    ImGui.GetClipboardText();

                if (!string.IsNullOrWhiteSpace(
                        clipboard))
                {
                    value =
                        clipboard.Trim();

                    preview =
                        imagePreviews.Get(
                            value);
                }
            }
        }

        if (preview.State ==
    ImagePreviewState.Failed)
        {
            ImGui.Dummy(
                UiVec(0f, 3f));

            using (
                ImRaii.PushStyle(
                    ImGuiStyleVar.FramePadding,
                    UiVec(3f, 2f)))
            using (
                ImRaii.PushColor(
                    ImGuiCol.Button,
                    Vector4.Zero)
                    .Push(
                        ImGuiCol.ButtonHovered,
                        new Vector4(
                            Accent.X,
                            Accent.Y,
                            Accent.Z,
                            0.14f))
                    .Push(
                        ImGuiCol.ButtonActive,
                        new Vector4(
                            Accent.X,
                            Accent.Y,
                            Accent.Z,
                            0.22f))
                    .Push(
                        ImGuiCol.Text,
                        Accent))
            {
                if (ImGui.SmallButton(
                        $"Retry image validation##retry_{id}"))
                {
                    //
                    // Remove the cached failure and immediately begin a completely
                    // new validation attempt for the same URL.
                    //
                    imagePreviews.Invalidate(
                        value);

                    preview =
                        imagePreviews.Get(
                            value);
                }
            }

            if (ImGui.IsItemHovered())
            {
                UiFonts.SetTooltip(
                    "Try downloading and validating this image again.");
            }
        }

        return preview;

    }


    


    private void AddSlideshowImage()
    {
        var url =
            imageUrlInput.Trim();

        if (url.Length == 0)
        {
            return;
        }

        for (var i = 0;
             i < MaxSlideshowImages;
             i++)
        {
            if (!string.IsNullOrWhiteSpace(
                    slideshowImageUrls[i]))
            {
                continue;
            }

            slideshowImageUrls[i] =
                url;

            selectedSlideshowImage =
                i;

            imageUrlInput =
                string.Empty;

            return;
        }
    }


    private void RemoveSlideshowImage(
        int index)
    {
        if (index < 0 ||
            index >=
            MaxSlideshowImages)
        {
            return;
        }

        for (var i = index;
             i <
             MaxSlideshowImages - 1;
             i++)
        {
            slideshowImageUrls[i] =
                slideshowImageUrls[i + 1];
        }

        slideshowImageUrls[
            MaxSlideshowImages - 1] =
            string.Empty;

        var count =
            GetSlideshowImageCount();

        if (count == 0)
        {
            selectedSlideshowImage =
                0;
        }
        else
        {
            selectedSlideshowImage =
                Math.Clamp(
                    selectedSlideshowImage,
                    0,
                    count - 1);
        }
    }


    private void MoveSlideshowImage(
        int from,
        int to)
    {
        if (from < 0 ||
            from >=
            MaxSlideshowImages ||
            to < 0 ||
            to >=
            MaxSlideshowImages)
        {
            return;
        }

        (
            slideshowImageUrls[from],
            slideshowImageUrls[to]
        ) =
        (
            slideshowImageUrls[to],
            slideshowImageUrls[from]
        );

        selectedSlideshowImage =
            to;
    }


    private int GetSlideshowImageCount()
    {
        var count =
            0;

        for (var i = 0;
             i < MaxSlideshowImages;
             i++)
        {
            if (!string.IsNullOrWhiteSpace(
                    slideshowImageUrls[i]))
            {
                count++;
            }
        }

        return count;
    }


    private static string TruncateImageUrl(
        string url,
        int maxLength)
    {
        if (string.IsNullOrWhiteSpace(
                url) ||
            url.Length <=
            maxLength)
        {
            return url;
        }

        return url[
            ..Math.Max(
                0,
                maxLength - 1)]
            .TrimEnd() +
            "…";
    }
    private void StartStillImage()
    {
        imageMediaError =
            null;

        if (!ImageMediaSelection.TryBuildStillImageUrl(
                imageUrlInput,
                out var transmittedUrl,
                out var error))
        {
            imageMediaError =
                error;

            return;
        }

        var selection =
            ImageMediaSelection.TryParse(
                transmittedUrl,
                out var parsed)
                ? parsed
                : null;

        if (selection is null)
        {
            imageMediaError =
                "The image playback information could not be created.";

            return;
        }

        queue.PlayTransient(
            new Video.VideoQueueEntry(
                transmittedUrl,
                "Still Image",
                "Image",
                null,
                selection.PrimaryImageUrl,
                0d,
                true));

    }

    private void StartImageSlideshow()
    {
        imageMediaError =
            null;

        var transition =
            slideshowTransition == 1
                ? ImageTransition.Fade
                : ImageTransition.Instant;

        if (!ImageMediaSelection.TryBuildSlideshowUrl(
                slideshowImageUrls,
                slideshowSeconds,
                transition,
                slideshowLoop,
                out var transmittedUrl,
                out var error))
        {
            imageMediaError =
                error;

            return;
        }

        if (!ImageMediaSelection.TryParse(
                transmittedUrl,
                out var selection) ||
            selection is null)
        {
            imageMediaError =
                "The slideshow playback information could not be created.";

            return;
        }

        var imageCount =
            selection.ImageUrls.Count;

        TimeSpan? duration =
            selection.Loop
                ? null
                : TimeSpan.FromSeconds(
                    selection.SecondsPerImage *
                    imageCount);

        queue.PlayTransient(
            new Video.VideoQueueEntry(
                transmittedUrl,
                imageCount == 1
                    ? "Image Slideshow"
                    : $"Slideshow ({imageCount} images)",
                "Slideshow",
                duration,
                selection.PrimaryImageUrl,
                0d,
                true));

    }
}
