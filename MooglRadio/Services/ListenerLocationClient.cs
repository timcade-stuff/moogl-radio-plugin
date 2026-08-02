using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace MooglRadio.Services;

/// <summary>
/// Sends anonymous "listener is here" heartbeats to the moogl-radio control
/// plane's public heartbeat endpoint, powering the site's "where are
/// listeners tuning in from" map. Opt-in (<see cref="Configuration.ShareListenerLocation"/>,
/// default off) and only active while that setting is on AND the stream is
/// actually playing.
///
/// The session ID is generated fresh in memory each time <see cref="Start"/>
/// runs (plugin load, or the setting being toggled on) and is never
/// persisted to disk or derived from any player-identifying data — it must
/// not become a stable cross-session fingerprint. There's deliberately no
/// "goodbye" call on Stop(): the server treats a session as gone after 3
/// missed heartbeats (90s, see <see cref="HeartbeatInterval"/>), the same
/// TTL approach that already covers crashes/alt-F4 a graceful disconnect
/// couldn't.
/// </summary>
public sealed class ListenerLocationClient : IDisposable
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    private readonly HttpClient httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    private PeriodicTimer? timer;
    private CancellationTokenSource? cts;
    private Task? loopTask;

    /// <summary>Regenerated every <see cref="Start"/> call; never persisted, never reused.</summary>
    private string sessionId = GenerateSessionId();

    public void Start(string apiBaseUrl, Func<(int territoryId, float x, float z)?> getLocation)
    {
        Stop();
        sessionId = GenerateSessionId();
        cts = new CancellationTokenSource();
        timer = new PeriodicTimer(HeartbeatInterval);
        loopTask = Task.Run(() => LoopAsync(apiBaseUrl, getLocation, cts.Token));
    }

    public void Stop()
    {
        cts?.Cancel();
        cts = null;
        timer?.Dispose();
        timer = null;
    }

    private async Task LoopAsync(string apiBaseUrl, Func<(int territoryId, float x, float z)?> getLocation, CancellationToken token)
    {
        var url = $"{apiBaseUrl.TrimEnd('/')}/api/listener-locations/heartbeat";

        // Send an immediate heartbeat rather than waiting out the first
        // interval, so the listener appears on the map right away.
        await SendHeartbeatAsync(url, getLocation, token);

        while (!token.IsCancellationRequested && await timer!.WaitForNextTickAsync(token))
        {
            await SendHeartbeatAsync(url, getLocation, token);
        }
    }

    private async Task SendHeartbeatAsync(string url, Func<(int territoryId, float x, float z)?> getLocation, CancellationToken token)
    {
        if (getLocation() is not { } location)
        {
            return;
        }

        try
        {
            var payload = new HeartbeatPayload(sessionId, location.territoryId, location.x, location.z);
            using var response = await httpClient.PostAsJsonAsync(url, payload, token);
            // Fire-and-forget: a failed/dropped heartbeat just means the
            // dot temporarily disappears from the map, not worth surfacing
            // or retrying aggressively.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort; playback and the rest of the plugin don't depend on this.
        }
    }

    private static string GenerateSessionId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    public void Dispose()
    {
        Stop();
        httpClient.Dispose();
    }

    private sealed record HeartbeatPayload(
        [property: JsonPropertyName("sessionId")] string SessionId,
        [property: JsonPropertyName("territoryId")] int TerritoryId,
        [property: JsonPropertyName("x")] float X,
        [property: JsonPropertyName("z")] float Z);
}
