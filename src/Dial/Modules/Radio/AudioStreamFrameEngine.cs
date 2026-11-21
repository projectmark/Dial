namespace Dial.Modules.Radio;

public enum AudioCodecKind
{
    Unknown = 0,
    Mp3 = 1,
    AacAdts = 2
}

public sealed record AudioStreamFormat(
    AudioCodecKind Codec,
    int SampleRate,
    int Channels,
    int BitrateKbps);

public sealed record AudioFrame(
    byte[] Payload,
    TimeSpan Duration,
    int SampleRate,
    int Channels,
    int BitrateKbps);

/// <summary>
/// Pure-C# MPEG-1/2/2.5 Layer III (MP3) and AAC ADTS frame parser and stream accumulator
/// with ID3v2 header skipping and 2-frame consecutive sync validation.
/// </summary>
public static class AudioFrameParser
{
    private static readonly int[,] Mp3SampleRates =
    {
        { 11025, 12000, 8000 }, // ver 0: MPEG 2.5
        { 0, 0, 0 },            // ver 1: reserved
        { 22050, 24000, 16000 },// ver 2: MPEG 2
        { 44100, 48000, 32000 } // ver 3: MPEG 1
    };

    private static readonly int[] Mp3BitratesMpeg1L3 =
        [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0];

    private static readonly int[] Mp3BitratesMpeg2L3 =
        [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0];

    private static readonly int[] AdtsSampleRates =
        [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350, 0, 0, 0];

    /// <summary>
    /// Returns the total byte length of a leading ID3v2 tag if present at offset 0, otherwise 0.
    /// </summary>
    public static int GetId3v2HeaderLength(ReadOnlySpan<byte> data)
    {
        if (data.Length < 10)
        {
            return 0;
        }

        if (data[0] != (byte)'I' || data[1] != (byte)'D' || data[2] != (byte)'3')
        {
            return 0;
        }

        if (data[3] == 0xFF || data[4] == 0xFF)
        {
            return 0;
        }

        if ((data[6] & 0x80) != 0 || (data[7] & 0x80) != 0 || (data[8] & 0x80) != 0 || (data[9] & 0x80) != 0)
        {
            return 0;
        }

        var size = ((data[6] & 0x7F) << 21) |
                   ((data[7] & 0x7F) << 14) |
                   ((data[8] & 0x7F) << 7) |
                   (data[9] & 0x7F);

        var total = 10 + size;
        return total is >= 10 and <= 1_048_576 ? total : 0;
    }

    public static bool TryParseMp3Header(
        ReadOnlySpan<byte> data,
        int offset,
        out int frameLength,
        out int sampleRate,
        out int channels,
        out int bitrateKbps,
        out int samplesPerFrame)
    {
        frameLength = sampleRate = channels = bitrateKbps = samplesPerFrame = 0;
        if (offset < 0 || offset + 4 > data.Length)
        {
            return false;
        }

        var b0 = data[offset];
        var b1 = data[offset + 1];
        if (b0 != 0xFF || (b1 & 0xE0) != 0xE0)
        {
            return false;
        }

        var version = (b1 >> 3) & 0x03;
        var layer = (b1 >> 1) & 0x03; // 1 == Layer III
        if (version == 1 || layer != 1)
        {
            return false;
        }

        var b2 = data[offset + 2];
        var bitrateIdx = (b2 >> 4) & 0x0F;
        var sampleRateIdx = (b2 >> 2) & 0x03;
        var padding = (b2 >> 1) & 0x01;
        if (bitrateIdx == 0 || bitrateIdx == 15 || sampleRateIdx == 3)
        {
            return false;
        }

        bitrateKbps = version == 3 ? Mp3BitratesMpeg1L3[bitrateIdx] : Mp3BitratesMpeg2L3[bitrateIdx];
        sampleRate = Mp3SampleRates[version, sampleRateIdx];
        if (bitrateKbps <= 0 || sampleRate <= 0)
        {
            return false;
        }

        samplesPerFrame = version == 3 ? 1152 : 576;
        var multiplier = version == 3 ? 144 : 72;
        frameLength = (multiplier * (bitrateKbps * 1000) / sampleRate) + padding;

        var mode = (data[offset + 3] >> 6) & 0x03;
        channels = mode == 3 ? 1 : 2;
        return frameLength is >= 24 and <= 4096;
    }

    public static bool TryParseAdtsHeader(
        ReadOnlySpan<byte> data,
        int offset,
        out int frameLength,
        out int sampleRate,
        out int channels,
        out int bitrateKbps,
        out int samplesPerFrame)
    {
        frameLength = sampleRate = channels = bitrateKbps = samplesPerFrame = 0;
        if (offset < 0 || offset + 7 > data.Length)
        {
            return false;
        }

        var b0 = data[offset];
        var b1 = data[offset + 1];
        if (b0 != 0xFF || (b1 & 0xF0) != 0xF0)
        {
            return false;
        }

        var layer = (b1 >> 1) & 0x03;
        if (layer != 0)
        {
            return false;
        }

        var b2 = data[offset + 2];
        var b3 = data[offset + 3];
        var b4 = data[offset + 4];
        var b5 = data[offset + 5];
        var b6 = data[offset + 6];

        var sampleRateIdx = (b2 >> 2) & 0x0F;
        if ((uint)sampleRateIdx >= (uint)AdtsSampleRates.Length)
        {
            return false;
        }

        sampleRate = AdtsSampleRates[sampleRateIdx];
        if (sampleRate <= 0)
        {
            return false;
        }

        var channelConfig = ((b2 & 0x01) << 2) | ((b3 >> 6) & 0x03);
        channels = channelConfig == 0 ? 2 : Math.Clamp(channelConfig, 1, 2);

        frameLength = ((b3 & 0x03) << 11) | (b4 << 3) | ((b5 >> 5) & 0x07);
        var rawDataBlocks = (b6 & 0x03) + 1;
        samplesPerFrame = 1024 * rawDataBlocks;
        if (frameLength is < 7 or > 8192)
        {
            return false;
        }

        bitrateKbps = Math.Clamp(
            (int)Math.Round(frameLength * 8.0 * sampleRate / (samplesPerFrame * 1000.0)),
            16,
            1411);
        return true;
    }

    /// <summary>
    /// Detects MP3 or ADTS AAC stream format by locating 2 consecutive valid sync frames.
    /// </summary>
    public static bool TryDetectFormat(
        ReadOnlySpan<byte> data,
        string? contentTypeHint,
        int fallbackBitrateKbps,
        out AudioStreamFormat format,
        out int firstFrameOffset)
    {
        format = default!;
        firstFrameOffset = 0;

        var scanStart = GetId3v2HeaderLength(data);
        if (scanStart >= data.Length)
        {
            return false;
        }

        var preferAac = !string.IsNullOrWhiteSpace(contentTypeHint) &&
                        contentTypeHint.Contains("aac", StringComparison.OrdinalIgnoreCase);

        if (preferAac)
        {
            return TryScanTwoConsecutive(data, scanStart, AudioCodecKind.AacAdts, fallbackBitrateKbps, out format, out firstFrameOffset) ||
                   TryScanTwoConsecutive(data, scanStart, AudioCodecKind.Mp3, fallbackBitrateKbps, out format, out firstFrameOffset);
        }

        return TryScanTwoConsecutive(data, scanStart, AudioCodecKind.Mp3, fallbackBitrateKbps, out format, out firstFrameOffset) ||
               TryScanTwoConsecutive(data, scanStart, AudioCodecKind.AacAdts, fallbackBitrateKbps, out format, out firstFrameOffset);
    }

    internal static bool TryScanTwoConsecutive(
        ReadOnlySpan<byte> data,
        int scanStart,
        AudioCodecKind codec,
        int fallbackBitrateKbps,
        out AudioStreamFormat format,
        out int firstFrameOffset)
    {
        format = default!;
        firstFrameOffset = 0;
        var minHeader = codec == AudioCodecKind.AacAdts ? 7 : 4;

        for (var i = Math.Max(0, scanStart); i <= data.Length - minHeader; i++)
        {
            if (!TryParseHeader(codec, data, i, out var len1, out var sr1, out var ch1, out var br1, out _))
            {
                continue;
            }

            if (i + len1 + minHeader > data.Length)
            {
                break;
            }

            if (TryParseHeader(codec, data, i + len1, out _, out var sr2, out _, out _, out _) && sr2 == sr1)
            {
                var resolvedBitrate = br1 > 0 ? br1 : AudioBufferSizing.DefaultBitrateKbps;
                if (fallbackBitrateKbps > 0 && br1 <= 0)
                {
                    resolvedBitrate = fallbackBitrateKbps;
                }

                format = new AudioStreamFormat(codec, sr1, ch1, resolvedBitrate);
                firstFrameOffset = i;
                return true;
            }
        }

        return false;
    }

    internal static bool TryParseHeader(
        AudioCodecKind codec,
        ReadOnlySpan<byte> data,
        int offset,
        out int frameLength,
        out int sampleRate,
        out int channels,
        out int bitrateKbps,
        out int samplesPerFrame)
    {
        return codec switch
        {
            AudioCodecKind.Mp3 => TryParseMp3Header(data, offset, out frameLength, out sampleRate, out channels, out bitrateKbps, out samplesPerFrame),
            AudioCodecKind.AacAdts => TryParseAdtsHeader(data, offset, out frameLength, out sampleRate, out channels, out bitrateKbps, out samplesPerFrame),
            _ => ZeroOut(out frameLength, out sampleRate, out channels, out bitrateKbps, out samplesPerFrame)
        };
    }

    private static bool ZeroOut(
        out int frameLength,
        out int sampleRate,
        out int channels,
        out int bitrateKbps,
        out int samplesPerFrame)
    {
        frameLength = sampleRate = channels = bitrateKbps = samplesPerFrame = 0;
        return false;
    }
}

/// <summary>
/// Incremental byte-to-frame extractor that buffers chunk boundaries, skips ID3v2 headers,
/// and drops incomplete tail bytes on reconnect so every emitted frame is codec-complete.
/// </summary>
public sealed class AudioFrameAccumulator
{
    private readonly AudioCodecKind _codec;
    private byte[] _buffer = new byte[32_768];
    private int _start;
    private int _end;
    private bool _locked;
    private int _pendingSkipBytes;

    public AudioFrameAccumulator(AudioCodecKind codec)
    {
        _codec = codec;
    }

    public void Append(ReadOnlySpan<byte> chunk)
    {
        if (chunk.IsEmpty)
        {
            return;
        }

        var input = chunk;
        if (_pendingSkipBytes > 0)
        {
            var skip = Math.Min(_pendingSkipBytes, input.Length);
            _pendingSkipBytes -= skip;
            input = input[skip..];
            if (input.IsEmpty)
            {
                return;
            }
        }

        CompactIfNeeded(input.Length);
        input.CopyTo(_buffer.AsSpan(_end, input.Length));
        _end += input.Length;
    }

    public void ExtractFrames(List<AudioFrame> destination)
    {
        while (true)
        {
            var available = _end - _start;
            if (available <= 0)
            {
                _start = _end = 0;
                return;
            }

            var span = _buffer.AsSpan(_start, available);

            if (!_locked)
            {
                if (span.Length >= 10)
                {
                    var id3Len = AudioFrameParser.GetId3v2HeaderLength(span);
                    if (id3Len > 0)
                    {
                        if (id3Len <= available)
                        {
                            _start += id3Len;
                            continue;
                        }

                        _pendingSkipBytes = id3Len - available;
                        _start = _end = 0;
                        return;
                    }
                }

                if (!AudioFrameParser.TryScanTwoConsecutive(_buffer.AsSpan(0, _end), _start, _codec, 0, out _, out var firstOffset))
                {
                    // Keep only last 16 KB while scanning for sync to bound unparsed memory.
                    if (_end - _start > 16_384)
                    {
                        _start = _end - 8_192;
                    }
                    return;
                }

                _start = firstOffset;
                _locked = true;
                available = _end - _start;
                span = _buffer.AsSpan(_start, available);
            }

            if (!AudioFrameParser.TryParseHeader(_codec, span, 0, out var frameLen, out var sr, out var ch, out var br, out var spf))
            {
                _locked = false;
                _start++;
                continue;
            }

            if (frameLen > available)
            {
                return;
            }

            var payload = new byte[frameLen];
            span[..frameLen].CopyTo(payload);
            var duration = TimeSpan.FromSeconds((double)spf / sr);
            destination.Add(new AudioFrame(payload, duration, sr, ch, br));
            _start += frameLen;
        }
    }

    public void ResetForNewConnection()
    {
        _start = 0;
        _end = 0;
        _locked = false;
        _pendingSkipBytes = 0;
    }

    private void CompactIfNeeded(int incomingLength)
    {
        var count = _end - _start;
        if (_start > 0 && count > 0)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, count);
            _start = 0;
            _end = count;
        }
        else if (_start > 0 && count == 0)
        {
            _start = _end = 0;
        }

        if (_end + incomingLength > _buffer.Length)
        {
            var newSize = Math.Max(_buffer.Length * 2, _end + incomingLength);
            Array.Resize(ref _buffer, newSize);
        }
    }
}

/// <summary>
/// Thread-safe duration-accurate frame queue that genuinely buffers N user-selected seconds ahead,
/// detects underruns with hysteresis refill, and supports live target buffer updates.
/// </summary>
public sealed class TimedAudioFrameQueue
{
    private readonly Lock _gate = new();
    private readonly Queue<AudioFrame> _frames = new();
    private TimeSpan _targetPrebuffer;
    private TimeSpan _maxCapacity;
    private TimeSpan _rebufferThreshold;
    private TimeSpan _bufferedDuration = TimeSpan.Zero;
    private bool _initialPrebufferReached;
    private bool _isRebuffering;
    private bool _completed;
    private TaskCompletionSource<bool>? _initialPrebufferWaiter;
    private TaskCompletionSource<bool>? _readWaiter;
    private TaskCompletionSource<bool>? _spaceWaiter;

    public TimedAudioFrameQueue(int bufferSeconds)
    {
        ApplyBufferSecondsLocked(AudioBufferSizing.NormalizeSeconds(bufferSeconds));
    }

    public event Action<bool>? BufferingChanged;

    public TimeSpan TargetPrebufferDuration
    {
        get
        {
            lock (_gate)
            {
                return _targetPrebuffer;
            }
        }
    }

    public TimeSpan BufferedDuration
    {
        get
        {
            lock (_gate)
            {
                return _bufferedDuration;
            }
        }
    }

    public int FrameCount
    {
        get
        {
            lock (_gate)
            {
                return _frames.Count;
            }
        }
    }

    public void UpdateBufferSeconds(int bufferSeconds)
    {
        TaskCompletionSource<bool>? spaceSignal = null;
        TaskCompletionSource<bool>? initSignal = null;

        lock (_gate)
        {
            ApplyBufferSecondsLocked(AudioBufferSizing.NormalizeSeconds(bufferSeconds));
            if (_bufferedDuration < _maxCapacity)
            {
                spaceSignal = Interlocked.Exchange(ref _spaceWaiter, null);
            }

            if (_bufferedDuration >= _targetPrebuffer && !_initialPrebufferReached)
            {
                _initialPrebufferReached = true;
                initSignal = Interlocked.Exchange(ref _initialPrebufferWaiter, null);
            }
        }

        spaceSignal?.TrySetResult(true);
        initSignal?.TrySetResult(true);
    }

    public async Task EnqueueAsync(AudioFrame frame, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(frame);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            TaskCompletionSource<bool>? waitSpace = null;
            TaskCompletionSource<bool>? wakeInit = null;
            TaskCompletionSource<bool>? wakeRead = null;
            bool? bufferingTransition = null;
            var enqueued = false;

            lock (_gate)
            {
                if (_completed)
                {
                    return;
                }

                if (_bufferedDuration < _maxCapacity || _frames.Count == 0)
                {
                    _frames.Enqueue(frame);
                    _bufferedDuration += frame.Duration;
                    enqueued = true;

                    if (!_initialPrebufferReached && _bufferedDuration >= _targetPrebuffer)
                    {
                        _initialPrebufferReached = true;
                        wakeInit = Interlocked.Exchange(ref _initialPrebufferWaiter, null);
                    }

                    if (_isRebuffering && _bufferedDuration >= _rebufferThreshold)
                    {
                        _isRebuffering = false;
                        bufferingTransition = false;
                        wakeRead = Interlocked.Exchange(ref _readWaiter, null);
                    }
                    else if (!_isRebuffering)
                    {
                        wakeRead = Interlocked.Exchange(ref _readWaiter, null);
                    }
                }
                else
                {
                    waitSpace = _spaceWaiter ??= NewWaiter();
                }
            }

            if (bufferingTransition.HasValue)
            {
                BufferingChanged?.Invoke(bufferingTransition.Value);
            }

            wakeInit?.TrySetResult(true);
            wakeRead?.TrySetResult(true);

            if (enqueued)
            {
                return;
            }

            await waitSpace!.Task.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task WaitForInitialPrebufferAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            TaskCompletionSource<bool>? waitInit;

            lock (_gate)
            {
                if (_initialPrebufferReached || _completed || _bufferedDuration >= _targetPrebuffer)
                {
                    _initialPrebufferReached = true;
                    return;
                }

                waitInit = _initialPrebufferWaiter ??= NewWaiter();
            }

            await waitInit.Task.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    public async ValueTask<AudioFrame?> DequeueAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            TaskCompletionSource<bool>? waitData = null;
            TaskCompletionSource<bool>? wakeSpace = null;
            AudioFrame? item = null;
            bool? bufferingTransition = null;

            lock (_gate)
            {
                if (_frames.Count > 0 && (!_isRebuffering || _completed || _bufferedDuration >= _rebufferThreshold))
                {
                    if (_isRebuffering)
                    {
                        _isRebuffering = false;
                        bufferingTransition = false;
                    }

                    item = _frames.Dequeue();
                    _bufferedDuration -= item.Duration;
                    if (_bufferedDuration < TimeSpan.Zero)
                    {
                        _bufferedDuration = TimeSpan.Zero;
                    }

                    wakeSpace = Interlocked.Exchange(ref _spaceWaiter, null);
                }
                else if (_completed && _frames.Count == 0)
                {
                    return null;
                }
                else
                {
                    if (!_isRebuffering && _frames.Count == 0 && !_completed)
                    {
                        _isRebuffering = true;
                        bufferingTransition = true;
                    }

                    waitData = _readWaiter ??= NewWaiter();
                }
            }

            if (bufferingTransition.HasValue)
            {
                BufferingChanged?.Invoke(bufferingTransition.Value);
            }

            wakeSpace?.TrySetResult(true);

            if (item is not null)
            {
                return item;
            }

            await waitData!.Task.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    public void Complete()
    {
        TaskCompletionSource<bool>? i;
        TaskCompletionSource<bool>? r;
        TaskCompletionSource<bool>? s;

        lock (_gate)
        {
            _completed = true;
            _initialPrebufferReached = true;
            _isRebuffering = false;
            i = Interlocked.Exchange(ref _initialPrebufferWaiter, null);
            r = Interlocked.Exchange(ref _readWaiter, null);
            s = Interlocked.Exchange(ref _spaceWaiter, null);
        }

        i?.TrySetResult(true);
        r?.TrySetResult(true);
        s?.TrySetResult(true);
    }

    private void ApplyBufferSecondsLocked(int bufferSeconds)
    {
        var secs = AudioBufferSizing.NormalizeSeconds(bufferSeconds);
        _targetPrebuffer = TimeSpan.FromSeconds(secs);
        _maxCapacity = _targetPrebuffer + TimeSpan.FromSeconds(12);
        var rebufSeconds = Math.Min(secs, Math.Clamp(secs * 0.5, 1.5, 6.0));
        _rebufferThreshold = TimeSpan.FromSeconds(rebufSeconds);
    }

    private static TaskCompletionSource<bool> NewWaiter()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
