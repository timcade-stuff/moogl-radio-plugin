using NAudio.CoreAudioApi;
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
///
/// Output goes through WasapiOut rather than WaveOutEvent: WaveOutEvent
/// (winmm-based) accepted Init()/Play() without error under Wine but
/// produced no audible output at all, even after the float->PCM16 fix
/// above — confirmed in-game. WASAPI is the modern audio API most
/// Windows games (including FFXIV itself) actually target, so Wine's
/// Mac compatibility layer is a much safer bet to have implemented
/// properly. <see cref="Diagnostic"/> logs key playback checkpoints in
/// case this still produces no sound — see its doc comment.
/// </summary>
public sealed class StreamPlayer : IDisposable
{
    private const int ReadBufferSize = 16384 * 4;

    private readonly HttpClient httpClient = new();
    private CancellationTokenSource? cts;
    private WasapiOut? waveOut;
    private WaveFloatTo16Provider? outputProvider;
    private float volume = 0.5f;

    public bool IsPlaying { get; private set; }

    /// <summary>Message from the most recent playback failure, or null if playback is fine.</summary>
    public string? LastError { get; private set; }

    public event Action<Exception>? Error;

    /// <summary>Fires when playback actually begins (a fresh Play() call, not a re-stop/restart).</summary>
    public event Action? Started;

    /// <summary>Fires when playback stops, whether via explicit Stop() or a playback failure.</summary>
    public event Action? Stopped;

    /// <summary>
    /// Fires with short human-readable status lines at key playback
    /// checkpoints (format detected, output device initialized, playback
    /// state after Play()). Not user-facing — intended for IPluginLog, so
    /// a "no error, no sound" report has something concrete to check next
    /// instead of another blind guess.
    /// </summary>
    public event Action<string>? Diagnostic;

    public float Volume
    {
        get => volume;
        set
        {
            volume = Math.Clamp(value, 0f, 1f);
            // Deliberately not waveOut.Volume: WasapiOut.Volume writes to the
            // *system's* master output volume (mmDevice.AudioEndpointVolume),
            // not a per-stream level. Scaling samples in the float->16 provider
            // instead keeps this to "this stream's" volume, like the slider implies.
            if (outputProvider is not null)
            {
                outputProvider.Volume = volume;
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
        outputProvider = null;
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
                    Diagnostic?.Invoke(
                        $"First frame decoded: {frame.SampleRate}Hz, {waveFormat.Channels}ch, {frame.BitRate}bps");
                }

                var decompressed = decompressor.DecompressFrame(frame, buffer, 0);
                bufferedWaveProvider!.AddSamples(buffer, 0, decompressed);

                if (waveOut is null && bufferedWaveProvider.BufferedDuration.TotalSeconds > 2)
                {
                    // NLayer's decoder outputs 32-bit IEEE float; convert to 16-bit PCM
                    // before handing off to the output device.
                    outputProvider = new WaveFloatTo16Provider(bufferedWaveProvider) { Volume = volume };
                    waveOut = new WasapiOut(AudioClientShareMode.Shared, 200);
                    waveOut.Init(outputProvider);
                    waveOut.Play();
                    Diagnostic?.Invoke($"WASAPI output initialized, PlaybackState={waveOut.PlaybackState}");
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
