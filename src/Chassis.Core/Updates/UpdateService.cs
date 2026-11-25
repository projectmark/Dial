using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chassis.Core.Paths;
using Chassis.Core.Settings;
using Chassis.Core.Versioning;

namespace Chassis.Core.Updates;

public sealed record UpdateManifest(
    string Version,
    string DownloadUrl,
    string Sha256,
    string? ReleaseNotesUrl = null,
    bool IsPreRelease = false);

public sealed record UpdateCheckResult(
    bool IsUpdateAvailable,
    string CurrentVersion,
    string? LatestVersion,
    UpdateManifest? Manifest,
    string? ErrorMessage = null);

/// <summary>
/// Checks release manifests (local JSON, HTTP, or GitHub Releases), verifies SHA256 hashes,
/// and opens verified GitHub Release pages in the default browser without downloading binaries into %AppData%.
/// </summary>
public interface IUpdateService
{
    bool ShouldAutoCheck(AppSettings settings, DateTimeOffset utcNow);
    bool ShouldShowWhatsNew(AppSettings settings);
    Task<UpdateCheckResult> CheckForUpdatesAsync(string manifestPathOrUrl, CancellationToken ct = default);
    Task<UpdateCheckResult> CheckForGitHubUpdatesAsync(
        string? repositorySlugOrUrl = null,
        bool includePreReleases = true,
        CancellationToken ct = default);
    bool VerifyPayloadHash(string filePath, string expectedSha256);
    bool TryOpenReleasePage(UpdateManifest manifest);
}

/// <summary>
/// Read-only release checker and SHA256 verifier supporting GitHub Releases.
/// </summary>
public sealed class UpdateService : IUpdateService
{
    private const int MaxManifestBytes = 1024 * 1024;
    private const int MaxRedirects = 5;

    private static readonly HttpClient SharedHttp = CreateHttpClient(allowAutoRedirect: true, TimeSpan.FromSeconds(15), disposeHandler: true);
    private static readonly HttpClient SharedAssetMetaHttp = CreateHttpClient(allowAutoRedirect: false, TimeSpan.FromSeconds(15), disposeHandler: true);

    private readonly HttpClient _apiHttp;
    private readonly HttpClient _assetMetaHttp;

    public UpdateService(IPathService paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        paths.EnsureDirectoriesExist();
        _apiHttp = SharedHttp;
        _assetMetaHttp = SharedAssetMetaHttp;
    }

    public UpdateService(IPathService paths, HttpMessageHandler testHandler)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(testHandler);
        paths.EnsureDirectoriesExist();
        _apiHttp = new HttpClient(testHandler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(15) };
        _assetMetaHttp = new HttpClient(testHandler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(15) };
    }

    private static HttpClient CreateHttpClient(bool allowAutoRedirect, TimeSpan timeout, bool disposeHandler)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = allowAutoRedirect,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        return new HttpClient(handler, disposeHandler) { Timeout = timeout };
    }

    /// <inheritdoc />
    public bool ShouldAutoCheck(AppSettings settings, DateTimeOffset utcNow)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.UpdateCheckFrequency switch
        {
            UpdateCheckFrequency.Startup => true,
            UpdateCheckFrequency.Daily => !settings.LastUpdateCheckUtc.HasValue ||
                                          (utcNow - settings.LastUpdateCheckUtc.Value) >= TimeSpan.FromDays(1),
            UpdateCheckFrequency.Weekly => !settings.LastUpdateCheckUtc.HasValue ||
                                           (utcNow - settings.LastUpdateCheckUtc.Value) >= TimeSpan.FromDays(7),
            _ => false
        };
    }

    /// <inheritdoc />
    public bool ShouldShowWhatsNew(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var current = AppVersion.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(settings.PendingWhatsNewVersion) &&
            string.Equals(settings.PendingWhatsNewVersion.Trim(), current, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(settings.LastSeenVersion) &&
            SemVersion.TryParse(current, out var currentSem) &&
            SemVersion.TryParse(settings.LastSeenVersion, out var seenSem))
        {
            return currentSem > seenSem;
        }

        return false;
    }

    /// <inheritdoc />
    public async Task<UpdateCheckResult> CheckForUpdatesAsync(string manifestPathOrUrl, CancellationToken ct = default)
    {
        var current = AppVersion.InformationalVersion;
        if (string.IsNullOrWhiteSpace(manifestPathOrUrl))
        {
            return new UpdateCheckResult(false, current, null, null, "No update manifest location configured.");
        }

        try
        {
            string rawJson;
            if (Uri.TryCreate(manifestPathOrUrl, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                rawJson = await DownloadLimitedStringAsync(_apiHttp, uri, MaxManifestBytes, ct).ConfigureAwait(false);
            }
            else if (File.Exists(manifestPathOrUrl))
            {
                var info = new FileInfo(manifestPathOrUrl);
                if (info.Length > MaxManifestBytes)
                {
                    return new UpdateCheckResult(false, current, null, null, "Update manifest exceeds maximum allowed size.");
                }

                rawJson = await File.ReadAllTextAsync(manifestPathOrUrl, ct).ConfigureAwait(false);
            }
            else
            {
                return new UpdateCheckResult(false, current, null, null, $"Manifest not found: {manifestPathOrUrl}");
            }

            return ParseManifestJson(rawJson, current);
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(false, current, null, null, ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<UpdateCheckResult> CheckForGitHubUpdatesAsync(
        string? repositorySlugOrUrl = null,
        bool includePreReleases = true,
        CancellationToken ct = default)
    {
        var current = AppVersion.InformationalVersion;
        var source = repositorySlugOrUrl ?? AppVersion.RepositoryUrl;
        if (!AppVersion.TryExtractGitHubSlug(source, out var slug))
        {
            return new UpdateCheckResult(false, current, null, null, "GitHub repository URL is not configured.");
        }

        try
        {
            var endpoint = includePreReleases
                ? $"https://api.github.com/repos/{slug}/releases?per_page=8"
                : $"https://api.github.com/repos/{slug}/releases/latest";

            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.TryAddWithoutValidation("User-Agent", AppVersion.UserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");

            using var response = await _apiHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!includePreReleases && response.StatusCode == HttpStatusCode.NotFound)
            {
                return new UpdateCheckResult(false, current, current, null);
            }

            response.EnsureSuccessStatusCode();
            var rawJson = await ReadLimitedStringAsync(response, MaxManifestBytes, ct).ConfigureAwait(false);
            return await ResolveGitHubReleaseAsync(rawJson, current, includePreReleases, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(false, current, null, null, ex.Message);
        }
    }

    internal async Task<UpdateCheckResult> ResolveGitHubReleaseAsync(
        string releasesJson,
        string currentVersion,
        bool includePreReleases,
        CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(releasesJson);
        var candidates = new List<(SemVersion SemVer, string RawTag, bool IsPreRelease, string? HtmlUrl, List<(string Name, string Url)> Assets)>();

        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                TryCollectRelease(item, includePreReleases, candidates);
            }
        }
        else if (doc.RootElement.ValueKind == JsonValueKind.Object)
        {
            TryCollectRelease(doc.RootElement, includePreReleases, candidates);
        }

        if (candidates.Count == 0)
        {
            return new UpdateCheckResult(false, currentVersion, currentVersion, null);
        }

        var best = candidates.OrderByDescending(c => c.SemVer).First();
        var latestVersion = best.SemVer.ToString();
        var isNewer = SemVersion.TryParse(currentVersion, out var currentSem)
            ? best.SemVer > currentSem
            : !string.Equals(latestVersion, currentVersion, StringComparison.OrdinalIgnoreCase);

        if (!isNewer)
        {
            return new UpdateCheckResult(false, currentVersion, latestVersion, null);
        }

        UpdateManifest? manifest = null;

        var manifestAsset = best.Assets.FirstOrDefault(a =>
            string.Equals(a.Name, "releases.json", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(manifestAsset.Url) &&
            Uri.TryCreate(manifestAsset.Url, UriKind.Absolute, out var manifestUri) &&
            IsAllowedGitHubUri(manifestUri))
        {
            var manifestJson = await DownloadGitHubStringAsync(manifestUri, MaxManifestBytes, ct).ConfigureAwait(false);
            var parsed = ParseManifestJson(manifestJson, currentVersion);
            if (parsed.Manifest is not null)
            {
                manifest = parsed.Manifest with
                {
                    ReleaseNotesUrl = IsValidGitHubHttps(parsed.Manifest.ReleaseNotesUrl)
                        ? parsed.Manifest.ReleaseNotesUrl
                        : best.HtmlUrl
                };
            }
        }

        var packageAsset = FindPreferredPackageAsset(best.Assets);
        if (manifest is null || !IsValidGitHubHttps(manifest.DownloadUrl))
        {
            var checksumAsset = best.Assets.FirstOrDefault(a =>
                string.Equals(a.Name, "checksum.sha256", StringComparison.OrdinalIgnoreCase));
            var sha256 = manifest?.Sha256 ?? string.Empty;

            if (string.IsNullOrWhiteSpace(sha256) &&
                !string.IsNullOrEmpty(checksumAsset.Url) &&
                Uri.TryCreate(checksumAsset.Url, UriKind.Absolute, out var shaUri) &&
                IsAllowedGitHubUri(shaUri) &&
                !string.IsNullOrEmpty(packageAsset.Name))
            {
                var checksumText = await DownloadGitHubStringAsync(shaUri, 64 * 1024, ct).ConfigureAwait(false);
                sha256 = ParseChecksumForFile(checksumText, packageAsset.Name) ?? string.Empty;
            }

            if (!string.IsNullOrEmpty(packageAsset.Url))
            {
                manifest = new UpdateManifest(
                    Version: manifest?.Version ?? latestVersion,
                    DownloadUrl: packageAsset.Url,
                    Sha256: sha256,
                    ReleaseNotesUrl: IsValidGitHubHttps(best.HtmlUrl) ? best.HtmlUrl : null,
                    IsPreRelease: best.IsPreRelease);
            }
        }

        if (manifest is null || !IsValidGitHubHttps(manifest.DownloadUrl))
        {
            return new UpdateCheckResult(false, currentVersion, latestVersion, null, "Release is missing a valid package asset.");
        }

        return new UpdateCheckResult(true, currentVersion, manifest.Version, manifest);
    }

    private async Task<string> DownloadGitHubStringAsync(Uri initialUri, int maxBytes, CancellationToken ct)
    {
        var currentUri = initialUri;
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            if (!IsAllowedGitHubUri(currentUri))
            {
                throw new InvalidDataException("Blocked redirect to untrusted host outside GitHub.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            request.Headers.TryAddWithoutValidation("User-Agent", AppVersion.UserAgent);
            using var response = await _assetMetaHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            var status = (int)response.StatusCode;
            if (status is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location)
            {
                currentUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                continue;
            }

            response.EnsureSuccessStatusCode();
            return await ReadLimitedStringAsync(response, maxBytes, ct).ConfigureAwait(false);
        }

        throw new HttpRequestException("Too many redirects while reading release metadata.");
    }

    private static async Task<string> DownloadLimitedStringAsync(HttpClient client, Uri uri, int maxBytes, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("User-Agent", AppVersion.UserAgent);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await ReadLimitedStringAsync(response, maxBytes, ct).ConfigureAwait(false);
    }

    private static async Task<string> ReadLimitedStringAsync(HttpResponseMessage response, int maxBytes, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is > 0 && response.Content.Headers.ContentLength.Value > maxBytes)
        {
            throw new InvalidDataException("Update metadata exceeds maximum allowed size.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        var total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new InvalidDataException("Update metadata exceeds maximum allowed size.");
            }

            ms.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    private static UpdateCheckResult ParseManifestJson(string rawJson, string current)
    {
        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;
        var latestVersion = TryReadProperty(root, "latestVersion")
            ?? TryReadProperty(root, "informationalVersion")
            ?? TryReadProperty(root, "version");

        var downloadUrl = TryReadProperty(root, "downloadUrl")
            ?? TryReadProperty(root, "artifact")
            ?? string.Empty;

        var sha256 = TryReadProperty(root, "sha256") ?? string.Empty;
        var notesUrl = TryReadProperty(root, "releaseNotesUrl");

        if (string.IsNullOrWhiteSpace(latestVersion))
        {
            return new UpdateCheckResult(false, current, null, null, "Manifest missing version field.");
        }

        var isNewer = SemVersion.TryParse(latestVersion, out var latestSem) &&
                      SemVersion.TryParse(current, out var currentSem)
            ? latestSem > currentSem
            : !string.Equals(latestVersion, current, StringComparison.OrdinalIgnoreCase);

        var manifest = new UpdateManifest(
            latestVersion,
            downloadUrl,
            sha256,
            notesUrl,
            latestSem?.PreRelease is not null);

        return new UpdateCheckResult(isNewer, current, latestVersion, manifest);
    }

    private static void TryCollectRelease(
        JsonElement item,
        bool includePreReleases,
        List<(SemVersion SemVer, string RawTag, bool IsPreRelease, string? HtmlUrl, List<(string Name, string Url)> Assets)> output)
    {
        if (item.ValueKind != JsonValueKind.Object || TryReadBool(item, "draft"))
        {
            return;
        }

        var isPreRelease = TryReadBool(item, "prerelease");
        var tag = TryReadProperty(item, "tag_name") ?? string.Empty;
        if (!SemVersion.TryParse(tag, out var semVer))
        {
            return;
        }

        var effectivePreRelease = isPreRelease || !string.IsNullOrWhiteSpace(semVer.PreRelease);
        if (!includePreReleases && effectivePreRelease)
        {
            return;
        }

        var htmlUrl = TryReadProperty(item, "html_url");
        var assets = new List<(string Name, string Url)>();
        if (item.TryGetProperty("assets", out var assetsEl) && assetsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assetsEl.EnumerateArray())
            {
                var name = TryReadProperty(asset, "name");
                var url = TryReadProperty(asset, "browser_download_url");
                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(url))
                {
                    assets.Add((name.Trim(), url.Trim()));
                }
            }
        }

        output.Add((semVer, tag, effectivePreRelease, htmlUrl, assets));
    }

    private static (string Name, string Url) FindPreferredPackageAsset(List<(string Name, string Url)> assets)
    {
        var preferredZipPrefix = $"{AppVersion.EntryAssemblyName}-";
        var preferredZip = assets.FirstOrDefault(a =>
            a.Name.StartsWith(preferredZipPrefix, StringComparison.OrdinalIgnoreCase) &&
            a.Name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(preferredZip.Url))
        {
            return preferredZip;
        }

        return assets.FirstOrDefault(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
    }

    internal static string? ParseChecksumForFile(string checksumText, string fileName)
    {
        if (string.IsNullOrWhiteSpace(checksumText) || string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        foreach (var rawLine in checksumText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                var hash = parts[0].Trim();
                var target = parts[^1].Trim().TrimStart('*');
                if (IsValidSha256Hex(hash) &&
                    string.Equals(target, fileName, StringComparison.OrdinalIgnoreCase))
                {
                    return hash.ToLowerInvariant();
                }
            }
        }

        return null;
    }

    public static bool IsAllowedGitHubUri(Uri? uri)
    {
        if (uri is null || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var host = uri.Host;
        return string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(host, "api.github.com", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValidGitHubHttps(string? rawUrl)
        => !string.IsNullOrWhiteSpace(rawUrl) &&
           Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out var uri) &&
           IsAllowedGitHubUri(uri);

    private static bool IsValidSha256Hex(string? sha256)
    {
        if (string.IsNullOrWhiteSpace(sha256))
        {
            return false;
        }

        var trimmed = sha256.Trim();
        return trimmed.Length == 64 && trimmed.All(Uri.IsHexDigit);
    }

    /// <inheritdoc />
    public bool VerifyPayloadHash(string filePath, string expectedSha256)
    {
        if (string.IsNullOrWhiteSpace(filePath) ||
            string.IsNullOrWhiteSpace(expectedSha256) ||
            !File.Exists(filePath) ||
            !IsValidSha256Hex(expectedSha256))
        {
            return false;
        }

        using var stream = File.OpenRead(filePath);
        var hash = SHA256.HashData(stream);
        var actualHex = Convert.ToHexString(hash);
        return string.Equals(actualHex, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public bool TryOpenReleasePage(UpdateManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var candidate = !string.IsNullOrWhiteSpace(manifest.ReleaseNotesUrl)
            ? manifest.ReleaseNotesUrl
            : manifest.DownloadUrl;

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            !IsAllowedGitHubUri(uri))
        {
            return false;
        }

        Process.Start(new ProcessStartInfo(uri.AbsoluteUri)
        {
            UseShellExecute = true
        });
        return true;
    }

    private static bool TryReadBool(JsonElement root, string propertyName)
    {
        foreach (var prop in root.EnumerateObject())
        {
            if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase) &&
                prop.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return prop.Value.GetBoolean();
            }
        }

        return false;
    }

    private static string? TryReadProperty(JsonElement root, string propertyName)
    {
        foreach (var prop in root.EnumerateObject())
        {
            if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase) &&
                prop.Value.ValueKind == JsonValueKind.String)
            {
                return prop.Value.GetString();
            }
        }

        return null;
    }
}
