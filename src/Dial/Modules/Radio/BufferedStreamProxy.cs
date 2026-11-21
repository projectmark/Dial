using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;

namespace Dial.Modules.Radio;

/// <summary>
/// Helpers for resolving and sizing user-configurable read-ahead audio buffers.
/// </summary>
public static class AudioBufferSizing
{
    public const string OptionKey = "radio.buffer_seconds";
    public const int DefaultBufferSeconds = 5;
    public const int DefaultBitrateKbps = 192;

    public static IReadOnlyList<int> Presets { get; } = (int[])[2, 5, 10, 15, 30];

    public static int NormalizeSeconds(int seconds)
        => seconds <= 0 ? DefaultBufferSeconds : Math.Clamp(seconds, 1, 60);

    public static int EstimateBitrateKbps(string? icyBrHeader)
    {
        if (string.IsNullOrWhiteSpace(icyBrHeader))
        {
            return DefaultBitrateKbps;
        }

        foreach (var part in icyBrHeader.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
            {
                return Math.Clamp(parsed, 32, 1411);
            }
        }

        return DefaultBitrateKbps;
    }

    public static int CalculateCapacityBytes(int bufferSeconds, int bitrateKbps)
    {
        var normalizedSeconds = NormalizeSeconds(bufferSeconds);
        var clampedKbps = Math.Clamp(bitrateKbps, 32, 1411);
        var bytesPerSecond = Math.Max(4_000, clampedKbps * 1000 / 8);
        return Math.Max(65_536, normalizedSeconds * bytesPerSecond);
    }

    public static int CalculateInitialPrebufferBytes(int bufferSeconds, int bitrateKbps)
    {
        var capacity = CalculateCapacityBytes(bufferSeconds, bitrateKbps);
        var clampedKbps = Math.Clamp(bitrateKbps, 32, 1411);
        var bytesPerSecond = Math.Max(4_000, clampedKbps * 1000 / 8);
        var desired = NormalizeSeconds(bufferSeconds) * bytesPerSecond;
        return Math.Clamp(desired, 16_384, capacity);
    }

    public static bool ShouldBypassProxy(string? streamUrl)
    {
        if (!Uri.TryCreate(streamUrl, UriKind.Absolute, out var uri))
        {
            return true;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var path = uri.AbsolutePath.ToLowerInvariant();
        return path.EndsWith(".m3u8", StringComparison.Ordinal) ||
               path.EndsWith(".m3u", StringComparison.Ordinal) ||
               path.EndsWith(".pls", StringComparison.Ordinal) ||
               path.EndsWith(".asx", StringComparison.Ordinal) ||
               path.EndsWith(".wax", StringComparison.Ordinal);
    }

    public static bool IsPlaylistContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        var normalized = contentType.Trim().ToLowerInvariant();
        return normalized.Contains("mpegurl", StringComparison.Ordinal) ||
               normalized.Contains("scpls", StringComparison.Ordinal) ||
               normalized.Contains("x-ms-wax", StringComparison.Ordinal) ||
               normalized.Contains("x-ms-asf", StringComparison.Ordinal) ||
               normalized.StartsWith("text/", StringComparison.Ordinal) ||
               normalized.Contains("json", StringComparison.Ordinal) ||
               normalized.Contains("xml", StringComparison.Ordinal);
    }
}

/// <summary>
/// Incremental state machine that strips Shoutcast/Icecast inline ICY metadata blocks cleanly
/// out of live audio chunks so only raw codec frames enter the playback buffer.
/// </summary>
public sealed class IcyStreamDemuxer
{
    private enum ParsePhase
    {
        Audio,
        MetaLength,
        MetaPayload
    }

    private readonly int _metaInterval;
    private readonly byte[] _metaBuffer = new byte[4080];
    private ParsePhase _phase = ParsePhase.Audio;
    private int _remainingAudio;
    private int _expectedMetaBytes;
    private int _collectedMetaBytes;

    public IcyStreamDemuxer(int metaInterval)
    {
        _metaInterval = Math.Max(0, metaInterval);
        _remainingAudio = _metaInterval;
    }

    public int Process(
        ReadOnlySpan<byte> input,
        Span<byte> audioDestination,
        Action<string?>? onTitleChanged)
    {
        if (_metaInterval <= 0)
        {
            var count = Math.Min(input.Length, audioDestination.Length);
            input[..count].CopyTo(audioDestination);
            return count;
        }

        var inIndex = 0;
        var outIndex = 0;

        while (inIndex < input.Length && outIndex < audioDestination.Length)
        {
            switch (_phase)
            {
                case ParsePhase.Audio:
                {
                    var available = input.Length - inIndex;
                    var space = audioDestination.Length - outIndex;
                    var take = Math.Min(_remainingAudio, Math.Min(available, space));
                    if (take > 0)
                    {
                        input.Slice(inIndex, take).CopyTo(audioDestination.Slice(outIndex, take));
                        inIndex += take;
                        outIndex += take;
                        _remainingAudio -= take;
                    }

                    if (_remainingAudio == 0)
                    {
                        _phase = ParsePhase.MetaLength;
                    }

                    break;
                }

                case ParsePhase.MetaLength:
                {
                    var lengthUnits = input[inIndex++];
                    _expectedMetaBytes = lengthUnits * 16;
                    _collectedMetaBytes = 0;
                    if (_expectedMetaBytes == 0)
                    {
                        _remainingAudio = _metaInterval;
                        _phase = ParsePhase.Audio;
                    }
                    else
                    {
                        _phase = ParsePhase.MetaPayload;
                    }

                    break;
                }

                case ParsePhase.MetaPayload:
                {
                    var remainingMeta = _expectedMetaBytes - _collectedMetaBytes;
                    var take = Math.Min(remainingMeta, input.Length - inIndex);
                    if (take > 0)
                    {
                        input.Slice(inIndex, take).CopyTo(_metaBuffer.AsSpan(_collectedMetaBytes, take));
                        inIndex += take;
                        _collectedMetaBytes += take;
                    }

                    if (_collectedMetaBytes == _expectedMetaBytes)
                    {
                        var raw = Encoding.UTF8.GetString(_metaBuffer, 0, _expectedMetaBytes).TrimEnd('\0');
                        var title = IcyMetadataReader.ParseStreamTitle(raw);
                        if (!string.IsNullOrWhiteSpace(title))
                        {
                            onTitleChanged?.Invoke(title);
                        }

                        _remainingAudio = _metaInterval;
                        _phase = ParsePhase.Audio;
                    }

                    break;
                }
            }
        }

        return outIndex;
    }
}

/// <summary>
/// Bounded circular byte buffer supporting live capacity resizing without dropping already-buffered audio.
/// </summary>
public sealed class AudioRingBuffer
{
    private readonly Lock _gate = new();
    private byte[] _buffer;
    private int _targetCapacity;
    private int _head;
    private int _tail;
    private int _count;
    private bool _completed;
    private TaskCompletionSource<bool>? _readWaiter;
    private TaskCompletionSource<bool>? _writeWaiter;

    public AudioRingBuffer(int capacityBytes)
    {
        _targetCapacity = Math.Max(16_384, capacityBytes);
        _buffer = new byte[_targetCapacity];
    }

    public int Capacity
    {
        get
        {
            lock (_gate)
            {
                return _targetCapacity;
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    public void SetCapacity(int newCapacityBytes)
    {
        var desired = Math.Max(16_384, newCapacityBytes);
        TaskCompletionSource<bool>? writeToSignal = null;

        lock (_gate)
        {
            _targetCapacity = desired;
            if (desired > _buffer.Length)
            {
                var expanded = new byte[desired];
                CopyUnreadLinear(_buffer, _head, _count, expanded);
                _buffer = expanded;
                _head = 0;
                _tail = _count % _buffer.Length;
                writeToSignal = Interlocked.Exchange(ref _writeWaiter, null);
            }
        }

        writeToSignal?.TrySetResult(true);
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> source, CancellationToken ct)
    {
        var offset = 0;
        while (offset < source.Length)
        {
            ct.ThrowIfCancellationRequested();
            TaskCompletionSource<bool>? readToSignal = null;
            TaskCompletionSource<bool>? waitForSpace = null;

            lock (_gate)
            {
                if (_completed)
                {
                    return;
                }

                var free = _targetCapacity - _count;
                if (free > 0)
                {
                    var toWrite = Math.Min(source.Length - offset, free);
                    var firstPart = Math.Min(toWrite, _buffer.Length - _tail);
                    source.Slice(offset, firstPart).Span.CopyTo(_buffer.AsSpan(_tail, firstPart));
                    if (toWrite > firstPart)
                    {
                        var secondPart = toWrite - firstPart;
                        source.Slice(offset + firstPart, secondPart).Span.CopyTo(_buffer.AsSpan(0, secondPart));
                    }

                    _tail = (_tail + toWrite) % _buffer.Length;
                    _count += toWrite;
                    offset += toWrite;
                    readToSignal = Interlocked.Exchange(ref _readWaiter, null);
                }
                else
                {
                    waitForSpace = _writeWaiter ??= NewWaiter();
                }
            }

            readToSignal?.TrySetResult(true);
            if (waitForSpace is not null)
            {
                await waitForSpace.Task.WaitAsync(ct).ConfigureAwait(false);
            }
        }
    }

    public async Task<int> ReadAsync(Memory<byte> destination, CancellationToken ct)
    {
        if (destination.IsEmpty)
        {
            return 0;
        }

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            TaskCompletionSource<bool>? writeToSignal = null;
            TaskCompletionSource<bool>? waitForData = null;

            lock (_gate)
            {
                if (_count > 0)
                {
                    var toRead = Math.Min(destination.Length, _count);
                    var firstPart = Math.Min(toRead, _buffer.Length - _head);
                    _buffer.AsSpan(_head, firstPart).CopyTo(destination[..firstPart].Span);
                    if (toRead > firstPart)
                    {
                        var secondPart = toRead - firstPart;
                        _buffer.AsSpan(0, secondPart).CopyTo(destination.Slice(firstPart, secondPart).Span);
                    }

                    _head = (_head + toRead) % _buffer.Length;
                    _count -= toRead;
                    writeToSignal = Interlocked.Exchange(ref _writeWaiter, null);
                    writeToSignal?.TrySetResult(true);
                    return toRead;
                }

                if (_completed)
                {
                    return 0;
                }

                waitForData = _readWaiter ??= NewWaiter();
            }

            await waitForData.Task.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    public void Complete()
    {
        TaskCompletionSource<bool>? r;
        TaskCompletionSource<bool>? w;
        lock (_gate)
        {
            _completed = true;
            r = Interlocked.Exchange(ref _readWaiter, null);
            w = Interlocked.Exchange(ref _writeWaiter, null);
        }

        r?.TrySetResult(true);
        w?.TrySetResult(true);
    }

    private static void CopyUnreadLinear(byte[] source, int head, int count, byte[] destination)
    {
        if (count <= 0)
        {
            return;
        }

        var firstPart = Math.Min(count, source.Length - head);
        source.AsSpan(head, firstPart).CopyTo(destination.AsSpan(0, firstPart));
        if (count > firstPart)
        {
            source.AsSpan(0, count - firstPart).CopyTo(destination.AsSpan(firstPart, count - firstPart));
        }
    }

    private static TaskCompletionSource<bool> NewWaiter()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public enum BufferedProxyStartStatus
{
    Started,
    BypassToDirect,
    Failed
}

public sealed record BufferedProxyStartResult(
    BufferedProxyStartStatus Status,
    Uri? LocalUri,
    int BitrateKbps,
    string? ContentType);

/// <summary>
/// Loopback HTTP buffer proxy bound strictly to 127.0.0.1 that maintains a user-configurable
/// read-ahead buffer over a single upstream Shoutcast/Icecast TCP connection and strips inline
/// ICY metadata before audio reaches Windows Media Foundation.
/// </summary>
public sealed class BufferedStreamProxy : IDisposable
{
    private const int ChunkSize = 16_384;
    private const int MaxTransientReconnects = 3;

    private readonly string _upstreamUrl;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<bool> _initialPrebufferReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _clientLock = new();

    private TcpListener? _listener;
    private AudioRingBuffer? _ringBuffer;
    private CancellationTokenSource? _activeGetCts;
    private string _streamToken = string.Empty;
    private string _contentType = "audio/mpeg";
    private int _bufferSeconds;
    private int _bitrateKbps = AudioBufferSizing.DefaultBitrateKbps;
    private int _initialPrebufferBytes;
    private string? _lastTitle;
    private bool _disposed;

    public BufferedStreamProxy(string upstreamUrl, int bufferSeconds, HttpClient? httpClient = null)
    {
        _upstreamUrl = upstreamUrl;
        _bufferSeconds = AudioBufferSizing.NormalizeSeconds(bufferSeconds);
        if (httpClient is not null)
        {
            _http = httpClient;
            _ownsHttpClient = false;
        }
        else
        {
            _http = new HttpClient
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            _ownsHttpClient = true;
        }
    }

    public Uri? LocalUri { get; private set; }
    public int BufferedBytes => _ringBuffer?.Count ?? 0;
    public int CapacityBytes => _ringBuffer?.Capacity ?? 0;

    public event Action<string?>? NowPlayingChanged;
    public event Action? UpstreamFailed;

    public void UpdateBufferSeconds(int bufferSeconds)
    {
        if (_disposed)
        {
            return;
        }

        _bufferSeconds = AudioBufferSizing.NormalizeSeconds(bufferSeconds);
        _initialPrebufferBytes = AudioBufferSizing.CalculateInitialPrebufferBytes(_bufferSeconds, _bitrateKbps);
        _ringBuffer?.SetCapacity(AudioBufferSizing.CalculateCapacityBytes(_bufferSeconds, _bitrateKbps));
    }

    public async Task<BufferedProxyStartResult> StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (AudioBufferSizing.ShouldBypassProxy(_upstreamUrl))
        {
            return new BufferedProxyStartResult(BufferedProxyStartStatus.BypassToDirect, null, _bitrateKbps, null);
        }

        HttpResponseMessage? initialResponse = null;
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            connectCts.CancelAfter(TimeSpan.FromSeconds(12));

            initialResponse = await OpenUpstreamAsync(connectCts.Token).ConfigureAwait(false);
            if (!initialResponse.IsSuccessStatusCode)
            {
                initialResponse.Dispose();
                return new BufferedProxyStartResult(BufferedProxyStartStatus.Failed, null, _bitrateKbps, null);
            }

            var mediaType = initialResponse.Content.Headers.ContentType?.MediaType;
            if (AudioBufferSizing.IsPlaylistContentType(mediaType))
            {
                initialResponse.Dispose();
                return new BufferedProxyStartResult(BufferedProxyStartStatus.BypassToDirect, null, _bitrateKbps, mediaType);
            }

            _contentType = string.IsNullOrWhiteSpace(mediaType) ? "audio/mpeg" : mediaType!;
            _bitrateKbps = AudioBufferSizing.EstimateBitrateKbps(GetHeaderValue(initialResponse.Headers, "icy-br"));
            var capacity = AudioBufferSizing.CalculateCapacityBytes(_bufferSeconds, _bitrateKbps);
            _initialPrebufferBytes = AudioBufferSizing.CalculateInitialPrebufferBytes(_bufferSeconds, _bitrateKbps);
            _ringBuffer = new AudioRingBuffer(capacity);

            _streamToken = Guid.NewGuid().ToString("N");
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();

            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            LocalUri = new Uri($"http://127.0.0.1:{port}/stream/{_streamToken}");

            var initialMetaInt = ParseMetaInt(initialResponse.Headers);
            var responseToPump = initialResponse;
            initialResponse = null;

            _ = Task.Run(() => PumpUpstreamLoopAsync(responseToPump, initialMetaInt), CancellationToken.None);
            _ = Task.Run(AcceptClientsLoopAsync, CancellationToken.None);

            return new BufferedProxyStartResult(BufferedProxyStartStatus.Started, LocalUri, _bitrateKbps, _contentType);
        }
        catch
        {
            initialResponse?.Dispose();
            return new BufferedProxyStartResult(BufferedProxyStartStatus.Failed, null, _bitrateKbps, null);
        }
    }

    internal BufferedProxyStartResult StartFromPreopened(
        HttpResponseMessage openResponse,
        Stream openBodyStream,
        IcyStreamDemuxer activeDemuxer,
        ReadOnlyMemory<byte> initialAudioPrefix,
        string contentType,
        int bitrateKbps,
        int metaInt)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _contentType = string.IsNullOrWhiteSpace(contentType) ? "audio/mpeg" : contentType;
        _bitrateKbps = bitrateKbps > 0 ? bitrateKbps : AudioBufferSizing.DefaultBitrateKbps;
        var capacity = AudioBufferSizing.CalculateCapacityBytes(_bufferSeconds, _bitrateKbps);
        _initialPrebufferBytes = AudioBufferSizing.CalculateInitialPrebufferBytes(_bufferSeconds, _bitrateKbps);
        _ringBuffer = new AudioRingBuffer(capacity);

        _streamToken = Guid.NewGuid().ToString("N");
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();

        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        LocalUri = new Uri($"http://127.0.0.1:{port}/stream/{_streamToken}");

        _ = Task.Run(
            () => PumpUpstreamLoopAsync(openResponse, metaInt, initialAudioPrefix, openBodyStream, activeDemuxer),
            CancellationToken.None);
        _ = Task.Run(AcceptClientsLoopAsync, CancellationToken.None);

        return new BufferedProxyStartResult(BufferedProxyStartStatus.Started, LocalUri, _bitrateKbps, _contentType);
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
        int firstMetaInt,
        ReadOnlyMemory<byte> initialAudioPrefix = default,
        Stream? alreadyOpenedStream = null,
        IcyStreamDemuxer? activeDemuxer = null)
    {
        var ring = _ringBuffer!;
        HttpResponseMessage? currentResponse = firstResponse;
        var currentMetaInt = firstMetaInt;
        var pendingStream = alreadyOpenedStream;
        var pendingDemuxer = activeDemuxer;
        var consecutiveFailures = 0;
        var readBuffer = new byte[ChunkSize];
        var audioBuffer = new byte[ChunkSize];

        var prebufferTimeoutMs = Math.Max(4000, (_bufferSeconds * 1000) + 3000);
        _ = Task.Delay(prebufferTimeoutMs, _cts.Token).ContinueWith(
            _ => _initialPrebufferReady.TrySetResult(true),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        if (!initialAudioPrefix.IsEmpty)
        {
            try
            {
                await ring.WriteAsync(initialAudioPrefix, _cts.Token).ConfigureAwait(false);
                if (ring.Count >= _initialPrebufferBytes)
                {
                    _initialPrebufferReady.TrySetResult(true);
                }
            }
            catch
            {
                // Handled by cancellation check below.
            }
        }

        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var receivedAnyAudioOnConnection = false;
                if (currentResponse is not null)
                {
                    var activeResponse = currentResponse;
                    currentResponse = null;
                    var preOpenedStream = pendingStream;
                    pendingStream = null;
                    var demuxer = pendingDemuxer ?? new IcyStreamDemuxer(currentMetaInt);
                    pendingDemuxer = null;
                    try
                    {
                        using (activeResponse)
                        await using (var stream = preOpenedStream ?? await activeResponse.Content.ReadAsStreamAsync(_cts.Token).ConfigureAwait(false))
                        {
                            while (!_cts.IsCancellationRequested)
                            {
                                var bytesRead = await stream.ReadAsync(readBuffer.AsMemory(), _cts.Token).ConfigureAwait(false);
                                if (bytesRead == 0)
                                {
                                    break;
                                }

                                var audioBytes = demuxer.Process(readBuffer.AsSpan(0, bytesRead), audioBuffer.AsSpan(), EmitNowPlaying);
                                if (audioBytes > 0)
                                {
                                    receivedAnyAudioOnConnection = true;
                                    consecutiveFailures = 0;
                                    await ring.WriteAsync(audioBuffer.AsMemory(0, audioBytes), _cts.Token).ConfigureAwait(false);

                                    if (ring.Count >= _initialPrebufferBytes)
                                    {
                                        _initialPrebufferReady.TrySetResult(true);
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
                        // Transient read error handled below by seamless reconnect while local buffer drains.
                    }
                }

                if (_cts.IsCancellationRequested)
                {
                    break;
                }

                consecutiveFailures = receivedAnyAudioOnConnection ? 1 : consecutiveFailures + 1;
                if (consecutiveFailures > MaxTransientReconnects)
                {
                    UpstreamFailed?.Invoke();
                    break;
                }

                var backoffMs = consecutiveFailures switch
                {
                    1 => 250,
                    2 => 750,
                    _ => 1500
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
                    currentMetaInt = ParseMetaInt(opened.Headers);
                }
                catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    // Continue retry loop up to MaxTransientReconnects.
                }
            }
        }
        finally
        {
            _initialPrebufferReady.TrySetResult(true);
            ring.Complete();
        }
    }

    private async Task AcceptClientsLoopAsync()
    {
        var listener = _listener;
        if (listener is null)
        {
            return;
        }

        while (!_cts.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                _ = Task.Run(() => HandleClientAsync(client), CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                client?.Dispose();
                break;
            }
            catch (ObjectDisposedException)
            {
                client?.Dispose();
                break;
            }
            catch (SocketException)
            {
                client?.Dispose();
                if (_cts.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        CancellationTokenSource? clientCts = null;
        try
        {
            using (client)
            {
                client.NoDelay = true;
                await using var stream = client.GetStream();

                var requestLine = await ReadHttpRequestLineAsync(stream, _cts.Token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(requestLine))
                {
                    return;
                }

                var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                {
                    return;
                }

                var method = parts[0].ToUpperInvariant();
                var path = parts[1];
                var expectedPath = $"/stream/{_streamToken}";
                if (!string.Equals(path, expectedPath, StringComparison.Ordinal) ||
                    (method != "GET" && method != "HEAD"))
                {
                    var notFound = Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(notFound, _cts.Token).ConfigureAwait(false);
                    return;
                }

                var headerBlock =
                    $"HTTP/1.1 200 OK\r\n" +
                    $"Content-Type: {_contentType}\r\n" +
                    $"Cache-Control: no-store\r\n" +
                    $"Accept-Ranges: none\r\n" +
                    $"Connection: close\r\n\r\n";

                await stream.WriteAsync(Encoding.ASCII.GetBytes(headerBlock), _cts.Token).ConfigureAwait(false);
                await stream.FlushAsync(_cts.Token).ConfigureAwait(false);

                if (method == "HEAD")
                {
                    return;
                }

                CancellationTokenSource? previousGetCts;
                lock (_clientLock)
                {
                    previousGetCts = _activeGetCts;
                    clientCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    _activeGetCts = clientCts;
                }

                if (previousGetCts is not null)
                {
                    try { previousGetCts.Cancel(); } catch { }
                    previousGetCts.Dispose();
                }

                await _initialPrebufferReady.Task.WaitAsync(clientCts.Token).ConfigureAwait(false);

                var ring = _ringBuffer!;
                var writeBuffer = new byte[ChunkSize];
                while (!clientCts.Token.IsCancellationRequested)
                {
                    var read = await ring.ReadAsync(writeBuffer.AsMemory(), clientCts.Token).ConfigureAwait(false);
                    if (read <= 0)
                    {
                        break;
                    }

                    await stream.WriteAsync(writeBuffer.AsMemory(0, read), clientCts.Token).ConfigureAwait(false);
                }

                await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch
        {
            // Client disconnect / cancellation is normal during station switch or stop.
        }
        finally
        {
            if (clientCts is not null)
            {
                lock (_clientLock)
                {
                    if (ReferenceEquals(_activeGetCts, clientCts))
                    {
                        _activeGetCts = null;
                    }
                }

                clientCts.Dispose();
            }
        }
    }

    private void EmitNowPlaying(string? title)
    {
        if (string.IsNullOrWhiteSpace(title) || string.Equals(_lastTitle, title, StringComparison.Ordinal))
        {
            return;
        }

        _lastTitle = title;
        NowPlayingChanged?.Invoke(title);
    }

    private static async Task<string?> ReadHttpRequestLineAsync(NetworkStream stream, CancellationToken ct)
    {
        using var headerTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headerTimeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

        var buffer = new byte[4096];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), headerTimeoutCts.Token).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            var span = buffer.AsSpan(0, total);
            var endOfHeaders = span.IndexOf("\r\n\r\n"u8);
            if (endOfHeaders >= 0)
            {
                var lineEnd = span.IndexOf("\r\n"u8);
                return Encoding.ASCII.GetString(lineEnd >= 0 ? span[..lineEnd] : span);
            }
        }

        return null;
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
        _cts.Cancel();
        _initialPrebufferReady.TrySetResult(true);
        _ringBuffer?.Complete();

        lock (_clientLock)
        {
            try { _activeGetCts?.Cancel(); } catch { }
            _activeGetCts?.Dispose();
            _activeGetCts = null;
        }

        try { _listener?.Stop(); } catch { }
        _cts.Dispose();
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }
}
