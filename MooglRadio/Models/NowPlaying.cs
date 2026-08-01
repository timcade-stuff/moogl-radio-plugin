namespace MooglRadio.Models;

/// <summary>
/// Mirrors the moogl-radio control-plane API's GET /api/now-playing
/// contract (see ARCHITECTURE.md in the moogl-radio repo).
/// </summary>
/// <param name="StreamUrl">
/// Part of the API contract but currently unused for playback —
/// <see cref="Services.StreamPlayer"/> always plays the locally configured
/// <see cref="Configuration.StreamUrl"/>, not this field. If a future
/// change ever wires this server-supplied value into actual playback, it
/// must first be validated the same way <see cref="Configuration.StreamUrl"/>
/// is (absolute URI, https scheme) before being handed to
/// <see cref="Services.StreamPlayer.Play"/> — don't fetch it unvalidated.
/// </param>
/// <param name="RemainingSeconds">
/// Seconds left in the current track as of this poll, per the same field
/// the web client's useRadioPlayer.ts ticks down locally rather than
/// re-polling every second — see <see cref="Services.NowPlayingClient.GetRemainingSeconds"/>.
/// Null while a DJ is live (no fixed track length to count down).
/// </param>
/// <param name="DurationSeconds">
/// Total length of the current track in seconds. Goes null together with
/// <paramref name="RemainingSeconds"/> for a live DJ set, but can also be
/// null on its own when ID3 tag reading failed for that file even though
/// a remaining-seconds countdown is still available — treat that as
/// "unknown length" rather than assuming it mirrors RemainingSeconds'
/// nullness. See <see cref="Services.NowPlayingClient.GetProgress"/>.
/// </param>
public sealed record NowPlaying(
    string Status,
    string? Block,
    NowPlayingTrack? Track,
    NowPlayingDj? Dj,
    string StreamUrl,
    int? ListenerCount = null,
    int? RemainingSeconds = null,
    int? DurationSeconds = null);

/// <summary>
/// ArtUrl is relative (e.g. "/api/now-playing/art") — prefix with the
/// configured ApiBaseUrl to get a fetchable URL. Null when the current
/// track has no cover art available.
/// </summary>
public sealed record NowPlayingTrack(string Title, string Artist, string? Album, string? ArtUrl);

public sealed record NowPlayingDj(string Name);
