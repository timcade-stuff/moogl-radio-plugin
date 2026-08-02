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

    /// <summary>Message from the most recent failed heartbeat (bad location read, HTTP
    /// error, exception), or null if the last attempt succeeded. Best-effort telemetry
    /// only — nothing in the plugin depends on this being non-null-checked.</summary>
    public string? LastError { get; private set; }

    /// <summary>Fires with a short line per heartbeat attempt (HTTP status, or the reason
    /// it didn't send) so failures are visible via <c>IPluginLog</c>/<c>/xllog</c> instead
    /// of silently vanishing — see <see cref="Plugin.GetCurrentLocationAsync"/>'s doc
    /// comment for the specific silent-failure mode this replaced.</summary>
    public event Action<string>? Diagnostic;

    public void Start(string apiBaseUrl, Func<Task<(int territoryId, float x, float z)?>> getLocationAsync)
    {
        Stop();
        sessionId = GenerateSessionId();
        cts = new CancellationTokenSource();
        timer = new PeriodicTimer(HeartbeatInterval);
        loopTask = Task.Run(() => LoopAsync(apiBaseUrl, getLocationAsync, cts.Token));
    }

    public void Stop()
    {
        cts?.Cancel();
        cts = null;
        timer?.Dispose();
        timer = null;
    }

    private async Task LoopAsync(string apiBaseUrl, Func<Task<(int territoryId, float x, float z)?>> getLocationAsync, CancellationToken token)
    {
        var url = $"{apiBaseUrl.TrimEnd('/')}/api/listener-locations/heartbeat";

        // Send an immediate heartbeat rather than waiting out the first
        // interval, so the listener appears on the map right away.
        await SendHeartbeatAsync(url, getLocationAsync, token);

        while (!token.IsCancellationRequested && await timer!.WaitForNextTickAsync(token))
        {
            await SendHeartbeatAsync(url, getLocationAsync, token);
        }
    }

    private async Task SendHeartbeatAsync(string url, Func<Task<(int territoryId, float x, float z)?>> getLocationAsync, CancellationToken token)
    {
        (int territoryId, float x, float z)? location;
        try
        {
            // Awaited, not called directly: getLocationAsync marshals onto Dalamud's
            // framework thread (see Plugin.GetCurrentLocationAsync) since this loop
            // itself runs on a background Task.Run thread. An unhandled exception here
            // used to escape into the un-awaited loop task and kill heartbeats for the
            // rest of the session with nothing logged — hence the try/catch.
            location = await getLocationAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastError = ex.Message;
            Diagnostic?.Invoke($"Skipped: failed to read current location ({ex.Message})");
            return;
        }

        if (location is not { } loc)
        {
            Diagnostic?.Invoke("Skipped: no local player (between zone loads?)");
            return;
        }

        try
        {
            var payload = new HeartbeatPayload(sessionId, loc.territoryId, loc.x, loc.z);
            using var response = await httpClient.PostAsJsonAsync(url, payload, token);

            // Fire-and-forget in spirit (a dropped heartbeat just means the dot
            // temporarily disappears, not worth retrying aggressively), but still
            // worth surfacing so a persistent failure is visible rather than silent.
            if (response.IsSuccessStatusCode)
            {
                LastError = null;
                Diagnostic?.Invoke($"Sent: HTTP {(int)response.StatusCode}");
            }
            else
            {
                LastError = $"HTTP {(int)response.StatusCode}";
                Diagnostic?.Invoke($"Rejected: HTTP {(int)response.StatusCode}");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastError = ex is HttpRequestException httpEx
                ? $"HTTP {(int?)httpEx.StatusCode}"
                : ex.Message;
            Diagnostic?.Invoke($"Failed: {LastError}");
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
