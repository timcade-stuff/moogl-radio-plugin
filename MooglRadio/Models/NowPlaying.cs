namespace MooglRadio.Models;

/// <summary>
/// Mirrors the moogl-radio control-plane API's GET /api/now-playing
/// contract (see ARCHITECTURE.md in the moogl-radio repo).
/// </summary>
public sealed record NowPlaying(
    string Status,
    string? Block,
    NowPlayingTrack? Track,
    NowPlayingDj? Dj,
    string StreamUrl,
    int? ListenerCount = null);

/// <summary>
/// ArtUrl is relative (e.g. "/api/now-playing/art") — prefix with the
/// configured ApiBaseUrl to get a fetchable URL. Null when the current
/// track has no cover art available.
/// </summary>
public sealed record NowPlayingTrack(string Title, string Artist, string? Album, string? ArtUrl);

public sealed record NowPlayingDj(string Name);
