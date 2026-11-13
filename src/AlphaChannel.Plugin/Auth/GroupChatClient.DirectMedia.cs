using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AlphaChannel.Contracts;
using AlphaChannel.Plugin.Crypto;

namespace AlphaChannel.Plugin.Auth;

internal sealed partial class GroupChatClient
{
    private static string DirectRoute(GroupImage image) => $"/direct/{image.GroupId}/media/{image.Id}/";
    private static string DirectQuery(GroupImage image) => "?messageId=" + image.MessageId;
    private GroupImage DirectImage(string conversation, MessageDto message, string plaintext)
    {
        var image = ImageContent(plaintext)?.Image ?? throw new CryptographicException("Invalid image message.");
        if (IsMessage(message) || !image.Direct || !Guid.TryParseExact(conversation,"D",out _) || image.GroupId != conversation ||
            message.SenderAccountId != image.SenderId || session!.AccountId != message.SenderAccountId && session.AccountId != message.RecipientAccountId)
            throw new CryptographicException("Image does not match this direct conversation.");
        return image;
    }

    // Commit signed ciphertext metadata before relaying the descriptor through the
    // unchanged DM server. A recipient can fetch immediately on receiving the DM.
    // Uncertain relay failures retain the encrypted copy until expiry; never delete
    // an image that may already have been delivered by the account server.
    internal Task<(MessageDto Message,string Plaintext)> SendDirectImageAsync(string token,string conversation,string caption,
        byte[] webp,int width,int height,Func<string,Task<MessageDto?>> relay,string? replyTo = null) => Use(token,async () => {
        if (!Guid.TryParseExact(conversation,"D",out _) || webp.Length > MaxImageBytes || width <= 0 || height <= 0 || width > 3840 || height > 3840)
            throw new InvalidOperationException("Invalid direct image.");
        var fingerprint=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new{conversation,caption,replyTo,digest=Convert.ToHexString(SHA256.HashData(webp))},Json)));
        if(vault!.PendingDirect(fingerprint) is { } pending) return await ResumeDirectImage(fingerprint,pending,relay).ConfigureAwait(false);
        var key = GroupChatCrypto.RandomKey();
        var descriptor = new GroupImage(Guid.NewGuid().ToString(),conversation,Guid.NewGuid().ToString(),session!.AccountId,deviceId!,"","","","",width,height,Direct:true);
        try {
            var sealedImage = GroupChatCrypto.Seal(key,webp,ImageContext(descriptor));
            var bytes = Convert.FromBase64String(sealedImage.Ciphertext);
            descriptor = descriptor with { Key=Convert.ToBase64String(key),Nonce=sealedImage.Nonce,Tag=sealedImage.Tag,Digest=Convert.ToHexString(SHA256.HashData(bytes)) };
            var route=DirectRoute(descriptor); var query=DirectQuery(descriptor);
            await Request<JsonElement>(route+"prepare"+query,HttpMethod.Post,new { size=bytes.Length,digest=descriptor.Digest },sourceToken:token).ConfigureAwait(false);
            var committed=false;
            try {
                using var upload=new HttpRequestMessage(HttpMethod.Put,origin+route+"upload"+query);
                upload.Headers.Authorization=new AuthenticationHeaderValue("Bearer",session.Token);
                upload.Content=new ByteArrayContent(bytes); upload.Content.Headers.ContentType=new("application/octet-stream");
                using var response=await Transfer(ct => http.SendAsync(upload,ct)).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Image upload failed. Please retry.");
                var text=ImageMarker+JsonSerializer.Serialize(new GroupImageContent(caption,descriptor,replyTo),Json);
                var proof=new GroupMessage(descriptor.MessageId,descriptor.SenderId,descriptor.DeviceId,
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))),"","","","",GroupChatCrypto.SigningPublicKey(vault!.SigningKey),
                    MediaId:descriptor.Id,MediaDigest:descriptor.Digest);
                proof=proof with { Signature=GroupChatCrypto.Sign(vault.SigningKey,GroupChatCrypto.SignedText("dm:"+conversation,proof)) };
                // Persist the exact descriptor before the possibly uncertain commit/relay.
                // A retry reuses its image slot and cryptographic identity, including after restart.
                vault.StoreMedia(descriptor.Id,bytes);
                var now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var pendingSend=new PendingDirectImage(text,proof.Signature,proof.Ciphertext,now+48*3600,now);
                vault.StorePendingDirect(fingerprint,pendingSend);
                committed=true; // May commit remotely even if its response is lost; retain for reconciliation.
                return await ResumeDirectImage(fingerprint,pendingSend,relay).ConfigureAwait(false);
            } catch {
                if (!committed) try { await Request<JsonElement>(route+"cancel"+query,HttpMethod.Post,new { }).ConfigureAwait(false); } catch { }
                throw;
            }
        } finally { CryptographicOperations.ZeroMemory(key); }
    });

    private async Task<(MessageDto Message,string Plaintext)> ResumeDirectImage(string fingerprint,PendingDirectImage pending,Func<string,Task<MessageDto?>> relay)
    {
        var image=ImageContent(pending.Text)?.Image ?? throw new CryptographicException("Invalid pending image.");
        if(pending.ExpiresAt<=DateTimeOffset.UtcNow.ToUnixTimeSeconds()) {vault!.ForgetPendingDirect(fingerprint);throw new InvalidOperationException("The previous image expired. Send it again to start a new upload.");}
        GroupImageStatus status;
        try {status=await Request<GroupImageStatus>(DirectRoute(image)+"status"+DirectQuery(image),HttpMethod.Get).ConfigureAwait(false);}
        catch(RequestFailure e)when(e.Status==HttpStatusCode.Gone){vault!.ForgetPendingDirect(fingerprint);throw new InvalidOperationException("The previous image expired. Retry to upload it again.");}
        if(status.ImageDeleted) {vault!.ForgetPendingDirect(fingerprint);throw new InvalidOperationException("This image was deleted. Remove the attachment before posting another copy.");}
        if(!status.DeliveryComplete) await Request<JsonElement>(DirectRoute(image)+"commit"+DirectQuery(image),HttpMethod.Post,new{payloadDigest=pending.PayloadDigest,signature=pending.Signature}).ConfigureAwait(false);
        var sent=await relay(pending.Text).ConfigureAwait(false) ?? throw new InvalidOperationException("Image message delivery could not be confirmed. Retrying will reuse this image upload and slot.");
        vault!.RememberDirectMedia(image.GroupId,sent.GroupId,image.Id);
        vault.ForgetPendingDirect(fingerprint);
        return (sent,pending.Text);
    }

    internal Task<byte[]?> CachedDirectImageAsync(string token,string conversation,MessageDto message,string plaintext) => Use<byte[]?>(token,async () => {
        var image=DirectImage(conversation,message,plaintext); vault!.RememberDirectMedia(conversation,message.GroupId,image.Id); var bytes=vault.ReadMedia(image.Id);
        if(bytes is null) return null;
        if(revokedMedia.Contains(image.Id))throw new InvalidOperationException("Expired image");
        var plain=OpenImage(image,bytes);
        if(session!.AccountId != image.SenderId) await AcknowledgeDirectImage(image).ConfigureAwait(false);
        return plain;
    });
    internal Task<byte[]> DownloadDirectImageAsync(string token,string conversation,MessageDto message,string plaintext) => Use(token,async () => {
        var image=DirectImage(conversation,message,plaintext);
        vault!.RememberDirectMedia(conversation,message.GroupId,image.Id);
        var bytes=await DownloadCiphertext(DirectRoute(image)+"download"+DirectQuery(image)).ConfigureAwait(false);
        if(revokedMedia.Contains(image.Id))throw new InvalidOperationException("Expired image");
        var plain=OpenImage(image,bytes);
        try { vault!.StoreMedia(image.Id,bytes); } catch { CryptographicOperations.ZeroMemory(plain); throw; }
        if(session!.AccountId != image.SenderId) await AcknowledgeDirectImage(image).ConfigureAwait(false);
        return plain;
    });
    internal Task<bool> ForgetDirectMessageImageAsync(string token,string conversation,string message) => Use(token,()=> {
        vault!.ForgetDirectMessageMedia(conversation,message);return Task.FromResult(true);
    });
    private async Task AcknowledgeDirectImage(GroupImage image)
    {
        try { await Request<JsonElement>(DirectRoute(image)+"ack"+DirectQuery(image),HttpMethod.Post,new { }).ConfigureAwait(false); }
        catch(Exception e) when(e is InvalidOperationException or HttpRequestException or TaskCanceledException) { }
    }
    internal Task<GroupImageStatus> DirectImageStatusAsync(string token,string conversation,MessageDto message,string plaintext) => Use(token,async () => {
        var image=DirectImage(conversation,message,plaintext);
        var status=await Request<GroupImageStatus>(DirectRoute(image)+"status"+DirectQuery(image),HttpMethod.Get).ConfigureAwait(false);
        if(status.ImageDeleted) {revokedMedia.Add(image.Id);vault!.DeleteMedia(image.Id);}
        return status;
    });
    internal Task<bool> ForgetDirectImageAsync(string token,string conversation,MessageDto message,string plaintext) => Use(token,() => {
        var image=DirectImage(conversation,message,plaintext); vault!.DeleteMedia(image.Id); return Task.FromResult(true);
    });
    internal Task<bool> RevokeDirectImageAsync(string token,string conversation,MessageDto message,string plaintext) => Use(token,async () => {
        var image=DirectImage(conversation,message,plaintext);
        if(image.SenderId!=session!.AccountId) throw new InvalidOperationException("Only the sender can delete this image.");
        try { await Request<JsonElement>(DirectRoute(image)+"revoke"+DirectQuery(image),HttpMethod.Post,new { }).ConfigureAwait(false); }
        catch(RequestFailure e) when(e.Status==HttpStatusCode.Gone) { }
        vault!.DeleteMedia(image.Id); return true;
    });
    internal Task<bool> ReportDirectImageAsync(string token,string conversation,MessageDto message,string plaintext) => Use(token,async () => {
        var image=DirectImage(conversation,message,plaintext); var route=DirectRoute(image); var query=DirectQuery(image);
        var proof=await Request<GroupMessage>(route+"proof"+query,HttpMethod.Get).ConfigureAwait(false);
        if(proof.Ciphertext!=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plaintext))) ||
            !GroupChatCrypto.Verify(proof.SigningKey,GroupChatCrypto.SignedText("dm:"+conversation,proof),proof.Signature))
            throw new CryptographicException("Image report did not match the sender's signed attachment.");
        CheckImage(proof,conversation,image);
        var recipients=(await Request<GroupDevices>("/media/moderators",HttpMethod.Get).ConfigureAwait(false)).Devices;
        if(recipients.Length==0) throw new InvalidOperationException("No moderator encryption identity is available. Please retry later.");
        var reportId=Guid.NewGuid().ToString(); var key=GroupChatCrypto.RandomKey();
        var plain=JsonSerializer.SerializeToUtf8Bytes(new GroupImageReportEvidence(new GroupChatCrypto.Payload(plaintext,"")),Json);
        try {
            foreach(var recipient in recipients) vault!.Pin($"enc:{recipient.AccountId}:{recipient.DeviceId}",recipient.EncryptionKey);
            var evidence=GroupChatCrypto.Seal(key,plain,"AC-GROUP-IMAGE-REPORT-1\n"+reportId);
            var packages=recipients.Select(d=>GroupChatCrypto.Wrap(reportId,vault!.Identity,d,key)).ToArray();
            var data=new { id=reportId,evidence,packages };
            try { await Request<JsonElement>(route+"report"+query,HttpMethod.Post,data).ConfigureAwait(false); }
            catch(RequestFailure e) when(e.Status==HttpStatusCode.Gone) {
                var ciphertext=vault!.ReadMedia(image.Id) ?? throw new InvalidOperationException("Report this image from the device where it was downloaded.");
                if(ciphertext.Length>MaxImageBytes || Convert.ToHexString(SHA256.HashData(ciphertext))!=image.Digest) throw new CryptographicException("Cached image evidence failed verification.");
                using var upload=new HttpRequestMessage(HttpMethod.Put,origin+route+"report-upload"+query);
                upload.Headers.Authorization=new AuthenticationHeaderValue("Bearer",session!.Token);
                upload.Content=new ByteArrayContent(ciphertext); upload.Content.Headers.ContentType=new("application/octet-stream");
                using var response=await Transfer(ct => http.SendAsync(upload,ct)).ConfigureAwait(false);
                if(!response.IsSuccessStatusCode) throw new InvalidOperationException("The encrypted image could not be retained for this report. Please retry.");
                await Request<JsonElement>(route+"report"+query,HttpMethod.Post,data).ConfigureAwait(false);
            }
            return true;
        } finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plain); }
    });
}
