using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Dial.Modules.Radio;

/// <summary>
/// Searches worldwide public internet radio directories via Radio Browser mirrors.
/// </summary>
public interface IRadioBrowserService : IDisposable
{
    Task<IReadOnlyList<RadioBrowserCountry>> GetCountriesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<RadioStation>> SearchStationsAsync(
        string? query,
        string? countryCode,
        int limit = 60,
        CancellationToken ct = default);
}

/// <summary>
/// Radio Browser API client with automatic mirror failover and in-memory country caching.
/// </summary>
public sealed class RadioBrowserService : IRadioBrowserService
{
    private static readonly string[] Mirrors =
    [
        "https://de1.api.radio-browser.info",
        "https://fi1.api.radio-browser.info",
        "https://at1.api.radio-browser.info"
    ];

    private readonly HttpClient _httpClient;
    private readonly Lock _cacheGate = new();
    private IReadOnlyList<RadioBrowserCountry>? _cachedCountries;
    private bool _disposed;

    public RadioBrowserService()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Dial", "0.2"));
    }

    public async Task<IReadOnlyList<RadioBrowserCountry>> GetCountriesAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_cacheGate)
        {
            if (_cachedCountries is not null)
            {
                return _cachedCountries;
            }
        }

        var json = await FetchFromMirrorsAsync("/json/countries?order=name&reverse=false", ct).ConfigureAwait(false);
        var parsed = ParseCountriesJson(json);

        lock (_cacheGate)
        {
            _cachedCountries = parsed;
        }

        return parsed;
    }

    public async Task<IReadOnlyList<RadioStation>> SearchStationsAsync(
        string? query,
        string? countryCode,
        int limit = 60,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var clampedLimit = Math.Clamp(limit, 1, 150);

        var sb = new StringBuilder($"/json/stations/search?hidebroken=true&order=clickcount&reverse=true&limit={clampedLimit}");
        if (!string.IsNullOrWhiteSpace(query))
        {
            sb.Append("&name=").Append(Uri.EscapeDataString(query.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(countryCode))
        {
            sb.Append("&countrycode=").Append(Uri.EscapeDataString(countryCode.Trim().ToUpperInvariant()));
        }

        var json = await FetchFromMirrorsAsync(sb.ToString(), ct).ConfigureAwait(false);
        var results = ParseStationsJson(json).ToList();

        // Fallback tag search for single-keyword genre queries if name search returned very few results.
        if (!string.IsNullOrWhiteSpace(query) && !query.Trim().Contains(' ') && results.Count < 8)
        {
            var tagSb = new StringBuilder($"/json/stations/search?hidebroken=true&order=clickcount&reverse=true&limit={clampedLimit}");
            tagSb.Append("&tag=").Append(Uri.EscapeDataString(query.Trim().ToLowerInvariant()));
            if (!string.IsNullOrWhiteSpace(countryCode))
            {
                tagSb.Append("&countrycode=").Append(Uri.EscapeDataString(countryCode.Trim().ToUpperInvariant()));
            }

            try
            {
                var tagJson = await FetchFromMirrorsAsync(tagSb.ToString(), ct).ConfigureAwait(false);
                var existingUrls = new HashSet<string>(results.Select(r => r.StreamUrl), StringComparer.OrdinalIgnoreCase);
                foreach (var item in ParseStationsJson(tagJson))
                {
                    if (existingUrls.Add(item.StreamUrl))
                    {
                        results.Add(item);
                        if (results.Count >= clampedLimit)
                        {
                            break;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Keep primary name search results if secondary tag query fails.
            }
        }

        return results;
    }

    public static IReadOnlyList<RadioStation> ParseStationsJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<RadioStation>();
        }

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<RadioStation>();
        }

        var stations = new List<RadioStation>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var name = GetString(el, "name").Trim();
            var urlResolved = GetString(el, "url_resolved").Trim();
            var urlRaw = GetString(el, "url").Trim();
            var primaryUrl = !string.IsNullOrWhiteSpace(urlResolved) ? urlResolved : urlRaw;

            if (string.IsNullOrWhiteSpace(name) ||
                !Uri.TryCreate(primaryUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                !seenUrls.Add(primaryUrl))
            {
                continue;
            }

            var uuid = GetString(el, "stationuuid").Trim();
            var id = string.IsNullOrWhiteSpace(uuid)
                ? $"rb-{Math.Abs(primaryUrl.GetHashCode(StringComparison.OrdinalIgnoreCase)):x8}"
                : $"rb-{uuid}";

            var fallbackUrl = !string.IsNullOrWhiteSpace(urlRaw) &&
                              !string.Equals(urlRaw, primaryUrl, StringComparison.OrdinalIgnoreCase) &&
                              Uri.TryCreate(urlRaw, UriKind.Absolute, out var fbUri) &&
                              (fbUri.Scheme == Uri.UriSchemeHttp || fbUri.Scheme == Uri.UriSchemeHttps)
                ? urlRaw
                : string.Empty;

            var countryCode = GetString(el, "countrycode").Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(countryCode))
            {
                countryCode = "INT";
            }

            var codec = GetString(el, "codec").Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(codec))
            {
                codec = "STREAM";
            }

            var bitrate = GetInt(el, "bitrate");
            var tags = NormalizeTags(GetString(el, "tags"));
            var tagline = string.IsNullOrWhiteSpace(tags)
                ? $"{countryCode} • {codec}"
                : tags;

            stations.Add(new RadioStation(
                Id: id,
                Name: name,
                Tagline: tagline,
                StreamUrl: primaryUrl,
                FallbackStreamUrl: fallbackUrl,
                IconGlyph: "\uE7F6",
                AccentHex: "#0078D4",
                BitrateKbps: bitrate,
                CountryCode: countryCode,
                Codec: codec,
                Tags: tags));
        }

        return stations;
    }

    public static IReadOnlyList<RadioBrowserCountry> ParseCountriesJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<RadioBrowserCountry>();
        }

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<RadioBrowserCountry>();
        }

        var countries = new List<RadioBrowserCountry>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var name = GetString(el, "name").Trim();
            var code = GetString(el, "iso_3166_1").Trim().ToUpperInvariant();
            var count = GetInt(el, "stationcount");

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(code) || count <= 0)
            {
                continue;
            }

            countries.Add(new RadioBrowserCountry(name, code, count));
        }

        return countries
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<string> FetchFromMirrorsAsync(string relativePath, CancellationToken ct)
    {
        Exception? lastException = null;
        foreach (var mirror in Mirrors)
        {
            try
            {
                using var response = await _httpClient.GetAsync($"{mirror}{relativePath}", ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;
            }
        }

        throw new HttpRequestException("All Radio Browser mirrors failed to respond.", lastException);
    }

    private static string GetString(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int GetInt(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var value))
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var num))
        {
            return num;
        }

        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed))
        {
            return parsed;
        }

        return 0;
    }

    private static string NormalizeTags(string rawTags)
    {
        if (string.IsNullOrWhiteSpace(rawTags))
        {
            return string.Empty;
        }

        var parts = rawTags
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4);

        return string.Join(", ", parts);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _httpClient.Dispose();
    }
}
