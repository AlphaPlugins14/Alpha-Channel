using System.Security.Cryptography;
using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Auth;
using AlphaChannel.Plugin.Video;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility.Raii;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace AlphaChannel.Plugin;

internal sealed partial class MainWindow
{
    private const string GroupImageNotice = "Private chat images are stored encrypted for up to 48 hours, if not downloaded within the 48 hour period then images will need to be resent. Once downloaded the image remains in your local encrypted chatlog.";
    private bool CanAttachGroupImage(string conversation) => CurrentSession?.IsGuest != true && (GroupChatClient.IsGroup(conversation)
        ? dmClient.Groups.State.Groups.Any(g => GroupChatClient.Prefix + g.Id == conversation && !g.IsPublic)
        : Guid.TryParseExact(conversation, "D", out _) && DirectImageMember(conversation) is not null);
    private string ImageConversation(MessageDto message) => GroupChatClient.IsMessage(message)
        ? message.Id[..message.Id.LastIndexOf(':')]
        : GroupChatClient.ImageContent(decryptedCache.GetValueOrDefault(message.Id))?.Image.GroupId ?? throw new InvalidOperationException("Invalid image message.");
    private string ImagePlaintext(MessageDto message) => decryptedCache.GetValueOrDefault(message.Id) ?? throw new InvalidOperationException("Decrypt this image message first.");
    private readonly PluginFilePicker groupImageFileDialog = new(), groupImageSaveDialog = new();
    private readonly Dictionary<string, GroupImageDraft> groupImageDrafts = new();
    private readonly Dictionary<string, GroupImageView> groupImageViews = new();
    private string? groupImageViewer;
    private int groupImageGeneration;
    private int groupImageLoads;
    private int groupImageStatusLoads;
    private CancellationTokenSource groupImageLifetime = new();
    private readonly HashSet<Task> groupImageWork = new();
    private readonly object groupImageWorkLock = new();
    private Task StartGroupImageWork(Func<CancellationToken,Task> work,bool propagateErrors=false)
    {
        var cancellation=groupImageLifetime.Token;
        var task=Task.Run(async()=>{try{await work(cancellation);}catch(OperationCanceledException)when(cancellation.IsCancellationRequested){}catch{if(propagateErrors)throw;AepLog.Warning("[Messages] Image background work failed.");}});
        lock(groupImageWorkLock)groupImageWork.Add(task);
        _=task.ContinueWith(done=>{_ = done.Exception;lock(groupImageWorkLock)groupImageWork.Remove(done);},CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
        return task;
    }
    private sealed class GroupImageDraft : IDisposable
    {
        internal PreparedChatImage? Image;
        internal IDalamudTextureWrap? Texture;
        internal bool Busy;
        internal string Label = "Preparing image…";
        internal string? Allowance;
        public void Dispose() { Texture?.Dispose(); if (Image is { } image) CryptographicOperations.ZeroMemory(image.Bytes); }
    }
    private sealed class GroupImageView : IDisposable
    {
        internal required string Conversation;
        internal string? MessageGroupId;
        internal byte[]? Bytes;
        internal IDalamudTextureWrap? Texture;
        internal bool Busy, CheckedCache, Expired;
        internal string? Error;
        internal long LastSeen;
        internal int Revision, LastSeenFrame = -10;
        internal bool Revoked;
        internal DateTime NextStatusUtc;
        internal bool CheckingStatus;
        internal long ExpiresAt;
        public void Dispose() { Revision++; Texture?.Dispose(); if (Bytes is { } bytes) CryptographicOperations.ZeroMemory(bytes); Bytes = null; Texture = null; }
    }
    private static async Task<IDalamudTextureWrap> GroupImageTexture(byte[] bytes,CancellationToken cancellation = default)
    {
        if (!Image.DetectFormat(bytes).Name.Equals("WEBP", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid chat image format.");
        using var lease = await BoundedImageDecode.EnterAsync(cancellation);
        var info = Image.Identify(bytes) ?? throw new InvalidDataException("Invalid chat image.");
        using var image = BoundedImageDecode.Load(bytes, Math.Min(1920, info.Width), Math.Min(1920, info.Height));
        if (image.Width > 1920 || image.Height > 1920) image.Mutate(x => x.Resize(new ResizeOptions { Size = new Size(1920, 1920), Mode = ResizeMode.Max }));
        using var png = new MemoryStream(); await image.SaveAsPngAsync(png,cancellation);
        cancellation.ThrowIfCancellationRequested();
        return await Plugin.TextureProvider.CreateFromImageAsync(png.ToArray());
    }
    private void ChooseGroupImage(CharacterSession session, string conversation)
    {
        if (!CanAttachGroupImage(conversation)) return;
        groupImageFileDialog.OpenFileDialog("Attach an image", "Images{.png,.jpg,.jpeg,.webp}", (bool success, string path) => {
            if (!success || !IsCurrentMessageSession(session) || !CanAttachGroupImage(conversation)) return;
            if (!groupImageDrafts.ContainsKey(conversation) && groupImageDrafts.Count >= 4) { messagesError = "Send or remove an image attachment in another chat before attaching more images."; return; }
            if (groupImageDrafts.Remove(conversation, out var old)) old.Dispose();
            var draft = new GroupImageDraft { Busy = true }; groupImageDrafts[conversation] = draft;
            var generation = groupImageGeneration;
            StartGroupImageWork(async cancellation => {
                PreparedChatImage? prepared = null; IDalamudTextureWrap? texture = null; string? error = null; GroupImageAllowance? allowance = null;
                try { prepared = await ChatImagePreparation.PrepareAsync(path,cancellation); texture = await GroupImageTexture(prepared.Bytes,cancellation);
                    try { allowance = await dmClient.Groups.ImageAllowanceAsync(session.Token); } catch { }
                }
                catch (Exception e) { error = e is InvalidOperationException ? e.Message : "This image could not be opened. Choose a PNG, JPEG or WebP up to 20 MB."; }
                var transferred = false;
                try {
                await Plugin.Framework.RunOnFrameworkThread(() => {
                    if (Volatile.Read(ref disposeStarted) != 0 || !IsCurrentMessageSession(session) || generation != groupImageGeneration || !groupImageDrafts.TryGetValue(conversation, out var current) || current != draft) {
                        return;
                    }
                    draft.Busy = false;
                    if (error is not null) { messagesError = error; groupImageDrafts.Remove(conversation); draft.Dispose(); }
                    else { messagesError = null; draft.Image = prepared; draft.Texture = texture; draft.Label = $"Image attached · {prepared!.Width} × {prepared.Height} · {prepared.Bytes.Length / 1024} KB";
                        if (allowance is not null) draft.Allowance = $"{allowance.Used} / {allowance.Limit} image slots in use. This image will use one slot when sent.";
                    }
                    transferred = error is null;
                });
                } finally {if(!transferred){texture?.Dispose();if(prepared is not null)CryptographicOperations.ZeroMemory(prepared.Bytes);}draft.Busy=false;}
            });
        });
    }
    private float GroupImageComposerExtra(string conversation, float? contentWidth = null)
    {
        if (!CanAttachGroupImage(conversation) || !groupImageDrafts.ContainsKey(conversation)) return 0;
        using var font = UiFonts.PushRegularRelative(.8f);
        return Ui(56) + ImGui.CalcTextSize(GroupImageNotice, false, MathF.Max(Ui(120), contentWidth ?? ImGui.GetContentRegionAvail().X - Ui(24))).Y;
    }
    private void DrawGroupImageDraft(string conversation)
    {
        if (!CanAttachGroupImage(conversation)) return;
        if (!groupImageDrafts.TryGetValue(conversation, out var draft)) return;
        var origin = ImGui.GetCursorScreenPos(); var width = ImGui.GetContentRegionAvail().X;
        var extra = GroupImageComposerExtra(conversation,width);
        using (UiDisabled.Scope(draft.Busy)) {
            ImGui.Button(draft.Label + "##imageAttachment", new Vector2(MathF.Max(Ui(60), width - Ui(40)), Ui(34)));
            if (ImGui.IsItemHovered() && draft.Texture is { } texture) {
                ImGui.BeginTooltip();
                var scale = MathF.Min(Ui(240) / texture.Width, Ui(150) / texture.Height);
                ImGui.Image(texture.Handle, new Vector2(texture.Width, texture.Height) * scale);
                if (draft.Allowance is { } allowance) ImGui.TextWrapped(allowance);
                ImGui.EndTooltip();
            }
            ImGui.SameLine();
            if (ImGui.Button("×##removeImage", UiVec(30,34))) { groupImageDrafts.Remove(conversation); draft.Dispose(); }
        }
        ImGui.SetCursorScreenPos(origin + UiVec(0,42));
        using (UiFonts.PushRegularRelative(.8f)) using (ImRaii.TextWrapPos(0)) ImGui.TextColored(MutedText, GroupImageNotice);
        ImGui.SetCursorScreenPos(origin + new Vector2(0,extra));
    }
    private void SendGroupImage(CharacterSession session, string conversation, string caption)
    {
        if (!CanAttachGroupImage(conversation)) return;
        if (!groupImageDrafts.TryGetValue(conversation, out var draft) || draft.Busy || draft.Image is not { } image) return;
        messagesError = null; draft.Busy = true; var generation = groupImageGeneration; var reply = messageReplyTarget?.GroupId;
        var uploadBytes = image.Bytes.ToArray();
        StartGroupImageWork(async cancellation => {
            try {
                var sent = GroupChatClient.IsGroup(conversation)
                    ? await dmClient.Groups.SendImageAsync(session.Token, conversation, caption, uploadBytes, image.Width, image.Height, reply)
                    : await dmClient.Groups.SendDirectImageAsync(session.Token, conversation, caption, uploadBytes, image.Width, image.Height,
                        text => RelayDirectImageMessage(session, conversation, text), reply);
                await Plugin.Framework.RunOnFrameworkThread(() => {
                    if (generation != groupImageGeneration || !IsCurrentMessageSession(session) || !CanAttachGroupImage(conversation)) return;
                    groupImageDrafts.Remove(conversation); draft.Dispose();
                    decryptedCache[sent.Message.Id] = sent.Plaintext;
                    UpdateChatsPreview(conversation, sent.Message, caption.Length == 0 ? "Image" : caption);
                    if (openConversationId == conversation) {
                        messageComposerInput = ""; messageReplyTarget = null;
                        if (!openMessages.Any(m => m.Id == sent.Message.Id)) openMessages = [.. openMessages, sent.Message];
                        threadScrollToBottom = true;
                    }
                });
            } catch (Exception e) {
                await Plugin.Framework.RunOnFrameworkThread(() => {
                    if (generation != groupImageGeneration || !IsCurrentMessageSession(session)) return;
                    draft.Busy = false; messagesError = e is InvalidOperationException ? e.Message : "Image could not be sent. Your attachment is still here to retry.";
                });
            } finally {draft.Busy=false;CryptographicOperations.ZeroMemory(uploadBytes);}
        });
    }
    private GroupImageView ImageView(string key, string conversation)
    {
        if (!groupImageViews.TryGetValue(key, out var view)) {
            foreach (var retired in groupImageViews.Where(p=>!p.Value.Busy && !p.Value.CheckingStatus && p.Key!=groupImageViewer && p.Value.LastSeenFrame<ImGui.GetFrameCount()-1)
                .OrderBy(p=>p.Value.LastSeen).Take(Math.Max(0,groupImageViews.Count-127)).Select(p=>p.Key).ToArray()) {
                groupImageViews[retired].Dispose(); groupImageViews.Remove(retired);
            }
            groupImageViews[key] = view = new() { Conversation = conversation };
        }
        return view;
    }
    private void TrimGroupImageMemory(string keep)
    {
        while (groupImageViews.Count(p => p.Value.Texture is not null) > 8) {
            var oldest = groupImageViews.Where(p => p.Key != keep && p.Key != groupImageViewer && p.Value.Texture is not null && !p.Value.Busy && p.Value.LastSeenFrame < ImGui.GetFrameCount()-1).MinBy(p => p.Value.LastSeen);
            if (oldest.Value is null) break;
            oldest.Value.Dispose(); oldest.Value.CheckedCache = false;
        }
    }
    private void LoadGroupImage(CharacterSession session, MessageDto message, bool download)
    {
        var view = ImageView(message.Id, ImageConversation(message)); if (view.Busy || view.Revoked || groupImageLoads >= 2) return;
        if (groupImageViews.Count(p=>p.Value.Texture is not null)>=8 && !groupImageViews.Any(p=>p.Value.Texture is not null && !p.Value.Busy && p.Value.LastSeenFrame<ImGui.GetFrameCount()-1 && p.Key!=groupImageViewer)) return;
        var revision = view.Revision;
        var plaintext = GroupChatClient.IsMessage(message) ? null : ImagePlaintext(message);
        view.Busy = true; Interlocked.Increment(ref groupImageLoads); view.Error = null; var generation = groupImageGeneration;
        StartGroupImageWork(async cancellation => {
            byte[]? bytes = null; IDalamudTextureWrap? texture = null; string? error = null;
            try {
                bytes = GroupChatClient.IsMessage(message)
                    ? download ? await dmClient.Groups.DownloadImageAsync(session.Token, message) : await dmClient.Groups.CachedImageAsync(session.Token, message)
                    : download ? await dmClient.Groups.DownloadDirectImageAsync(session.Token, view.Conversation, message, plaintext!)
                        : await dmClient.Groups.CachedDirectImageAsync(session.Token, view.Conversation, message, plaintext!);
                if (bytes is not null) texture = await GroupImageTexture(bytes,cancellation);
            } catch (Exception e) {
                if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); bytes = null;
                error = e is InvalidOperationException ? e.Message : "Image could not be opened. Please retry.";
            }
            var transferred = false;
            try {
            await Plugin.Framework.RunOnFrameworkThread(() => {
                if (Volatile.Read(ref disposeStarted) != 0 || !IsCurrentMessageSession(session) || generation != groupImageGeneration || !groupImageViews.TryGetValue(message.Id, out var current) || current != view || view.Revision != revision || view.Revoked) {
                    return;
                }
                view.Busy = false; view.CheckedCache = true; view.Bytes = bytes; view.Texture = texture; transferred = true;
                view.Expired = error == "Expired image"; view.Error = error;
                TrimGroupImageMemory(message.Id);
            });
            } finally {
                if(!transferred){texture?.Dispose();if(bytes is not null)CryptographicOperations.ZeroMemory(bytes);}
                view.Busy=false;Interlocked.Decrement(ref groupImageLoads);
            }
        });
    }
    private void DrawGroupImage(CharacterSession session, MessageDto message, GroupImageContent content, float width)
    {
        var view = ImageView(message.Id, content.Image.Direct ? content.Image.GroupId : GroupChatClient.Prefix + content.Image.GroupId);
        view.MessageGroupId=message.GroupId;
        if (view.ExpiresAt == 0) view.ExpiresAt = MediaExpiry(message);
        var visible = ImGui.IsRectVisible(new Vector2(MathF.Min(width, Ui(340)), Ui(260)));
        if (visible) {
            view.LastSeen = DateTime.UtcNow.Ticks; view.LastSeenFrame = ImGui.GetFrameCount();
            if (!view.CheckedCache && !view.Busy && dmClient.Groups.Ready) LoadGroupImage(session, message, download: false);
            if (view.CheckedCache && !view.CheckingStatus && dmClient.Groups.Ready && DateTime.UtcNow >= view.NextStatusUtc) CheckGroupImageStatus(session, message, view);
        }
        if (!string.IsNullOrWhiteSpace(content.Text)) {
            var caption = GroupChatClient.ImageCaption(content.Text);
            var origin = ImGui.GetCursorScreenPos(); var height = LayoutChatRichText(caption, origin, width, true);
            ImGui.SetCursorScreenPos(origin + new Vector2(0, height + Ui(8)));
        }
        var size = new Vector2(MathF.Min(width, Ui(340)), Ui(180));
        if (view.Texture is { } texture) {
            var factor = MathF.Min(size.X / texture.Width, Ui(230) / texture.Height);
            ImGui.Image(texture.Handle, new Vector2(texture.Width, texture.Height) * factor);
            if (ImGui.IsItemClicked()) groupImageViewer = message.Id;
        } else {
            var expired = view.Expired || view.ExpiresAt > 0 && view.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var p = ImGui.GetCursorScreenPos(); var draw = ImGui.GetWindowDrawList();
            draw.AddRectFilled(p, p + size, ImGui.GetColorU32(FrameBg), Ui(9));
            // Synthetic blur: no thumbnail or original image is fetched until the user clicks.
            draw.PushClipRect(p,p+size,true);
            for (var i = 0; i < 6; i++) draw.AddRectFilled(p + UiVec(14 + i * 39, 15 + (i % 3) * 42), p + UiVec(90 + i * 32, 90 + (i % 2) * 55), ImGui.GetColorU32(AccentInk(.025f)), Ui(25));
            var label = TruncateToWidth(expired ? "Expired image" : view.Busy ? "Loading…" : "Click to Download Media",MathF.Max(1,size.X-Ui(24)));
            var labelSize = ImGui.CalcTextSize(label); draw.AddText(p + (size - labelSize) * .5f, ImGui.GetColorU32(MutedText), label);
            draw.PopClipRect();
            if (ImGui.InvisibleButton("##downloadImage", size) && !expired && !view.Busy) LoadGroupImage(session, message, download: true);
            DrawGroupImageExpiry(view.ExpiresAt,expired);
            if (view.Error is { } error && !expired) ImGui.TextColored(Danger, error);
        }
        if (ImGui.SmallButton("…##imageMenu")) ImGui.OpenPopup("##imageMenu");
        if (ImGui.BeginPopup("##imageMenu")) {
            DrawGroupImageMenu(session, message, view);
            ImGui.EndPopup();
        }
        ImGui.Dummy(UiVec(0,6));
    }
    private void CheckGroupImageStatus(CharacterSession session, MessageDto message, GroupImageView view)
    {
        if(Volatile.Read(ref groupImageStatusLoads)>=2)return;
        var plaintext = GroupChatClient.IsMessage(message) ? null : ImagePlaintext(message);
        Interlocked.Increment(ref groupImageStatusLoads);
        view.CheckingStatus = true; view.NextStatusUtc = DateTime.UtcNow.AddMinutes(1); var generation = groupImageGeneration;
        StartGroupImageWork(async cancellation => {
            GroupImageStatus? status = null; bool expired = false;
            try { status = GroupChatClient.IsMessage(message) ? await dmClient.Groups.ImageStatusAsync(session.Token, message)
                : await dmClient.Groups.DirectImageStatusAsync(session.Token, view.Conversation, message, plaintext!); }
            catch (InvalidOperationException e) { expired = e.Message == "Expired image"; }
            catch { }
            try {await Plugin.Framework.RunOnFrameworkThread(() => {
                if (generation != groupImageGeneration || !IsCurrentMessageSession(session)) return;
                view.CheckingStatus = false; view.Expired = expired || status?.Available == false;
                if (status is { ExpiresAt: > 0 }) view.ExpiresAt = view.ExpiresAt > 0 ? Math.Min(view.ExpiresAt,status.ExpiresAt) : status.ExpiresAt;
                if (status?.ImageDeleted == true) { view.Revoked = true; view.Dispose(); view.CheckedCache = true; if (groupImageViewer == message.Id) groupImageViewer = null; }
            });}finally{view.CheckingStatus=false;Interlocked.Decrement(ref groupImageStatusLoads);}
        });
    }
    private static long MediaExpiry(MessageDto message)
    {
        if (!GroupChatClient.IsMessage(message)) return message.SentAtUnix + GroupImagePolicy.RetentionHours * 3600L;
        try { var m = System.Text.Json.JsonSerializer.Deserialize<GroupMessage>(message.Ciphertext, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)); return m is null ? 0 : GroupImagePolicy.ExpiresAt(m); }
        catch { return 0; }
    }
    private void DrawGroupImageExpiry(long expiry, bool expired)
    {
        using (UiFonts.PushRegularRelative(.8f)) {
            ImGui.TextColored(MutedText,expired ? "Server copy unavailable" : GroupImagePolicy.Countdown(expiry,DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            ImGui.SameLine(0,Ui(6));
            if (AlphaChatTextAction("imageExpiryInfo","",FontAwesomeIcon.InfoCircle,MutedText,Ui(20),Ui(20))) ImGui.OpenPopup("##imageExpiryInfo");
            if (ImGui.IsItemHovered()) UiFonts.SetTooltip("Image expiry information · Click for details");
        }
        ImGui.SetNextWindowSize(new Vector2(Ui(330),0),ImGuiCond.Appearing);
        using var style = ImRaii.PushStyle(ImGuiStyleVar.PopupRounding,Ui(10)).Push(ImGuiStyleVar.PopupBorderSize,Ui(1)).Push(ImGuiStyleVar.WindowPadding,UiVec(16,14));
        using var colours = ImRaii.PushColor(ImGuiCol.PopupBg,CardBg).Push(ImGuiCol.Border,AccentInk(.55f));
        if (!UiFonts.Window(ImGui.GetFontSize(),ImGui.BeginPopup("##imageExpiryInfo"))) return;
        DrawSocialHeading("Image expiry",1.05f);
        using (ImRaii.TextWrapPos(0)) {
            ImGui.TextUnformatted($"Images are stored end-to-end encrypted on our server for up to {GroupImagePolicy.RetentionHours} hours before being deleted.");
            ImGui.TextUnformatted("The server copy can be deleted sooner once every recipient has downloaded and cached it.");
            ImGui.Spacing();
            ImGui.TextUnformatted("If you download an image before it expires, your copy remains encrypted in your local chat history unless you delete it.");
            ImGui.Spacing();
            using (UiFonts.PushRegularRelative(.85f)) ImGui.TextColored(MutedText,"Reported images may be retained encrypted until the report is resolved or a moderator deletes the image.");
        }
        ImGui.EndPopup();
    }
    private void DrawGroupImageMenu(CharacterSession session, MessageDto message, GroupImageView view)
    {
        using (UiDisabled.Scope(view.Bytes is null)) if (ImGui.MenuItem("Save image…")) SaveGroupImage(session, view);
        using (UiDisabled.Scope(view.Bytes is null)) if (ImGui.MenuItem("Remove downloaded copy")) RunCloudGroupAction(session, async () => {
            if (GroupChatClient.IsMessage(message)) await dmClient.Groups.ForgetCachedImageAsync(session.Token, message);
            else await dmClient.Groups.ForgetDirectImageAsync(session.Token, view.Conversation, message, ImagePlaintext(message));
            await Plugin.Framework.RunOnFrameworkThread(() => { view.Dispose(); view.CheckedCache = true; groupImageViewer = null; });
        });
        if (message.SenderAccountId != session.AccountId && ImGui.MenuItem("Report image")) QueueDmMessageReport(message, decryptedCache.GetValueOrDefault(message.Id) ?? "Image");
        if (message.SenderAccountId == session.AccountId && ImGui.MenuItem("Delete image")) {
            DeleteDmForAll(session, view.Conversation, message); view.Dispose(); groupImageViews.Remove(message.Id); groupImageViewer = null;
        }
    }
    private void SaveGroupImage(CharacterSession session, GroupImageView view)
    {
        groupImageSaveDialog.SaveFileDialog("Save image (unencrypted file)", "WebP{.webp}", "AlphaChannel-image.webp", ".webp", (success, path) => {
            if (!success || !IsCurrentMessageSession(session) || view.Bytes is null) return;
            try { File.WriteAllBytes(path, view.Bytes); }
            catch { messagesError = "Image could not be saved to that location."; }
        });
    }
    private void DrawGroupImageViewer()
    {
        if (groupImageViewer is not { } id || CurrentSession is not { } session || !groupImageViews.TryGetValue(id, out var view) || view.Texture is not { } texture) return;
        if(view.Conversation.StartsWith("report:",StringComparison.Ordinal)&&!CanViewModeratorEvidence){groupImageViewer=null;return;}
        if (!view.Conversation.StartsWith("report:", StringComparison.Ordinal) && (!CanAttachGroupImage(view.Conversation) || openConversationId != view.Conversation || !openMessages.Any(m => m.Id == id))) { groupImageViewer = null; return; }
        var root = ImGuiP.GetCurrentWindow().RootWindow; var position = root.Pos; var size = root.Size;
        ImGui.SetNextWindowPos(position); ImGui.SetNextWindowSize(size); ImGui.SetNextWindowBgAlpha(0);
        if (!BeginWindowOverlay("##groupImageViewer", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoScrollbar)) { ImGui.End(); return; }
        try {
            ImGui.GetWindowDrawList().AddRectFilled(position, position + size, ImGui.GetColorU32(new Vector4(0,0,0,.8f)));
            if (ImGui.IsKeyPressed(ImGuiKey.Escape)) groupImageViewer = null;
            ImGui.SetCursorScreenPos(position + UiVec(22,18));
            if (ImGui.Button("Close")) groupImageViewer = null;
            ImGui.SameLine(); if (ImGui.Button("Save image…")) SaveGroupImage(session, view);
            var available = Vector2.Max(Vector2.One, size - UiVec(48,100));
            var factor = MathF.Min(available.X / texture.Width, available.Y / texture.Height);
            var imageSize = new Vector2(texture.Width, texture.Height) * factor;
            ImGui.SetCursorScreenPos(position + new Vector2((size.X-imageSize.X)*.5f, Ui(70)+(available.Y-imageSize.Y)*.5f));
            ImGui.Image(texture.Handle, imageSize);
        } finally { ImGui.End(); }
    }
    private void ReconcileGroupImageCache(HashSet<string> allowed, bool clearAll)
    {
        if (clearAll) {
            groupImageLifetime.Cancel();
            var retired=groupImageLifetime;
            groupImageLifetime=new();
            Task[] work;lock(groupImageWorkLock)work=groupImageWork.ToArray();
            _=Task.WhenAll(work).ContinueWith(done=>{_ = done.Exception;retired.Dispose();},TaskScheduler.Default);
            groupImageGeneration++; groupImageFileDialog.Reset(); groupImageSaveDialog.Reset(); }
        foreach (var key in groupImageDrafts.Keys.Where(k => clearAll || GroupChatClient.IsGroup(k) && !allowed.Contains(k)).ToArray()) { groupImageDrafts[key].Dispose(); groupImageDrafts.Remove(key); }
        foreach (var key in groupImageViews.Where(p => clearAll || GroupChatClient.IsGroup(p.Value.Conversation) && !allowed.Contains(p.Value.Conversation)).Select(p => p.Key).ToArray()) {
            groupImageViews[key].Dispose(); groupImageViews.Remove(key); if (groupImageViewer == key) groupImageViewer = null;
        }
    }
    private void DrawReportedGroupImage(CharacterSession session, GroupReport report)
    {
        if (!CanViewModeratorEvidence || report.MediaId is null) return;
        if (report.ImageDeleted) { ImGui.TextColored(MutedText, "Image Deleted"); return; }
        var view = ImageView("report:" + report.Id, "report:" + report.Id);
        using var disabled = UiDisabled.Scope(cloudGroupBusy);
        if (ImGui.SmallButton(view.Busy ? "Decrypting evidence…" : "View encrypted image evidence") && !view.Busy) {
            view.Busy = true; var generation = groupImageGeneration;var revision=view.Revision;
            RunCloudGroupAction(session, () => StartGroupImageWork(async cancellation => {
                byte[]? bytes = null; IDalamudTextureWrap? texture = null;var transferred=false;
                try {
                    bytes = await dmClient.Groups.ReviewImageAsync(session.Token, report); texture = await GroupImageTexture(bytes,cancellation);
                    await Plugin.Framework.RunOnFrameworkThread(() => {
                        if (!IsCurrentMessageSession(session) || !CanViewModeratorEvidence || generation != groupImageGeneration || view.Revision!=revision || view.Revoked) return;
                        view.Dispose(); view.Bytes = bytes; view.Texture = texture; transferred=true; groupImageViewer = "report:" + report.Id;
                        TrimGroupImageMemory("report:" + report.Id);
                    });
                } finally {if(!transferred){texture?.Dispose();if(bytes is not null)CryptographicOperations.ZeroMemory(bytes);}view.Busy=false;}
            },propagateErrors:true));
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Delete image, keep report open")) RunCloudGroupAction(session, async () => {
            await dmClient.Groups.DeleteReportedImageAsync(session.Token, report.Id);
            await Plugin.Framework.RunOnFrameworkThread(() => {
                cloudGroupReports = cloudGroupReports.Select(r => r.MediaId == report.MediaId ? r with { ImageDeleted = true } : r).ToArray();
                view.Revoked=true;view.Dispose(); groupImageViewer = null;
            });
        });
    }
}
