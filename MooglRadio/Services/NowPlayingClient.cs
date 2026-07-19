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

    private readonly HttpClient httpClient = new();
    private readonly PeriodicTimer timer = new(TimeSpan.FromSeconds(15));
    private CancellationTokenSource? cts;
    private Task? pollTask;

    public NowPlaying? Latest { get; private set; }

    public event Action<NowPlaying>? Updated;

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
                    Updated?.Invoke(result);
                }
            }
            catch
            {
                // Best-effort metadata; playback doesn't depend on this succeeding.
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
