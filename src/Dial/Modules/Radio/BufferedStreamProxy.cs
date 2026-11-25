using System.Globalization;
using System.Net;
using System.Text;

namespace Dial.Modules.Radio;

/// <summary>
/// Helpers for resolving and sizing user-configurable read-ahead audio buffers
/// and sharing a single pooled HTTP transport across radio services.
/// </summary>
public static class AudioBufferSizing
{
    public const string OptionKey = RadioOptions.BufferSeconds;
    public const int DefaultBufferSeconds = 5;
    public const int DefaultBitrateKbps = 192;

    public static IReadOnlyList<int> Presets { get; } = (int[])[2, 5, 10, 15, 30];

    /// <summary>
    /// Shared pooled HTTP client for live audio streams, ICY metadata, and directory queries
    /// to preserve DNS/TLS reuse and avoid redundant socket pools.
    /// </summary>
    internal static HttpClient SharedStreamHttpClient { get; } = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        ConnectTimeout = TimeSpan.FromSeconds(12),
        AutomaticDecompression = DecompressionMethods.None,
        AllowAutoRedirect = true
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

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
