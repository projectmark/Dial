using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Media.Core;
using Windows.Media.MediaProperties;

namespace Dial.Modules.Radio;

public enum BufferedMediaStartStatus
{
    Started,
    BypassToDirect,
    Failed
}

public sealed record BufferedMediaStartResult(
    BufferedMediaStartStatus Status,
    MediaSource? Source,
    int BitrateKbps,
    double BufferedSeconds,
    int TargetBufferSeconds);

/// <summary>
/// Frame-accurate buffered live stream playout engine built on WinRT <see cref="MediaStreamSource"/>.
/// Separates inline ICY metadata on a single HTTP connection, parses discrete MP3 / ADTS AAC frames,
/// pre-buffers the exact user-configured duration (2s–30s), and seamlessly auto-reconnects on frame
/// boundaries while audio plays uninterrupted from <see cref="TimedAudioFrameQueue"/>.
/// Non-MP3/non-AAC streams (Ogg, Opus, FLAC) transparently reuse the open socket via <see cref="BufferedStreamProxy"/>.
/// </summary>
public sealed class BufferedFrameMediaSource : IDisposable
{
    private const int ReadChunkSize = 16_384;
    private const int MaxSniffBytes = 65_536;
    private const int MaxTransientReconnects = 4;

    private readonly string _upstreamUrl;
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _cts = new();
    private readonly byte[] _scratchRead = new byte[ReadChunkSize];
    private readonly byte[] _scratchAudio = new byte[ReadChunkSize];

    private int _bufferSeconds;
    private TimedAudioFrameQueue? _queue;
    private AudioFrameAccumulator? _accumulator;
    private BufferedStreamProxy? _fallbackProxy;
    private MediaStreamSource? _mss;
    private TimeSpan _nextTimestamp = TimeSpan.Zero;
    private string? _lastTitle;
    private bool _disposed;

    public BufferedFrameMediaSource(string upstreamUrl, int bufferSeconds)
    {
        _upstreamUrl = upstreamUrl;
        _bufferSeconds = AudioBufferSizing.NormalizeSeconds(bufferSeconds);
        _http = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public double BufferedSeconds => _queue?.BufferedDuration.TotalSeconds ?? 0;
    public int TargetBufferSeconds => _bufferSeconds;

    public event Action<string?>? NowPlayingChanged;
    public event Action<bool>? BufferingStateChanged;
    public event Action<double, int>? BufferLevelChanged;
    public event Action? UpstreamFailed;

    public void UpdateBufferSeconds(int bufferSeconds)
    {
        if (_disposed)
        {
            return;
        }

        _bufferSeconds = AudioBufferSizing.NormalizeSeconds(bufferSeconds);
        _queue?.UpdateBufferSeconds(_bufferSeconds);
        _fallbackProxy?.UpdateBufferSeconds(_bufferSeconds);
        BufferLevelChanged?.Invoke(BufferedSeconds, _bufferSeconds);
    }

    public async Task<BufferedMediaStartResult> StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (AudioBufferSizing.ShouldBypassProxy(_upstreamUrl))
        {
            return new BufferedMediaStartResult(BufferedMediaStartStatus.BypassToDirect, null, AudioBufferSizing.DefaultBitrateKbps, 0, _bufferSeconds);
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        HttpResponseMessage? initialResponse = null;
        Stream? initialBodyStream = null;

        try
        {
            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(linkedCts.Token))
            {
                connectCts.CancelAfter(TimeSpan.FromSeconds(12));
                initialResponse = await OpenUpstreamAsync(connectCts.Token).ConfigureAwait(false);
            }

            if (!initialResponse.IsSuccessStatusCode)
            {
                initialResponse.Dispose();
                return new BufferedMediaStartResult(BufferedMediaStartStatus.Failed, null, AudioBufferSizing.DefaultBitrateKbps, 0, _bufferSeconds);
            }

            var mediaType = initialResponse.Content.Headers.ContentType?.MediaType;
            if (AudioBufferSizing.IsPlaylistContentType(mediaType))
            {
                initialResponse.Dispose();
                return new BufferedMediaStartResult(BufferedMediaStartStatus.BypassToDirect, null, AudioBufferSizing.DefaultBitrateKbps, 0, _bufferSeconds);
            }

            var headerBitrate = AudioBufferSizing.EstimateBitrateKbps(GetHeaderValue(initialResponse.Headers, "icy-br"));
            var initialMetaInt = ParseMetaInt(initialResponse.Headers);
            var demuxer = new IcyStreamDemuxer(initialMetaInt);
            initialBodyStream = await initialResponse.Content.ReadAsStreamAsync(linkedCts.Token).ConfigureAwait(false);

            // Sniff initial audio stream until MP3 or ADTS AAC frame sync locks (or fallback to HTTP buffer proxy).
            var sniffBuffer = new List<byte>(32_768);
            AudioStreamFormat? detectedFormat = null;

            using (var sniffTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(linkedCts.Token))
            {
                sniffTimeoutCts.CancelAfter(TimeSpan.FromSeconds(4));
                while (sniffBuffer.Count < MaxSniffBytes && !sniffTimeoutCts.IsCancellationRequested)
                {
                    int bytesRead;
                    try
                    {
                        bytesRead = await initialBodyStream.ReadAsync(_scratchRead.AsMemory(), sniffTimeoutCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!linkedCts.IsCancellationRequested)
                    {
                        break;
                    }

                    if (bytesRead == 0)
                    {
                        break;
                    }

                    var audioCount = demuxer.Process(_scratchRead.AsSpan(0, bytesRead), _scratchAudio.AsSpan(), EmitNowPlaying);
                    if (audioCount > 0)
                    {
                        sniffBuffer.AddRange(_scratchAudio.AsSpan(0, audioCount));
                        if (sniffBuffer.Count >= 4096 &&
                            AudioFrameParser.TryDetectFormat(CollectionsMarshal.AsSpan(sniffBuffer), mediaType, headerBitrate, out var fmt, out _))
                        {
                            detectedFormat = fmt;
                            break;
                        }
                    }
                }
            }

            if (linkedCts.IsCancellationRequested)
            {
                DisposeUpstreamObjects(initialResponse, initialBodyStream);
                return new BufferedMediaStartResult(BufferedMediaStartStatus.Failed, null, headerBitrate, 0, _bufferSeconds);
            }

            // Try once more across the complete sniff buffer in case threshold just passed
            if (detectedFormat is null && sniffBuffer.Count >= 128 &&
                AudioFrameParser.TryDetectFormat(CollectionsMarshal.AsSpan(sniffBuffer), mediaType, headerBitrate, out var lastChanceFmt, out _))
            {
                detectedFormat = lastChanceFmt;
            }

            if (detectedFormat is null)
            {
                // Fallback for non-MP3/non-AAC streams (Ogg, Opus, FLAC): preserve single open socket via BufferedStreamProxy.
                if (sniffBuffer.Count == 0 && initialBodyStream is not null)
                {
                    DisposeUpstreamObjects(initialResponse, initialBodyStream);
                    return new BufferedMediaStartResult(BufferedMediaStartStatus.Failed, null, headerBitrate, 0, _bufferSeconds);
                }

                var proxy = new BufferedStreamProxy(_upstreamUrl, _bufferSeconds, _http);
                proxy.NowPlayingChanged += title => EmitNowPlaying(title);
                proxy.UpstreamFailed += () => UpstreamFailed?.Invoke();

                var respToHandOff = initialResponse;
                var streamToHandOff = initialBodyStream!;
                initialResponse = null;
                initialBodyStream = null;

                var proxyResult = proxy.StartFromPreopened(
                    respToHandOff,
                    streamToHandOff,
                    demuxer,
                    sniffBuffer.ToArray(),
                    string.IsNullOrWhiteSpace(mediaType) ? "audio/mpeg" : mediaType!,
                    headerBitrate,
                    initialMetaInt);

                if (proxyResult.Status == BufferedProxyStartStatus.Started && proxyResult.LocalUri is not null)
                {
                    _fallbackProxy = proxy;
                    var uriSource = MediaSource.CreateFromUri(proxyResult.LocalUri);
                    return new BufferedMediaStartResult(
                        BufferedMediaStartStatus.Started,
                        uriSource,
                        proxyResult.BitrateKbps,
                        _bufferSeconds,
                        _bufferSeconds);
                }

                proxy.Dispose();
                return new BufferedMediaStartResult(BufferedMediaStartStatus.Failed, null, headerBitrate, 0, _bufferSeconds);
            }

            // Primary MediaStreamSource path with true frame-timed N-second buffering.
            var queue = new TimedAudioFrameQueue(_bufferSeconds);
            queue.BufferingChanged += isBuf => BufferingStateChanged?.Invoke(isBuf);
            var accumulator = new AudioFrameAccumulator(detectedFormat.Codec);

            _queue = queue;
            _accumulator = accumulator;

            var initialFrames = new List<AudioFrame>(256);
            accumulator.Append(sniffBuffer.ToArray());
            accumulator.ExtractFrames(initialFrames);
            foreach (var frame in initialFrames)
            {
                await queue.EnqueueAsync(frame, linkedCts.Token).ConfigureAwait(false);
            }

            var ownedResp = initialResponse;
            var ownedStream = initialBodyStream!;
            initialResponse = null;
            initialBodyStream = null;

            _ = Task.Run(() => PumpUpstreamLoopAsync(ownedResp, ownedStream, demuxer), CancellationToken.None);
            _ = Task.Run(MonitorBufferLevelsAsync, CancellationToken.None);

            // Wait until the full configured TargetBuffer (2s..30s) is filled in RAM (or aborted/completed).
            await queue.WaitForInitialPrebufferAsync(linkedCts.Token).ConfigureAwait(false);

            if (linkedCts.IsCancellationRequested || queue.FrameCount == 0)
            {
                Cancel();
                return new BufferedMediaStartResult(BufferedMediaStartStatus.Failed, null, detectedFormat.BitrateKbps, 0, _bufferSeconds);
            }

            var encodingProps = detectedFormat.Codec == AudioCodecKind.AacAdts
                ? AudioEncodingProperties.CreateAacAdts((uint)detectedFormat.SampleRate, (uint)detectedFormat.Channels, 0)
                : AudioEncodingProperties.CreateMp3((uint)detectedFormat.SampleRate, (uint)detectedFormat.Channels, (uint)(detectedFormat.BitrateKbps * 1000));

            var mss = new MediaStreamSource(new AudioStreamDescriptor(encodingProps))
            {
                BufferTime = TimeSpan.Zero,
                CanSeek = false,
                IsLive = true
            };
            mss.SampleRequested += OnSampleRequested;
            mss.Closed += OnMediaStreamSourceClosed;
            _mss = mss;

            var mediaSource = MediaSource.CreateFromMediaStreamSource(mss);
            BufferLevelChanged?.Invoke(queue.BufferedDuration.TotalSeconds, _bufferSeconds);
            return new BufferedMediaStartResult(
                BufferedMediaStartStatus.Started,
                mediaSource,
                detectedFormat.BitrateKbps,
                queue.BufferedDuration.TotalSeconds,
                _bufferSeconds);
        }
        catch
        {
            DisposeUpstreamObjects(initialResponse, initialBodyStream);
            return new BufferedMediaStartResult(BufferedMediaStartStatus.Failed, null, AudioBufferSizing.DefaultBitrateKbps, 0, _bufferSeconds);
        }
    }

    private Task<HttpResponseMessage> OpenUpstreamAsync(CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, _upstreamUrl);
        request.Headers.TryAddWithoutValidation("Icy-MetaData", "1");
        request.Headers.TryAddWithoutValidation("User-Agent", "Dial/1.0");
        return _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private async Task PumpUpstreamLoopAsync(
        HttpResponseMessage firstResponse,
        Stream firstStream,
        IcyStreamDemuxer firstDemuxer)
    {
        var queue = _queue!;
        var accumulator = _accumulator!;
        HttpResponseMessage? currentResponse = firstResponse;
        Stream? currentStream = firstStream;
        IcyStreamDemuxer? currentDemuxer = firstDemuxer;
        var parsedFrames = new List<AudioFrame>(64);
        var consecutiveFailures = 0;

        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var receivedFramesOnConnection = false;
                if (currentResponse is not null)
                {
                    var activeResp = currentResponse;
                    var activeStream = currentStream;
                    var demuxer = currentDemuxer ?? new IcyStreamDemuxer(ParseMetaInt(activeResp.Headers));
                    currentResponse = null;
                    currentStream = null;
                    currentDemuxer = null;

                    try
                    {
                        using (activeResp)
                        await using (var stream = activeStream ?? await activeResp.Content.ReadAsStreamAsync(_cts.Token).ConfigureAwait(false))
                        {
                            while (!_cts.IsCancellationRequested)
                            {
                                var bytesRead = await stream.ReadAsync(_scratchRead.AsMemory(), _cts.Token).ConfigureAwait(false);
                                if (bytesRead == 0)
                                {
                                    break;
                                }

                                var audioCount = demuxer.Process(_scratchRead.AsSpan(0, bytesRead), _scratchAudio.AsSpan(), EmitNowPlaying);
                                if (audioCount > 0)
                                {
                                    accumulator.Append(_scratchAudio.AsSpan(0, audioCount));
                                    parsedFrames.Clear();
                                    accumulator.ExtractFrames(parsedFrames);

                                    if (parsedFrames.Count > 0)
                                    {
                                        receivedFramesOnConnection = true;
                                        consecutiveFailures = 0;
                                        for (var i = 0; i < parsedFrames.Count; i++)
                                        {
                                            await queue.EnqueueAsync(parsedFrames[i], _cts.Token).ConfigureAwait(false);
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                    {
                        break;
                    }
                    catch
                    {
                        // Recover seamlessly below on next clean frame sync boundary while queue drains.
                    }
                }

                if (_cts.IsCancellationRequested)
                {
                    break;
                }

                accumulator.ResetForNewConnection();
                consecutiveFailures = receivedFramesOnConnection ? 1 : consecutiveFailures + 1;
                if (consecutiveFailures > MaxTransientReconnects)
                {
                    UpstreamFailed?.Invoke();
                    break;
                }

                var backoffMs = consecutiveFailures switch
                {
                    1 => 250,
                    2 => 750,
                    3 => 1500,
                    _ => 2500
                };

                try
                {
                    await Task.Delay(backoffMs, _cts.Token).ConfigureAwait(false);
                    using var reconnectCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    reconnectCts.CancelAfter(TimeSpan.FromSeconds(10));
                    var opened = await OpenUpstreamAsync(reconnectCts.Token).ConfigureAwait(false);
                    if (!opened.IsSuccessStatusCode)
                    {
                        var isTerminal = (int)opened.StatusCode is >= 400 and < 500;
                        opened.Dispose();
                        if (isTerminal)
                        {
                            UpstreamFailed?.Invoke();
                            break;
                        }

                        continue;
                    }

                    currentResponse = opened;
                    currentStream = null;
                    currentDemuxer = new IcyStreamDemuxer(ParseMetaInt(opened.Headers));
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    // Retry loop handles transient errors cleanly up to MaxTransientReconnects.
                }
            }
        }
        finally
        {
            queue.Complete();
        }
    }

    private async Task MonitorBufferLevelsAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
            while (await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
            {
                BufferLevelChanged?.Invoke(BufferedSeconds, _bufferSeconds);
            }
        }
        catch
        {
            // Expected on cancellation/dispose.
        }
    }

    private async void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
    {
        var deferral = args.Request.GetDeferral();
        try
        {
            var queue = _queue;
            if (queue is null || _cts.IsCancellationRequested)
            {
                return;
            }

            var frame = await queue.DequeueAsync(_cts.Token).ConfigureAwait(false);
            if (frame is null || _cts.IsCancellationRequested)
            {
                return;
            }

            var sample = MediaStreamSample.CreateFromBuffer(frame.Payload.AsBuffer(), _nextTimestamp);
            sample.Duration = frame.Duration;
            _nextTimestamp += frame.Duration;
            args.Request.Sample = sample;
        }
        catch (OperationCanceledException)
        {
            // Shutdown / track switch.
        }
        catch
        {
            // End cleanly on invalid buffer state.
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void OnMediaStreamSourceClosed(MediaStreamSource sender, MediaStreamSourceClosedEventArgs args)
        => Cancel();

    private void EmitNowPlaying(string? title)
    {
        if (string.IsNullOrWhiteSpace(title) || string.Equals(_lastTitle, title, StringComparison.Ordinal))
        {
            return;
        }

        _lastTitle = title;
        NowPlayingChanged?.Invoke(title);
    }

    private void Cancel()
    {
        try { _cts.Cancel(); } catch { }
        _queue?.Complete();
    }

    private static void DisposeUpstreamObjects(HttpResponseMessage? response, Stream? stream)
    {
        try { stream?.Dispose(); } catch { }
        try { response?.Dispose(); } catch { }
    }

    private static int ParseMetaInt(HttpResponseHeaders headers)
    {
        var raw = GetHeaderValue(headers, "icy-metaint");
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var metaInt) && metaInt > 0
            ? metaInt
            : 0;
    }

    private static string? GetHeaderValue(HttpResponseHeaders headers, string name)
        => headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Cancel();

        if (_mss is not null)
        {
            _mss.SampleRequested -= OnSampleRequested;
            _mss.Closed -= OnMediaStreamSourceClosed;
            _mss = null;
        }

        _fallbackProxy?.Dispose();
        _fallbackProxy = null;

        _cts.Dispose();
        _http.Dispose();
    }
}
