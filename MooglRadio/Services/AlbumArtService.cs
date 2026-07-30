using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;

namespace MooglRadio.Services;

/// <summary>
/// Downloads and caches the current track's cover art as a renderable
/// ImGui texture. Re-fetches only when the resolved art URL actually
/// changes (the now-playing poll fires every 15s regardless of whether
/// the track did). Unverified against a real Dalamud install: whether
/// ITextureProvider.CreateFromImageAsync is safe to call off the main
/// thread — its doc comment doesn't flag a main-thread requirement
/// (unlike CreateTextureFromSeString, which explicitly does), but this
/// hasn't been confirmed in-game.
/// </summary>
public sealed class AlbumArtService(ITextureProvider textureProvider, IPluginLog log) : IDisposable
{
    private readonly HttpClient httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
        MaxResponseContentBufferSize = 8 * 1024 * 1024, // cover art is a small image; bound worst-case memory use
    };
    private string? currentUrl;
    private IDalamudTextureWrap? currentTexture;
    private CancellationTokenSource? cts;

    public IDalamudTextureWrap? Texture => currentTexture;

    public void UpdateFor(string apiBaseUrl, string? artUrl)
    {
        if (artUrl is not null && !IsSameOriginRelativePath(artUrl))
        {
            log.Warning($"MOOGLradio: ignoring non-relative ArtUrl from now-playing API: {artUrl}");
            artUrl = null;
        }

        var fullUrl = artUrl is null ? null : $"{apiBaseUrl.TrimEnd('/')}{artUrl}";
        if (fullUrl == currentUrl)
        {
            return;
        }

        currentUrl = fullUrl;
        cts?.Cancel();
        currentTexture?.Dispose();
        currentTexture = null;

        if (fullUrl is null)
        {
            return;
        }

        cts = new CancellationTokenSource();
        _ = LoadAsync(fullUrl, cts.Token);
    }

    private async Task LoadAsync(string url, CancellationToken token)
    {
        try
        {
            var bytes = await httpClient.GetByteArrayAsync(url, token);
            var wrap = await textureProvider.CreateFromImageAsync(bytes, "MooglRadio album art", token);

            if (token.IsCancellationRequested || url != currentUrl)
            {
                wrap.Dispose();
                return;
            }

            currentTexture?.Dispose();
            currentTexture = wrap;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Warning(ex, "MOOGLradio: failed to load album art");
        }
    }

    public void Dispose()
    {
        cts?.Cancel();
        currentTexture?.Dispose();
        httpClient.Dispose();
    }

    /// <summary>
    /// True only for a plain path-and-optionally-query relative reference
    /// (e.g. "/api/now-playing/art") — rejects absolute URLs ("https://...")
    /// and protocol-relative ones ("//evil.example/x"), so a compromised or
    /// malicious now-playing API can't redirect this fetch off the
    /// configured host via the ArtUrl field.
    /// </summary>
    private static bool IsSameOriginRelativePath(string artUrl) =>
        artUrl.StartsWith('/') && !artUrl.StartsWith("//", StringComparison.Ordinal) && !artUrl.Contains("://", StringComparison.Ordinal);
}
