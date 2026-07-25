using NAudio.Wave;
using NLayer.NAudioSupport;

namespace MooglRadio.Services;

/// <summary>
/// Plays an HTTP MP3 stream (the moogl-radio Icecast mount) using the
/// standard NAudio "streaming mp3 over HTTP" recipe: read raw MP3
/// frames off the response stream, decompress into a BufferedWaveProvider,
/// and start playback once a couple of seconds are buffered.
///
/// Uses NLayer's pure-C# decoder rather than NAudio's ACM-based one:
/// AcmMp3FrameDecompressor calls into the Windows ACM codec, which
/// doesn't exist under Wine (confirmed in-game — it threw
/// NotSupportedException, since FFXIV on Mac runs under Wine, not
/// native Windows). NLayer.NAudioSupport.Mp3FrameDecompressor has the
/// same constructor shape and no native codec dependency.
/// </summary>
public sealed class StreamPlayer : IDisposable
{
    private const int ReadBufferSize = 16384 * 4;

    private readonly HttpClient httpClient = new();
    private CancellationTokenSource? cts;
    private WaveOutEvent? waveOut;
    private float volume = 0.5f;

    public bool IsPlaying { get; private set; }

    /// <summary>Message from the most recent playback failure, or null if playback is fine.</summary>
    public string? LastError { get; private set; }

    public event Action<Exception>? Error;

    public float Volume
    {
        get => volume;
        set
        {
            volume = Math.Clamp(value, 0f, 1f);
            if (waveOut is not null)
            {
                waveOut.Volume = volume;
            }
        }
    }

    public void Play(string streamUrl)
    {
        Stop();
        cts = new CancellationTokenSource();
        IsPlaying = true;
        LastError = null;
        _ = Task.Run(() => RunAsync(streamUrl, cts.Token));
    }

    public void Stop()
    {
        cts?.Cancel();
        cts = null;
        waveOut?.Stop();
        waveOut?.Dispose();
        waveOut = null;
        IsPlaying = false;
    }

    private async Task RunAsync(string streamUrl, CancellationToken token)
    {
        try
        {
            using var response = await httpClient.GetAsync(
                streamUrl, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(token);

            var buffer = new byte[ReadBufferSize];
            IMp3FrameDecompressor? decompressor = null;
            BufferedWaveProvider? bufferedWaveProvider = null;

            while (!token.IsCancellationRequested)
            {
                if (bufferedWaveProvider is not null &&
                    bufferedWaveProvider.BufferLength - bufferedWaveProvider.BufferedBytes
                        < bufferedWaveProvider.WaveFormat.AverageBytesPerSecond / 4)
                {
                    await Task.Delay(250, token);
                    continue;
                }

                Mp3Frame frame;
                try
                {
                    frame = Mp3Frame.LoadFromStream(stream);
                }
                catch (EndOfStreamException)
                {
                    break;
                }

                if (decompressor is null)
                {
                    var waveFormat = new Mp3WaveFormat(
                        frame.SampleRate,
                        frame.ChannelMode == ChannelMode.Mono ? 1 : 2,
                        frame.FrameLength,
                        frame.BitRate);
                    decompressor = new Mp3FrameDecompressor(waveFormat);
                    bufferedWaveProvider = new BufferedWaveProvider(decompressor.OutputFormat)
                    {
                        BufferDuration = TimeSpan.FromSeconds(20),
                    };
                }

                var decompressed = decompressor.DecompressFrame(frame, buffer, 0);
                bufferedWaveProvider!.AddSamples(buffer, 0, decompressed);

                if (waveOut is null && bufferedWaveProvider.BufferedDuration.TotalSeconds > 2)
                {
                    waveOut = new WaveOutEvent { Volume = volume };
                    waveOut.Init(bufferedWaveProvider);
                    waveOut.Play();
                }
            }

            decompressor?.Dispose();
        }
        catch (OperationCanceledException)
        {
            // Expected on Stop().
        }
        catch (Exception ex)
        {
            IsPlaying = false;
            LastError = ex is HttpRequestException httpEx
                ? $"HTTP {(int?)httpEx.StatusCode} fetching stream"
                : ex.Message;
            Error?.Invoke(ex);
        }
    }

    public void Dispose()
    {
        Stop();
        httpClient.Dispose();
    }
}
