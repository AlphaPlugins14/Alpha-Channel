using Dalamud.Interface.Textures.TextureWraps;

namespace AlphaChannel.Plugin;

// Owns one texture; uploads never block the draw thread and late results are
// disposed on unload. Artwork is neither written to disk nor fetched remotely.
internal sealed class SpotifyArtwork : IDisposable
{
    private readonly object gate = new();
    private string? key;
    private IDalamudTextureWrap? texture;
    private bool disposed;
    private int revision;
    internal IDalamudTextureWrap? Texture { get { lock (gate) return texture; } }
    internal void Update(string? encoded)
    {
        lock (gate)
        {
            if (disposed || key == encoded) return;
            key = encoded;
            var generation = ++revision;
            texture?.Dispose(); texture = null;
            if (encoded is null || encoded.Length > 700000) return;
            _ = Task.Run(async () =>
            {
                IDalamudTextureWrap? next = null;
                try
                {
                    var bytes = Convert.FromBase64String(encoded);
                    next = await Plugin.TextureProvider.CreateFromImageAsync(bytes);
                    lock (gate)
                    {
                        if (!disposed && revision == generation) { texture = next; next = null; }
                    }
                }
                catch { /* Missing/unsupported art uses the music placeholder. */ }
                finally { next?.Dispose(); }
            });
        }
    }
    public void Dispose()
    {
        lock (gate) { disposed = true; revision++; texture?.Dispose(); texture = null; key = null; }
    }
}
