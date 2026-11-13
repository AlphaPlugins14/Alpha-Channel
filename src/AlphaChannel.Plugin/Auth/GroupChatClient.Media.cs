using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Crypto;

namespace AlphaChannel.Plugin.Auth;

internal sealed record GroupImageStatus(long ExpiresAt, bool Available, bool ImageDeleted = false, bool DeliveryComplete = false);
internal sealed record GroupImageReportEvidence(GroupChatCrypto.Payload Payload);

internal sealed partial class GroupChatClient
{
    internal const int MaxImageBytes = 3 * 1024 * 1024;
    private const string ImageMarker = "AC-IMAGE-1:";
    private static string DisplayPayload(GroupChatCrypto.Payload payload) => payload.Image is null ? payload.Text : ImageMarker + JsonSerializer.Serialize(payload.Image, Json);
    internal Task<GroupImageStorage> ImageStorageAsync(string token) => Use(token, () => Request<GroupImageStorage>("/admin/media/storage", HttpMethod.Get));
    internal Task<GroupImageAllowance> ImageAllowanceAsync(string token) => Use(token, () => Request<GroupImageAllowance>("/media/allowance", HttpMethod.Get));
    internal static GroupImageContent? ImageContent(string? text)
    {
        if (text is null || !text.StartsWith(ImageMarker, StringComparison.Ordinal) || text.Length > 18000) return null;
        try {
            var content = JsonSerializer.Deserialize<GroupImageContent>(text[ImageMarker.Length..], Json);
            if (content?.Text is not { Length: <= 2000 } || content.Image is not { } image || !Guid.TryParseExact(image.Id, "D", out _) || !Guid.TryParseExact(image.GroupId, "D", out _) ||
                image.Width <= 0 || image.Height <= 0 || image.Width > 3840 || image.Height > 3840 || image.Format != "webp" ||
                image.Digest is not { Length: 64 } || image.Digest.Any(c => !char.IsAsciiHexDigit(c)) ||
                Convert.FromBase64String(image.Key).Length != 32 || Convert.FromBase64String(image.Nonce).Length != 12 || Convert.FromBase64String(image.Tag).Length != 16) return null;
            return content;
        } catch (Exception e) when (e is JsonException or FormatException or ArgumentException) { return null; }
    }
    internal static string ImageCaption(string? text) => ImageContent(text) is { } content ? (string.IsNullOrWhiteSpace(content.Text) ? "Image" : content.Text) : text ?? "";
    private static string ImageContext(GroupImage image) => $"{(image.Direct ? "AC-DM-IMAGE-1" : "AC-GROUP-IMAGE-1")}\n{image.GroupId}\n{image.MessageId}\n{image.Id}\n{image.SenderId}\n{image.DeviceId}";
    private static void CheckImage(GroupMessage m, string group, GroupImage image)
    {
        if (m.MediaId != image.Id || m.MediaDigest != image.Digest || m.Id != image.MessageId || group != image.GroupId || m.SenderAccountId != image.SenderId || m.DeviceId != image.DeviceId)
            throw new CryptographicException("Image does not match the signed message.");
    }
    private static byte[] OpenImage(GroupImage image, byte[] bytes)
    {
        if (bytes.Length > MaxImageBytes || Convert.ToHexString(SHA256.HashData(bytes)) != image.Digest) throw new CryptographicException("Image integrity check failed.");
        var key = Convert.FromBase64String(image.Key);
        try { return GroupChatCrypto.Open(key, new(Convert.ToBase64String(bytes), image.Nonce, image.Tag), ImageContext(image)); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
    internal Task<(MessageDto Message, string Plaintext)> SendImageAsync(string token, string conversation, string caption, byte[] webp, int width, int height, string? replyTo = null) => Use(token, async () => {
        if (!state.Groups.Any(g => g.Id == Raw(conversation) && !g.IsPublic)) throw new InvalidOperationException("Direct image uploading is only available in private groups.");
        if (webp.Length > MaxImageBytes || width <= 0 || height <= 0 || width > 3840 || height > 3840) throw new InvalidOperationException("Image is too large.");
        var group = Raw(conversation); var messageId = Guid.NewGuid().ToString(); var imageId = Guid.NewGuid().ToString();
        var key = GroupChatCrypto.RandomKey();
        var descriptor = new GroupImage(imageId, group, messageId, session!.AccountId, deviceId!, "", "", "", "", width, height);
        try {
            var sealedImage = GroupChatCrypto.Seal(key, webp, ImageContext(descriptor));
            var bytes = Convert.FromBase64String(sealedImage.Ciphertext);
            descriptor = descriptor with { Key = Convert.ToBase64String(key), Nonce = sealedImage.Nonce, Tag = sealedImage.Tag, Digest = Convert.ToHexString(SHA256.HashData(bytes)) };
            var route = $"/groups/{group}/media/{imageId}/";
            var query = "?messageId=" + messageId;
            await Request<JsonElement>(route + "prepare" + query, HttpMethod.Post, new { size = bytes.Length, digest = descriptor.Digest }).ConfigureAwait(false);
            try {
                using var request = new HttpRequestMessage(HttpMethod.Put, origin + route + "upload" + query);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
                request.Content = new ByteArrayContent(bytes); request.Content.Headers.ContentType = new("application/octet-stream");
                using var response = await Transfer(ct => http.SendAsync(request,ct)).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Image upload failed. Please retry.");
                var text = ImageMarker + JsonSerializer.Serialize(new GroupImageContent(caption, descriptor, replyTo), Json);
                var sent = await SendCore(conversation, text, messageId, imageId, descriptor.Digest).ConfigureAwait(false);
                try { vault!.StoreMedia(imageId, bytes); await AcknowledgeImage(group,messageId,imageId).ConfigureAwait(false); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* The posted image can still be downloaded. */ }
                return (sent, text);
            } catch {
                // An uncertain send can already have succeeded: cancel never revokes a posted attachment.
                try { await Request<JsonElement>(route + "cancel" + query, HttpMethod.Post, new { }).ConfigureAwait(false); } catch { }
                throw;
            }
        } finally { CryptographicOperations.ZeroMemory(key); }
    });
    private (string Group, GroupMessage Message, GroupImage Image) ImagePacket(MessageDto message)
    {
        var (group, m) = Packet(message);
        if (!state.Groups.Any(g => g.Id == group && !g.IsPublic && m.Sequence >= g.JoinedSequence)) throw new InvalidOperationException("You no longer have access to this image.");
        var image = ImageContent(DisplayPayload(Decrypt(message)))?.Image ?? throw new InvalidOperationException("Invalid image message.");
        if (image.Direct) throw new CryptographicException("Wrong image scope.");
        CheckImage(m, group, image); return (group, m, image);
    }
    internal Task<byte[]?> CachedImageAsync(string token, MessageDto message) => Use<byte[]?>(token, async () => {
        var (group, m, image) = ImagePacket(message); var bytes = vault!.ReadMedia(image.Id);
        if (bytes is null) return null;
        var plain = OpenImage(image,bytes);
        await AcknowledgeImage(group,m.Id,image.Id).ConfigureAwait(false);
        return plain;
    });
    internal Task<bool> ForgetCachedImageAsync(string token, MessageDto message) => Use(token, () => {
        var (_, _, image) = ImagePacket(message); vault!.DeleteMedia(image.Id); return Task.FromResult(true);
    });
    internal Task<GroupImageStatus> ImageStatusAsync(string token, MessageDto message) => Use(token, async () => {
        var (group, m, image) = ImagePacket(message);
        var status = await Request<GroupImageStatus>($"/groups/{group}/media/{image.Id}/status?messageId={m.Id}", HttpMethod.Get).ConfigureAwait(false);
        if (status.ImageDeleted) {revokedMedia.Add(image.Id);vault!.DeleteMedia(image.Id);}
        return status;
    });
    internal Task<byte[]> DownloadImageAsync(string token, MessageDto message) => Use(token, async () => {
        var (group, m, image) = ImagePacket(message);
        var bytes = await DownloadCiphertext($"/groups/{group}/media/{image.Id}/download?messageId={m.Id}").ConfigureAwait(false);
        // Membership can change while ciphertext is transferred outside the state lock.
        // Revalidate the current history boundary before decrypting or persisting it.
        _ = ImagePacket(message);
        if(revokedMedia.Contains(image.Id))throw new InvalidOperationException("Expired image");
        var plain = OpenImage(image, bytes);
        try { vault!.StoreMedia(image.Id, bytes); } catch { CryptographicOperations.ZeroMemory(plain); throw; }
        await AcknowledgeImage(group,m.Id,image.Id).ConfigureAwait(false);
        return plain;
    });
    private async Task AcknowledgeImage(string group,string messageId,string imageId)
    {
        // A lost acknowledgement leaves the server copy intact; retry when the local
        // encrypted cache is opened again. Never acknowledge an unverified or unsaved image.
        try { await Request<JsonElement>($"/groups/{group}/media/{imageId}/ack?messageId={messageId}",HttpMethod.Post,new { }).ConfigureAwait(false); }
        catch (Exception e) when (e is InvalidOperationException or HttpRequestException or TaskCanceledException) { }
    }
    private async Task<byte[]> DownloadCiphertext(string path)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, origin + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session!.Token);
        return await Transfer(async cancellation => {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var ct = timeout.Token;
        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead,ct).ConfigureAwait(false);
        if (res.StatusCode == HttpStatusCode.Gone) throw new InvalidOperationException("Expired image");
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException("Image unavailable. Please retry when connected.");
        if (res.Content.Headers.ContentLength > MaxImageBytes) throw new InvalidDataException("Image is too large.");
        await using var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new MemoryStream(); var buffer = new byte[16384]; int read;
        while ((read = await stream.ReadAsync(buffer,ct).ConfigureAwait(false)) > 0) {
            if (output.Length + read > MaxImageBytes) throw new InvalidDataException("Image is too large.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
        }).ConfigureAwait(false);
    }
    private async Task<bool> ReportMediaCore(string group, GroupMessage m, GroupChatCrypto.Payload payload)
    {
        var recipients = (await Request<GroupDevices>("/media/moderators", HttpMethod.Get).ConfigureAwait(false)).Devices;
        if (recipients.Length == 0) throw new InvalidOperationException("No moderator encryption identity is available. Please retry after a moderator has opened group chats.");
        var reportId = Guid.NewGuid().ToString(); var key = GroupChatCrypto.RandomKey();
        var plain = JsonSerializer.SerializeToUtf8Bytes(new GroupImageReportEvidence(payload), Json);
        try {
            foreach (var recipient in recipients) vault!.Pin($"enc:{recipient.AccountId}:{recipient.DeviceId}", recipient.EncryptionKey);
            var evidence = GroupChatCrypto.Seal(key, plain, "AC-GROUP-IMAGE-REPORT-1\n" + reportId);
            var packages = recipients.Select(d => GroupChatCrypto.Wrap(reportId, vault!.Identity, d, key)).ToArray();
            var reportBody = new { id = reportId, messageId = m.Id, evidence, packages };
            try { await Request<JsonElement>($"/groups/{group}/report",HttpMethod.Post,reportBody).ConfigureAwait(false); }
            catch (RequestFailure e) when (e.Status == HttpStatusCode.Gone) {
                // Everyone may already have cached it and released the server copy.
                // Re-submit only its original authenticated ciphertext for encrypted evidence.
                var ciphertext = vault!.ReadMedia(m.MediaId!) ?? throw new InvalidOperationException("Report this image from the device where it was downloaded; the server copy has been removed.");
                if (ciphertext.Length > MaxImageBytes || Convert.ToHexString(SHA256.HashData(ciphertext)) != m.MediaDigest) throw new CryptographicException("Cached image evidence failed its integrity check.");
                using var upload = new HttpRequestMessage(HttpMethod.Put,origin+$"/groups/{group}/media/{m.MediaId}/report-upload?messageId={m.Id}");
                upload.Headers.Authorization = new AuthenticationHeaderValue("Bearer",session!.Token);
                upload.Content = new ByteArrayContent(ciphertext); upload.Content.Headers.ContentType = new("application/octet-stream");
                using var response = await Transfer(ct => http.SendAsync(upload,ct)).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException("The encrypted image could not be retained for this report. Please retry.");
                await Request<JsonElement>($"/groups/{group}/report",HttpMethod.Post,reportBody).ConfigureAwait(false);
            }
            return true;
        } finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plain); }
    }
    internal Task<byte[]> ReviewImageAsync(string token, GroupReport report) => Use(token, async () => {
        var packet = report.Packages?.FirstOrDefault(p => p.AccountId == session!.AccountId && p.DeviceId == deviceId)
            ?? throw new InvalidOperationException("This report was encrypted for another moderator installation. Use an installation registered when it was reported.");
        vault!.Pin($"enc:{packet.SenderAccountId}:{packet.SenderDeviceId}", packet.SenderEncryptionKey!);
        var key = GroupChatCrypto.Unwrap(report.Id, vault.Identity, packet);
        try {
            var plain = GroupChatCrypto.Open(key, report.Evidence!, "AC-GROUP-IMAGE-REPORT-1\n" + report.Id);
            GroupImageReportEvidence evidence;
            try { evidence = JsonSerializer.Deserialize<GroupImageReportEvidence>(plain, Json)!; } finally { CryptographicOperations.ZeroMemory(plain); }
            var m = report.Message!;
            if (report.Direct) {
                var text = evidence.Payload.Text;
                var directImage = ImageContent(text)?.Image ?? throw new CryptographicException("Invalid reported image.");
                if (!directImage.Direct || m.Ciphertext != Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))) ||
                    !GroupChatCrypto.Verify(m.SigningKey, GroupChatCrypto.SignedText("dm:" + report.GroupId, m), m.Signature))
                    throw new CryptographicException("Reported image did not match the sender's signed attachment.");
                CheckImage(m, report.GroupId, directImage);
                return OpenImage(directImage, await DownloadCiphertext("/moderation/image?id=" + report.Id).ConfigureAwait(false));
            }
            var franking = Convert.FromBase64String(evidence.Payload.FrankingKey);
            try {
                if (!CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(franking, Encoding.UTF8.GetBytes(DisplayPayload(evidence.Payload))), Convert.FromBase64String(m.Commitment)) ||
                    !GroupChatCrypto.Verify(m.SigningKey, GroupChatCrypto.SignedText(report.GroupId, m), m.Signature)) throw new CryptographicException("Report evidence did not match the original message.");
            } finally { CryptographicOperations.ZeroMemory(franking); }
            var image = ImageContent(DisplayPayload(evidence.Payload))?.Image ?? throw new CryptographicException("Invalid reported image.");
            CheckImage(m, report.GroupId, image);
            return OpenImage(image, await DownloadCiphertext("/moderation/image?id=" + report.Id).ConfigureAwait(false));
        } finally { CryptographicOperations.ZeroMemory(key); }
    });
    internal Task<bool> DeleteReportedImageAsync(string token, string reportId) => Use(token, async () => {
        await Request<JsonElement>("/moderation/image", HttpMethod.Post, new { id = reportId }).ConfigureAwait(false); return true;
    });
}
