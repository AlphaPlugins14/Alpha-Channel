using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using AlphaChannel.Plugin.Net;
using AlphaChannel.Plugin.YouTube;
namespace AlphaChannel.Plugin.Video;
internal sealed record WatchHistoryEntry(string Key,string Provider,string VideoId,string? EpisodeKey,string Title,
    string Creator,string? ThumbnailUrl,double? DurationSeconds,long WatchedAtUnix)
{
    internal string Url => Provider switch
    {
        "youtube" => "https://www.youtube.com/watch?v="+VideoId,
        "dailymotion" => "https://www.dailymotion.com/video/"+VideoId,
        _ => "https://archive.org/download/"+Uri.EscapeDataString(VideoId)+"/"+string.Join("/",(EpisodeKey??"").Split('/').Select(Uri.EscapeDataString)),
    };
    internal string Source => Provider switch { "youtube"=>"YouTube", "dailymotion"=>"Dailymotion", _=>"Internet Archive" };
    internal TimeSpan? Duration => DurationSeconds is >0 and <=31536000 ? TimeSpan.FromSeconds(DurationSeconds.Value):null;
}
internal sealed record WatchHistoryPlay(string Provider,string VideoId,string? EpisodeKey,string Title,string Creator,
    string? ThumbnailUrl,double? DurationSeconds,string EventId,double WatchedSeconds,long QualifiedAtUnix);
internal sealed record WatchHistoryPage(string Visibility,bool Hidden,WatchHistoryEntry[] Items,int? NextOffset);
internal sealed record WatchHistoryPrivacy(string Visibility);
internal sealed class WatchHistoryClient(Func<HttpClient>? factory=null)
{
    internal const string WorkerUrl="https://watch-history.example.invalid";
    private readonly Func<HttpClient> create=factory??PluginHttpClients.CreateMetadataClient;
    internal static bool ValidVisibility(string? value)=>value is "everyone" or "friends" or "none";
    internal static (string Provider,string Id,string? Episode)? Identity(string? url)
    {
        if(!Uri.TryCreate(url,UriKind.Absolute,out var uri)||uri.Scheme is not("https" or "http")||!string.IsNullOrEmpty(uri.UserInfo))return null;
        if(ArchivePopularityClient.Identity(url) is {} archive)return ("archive",archive.Id,archive.Episode);
        if(DailymotionClient.VideoId(url) is {} dm)return ("dailymotion",dm,null);
        var parts=uri.AbsolutePath.Split('/',StringSplitOptions.RemoveEmptyEntries);
        string? id=null;
        if(uri.Host=="youtu.be"&&parts.Length==1)id=parts[0];
        else if(uri.Host=="youtube.com"||uri.Host=="www.youtube.com"||uri.Host=="m.youtube.com"||uri.Host=="music.youtube.com")
        {
            if(parts.Length>=2&&parts[0] is "embed" or "shorts" or "live")id=parts[1];
            else if(parts.Length==1&&parts[0]=="watch")
                id=uri.Query.TrimStart('?').Split('&').Select(s=>s.Split('=',2)).FirstOrDefault(p=>p.Length==2&&p[0]=="v")?.Last();
        }
        return PopularWorkerClient.ValidVideoId(id)?("youtube",id!,null):null;
    }
    private HttpClient Http(string token)
    {
        var http=create();http.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",token);return http;
    }
    internal async Task<WatchHistoryPrivacy> PrivacyAsync(string token,string? value,CancellationToken ct)
    {
        using var http=Http(token);
        using var response=value is null?await http.GetAsync(WorkerUrl+"/me/settings",ct).ConfigureAwait(false)
            :await http.PutAsJsonAsync(WorkerUrl+"/me/settings",new {visibility=value},ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var privacy=await response.Content.ReadFromJsonAsync<WatchHistoryPrivacy>(cancellationToken:ct).ConfigureAwait(false);
        if(!ValidVisibility(privacy?.Visibility))throw new InvalidDataException("Invalid watch history privacy.");
        return privacy!;
    }
    internal async Task<WatchHistoryPage> GetAsync(string token,string accountId,int offset,CancellationToken ct)
    {
        if(!Guid.TryParse(accountId,out _)||offset is <0 or >100)throw new ArgumentException("Invalid history request.");
        using var http=Http(token);
        var page=await http.GetFromJsonAsync<WatchHistoryPage>(WorkerUrl+"/profiles/"+Uri.EscapeDataString(accountId)+"/history?offset="+offset,ct).ConfigureAwait(false);
        if(page is null||!ValidVisibility(page.Visibility)||page.Items is null||page.Items.Length>20||page.NextOffset is <0 or >100||page.NextOffset is {} next&&next<=offset||
            page.Hidden&&(page.Items.Length>0||page.NextOffset is not null)||page.Items.Any(e=>e is null||!ValidEntry(e)))throw new InvalidDataException("Invalid watch history response.");
        return page;
    }
    private static bool ValidEntry(WatchHistoryEntry e)
    {
        bool Text(string? s,int max)=>s is not null&&s.Length<=max&&!s.Any(char.IsControl);
        if(!Text(e.Key,2048)||!Text(e.Title,512)||!Text(e.Creator,256)||e.WatchedAtUnix<0||e.WatchedAtUnix>DateTimeOffset.UtcNow.ToUnixTimeSeconds()+60||
            e.DurationSeconds is {} d&&(!double.IsFinite(d)||d<=0||d>31536000))return false;
        var identity=Identity(e.Url);
        return identity is {} value&&value.Provider==e.Provider&&value.Id==e.VideoId&&value.Episode==e.EpisodeKey&&
            (e.ThumbnailUrl is null||SafeThumbnail(e.ThumbnailUrl,e.Provider));
    }
    private static bool SafeThumbnail(string url,string provider)
    {
        if(!Uri.TryCreate(url,UriKind.Absolute,out var u)||u.Scheme!="https"||u.UserInfo.Length>0||u.Port!=443)return false;
        return provider switch { "youtube"=>Regex.IsMatch(u.Host,"^(i[0-9]*\\.ytimg\\.com|img\\.youtube\\.com)$"),
            "dailymotion"=>u.Host=="dmcdn.net"||u.Host.EndsWith(".dmcdn.net",StringComparison.OrdinalIgnoreCase),
            _=>u.Host=="archive.org"||u.Host.EndsWith(".archive.org",StringComparison.OrdinalIgnoreCase) };
    }
    internal async Task<PopularSubmitResult> SubmitAsync(string token,WatchHistoryPlay play,CancellationToken ct)
    {
        using var http=Http(token);
        using var response=await http.PostAsJsonAsync(WorkerUrl+"/me/history",play,ct).ConfigureAwait(false);
        var status=(int)response.StatusCode;
        if(response.IsSuccessStatusCode)
        {
            var result=await response.Content.ReadFromJsonAsync<Reply>(cancellationToken:ct).ConfigureAwait(false);
            if(result?.Accepted==true||result?.Duplicate==true||result?.Ignored==true)return new(true,result.Accepted,result.Duplicate,status,null);
            return new(false,false,false,status,"invalid_response");
        }
        return new(status is >=400 and <500&&status is not(408 or 429),false,false,status,"history_submission_failed");
    }
    internal async Task RemoveAsync(string token,string accountId,string key,CancellationToken ct)
    {
        if(!Guid.TryParse(accountId,out _)||string.IsNullOrWhiteSpace(key)||key.Length>4096)throw new ArgumentException("Invalid history removal.");
        using var http=Http(token);
        using var request=new HttpRequestMessage(HttpMethod.Delete,WorkerUrl+"/profiles/"+Uri.EscapeDataString(accountId)+"/history")
        {Content=JsonContent.Create(new {key})};
        using var response=await http.SendAsync(request,ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var result=await response.Content.ReadFromJsonAsync<RemovalReply>(cancellationToken:ct).ConfigureAwait(false);
        if(result?.Removed!=true)throw new InvalidDataException("History removal wasn't confirmed.");
    }
    private sealed record RemovalReply(bool Removed);
    private sealed record Reply(bool Accepted,bool Duplicate,bool Ignored = false);
}
