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
/// same constructor shape and no native codec dependency. The response
/// stream is also wrapped in <see cref="PositionTrackingStream"/> — see
/// its doc comment for why (also confirmed in-game).
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

    /// <summary>Fires when playback actually begins (a fresh Play() call, not a re-stop/restart).</summary>
    public event Action? Started;

    /// <summary>Fires when playback stops, whether via explicit Stop() or a playback failure.</summary>
    public event Action? Stopped;

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
        StopInternal();
        cts = new CancellationTokenSource();
        IsPlaying = true;
        LastError = null;
        Started?.Invoke();
        _ = Task.Run(() => RunAsync(streamUrl, cts.Token));
    }

    public void Stop()
    {
        var wasPlaying = IsPlaying;
        StopInternal();
        if (wasPlaying)
        {
            Stopped?.Invoke();
        }
    }

    private void StopInternal()
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
            await using var rawStream = await response.Content.ReadAsStreamAsync(token);
            await using var stream = new PositionTrackingStream(rawStream);

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
                    // NLayer's decoder outputs 32-bit IEEE float; convert to 16-bit PCM
                    // before handing off to WaveOutEvent — Wine's DirectSound/WASAPI
                    // shims have historically accepted IEEE float Init()/Play() calls
                    // without erroring, then produced no audible output at all.
                    waveOut.Init(new WaveFloatTo16Provider(bufferedWaveProvider));
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
            Stopped?.Invoke();
        }
    }

    public void Dispose()
    {
        Stop();
        httpClient.Dispose();
    }

    /// <summary>
    /// Wraps a non-seekable network stream to satisfy NAudio's
    /// <see cref="Mp3Frame.LoadFromStream(Stream)"/>, whose very first line
    /// reads <c>input.Position</c> unconditionally — confirmed in-game as a
    /// <see cref="NotSupportedException"/> thrown by the HTTP response
    /// stream's Position getter, since HttpClient response streams aren't
    /// seekable and don't track a position. This just reports a running
    /// count of bytes read so far, which is all LoadFromStream needs it for
    /// (recording each frame's offset, not actually seeking).
    /// </summary>
    private sealed class PositionTrackingStream(Stream inner) : Stream
    {
        private long position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            position += read;
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
