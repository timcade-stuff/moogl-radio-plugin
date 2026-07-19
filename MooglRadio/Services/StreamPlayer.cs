using NAudio.Wave;
using NAudio.Wave.Compression;

namespace MooglRadio.Services;

/// <summary>
/// Plays an HTTP MP3 stream (the moogl-radio Icecast mount) using the
/// standard NAudio "streaming mp3 over HTTP" recipe: read raw MP3
/// frames off the response stream, decompress into a BufferedWaveProvider,
/// and start playback once a couple of seconds are buffered.
///
/// DRAFT / untested in-game. In particular, <see cref="AcmMp3FrameDecompressor"/>
/// uses the Windows ACM mp3 codec, which may not be present in every Wine
/// prefix (relevant here since FFXIV runs under Wine on Mac/Linux, not
/// just native Windows). If playback fails silently under Wine, swap
/// this for the pure-C# decoder from the `NLayer.NAudioSupport` NuGet
/// package (`NLayer.NAudioSupport.Mp3FrameDecompressor`), which has no
/// native codec dependency — same constructor shape as AcmMp3FrameDecompressor.
/// </summary>
public sealed class StreamPlayer : IDisposable
{
    private const int ReadBufferSize = 16384 * 4;

    private readonly HttpClient httpClient = new();
    private CancellationTokenSource? cts;
    private WaveOutEvent? waveOut;
    private float volume = 0.5f;

    public bool IsPlaying { get; private set; }

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
                    decompressor = new AcmMp3FrameDecompressor(waveFormat);
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
            Error?.Invoke(ex);
        }
    }

    public void Dispose()
    {
        Stop();
        httpClient.Dispose();
    }
}
