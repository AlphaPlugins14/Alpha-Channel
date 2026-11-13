using System.Globalization;
using System.Text.Json;

namespace AlphaChannel.Plugin.YouTube;

internal static class YouTubeFeedEntryMetadata
{
    internal static DateTimeOffset? UploadDate(JsonElement entry)
    {
        foreach (var field in new[] { "upload_date", "release_date" })
        {
            if (entry.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String &&
                DateTime.TryParseExact(value.GetString(), "yyyyMMdd", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
                return new DateTimeOffset(date, TimeSpan.Zero);
        }

        foreach (var field in new[] { "timestamp", "release_timestamp" })
        {
            if (entry.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt64(out var seconds))
            {
                try { return DateTimeOffset.FromUnixTimeSeconds(seconds); }
                catch (ArgumentOutOfRangeException) { }
            }
        }

        return null;
    }
}
