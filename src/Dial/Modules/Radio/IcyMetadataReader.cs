using System.Net.Http.Headers;
using System.Text;

namespace Dial.Modules.Radio;

/// <summary>
/// Reads inline ICY Shoutcast/Icecast metadata blocks from a live audio stream without interrupting playback.
/// </summary>
public interface IIcyMetadataReader : IDisposable
{
    event Action<string?>? NowPlayingChanged;
    void Start(string streamUrl);
    void Stop();
}

/// <summary>
/// HTTP ICY-MetaData background reader extracting StreamTitle updates from live streams.
/// </summary>
public sealed class IcyMetadataReader : IIcyMetadataReader
{
    private readonly HttpClient _httpClient;
    private readonly Lock _gate = new();
    private CancellationTokenSource? _cts;
    private string? _lastTitle;
    private bool _disposed;

    public IcyMetadataReader()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
    }

    public event Action<string?>? NowPlayingChanged;

    public void Start(string streamUrl)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(streamUrl);

        CancellationTokenSource cts;
        lock (_gate)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _lastTitle = null;
            cts = new CancellationTokenSource();
            _cts = cts;
        }

        _ = Task.Run(() => RunWithRetriesAsync(streamUrl, cts.Token));
    }

    public void Stop()
    {
        lock (_gate)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _lastTitle = null;
        }
    }

    /// <summary>
    /// Extracts the track title from an ICY metadata payload such as <c>StreamTitle='Artist - Song';</c>.
    /// </summary>
    public static string? ParseStreamTitle(string? rawMetadata)
    {
        if (string.IsNullOrWhiteSpace(rawMetadata))
        {
            return null;
        }

        const string key = "StreamTitle=";
        var keyIndex = rawMetadata.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (keyIndex < 0)
        {
            return null;
        }

        var valueStart = keyIndex + key.Length;
        if (valueStart >= rawMetadata.Length)
        {
            return null;
        }

        var quoteChar = rawMetadata[valueStart];
        if (quoteChar is '\'' or '"')
        {
            var contentStart = valueStart + 1;
            var closingSequence = $"{quoteChar};";
            var closingIndex = rawMetadata.IndexOf(closingSequence, contentStart, StringComparison.Ordinal);
            if (closingIndex < 0)
            {
                closingIndex = rawMetadata.LastIndexOf(quoteChar);
            }

            if (closingIndex <= contentStart)
            {
                return null;
            }

            var extracted = rawMetadata[contentStart..closingIndex].Trim();
            return string.IsNullOrWhiteSpace(extracted) ? null : extracted;
        }

        var semicolonIndex = rawMetadata.IndexOf(';', valueStart);
        var unquoted = (semicolonIndex >= 0
                ? rawMetadata[valueStart..semicolonIndex]
                : rawMetadata[valueStart..])
            .Trim();
        return string.IsNullOrWhiteSpace(unquoted) ? null : unquoted;
    }

    private async Task RunWithRetriesAsync(string streamUrl, CancellationToken ct)
    {
        var attempts = 0;
        while (!ct.IsCancellationRequested && attempts < 4)
        {
            try
            {
                await ReadStreamLoopAsync(streamUrl, ct).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                attempts++;
                if (ct.IsCancellationRequested || attempts >= 4)
                {
                    return;
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(4), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task ReadStreamLoopAsync(string streamUrl, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, streamUrl);
        request.Headers.TryAddWithoutValidation("Icy-MetaData", "1");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Dial", "0.2"));

        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        if (!TryGetHeaderInt(response, "icy-metaint", out var metaInterval) || metaInterval <= 0)
        {
            return;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var skipBuffer = new byte[Math.Min(metaInterval, 16384)];
        var lenBuffer = new byte[1];

        while (!ct.IsCancellationRequested)
        {
            if (!await SkipExactAsync(stream, metaInterval, skipBuffer, ct).ConfigureAwait(false))
            {
                break;
            }

            if (!await ReadExactAsync(stream, lenBuffer, 1, ct).ConfigureAwait(false))
            {
                break;
            }

            var metaLength = lenBuffer[0] * 16;
            if (metaLength == 0)
            {
                continue;
            }

            var metaBuffer = new byte[metaLength];
            if (!await ReadExactAsync(stream, metaBuffer, metaLength, ct).ConfigureAwait(false))
            {
                break;
            }

            var raw = Encoding.UTF8.GetString(metaBuffer).TrimEnd('\0', ' ');
            var title = ParseStreamTitle(raw);

            var shouldNotify = false;
            lock (_gate)
            {
                if (!ct.IsCancellationRequested && !string.Equals(_lastTitle, title, StringComparison.Ordinal))
                {
                    _lastTitle = title;
                    shouldNotify = true;
                }
            }

            if (shouldNotify)
            {
                NowPlayingChanged?.Invoke(title);
            }
        }
    }

    private static bool TryGetHeaderInt(HttpResponseMessage response, string name, out int value)
    {
        value = 0;
        if (response.Headers.TryGetValues(name, out var values) ||
            response.Content.Headers.TryGetValues(name, out values))
        {
            var first = values.FirstOrDefault();
            return int.TryParse(first, out value);
        }

        return false;
    }

    private static async Task<bool> SkipExactAsync(Stream stream, int count, byte[] buffer, CancellationToken ct)
    {
        var remaining = count;
        while (remaining > 0)
        {
            var chunk = Math.Min(remaining, buffer.Length);
            var read = await stream.ReadAsync(buffer.AsMemory(0, chunk), ct).ConfigureAwait(false);
            if (read <= 0)
            {
                return false;
            }

            remaining -= read;
        }

        return true;
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
    {
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), ct).ConfigureAwait(false);
            if (read <= 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;
        _httpClient.Dispose();
    }
}
