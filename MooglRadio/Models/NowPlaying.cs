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
    string StreamUrl);

public sealed record NowPlayingTrack(string Title, string Artist, DateTimeOffset StartedAt);

public sealed record NowPlayingDj(string Name);
