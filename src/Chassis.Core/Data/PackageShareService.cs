using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Chassis.Core.Versioning;

namespace Chassis.Core.Data;

/// <summary>
/// Envelope used for portable .chassispack files and compressed share codes.
/// </summary>
public sealed record SharePackage<T>(
    string Format,
    string SourceApp,
    string AppVersion,
    DateTimeOffset CreatedUtc,
    string PayloadType,
    T Payload);

/// <summary>
/// Opt-in portable package exporter/importer supporting .chassispack files and Brotli share codes.
/// </summary>
public interface IPackageShareService<T>
{
    Task ExportToFileAsync(T payload, string path, CancellationToken ct = default);
    Task<T> ImportFromFileAsync(string path, CancellationToken ct = default);
    string ExportToShareCode(T payload);
    T ImportFromShareCode(string shareCode);
}

/// <summary>
/// Default implementation of <see cref="IPackageShareService{T}"/> using Brotli + URL-safe Base64 and JSON envelopes.
/// </summary>
public sealed class PackageShareService<T> : IPackageShareService<T>
{
    private const string PackageFormat = "chassispack-v1";

    private static readonly JsonSerializerOptions FileJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    private readonly string? _sourceAppOverride;

    public PackageShareService(string? sourceAppOverride = null)
    {
        _sourceAppOverride = string.IsNullOrWhiteSpace(sourceAppOverride)
            ? null
            : new string(sourceAppOverride.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
    }

    private string SharePrefix => _sourceAppOverride ?? AppVersion.ShareCodeAppPrefix;

    /// <inheritdoc />
    public async Task ExportToFileAsync(T payload, string path, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var envelope = CreateEnvelope(payload);
        var tmp = fullPath + ".tmp";
        await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(fs, envelope, FileJsonOptions, ct).ConfigureAwait(false);
            await fs.FlushAsync(ct).ConfigureAwait(false);
        }

        File.Move(tmp, fullPath, overwrite: true);
    }

    /// <inheritdoc />
    public async Task<T> ImportFromFileAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Package file not found.", path);
        }

        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var envelope = await JsonSerializer.DeserializeAsync<SharePackage<T>>(fs, FileJsonOptions, ct).ConfigureAwait(false)
            ?? throw new InvalidDataException("Invalid .chassispack payload.");

        if (!string.Equals(envelope.Format, PackageFormat, StringComparison.OrdinalIgnoreCase) || envelope.Payload is null)
        {
            throw new InvalidDataException("Unsupported or empty .chassispack package.");
        }

        return envelope.Payload;
    }

    /// <inheritdoc />
    public string ExportToShareCode(T payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var envelope = CreateEnvelope(payload);
        var utf8 = JsonSerializer.SerializeToUtf8Bytes(envelope, CompactJsonOptions);

        using var ms = new MemoryStream();
        using (var brotli = new BrotliStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            brotli.Write(utf8, 0, utf8.Length);
        }

        var encoded = ToBase64Url(ms.ToArray());
        return $"{SharePrefix}-v1:{encoded}";
    }

    /// <inheritdoc />
    public T ImportFromShareCode(string shareCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shareCode);
        var trimmed = shareCode.Trim();
        var markerIndex = trimmed.IndexOf("-v1:", StringComparison.OrdinalIgnoreCase);
        if (markerIndex <= 0)
        {
            throw new InvalidDataException("Share code must follow '<APP>-v1:<payload>' format.");
        }

        var payloadPart = trimmed[(markerIndex + 4)..];
        if (string.IsNullOrWhiteSpace(payloadPart))
        {
            throw new InvalidDataException("Share code payload is empty.");
        }

        var compressed = FromBase64Url(payloadPart);
        using var input = new MemoryStream(compressed);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        brotli.CopyTo(output);

        var envelope = JsonSerializer.Deserialize<SharePackage<T>>(output.ToArray(), CompactJsonOptions)
            ?? throw new InvalidDataException("Share code could not be deserialized.");

        if (!string.Equals(envelope.Format, PackageFormat, StringComparison.OrdinalIgnoreCase) || envelope.Payload is null)
        {
            throw new InvalidDataException("Share code payload format is invalid.");
        }

        return envelope.Payload;
    }

    private SharePackage<T> CreateEnvelope(T payload)
        => new(
            Format: PackageFormat,
            SourceApp: _sourceAppOverride ?? AppVersion.EntryAssemblyName,
            AppVersion: Versioning.AppVersion.InformationalVersion,
            CreatedUtc: DateTimeOffset.UtcNow,
            PayloadType: typeof(T).Name,
            Payload: payload);

    private static string ToBase64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static byte[] FromBase64Url(string input)
    {
        var padded = new StringBuilder(input.Replace('-', '+').Replace('_', '/'));
        while (padded.Length % 4 != 0)
        {
            padded.Append('=');
        }

        return Convert.FromBase64String(padded.ToString());
    }
}
