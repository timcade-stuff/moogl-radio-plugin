using System.Net;
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
/// Output uses WaveOutEvent (not WasapiOut, despite an earlier attempt):
/// CrystalRadio, a real published Dalamud radio plugin
/// (github.com/Saevath/CrystalRadio), uses plain WaveOutEvent
/// successfully, which is real-world evidence it works under Wine —
/// undercutting the WASAPI theory from that attempt. CrystalRadio also
/// uses MediaFoundationReader instead of manual MP3 frame parsing, but
/// that's a native Windows COM API with inconsistent Wine/Proton support
/// (see e.g. github.com/HoodedDeath/mf-fix) — deliberately not adopted
/// here, since it would reintroduce the exact kind of platform-specific
/// risk NLayer's pure-C# decoding was chosen to avoid.
///
/// The request is pinned to HTTP/1.1 — confirmed in-game the stream was
/// disconnecting after only 3-6 frames (~100ms of audio) even though the
/// same URL streams fine via curl. HttpClient negotiates HTTP/2 by
/// default against Cloudflare, and HTTP/2 combined with
/// Mp3Frame.LoadFromStream's synchronous Stream.Read() calls has known
/// premature-completion flakiness on some .NET runtimes.
///
/// <see cref="Diagnostic"/> logs key playback checkpoints, including
/// periodic buffering progress and the negotiated HTTP version.
/// </summary>
public sealed class StreamPlayer : IDisposable
{
    private const int ReadBufferSize = 16384 * 4;

    private readonly HttpClient httpClient = new();
    private CancellationTokenSource? cts;
    private WaveOutEvent? waveOut;
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
            // Force HTTP/1.1: HttpClient negotiates HTTP/2 by default when the
            // server offers it (Cloudflare does, here), and HTTP/2 combined with
            // Mp3Frame.LoadFromStream's synchronous Stream.Read() calls has known
            // premature-stream-completion flakiness on some .NET runtimes.
            // Confirmed in-game: the stream was ending after 3-6 frames
            // (~100ms of audio), consistent with this rather than a real
            // server-side disconnect (curl against the same URL streams fine).
            var request = new HttpRequestMessage(HttpMethod.Get, streamUrl)
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            };
            using var response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            Diagnostic?.Invoke($"Connected: HTTP/{response.Version}, status {(int)response.StatusCode}");
            await using var rawStream = await response.Content.ReadAsStreamAsync(token);
            await using var stream = new PositionTrackingStream(rawStream);

            var buffer = new byte[ReadBufferSize];
            IMp3FrameDecompressor? decompressor = null;
            BufferedWaveProvider? bufferedWaveProvider = null;
            var frameCount = 0;

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
                    Diagnostic?.Invoke($"Stream ended after {frameCount} frames");
                    break;
                }

                if (frame is null)
                {
                    Diagnostic?.Invoke($"LoadFromStream returned null after {frameCount} frames (end of stream)");
                    break;
                }

                frameCount++;

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
                else if (frameCount % 20 == 0)
                {
                    Diagnostic?.Invoke(
                        $"Frame {frameCount}: buffered {bufferedWaveProvider!.BufferedDuration.TotalSeconds:F2}s, " +
                        $"output {(waveOut is null ? "not started" : $"started (PlaybackState={waveOut.PlaybackState})")}");
                }

                var decompressed = decompressor.DecompressFrame(frame, buffer, 0);
                bufferedWaveProvider!.AddSamples(buffer, 0, decompressed);

                if (waveOut is null && bufferedWaveProvider.BufferedDuration.TotalSeconds > 2)
                {
                    // NLayer's decoder outputs 32-bit IEEE float; convert to 16-bit PCM
                    // before handing off to the output device.
                    outputProvider = new WaveFloatTo16Provider(bufferedWaveProvider) { Volume = volume };
                    waveOut = new WaveOutEvent();
                    waveOut.Init(outputProvider);
                    waveOut.Play();
                    Diagnostic?.Invoke($"Output initialized at frame {frameCount}, PlaybackState={waveOut.PlaybackState}");
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
