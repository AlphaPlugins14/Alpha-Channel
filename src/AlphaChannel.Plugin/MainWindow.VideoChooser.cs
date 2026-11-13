using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private VideoPageChoice.Request? videoChoice;
    private int videoChoiceGeneration;

    internal void ShowPendingVideoChooser()
    {
        if (!VideoPageChoice.Pending.IsEmpty)
        {
            IsOpen = true;
            SetMinimized(false);
        }
    }

    private bool ChoosePageVideo(VideoQueueEntry entry, Action<VideoQueueEntry> apply, bool forceChoice = false)
    {
        if (!WebMediaUrlResolver.NeedsPageChoice(entry.Url)) return false;
        var choiceGeneration = ++videoChoiceGeneration;
        var account = CurrentSession?.AccountId;
        var token = CurrentSession?.Token;
        var generation = partyQueueGeneration;
        var mode = stream.Mode;
        var host = stream.HostId;
        bool Current() => Volatile.Read(ref disposeStarted) == 0 && choiceGeneration == videoChoiceGeneration && CurrentSession?.AccountId == account &&
            CurrentSession?.Token == token && generation == partyQueueGeneration && mode == stream.Mode && host == stream.HostId;
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await VideoPageChoice.ResolveAsync(screenController.Engine.Resources, entry.Url, youtubeFeedToken, Current, forceChoice).ConfigureAwait(false);
                if (result.Url is null) return;
                homeSearchQueueAdditions.Enqueue(() =>
                {
                    if (!Current()) return;
                    apply(new VideoQueueEntry(ResolvedMediaLink.Encode(entry.Url, result), string.IsNullOrWhiteSpace(result.Title) ? entry.Title : result.Title,
                        string.IsNullOrWhiteSpace(entry.Source) ? new Uri(entry.Url).Host : entry.Source, result.DurationSeconds is > 0 ? TimeSpan.FromSeconds(result.DurationSeconds.Value) : entry.Duration, result.ThumbnailUrl ?? entry.ThumbnailUrl, entry.ResumePositionSeconds, entry.IsTransient));
                });
            }
            catch (OperationCanceledException) { }
        });
        return true;
    }

    private void DrawVideoChooser()
    {
        if (videoChoice is not null && !videoChoice.Active)
        {
            videoChoice.Completion.TrySetCanceled();
            videoChoice = null;
        }
        while (videoChoice is null && VideoPageChoice.Pending.TryDequeue(out var next))
        {
            if (next.Active) videoChoice = next;
            else next.Completion.TrySetCanceled();
        }
        if (videoChoice is not { } request) return;
        var discovery = request.Discovery.IsCompletedSuccessfully ? request.Discovery.Result : null;
        if (!request.ForceChoice && discovery is { Reliable: true, Candidates.Count: 1 })
        {
            request.Completion.TrySetResult(discovery.Candidates[0]);
            videoChoice = null;
            return;
        }
        void Cancel() { request.Completion.TrySetResult(new(null, "Video selection cancelled.")); videoChoice = null; }
        DrawEventWindow("videoChooser", "Choose a video", UiVec(590, 490), () =>
        {
            if (!request.Discovery.IsCompleted)
            {
                ImGui.TextWrapped("Looking for videos on this page...");
                return;
            }
            ImGui.TextWrapped(discovery?.Candidates.Count > 0
                ? "These videos were found on the page. Choose the one you want to watch. A detected video may be a trailer or advert."
                : "No video could be extracted from this page. You can still try opening it directly.");
            ImGui.Spacing();
            if (discovery is not null)
            {
                var groups = WebVideoHtmlMetadata.Group(discovery.Candidates);
                for (var i = 0; i < groups.Count; i++)
                {
                    ImGui.PushID(i);
                    DrawVideoChoiceCard(request, groups[i][0], i + 1, showPreview: true);
                    if (groups[i].Count > 1 && ImGui.CollapsingHeader($"Other formats ({groups[i].Count - 1})"))
                    {
                        for (var j = 1; j < groups[i].Count; j++)
                        {
                            ImGui.PushID(j);
                            DrawVideoChoiceCard(request, groups[i][j], i + 1, showPreview: false);
                            ImGui.PopID();
                        }
                    }
                    ImGui.PopID();
                    ImGui.Spacing(); ImGui.Separator(); ImGui.Spacing();
                }
            }
            if (ImGui.Button("Try original page"))
            {
                request.Completion.TrySetResult(new(request.Page, null, "Original page"));
                videoChoice = null;
            }
        }, Cancel, () => { if (ImGui.Button("Cancel")) Cancel(); });
    }
    private void DrawVideoChoiceCard(VideoPageChoice.Request request, ResolvedMediaRequest source, int number, bool showPreview)
    {
        // Off-screen cards do not start network probes or thumbnail downloads.
        var visible = ImGui.IsRectVisible(new Vector2(ImGui.GetContentRegionAvail().X, Ui(100)));
        var details = visible ? request.Details(source, screenController.Engine.Resources.GetLocationFFmpeg(prepareIfMissing: false)) : null;
        var candidate = details?.IsCompletedSuccessfully == true ? details.Result : source;
        var uri = new Uri(candidate.Url!);
        void Text()
        {
            var title = !candidate.TitleIsPage && !string.IsNullOrWhiteSpace(candidate.Title) ? candidate.Title : $"Detected video {number}";
            ImGui.TextWrapped(title.Length > 180 ? title[..177] + "..." : title);
            if (candidate.TitleIsPage && !string.IsNullOrWhiteSpace(candidate.Title))
            {
                var pageTitle = candidate.Title.Length > 100 ? candidate.Title[..97] + "..." : candidate.Title;
                ImGui.TextWrapped("Page: " + pageTitle);
            }
            var description = new List<string>();
            if (candidate.DurationSeconds is > 0)
            {
                var duration = TimeSpan.FromSeconds(candidate.DurationSeconds.Value);
                description.Add(duration.TotalHours >= 1 ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}" : $"{duration.Minutes}:{duration.Seconds:00}");
            }
            if (candidate.Height is > 0) description.Add(candidate.Height + "p");
            if (candidate.HasVideo == true && candidate.HasAudio == true) description.Add("Video with audio");
            else if (candidate.HasVideo == true && candidate.HasAudio == false) description.Add("Video only");
            else if (candidate.HasVideo == false && candidate.HasAudio == true) description.Add("Audio only");
            if (description.Count > 0) ImGui.TextWrapped(string.Join(" | ", description));
            var format = candidate.Format ?? System.IO.Path.GetExtension(uri.AbsolutePath).TrimStart('.');
            if (format.Contains('/')) format = format.Split('/')[^1].Split(';')[0];
            format = format.ToLowerInvariant() switch { "x-mpegurl" or "vnd.apple.mpegurl" or "m3u8" => "HLS", "dash+xml" or "mpd" => "DASH", _ => format.ToUpperInvariant() };
            if (format.Length > 20) format = format[..20];
            ImGui.TextColored(MutedText, string.IsNullOrWhiteSpace(format) ? uri.Host : format + " | " + uri.Host);
            var filename = Uri.UnescapeDataString(uri.Segments.LastOrDefault() ?? "Video stream");
            ImGui.TextWrapped(filename.Length > 160 ? filename[..157] + "..." : filename);
            if (details is { IsCompleted: false }) ImGui.TextColored(MutedText, "Checking media details...");
            if (ImGui.Button(showPreview ? "Use this video" : "Use this format"))
            {
                request.Completion.TrySetResult(candidate);
                videoChoice = null;
            }
        }
        if (showPreview && ImGui.BeginTable("videoChoiceCard", 2, ImGuiTableFlags.SizingFixedFit))
        {
            ImGui.TableSetupColumn("Preview", ImGuiTableColumnFlags.WidthFixed, Ui(112));
            ImGui.TableSetupColumn("Details", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableNextRow(); ImGui.TableSetColumnIndex(0);
            var origin = ImGui.GetCursorScreenPos();
            var size = UiVec(112, 63);
            var draw = ImGui.GetWindowDrawList();
            draw.AddRectFilled(origin, origin + size, ImGui.GetColorU32(FrameBg), Ui(6));
            if (visible && thumbnails.Get(candidate.ThumbnailUrl) is { } thumbnail)
            {
                var scale = MathF.Min(size.X / thumbnail.Width, size.Y / thumbnail.Height);
                var imageSize = new Vector2(thumbnail.Width, thumbnail.Height) * scale;
                var imageOrigin = origin + (size - imageSize) * .5f;
                draw.AddImageRounded(thumbnail.Handle, imageOrigin, imageOrigin + imageSize, Vector2.Zero, Vector2.One, uint.MaxValue, Ui(6));
            }
            else
            {
                var label = "No preview";
                draw.AddText(origin + (size - ImGui.CalcTextSize(label)) * .5f, ImGui.GetColorU32(MutedText), label);
            }
            ImGui.Dummy(size);
            ImGui.TableSetColumnIndex(1); Text();
            ImGui.EndTable();
        }
        else if (!showPreview) Text();
    }

}
