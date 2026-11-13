using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private void DrawImagesSlideshows()
    {
        using (UiFonts.PushRegularRelative(.78f)) ImGui.TextColored(AccentHover, "IMAGES & SLIDESHOWS");
        using (UiFonts.PushRelative(1.6f, emphasis: true)) ImGui.TextUnformatted("Your pictures. Your screen.");
        using (UiFonts.PushRegularRelative(.88f))
        using (ImRaii.TextWrapPos(0)) ImGui.TextColored(MutedText, "Show a favourite image or build a slideshow for your TV.");
        SetGameLayoutRowGap(Ui(12));
        var width = ImGui.GetContentRegionAvail().X;
        var tabsOrigin = ImGui.GetCursorScreenPos();
        DrawImagesHubModeTab("Still Image", FontAwesomeIcon.Image, ImageDisplayMode.StillImage, width / 2);
        ImGui.SameLine(0, 0);
        DrawImagesHubModeTab("Slideshow", FontAwesomeIcon.Images, ImageDisplayMode.Slideshow, width / 2);
        ImGui.GetWindowDrawList().AddLine(tabsOrigin + UiVec(0, 38), tabsOrigin + new Vector2(width, Ui(38)), ImGui.GetColorU32(BorderSubtle));
        SetGameLayoutRowGap(Ui(14));

        var slideshow = imageDisplayMode == ImageDisplayMode.Slideshow;
        var count = GetSlideshowImageCount();
        var stacked = width < Ui(620);
        var gap = Ui(14);
        var leftWidth = stacked ? width : (width - gap) * .58f;
        var rightWidth = stacked ? width : width - leftWidth - gap;
        var leftHeight = Ui((slideshow ? Math.Max(376, 300 + count * 64) : 320)
            + (leftWidth - Ui(36) < Ui(315) ? 48 : 0)
            + (imagePreviews.Get(imageUrlInput).State == ImagePreviewState.Failed ? 36 : 0));
        var visibleError = imageMediaError ?? imageGalleryError;
        if (!string.IsNullOrWhiteSpace(visibleError))
        {
            using (UiFonts.PushRegularRelative(.78f))
                leftHeight += MathF.Max(0, ImGui.CalcTextSize(visibleError, false, MathF.Max(1, leftWidth - Ui(36))).Y - ImGui.GetTextLineHeight());
        }
        var origin = ImGui.GetCursorScreenPos();
        DrawPlayerHubPanel("##imagesHubControls", leftWidth, leftHeight, false, () => DrawImagesHubControls(slideshow));
        ImGui.SetCursorScreenPos(origin + new Vector2(stacked ? 0 : leftWidth + gap, stacked ? leftHeight + gap : 0));
        var previewHeight = Ui(88) + MathF.Max(1, rightWidth - Ui(36)) * 9 / 16;
        DrawPlayerHubPanel("##imagesHubPreview", rightWidth, previewHeight, false, () =>
        {
            using (UiFonts.PushRelative(1.05f, emphasis: true)) ImGui.TextUnformatted("Preview");
            SetGameLayoutRowGap(Ui(12));
            var url = slideshow ? slideshowImageUrls[Math.Clamp(selectedSlideshowImage, 0, MaxSlideshowImages - 1)] : imageUrlInput;
            var frameWidth = ImGui.GetContentRegionAvail().X;
            DrawImagePreviewSurface(url, new(frameWidth, frameWidth * 9 / 16));
            using (UiFonts.PushRegularRelative(.8f))
            {
                var counter = slideshow ? $"{(count == 0 ? 0 : Math.Min(selectedSlideshowImage + 1, count))} / {count}" : "Preview preserves image proportions";
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0, (frameWidth - ImGui.CalcTextSize(counter).X) / 2));
                ImGui.TextColored(MutedText, counter);
            }
        });
        var rightHeight = previewHeight;
        if (slideshow)
        {
            ImGui.SetCursorScreenPos(origin + new Vector2(stacked ? 0 : leftWidth + gap,
                (stacked ? leftHeight + gap : 0) + previewHeight + gap));
            var settingsHeight = ImagesHubSettingsHeight();
            DrawPlayerHubPanel("##imagesHubSettings", rightWidth, settingsHeight, false, DrawImagesHubSettings);
            rightHeight += gap + settingsHeight;
        }
        ImGui.SetCursorScreenPos(origin + new Vector2(0, stacked ? leftHeight + gap + rightHeight : MathF.Max(leftHeight, rightHeight)));
        ImGui.Dummy(new(width, 1));
    }

    private void DrawImagesHubModeTab(string label, FontAwesomeIcon icon, ImageDisplayMode mode, float width)
    {
        var origin = ImGui.GetCursorScreenPos();
        var selected = imageDisplayMode == mode;
        if (ImGui.InvisibleButton($"##imagesHubMode{mode}", new(width, Ui(38)))) imageDisplayMode = mode;
        var draw = ImGui.GetWindowDrawList();
        if (ImGui.IsItemHovered())
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            draw.AddRectFilled(origin, origin + new Vector2(width, Ui(38)), ImGui.GetColorU32(AccentInk(.07f)), Ui(6));
        }
        DrawPlayerActionButtonContent(origin, new(width, Ui(38)), icon, label, selected ? AccentHover : MutedText);
        if (selected) draw.AddRectFilled(origin + UiVec(0, 36), origin + new Vector2(width, Ui(39)), ImGui.GetColorU32(Accent));
    }

    private void DrawImagesHubControls(bool slideshow)
    {
        var width = ImGui.GetContentRegionAvail().X;
        var origin = ImGui.GetCursorScreenPos();
        using (UiFonts.PushRelative(1.05f, emphasis: true)) ImGui.TextUnformatted(slideshow ? "Your slides" : "Still image");
        ImGui.SetCursorScreenPos(origin + new Vector2(width - Ui(140), 0));
        if (GameLayoutButton("Open Gallery", FontAwesomeIcon.Images, Ui(140), height: 32))
            OpenImageGallery(slideshow ? ImageGalleryContext.Slideshow : ImageGalleryContext.StillImage);
        ImGui.SetCursorScreenPos(origin + UiVec(0, 43));
        using (UiFonts.PushRegularRelative(.82f))
        using (ImRaii.TextWrapPos(0))
            ImGui.TextColored(MutedText, slideshow ? $"{GetSlideshowImageCount()} / {MaxSlideshowImages} images · Arrange your slides below." : "Display one image continuously on your TV.");
        SetGameLayoutRowGap(Ui(14));
        if (slideshow)
        {
            var count = GetSlideshowImageCount();
            if (count == 0)
            {
                var empty = ImGui.GetCursorScreenPos();
                ImGui.Dummy(new(width, Ui(94)));
                UiFonts.DrawCenteredIcon(ImGui.GetWindowDrawList(), Ui(24), empty + new Vector2(width / 2, Ui(24)), ImGui.GetColorU32(AccentInk(.5f)), FontAwesomeIcon.Images.ToIconString());
                using (UiFonts.PushRegularRelative(.9f))
                {
                    const string text = "Add your first image";
                    ImGui.GetWindowDrawList().AddText(empty + new Vector2(MathF.Max(0, (width - ImGui.CalcTextSize(text).X) / 2), Ui(50)), ImGui.GetColorU32(MutedText), text);
                }
            }
            else
            {
                for (var i = 0; i < count; i++)
                {
                    var changed = DrawImagesHubSlideRow(i, count);
                    SetGameLayoutRowGap(Ui(6));
                    if (changed) break; // The array was reordered or compacted by removal.
                }
            }
            SetGameLayoutRowGap(Ui(12));
        }
        else
        {
            using (UiFonts.PushRegularRelative(.88f)) ImGui.TextUnformatted("Image URL");
            SetGameLayoutRowGap(Ui(8));
        }
        ImagePreviewResult validation;
        using (UiDisabled.Scope(slideshow && GetSlideshowImageCount() >= MaxSlideshowImages))
            validation = DrawImageUrlInput(slideshow ? "##hubSlideshowUrl" : "##hubStillUrl", ref imageUrlInput);
        SetGameLayoutRowGap(Ui(12));
        var ready = validation.State == ImagePreviewState.Ready;
        var saved = IsImageSavedToGallery(imageUrlInput);
        var saveWidth = Ui(148);
        var stackActions = width < Ui(315);
        using (UiDisabled.Scope(!ready || (slideshow && GetSlideshowImageCount() >= MaxSlideshowImages)))
            if (GameLayoutButton(slideshow ? "Add image" : "Show on TV", slideshow ? FontAwesomeIcon.Plus : FontAwesomeIcon.Image,
                    stackActions ? width : width - saveWidth - Ui(8), primary: true, height: 38))
            {
                if (slideshow) AddSlideshowImage(); else StartStillImage();
            }
        if (stackActions) SetGameLayoutRowGap(Ui(8)); else ImGui.SameLine(0, Ui(8));
        using (UiDisabled.Scope(!ready || saved))
            if (GameLayoutButton(saved ? "Saved" : "Save to Gallery", FontAwesomeIcon.Save, stackActions ? width : saveWidth, height: 38))
                SaveImageToGallery(imageUrlInput);
        SetGameLayoutRowGap(Ui(14));
        using (UiFonts.PushRegularRelative(.78f))
        using (ImRaii.TextWrapPos(0))
        {
            var error = imageMediaError ?? imageGalleryError;
            ImGui.TextColored(string.IsNullOrWhiteSpace(error) ? MutedText : Danger,
                error ?? (slideshow ? "Images load directly from their URLs. Add up to 5 images." : "Images must be available from a public URL."));
            if (!slideshow) ImGui.TextColored(MutedText, "Supported hosts: MyImgs, ImgPile, Imgur and Postimages.");
        }
        SetGameLayoutRowGap(Ui(12));
        using (UiFonts.PushRegularRelative(.86f))
        {
            const string text = "Image Hosting Guide";
            var position = ImGui.GetCursorScreenPos();
            if (ImGui.InvisibleButton("##imagesHubHostingGuide", new(ImGui.CalcTextSize(text).X + Ui(8), Ui(24)))) imageHostingGuideOpen = true;
            ImGui.GetWindowDrawList().AddText(position + UiVec(0, 3), ImGui.GetColorU32(ImGui.IsItemHovered() ? AccentHover : Accent), text);
            if (ImGui.IsItemHovered()) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }
    }

    private bool DrawImagesHubSlideRow(int index, int count)
    {
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var draw = ImGui.GetWindowDrawList();
        var url = slideshowImageUrls[index];
        DrawArcadeGradientPanel(draw, origin, new(width, Ui(58)), HubSurface(0x111E33), HubSurface(0x0C1627),
            selectedSlideshowImage == index ? Accent : HubTint(0x24456B), 7);
        using (UiFonts.PushRegularRelative(.82f)) draw.AddText(origin + UiVec(8, 21), ImGui.GetColorU32(MutedText), (index + 1).ToString());
        var thumbnail = origin + UiVec(25, 7);
        var preview = imagePreviews.Get(url);
        if (preview.State == ImagePreviewState.Ready && preview.Texture is { } texture)
            DrawImagesHubContainedTexture(texture.Handle, texture.Width, texture.Height, thumbnail, UiVec(64, 44));
        else DrawEmptyImageThumbnail(thumbnail, thumbnail + UiVec(64, 44), index + 1);
        var textWidth = MathF.Max(1, width - Ui(192));
        using (UiFonts.PushRegularRelative(.86f))
            draw.AddText(origin + UiVec(99, 12), ImGui.GetColorU32(Vector4.One), TruncateToWidth(CreateSavedImageName(url), textWidth));
        using (UiFonts.PushRegularRelative(.74f))
            draw.AddText(origin + UiVec(99, 34), ImGui.GetColorU32(MutedText), TruncateToWidth(GetSavedImageHost(url), textWidth));
        ImGui.SetCursorScreenPos(origin);
        if (ImGui.InvisibleButton($"##hubSlideSelect{index}", new(MathF.Max(1, width - Ui(91)), Ui(58)))) selectedSlideshowImage = index;
        if (ImGui.IsItemHovered()) UiFonts.SetTooltip(url);
        var changed = false;
        for (var action = 0; action < 3; action++)
        {
            var icon = action == 0 ? FontAwesomeIcon.ChevronUp : action == 1 ? FontAwesomeIcon.ChevronDown : FontAwesomeIcon.Trash;
            var disabled = action == 0 ? index == 0 : action == 1 && index == count - 1;
            if (DrawSlideshowRowIconButton($"##hubSlide{index}Action{action}", icon,
                    origin + new Vector2(width - Ui(87) + Ui(action * 27), Ui(16)), Ui(24), disabled))
            {
                if (action == 2) RemoveSlideshowImage(index); else MoveSlideshowImage(index, index + (action == 0 ? -1 : 1));
                changed = true;
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) UiFonts.SetTooltip(action == 0 ? "Move up" : action == 1 ? "Move down" : "Remove image");
        }
        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new(width, Ui(58)));
        return changed;
    }

    private static float ImagesHubSettingsHeight()
    {
        float headingHeight, labelHeight;
        using (UiFonts.PushRelative(1.04f, emphasis: true)) headingHeight = ImGui.GetTextLineHeight();
        using (UiFonts.PushRegularRelative(.83f)) labelHeight = ImGui.GetTextLineHeight();
        // Match the actual controls and gaps, including bottom padding for the action.
        return Ui(36 + 12 + 10 + 10 + 12 + 38) + headingHeight + 2 * labelHeight
            + 2 * ImGui.GetStyle().ItemSpacing.Y + 3 * ImGui.GetFrameHeight();
    }

    private void DrawImagesHubSettings()
    {
        using (UiFonts.PushRelative(1.04f, emphasis: true)) ImGui.TextUnformatted("Slideshow settings");
        SetGameLayoutRowGap(Ui(12));
        using (UiFonts.PushRegularRelative(.83f)) ImGui.TextColored(MutedText, "Change image every");
        ImGui.SetNextItemWidth(-1);
        ImGui.SliderInt("##hubSlideshowSeconds", ref slideshowSeconds, 2, 60, "%d sec");
        SetGameLayoutRowGap(Ui(10));
        using (UiFonts.PushRegularRelative(.83f)) ImGui.TextColored(MutedText, "Transition");
        ImGui.SetNextItemWidth(-1);
        ImGui.Combo("##hubSlideshowTransition", ref slideshowTransition, slideshowTransitions, slideshowTransitions.Length);
        SetGameLayoutRowGap(Ui(10));
        ImGui.Checkbox("Loop slideshow", ref slideshowLoop);
        SetGameLayoutRowGap(Ui(12));
        var ready = GetSlideshowImageCount() > 0;
        foreach (var url in slideshowImageUrls)
            if (!string.IsNullOrWhiteSpace(url) && imagePreviews.Get(url).State != ImagePreviewState.Ready) ready = false;
        using (UiDisabled.Scope(!ready))
            if (GameLayoutButton("Start slideshow", FontAwesomeIcon.Play, ImGui.GetContentRegionAvail().X, primary: true, height: 38)) StartImageSlideshow();
        if (!ready && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) UiFonts.SetTooltip("Add images and wait for them to finish loading before starting.");
    }

    private static void DrawImagesHubContainedTexture(ImTextureID handle, int textureWidth, int textureHeight, Vector2 origin, Vector2 size)
    {
        var scale = MathF.Min(size.X / Math.Max(1, textureWidth), size.Y / Math.Max(1, textureHeight));
        var imageSize = new Vector2(textureWidth, textureHeight) * scale;
        var min = origin + (size - imageSize) / 2;
        ImGui.GetWindowDrawList().AddImageRounded(handle, min, min + imageSize, Vector2.Zero, Vector2.One, uint.MaxValue, Ui(4));
    }
}
