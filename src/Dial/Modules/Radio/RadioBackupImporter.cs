using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chassis.Core.Data;

namespace Dial.Modules.Radio;

/// <summary>
/// Parses Radi-oh! JSON backups as well as Dial .chassispack and share-code payloads.
/// </summary>
public static class RadioBackupImporter
{
    public const int MaxImportPayloadBytes = 8 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Parses a Radi-oh! JSON array backup (<c>id</c>, <c>manualSortingId</c>, <c>name</c>, <c>customName</c>, <c>url</c>).
    /// </summary>
    public static IReadOnlyList<RadioStation> ParseRadiOhBackupJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidDataException("Backup JSON is empty.");
        }

        if (Encoding.UTF8.GetByteCount(json) > MaxImportPayloadBytes)
        {
            throw new InvalidDataException("Backup JSON exceeds maximum allowed size.");
        }

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Radi-oh! backup must be a JSON array.");
        }

        var entries = new List<(int SortOrder, RadioStation Station)>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var el in doc.RootElement.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var url = GetString(el, "url").Trim();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                continue;
            }

            var rawName = GetString(el, "name").Trim();
            var customName = GetString(el, "customName").Trim();
            var displayName = !string.IsNullOrWhiteSpace(customName) ? customName : rawName;
            if (string.IsNullOrWhiteSpace(displayName))
            {
                continue;
            }

            var rawId = GetString(el, "id").Trim();
            var stationId = string.IsNullOrWhiteSpace(rawId)
                ? $"radioh-{ComputeStableShortHash(url)}"
                : $"radioh-{rawId}";

            var sortOrder = GetInt(el, "manualSortingId", int.MaxValue);
            var tagline = !string.IsNullOrWhiteSpace(customName) &&
                          !string.IsNullOrWhiteSpace(rawName) &&
                          !string.Equals(customName, rawName, StringComparison.OrdinalIgnoreCase)
                ? rawName
                : uri.Host;

            entries.Add((sortOrder, new RadioStation(
                Id: stationId,
                Name: displayName,
                Tagline: tagline,
                StreamUrl: url,
                FallbackStreamUrl: string.Empty,
                IconGlyph: "\uE7F6",
                AccentHex: "#0078D4",
                BitrateKbps: 0,
                CountryCode: "INT",
                Codec: "STREAM",
                Tags: string.Empty)));
        }

        return entries
            .OrderBy(e => e.SortOrder)
            .Where(e => seenUrls.Add(e.Station.StreamUrl))
            .Select(e => e.Station)
            .ToList();
    }

    /// <summary>
    /// Auto-detects and parses Radi-oh! JSON, Dial raw station JSON, .chassispack JSON, or <c>DIAL-v1:...</c> share codes.
    /// </summary>
    public static IReadOnlyList<RadioStation> ParseAny(string rawInput, IPackageShareService<List<RadioStation>> shareService)
    {
        ArgumentNullException.ThrowIfNull(shareService);
        if (string.IsNullOrWhiteSpace(rawInput))
        {
            throw new InvalidDataException("Import input is empty.");
        }

        if (Encoding.UTF8.GetByteCount(rawInput) > MaxImportPayloadBytes * 2)
        {
            throw new InvalidDataException("Import payload exceeds maximum allowed size.");
        }

        var trimmed = rawInput.Trim();
        if (trimmed.StartsWith('['))
        {
            // Distinguish Dial RadioStation array (StreamUrl) from Radi-oh! array (url).
            using var doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                var firstObj = doc.RootElement.EnumerateArray().FirstOrDefault(e => e.ValueKind == JsonValueKind.Object);
                if (firstObj.ValueKind == JsonValueKind.Object && firstObj.TryGetProperty("streamUrl", out _))
                {
                    var stations = JsonSerializer.Deserialize<List<RadioStation>>(trimmed, JsonOptions) ?? [];
                    return SanitizeImportedStations(stations);
                }

                return ParseRadiOhBackupJson(trimmed);
            }
        }

        if (trimmed.StartsWith('{'))
        {
            var envelope = JsonSerializer.Deserialize<SharePackage<List<RadioStation>>>(trimmed, JsonOptions);
            if (envelope?.Payload is not null)
            {
                return SanitizeImportedStations(envelope.Payload);
            }
        }

        return SanitizeImportedStations(shareService.ImportFromShareCode(trimmed));
    }

    public static bool IsValidHttpStreamUrl(string? url)
        => !string.IsNullOrWhiteSpace(url) &&
           Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) &&
           (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    public static string ComputeStableShortHash(string input)
    {
        var normalized = input.Trim().ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash.AsSpan(0, 4)).ToLowerInvariant();
    }

    private static IReadOnlyList<RadioStation> SanitizeImportedStations(IEnumerable<RadioStation>? stations)
    {
        if (stations is null)
        {
            return Array.Empty<RadioStation>();
        }

        var valid = new List<RadioStation>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var s in stations)
        {
            if (s is null || string.IsNullOrWhiteSpace(s.Name) || !IsValidHttpStreamUrl(s.StreamUrl))
            {
                continue;
            }

            var cleanUrl = s.StreamUrl.Trim();
            if (!seenUrls.Add(cleanUrl))
            {
                continue;
            }

            var cleanFallback = IsValidHttpStreamUrl(s.FallbackStreamUrl)
                ? s.FallbackStreamUrl.Trim()
                : string.Empty;

            valid.Add(s with
            {
                Name = s.Name.Trim(),
                StreamUrl = cleanUrl,
                FallbackStreamUrl = cleanFallback
            });
        }

        return valid;
    }

    private static string GetString(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int GetInt(JsonElement el, string prop, int fallback)
        => el.TryGetProperty(prop, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var num)
            ? num
            : fallback;
}
