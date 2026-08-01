using System.Net.Http.Json;
using System.Text.Json;
using MooglRadio.Models;

namespace MooglRadio.Services;

/// <summary>
/// Polls the moogl-radio control-plane's public now-playing endpoint on
/// a timer. Deliberately dumb (polling, not push) to match the API's v1
/// contract — see ARCHITECTURE.md "Now-playing contract" in the
/// moogl-radio repo.
/// </summary>
public sealed class NowPlayingClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
        MaxResponseContentBufferSize = 64 * 1024, // now-playing payload is a handful of short fields
    };
    private readonly PeriodicTimer timer = new(TimeSpan.FromSeconds(15));
    private CancellationTokenSource? cts;
    private Task? pollTask;

    /// <summary>UTC time this poll's <see cref="NowPlaying.RemainingSeconds"/>
    /// was captured, so <see cref="GetRemainingSeconds"/> can subtract elapsed
    /// wall-clock time instead of re-polling every second for a countdown.</summary>
    private DateTime latestPolledAtUtc;

    public NowPlaying? Latest { get; private set; }

    /// <summary>Message from the most recent failed poll, or null if the last poll succeeded.</summary>
    public string? LastError { get; private set; }

    public event Action<NowPlaying>? Updated;

    /// <summary>
    /// Ticks <see cref="NowPlaying.RemainingSeconds"/> down locally between
    /// polls, the same way the web client's useRadioPlayer.ts does, rather
    /// than hammering the API for a per-second countdown. Null when there's
    /// no track length to count down (e.g. a live DJ set) or no data yet.
    /// </summary>
    public int? GetRemainingSeconds()
    {
        if (Latest?.RemainingSeconds is not { } remainingAtPoll)
        {
            return null;
        }

        var elapsed = (DateTime.UtcNow - latestPolledAtUtc).TotalSeconds;
        return Math.Max(0, (int)Math.Round(remainingAtPoll - elapsed));
    }

    public void Start(string apiBaseUrl)
    {
        Stop();
        cts = new CancellationTokenSource();
        pollTask = Task.Run(() => PollLoopAsync(apiBaseUrl, cts.Token));
    }

    public void Stop()
    {
        cts?.Cancel();
        cts = null;
    }

    private async Task PollLoopAsync(string apiBaseUrl, CancellationToken token)
    {
        var url = $"{apiBaseUrl.TrimEnd('/')}/api/now-playing";

        do
        {
            try
            {
                var result = await httpClient.GetFromJsonAsync<NowPlaying>(url, JsonOptions, token);
                if (result is not null)
                {
                    Latest = result;
                    latestPolledAtUtc = DateTime.UtcNow;
                    LastError = null;
                    Updated?.Invoke(result);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Best-effort metadata; playback doesn't depend on this succeeding.
                LastError = ex is HttpRequestException httpEx
                    ? $"HTTP {(int?)httpEx.StatusCode}"
                    : ex.Message;
            }
        } while (!token.IsCancellationRequested && await timer.WaitForNextTickAsync(token));
    }

    public void Dispose()
    {
        Stop();
        timer.Dispose();
        httpClient.Dispose();
    }
}
