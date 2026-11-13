using System.Text.Json;

namespace AlphaChannel.Plugin.Crypto;

internal static class DmPrivateKeyBackupReader
{
    internal static string[] Read(string directory, string configurationName, string accountId)
    {
        var candidates = new List<string>();
        try
        {
            var prefix = configurationName + ".";
            var files = Directory.EnumerateFiles(directory, configurationName + ".*.json")
                .Where(path =>
                {
                    var suffix = Path.GetFileName(path)[prefix.Length..];
                    return suffix.StartsWith("pre-v", StringComparison.Ordinal) ||
                        (suffix.StartsWith("v", StringComparison.Ordinal) && suffix.Contains("-before-v", StringComparison.Ordinal));
                }).OrderByDescending(File.GetLastWriteTimeUtc).Take(50);
            foreach (var file in files)
            {
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(file));
                    if (!document.RootElement.TryGetProperty("DmPrivateKeys", out var keys) ||
                        keys.ValueKind != JsonValueKind.Object) continue;
                    foreach (var key in keys.EnumerateObject())
                        if (string.Equals(key.Name, accountId, StringComparison.OrdinalIgnoreCase) &&
                            key.Value.ValueKind == JsonValueKind.String && key.Value.GetString() is { Length: > 0 } value)
                            candidates.Add(value);
                }
                catch (Exception) { /* A damaged or inaccessible backup is not a recovery candidate. */ }
            }
        }
        catch (Exception) { /* Recovery is optional; keep the current configuration untouched. */ }
        return candidates.Distinct(StringComparer.Ordinal).ToArray();
    }
}
