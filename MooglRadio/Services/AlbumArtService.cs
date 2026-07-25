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
    private readonly HttpClient httpClient = new();
    private string? currentUrl;
    private IDalamudTextureWrap? currentTexture;
    private CancellationTokenSource? cts;

    public IDalamudTextureWrap? Texture => currentTexture;

    public void UpdateFor(string apiBaseUrl, string? artUrl)
    {
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
}
