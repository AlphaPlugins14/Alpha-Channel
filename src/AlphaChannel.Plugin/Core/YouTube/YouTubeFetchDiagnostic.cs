using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;

namespace AlphaChannel.Plugin.YouTube;

internal enum YouTubeFetchSection
{ Background, Popular, Recommended, Topics, Subscriptions, Likes, Search, Channel, Home, Playback, LinkPreview, PartyDirectory, Featured, Admin }
internal enum YouTubeFetchData { FullMetadata, TitleAndChannel, ViewCount, FeedEntries, VisibleQueue }
internal enum YouTubeFetchMethod { FullDetails, OEmbed, CloudflareClaim, CloudflareReport, CloudflareFeed, Queue, YtDlp, YoutubeExplodeNoCookie }
internal enum YouTubeFetchStage { Unspecified, SchedulerWait, FallbackSlotWait, Fetching }
internal enum YouTubeFetchFailure
{
    Unknown, Timeout, QueueTimeout, RequestPaused, RateLimited, BotCheckRequired, SignInRequired, AgeRestricted,
    VideoUnavailable, VideoUnplayable, PurchaseRequired, Forbidden, NotFound,
    UpstreamFailure, RequestConflict, HttpFailure, DnsFailure, ConnectionFailure,
    TlsFailure, InvalidResponse, InvalidInput, MissingUploadDate, MissingTitle,
    MissingChannel, MissingThumbnail, MissingVideoId, ResponseExtractionFailed,
    VisitorDataUnavailable, ProviderFailure, WatchPageUnavailable
}
internal enum YouTubeFetchExceptionKind
{ Other, Provider, Http, Cancelled, Timeout, Json, InvalidData, InvalidOperation, Argument, Socket, Tls }

// Raw provider messages are inspected only to select fixed reason labels.
// No URLs, cookies, tokens, response bodies or free-form strings enter the log.
internal readonly record struct YouTubeFetchDiagnostic(YouTubeFetchSection Section, YouTubeFetchData Data,
    YouTubeFetchMethod Method, YouTubeFetchFailure Failure, int HttpStatus, int ErrorCode,
    YouTubeFetchExceptionKind ExceptionKind = YouTubeFetchExceptionKind.Other,
    YouTubeFetchStage Stage = YouTubeFetchStage.Unspecified)
{
    internal string Format() => $"[YouTube/Metadata] section={Section}; fetch={Method}; data={Data}; stage={Stage}; reason={Failure}; exception={ExceptionKind}; http={HttpStatus}; error=0x{ErrorCode:X8}.";

    internal static YouTubeFetchDiagnostic Classify(YouTubeFetchSection section, YouTubeFetchData data,
        YouTubeFetchMethod method, Exception error, int httpStatus = 0)
    {
        var failure = YouTubeFetchFailure.Unknown;
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException { StatusCode: { } status }) httpStatus = (int)status;
            // These messages may contain private data; they are never formatted.
            var message = current.Message.Replace('\u2019', '\'');
            var type = current.GetType().Name;
            if (Contains(message, "confirm you are not a bot", "confirm you're not a bot", "bot detection", "LOGIN_REQUIRED: bot"))
                failure = YouTubeFetchFailure.BotCheckRequired;
            else if (failure == YouTubeFetchFailure.BotCheckRequired) { }
            else if (httpStatus == 429 || type == "RequestLimitExceededException" || Contains(message, "too many requests", "rate limit", "HTTP 429", "(429)"))
                failure = YouTubeFetchFailure.RateLimited;
            else if (failure == YouTubeFetchFailure.RateLimited) { }
            else if (Contains(message, "age-restricted", "age restricted", "confirm your age")) failure = YouTubeFetchFailure.AgeRestricted;
            else if (Contains(message, "sign in to confirm", "login required", "sign-in required")) failure = YouTubeFetchFailure.SignInRequired;
            else if (type == "VideoRequiresPurchaseException") failure = YouTubeFetchFailure.PurchaseRequired;
            else if (type == "VideoUnavailableException" || Contains(message, "video is unavailable", "video is private", "video has been removed")) failure = YouTubeFetchFailure.VideoUnavailable;
            else if (type == "VideoUnplayableException") failure = YouTubeFetchFailure.VideoUnplayable;
            else if (current is OperationCanceledException or TimeoutException) failure = YouTubeFetchFailure.Timeout;
            else if (current is AuthenticationException) failure = YouTubeFetchFailure.TlsFailure;
            else if (current is SocketException socket) failure = socket.SocketErrorCode is SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData
                ? YouTubeFetchFailure.DnsFailure : YouTubeFetchFailure.ConnectionFailure;
            else if (current is JsonException or InvalidDataException or FormatException or KeyNotFoundException || type.Contains("Parse", StringComparison.Ordinal)) failure = YouTubeFetchFailure.InvalidResponse;
            else if (current is ArgumentException) failure = YouTubeFetchFailure.InvalidInput;
            else if (Contains(message, "Failed to extract the video upload date")) failure = YouTubeFetchFailure.MissingUploadDate;
            else if (Contains(message, "Failed to extract the video title")) failure = YouTubeFetchFailure.MissingTitle;
            else if (Contains(message, "Failed to extract the video author", "Failed to extract the video channel ID")) failure = YouTubeFetchFailure.MissingChannel;
            else if (Contains(message, "Failed to extract the video thumbnail")) failure = YouTubeFetchFailure.MissingThumbnail;
            else if (Contains(message, "Failed to extract the video ID")) failure = YouTubeFetchFailure.MissingVideoId;
            else if (Contains(message, "Failed to resolve visitor data")) failure = YouTubeFetchFailure.VisitorDataUnavailable;
            else if (Contains(message, "Video watch page is broken")) failure = YouTubeFetchFailure.WatchPageUnavailable;
            else if (Contains(message, "Failed to extract", "Could not extract", "Failed to parse", "Could not parse")) failure = YouTubeFetchFailure.ResponseExtractionFailed;
            else if (type == "YoutubeExplodeException" && failure == YouTubeFetchFailure.Unknown) failure = YouTubeFetchFailure.ProviderFailure;
            else if (current is HttpRequestException http && httpStatus == 0)
                failure = http.HttpRequestError switch
                {
                    HttpRequestError.NameResolutionError => YouTubeFetchFailure.DnsFailure,
                    HttpRequestError.SecureConnectionError => YouTubeFetchFailure.TlsFailure,
                    HttpRequestError.ConnectionError => YouTubeFetchFailure.ConnectionFailure,
                    HttpRequestError.InvalidResponse or HttpRequestError.ResponseEnded => YouTubeFetchFailure.InvalidResponse,
                    _ => YouTubeFetchFailure.HttpFailure
                };
        }
        if (failure is YouTubeFetchFailure.Unknown or YouTubeFetchFailure.HttpFailure)
            failure = httpStatus switch
            {
                401 => YouTubeFetchFailure.SignInRequired, 403 => YouTubeFetchFailure.Forbidden,
                404 or 410 => YouTubeFetchFailure.NotFound, 409 => YouTubeFetchFailure.RequestConflict,
                429 => YouTubeFetchFailure.RateLimited, >= 500 => YouTubeFetchFailure.UpstreamFailure,
                > 0 => YouTubeFetchFailure.HttpFailure, _ => failure
            };
        var kind = error.GetType().Namespace?.StartsWith("YoutubeExplode", StringComparison.Ordinal) == true
            ? YouTubeFetchExceptionKind.Provider : error switch
            {
                HttpRequestException => YouTubeFetchExceptionKind.Http,
                OperationCanceledException => YouTubeFetchExceptionKind.Cancelled,
                TimeoutException => YouTubeFetchExceptionKind.Timeout,
                JsonException => YouTubeFetchExceptionKind.Json,
                InvalidDataException => YouTubeFetchExceptionKind.InvalidData,
                InvalidOperationException => YouTubeFetchExceptionKind.InvalidOperation,
                ArgumentException => YouTubeFetchExceptionKind.Argument,
                SocketException => YouTubeFetchExceptionKind.Socket,
                AuthenticationException => YouTubeFetchExceptionKind.Tls,
                _ => YouTubeFetchExceptionKind.Other
            };
        // A timeout may wrap a cancelled socket read. Preserve the outer cause
        // instead of relabeling its inner connection exception as the failure.
        if (error is TimeoutException) failure = YouTubeFetchFailure.Timeout;
        var stage = YouTubeFetchStage.Unspecified;
        if (error is YouTubeFetchCancelledException cancelled)
        {
            method = cancelled.Method;
            stage = cancelled.Stage;
            failure = cancelled.Paused ? YouTubeFetchFailure.RequestPaused :
                stage is YouTubeFetchStage.SchedulerWait or YouTubeFetchStage.FallbackSlotWait
                    ? YouTubeFetchFailure.QueueTimeout : YouTubeFetchFailure.Timeout;
        }
        return new(section, data, method, failure, httpStatus, error.HResult, kind, stage);
    }

    private static bool Contains(string message, params string[] fragments) =>
        fragments.Any(fragment => message.Contains(fragment, StringComparison.OrdinalIgnoreCase));
}

// Carries only fixed fetch context through shared-cache waits. It remains a
// cancellation so navigation/shutdown cancellation is still suppressed by callers.
internal sealed class YouTubeFetchCancelledException(YouTubeFetchMethod method, YouTubeFetchStage stage,
    bool paused, OperationCanceledException inner) : OperationCanceledException("YouTube metadata request cancelled.", inner, inner.CancellationToken)
{
    internal YouTubeFetchMethod Method { get; } = method;
    internal YouTubeFetchStage Stage { get; } = stage;
    internal bool Paused { get; } = paused;
}
