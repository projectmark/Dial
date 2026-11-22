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
/// downloads updates safely, and stages clean self-replacing restarts.
/// </summary>
public interface IUpdateService
{
    bool ShouldAutoCheck(AppSettings settings, DateTimeOffset utcNow);
    bool ShouldShowWhatsNew(AppSettings settings);
    bool CanOverwriteCurrentExecutable(out string? targetExePath);
    Task<UpdateCheckResult> CheckForUpdatesAsync(string manifestPathOrUrl, CancellationToken ct = default);
    Task<UpdateCheckResult> CheckForGitHubUpdatesAsync(
        string? repositorySlugOrUrl = null,
        bool includePreReleases = true,
        CancellationToken ct = default);
    Task<string> DownloadAndVerifyUpdateAsync(
        UpdateManifest manifest,
        IProgress<double>? progress = null,
        CancellationToken ct = default);
    bool VerifyPayloadHash(string filePath, string expectedSha256);
    string StageUpdateScript(string stagedExePath, string targetExePath, string expectedSha256);
    bool ApplyUpdateAndRestart(string stagedExePath, string expectedSha256, bool launchDetached = true);
}

/// <summary>
/// Default SHA256-verified self-updater supporting GitHub Releases and batch swaps inside %AppData%/&lt;App&gt;/pending/.
/// </summary>
public sealed class UpdateService : IUpdateService
{
    private const int MaxManifestBytes = 1024 * 1024;
    private const long MaxPayloadBytes = 250L * 1024 * 1024;
    private const int MaxRedirects = 5;

    private static readonly HttpClient SharedHttp = CreateHttpClient(allowAutoRedirect: true, TimeSpan.FromSeconds(15), disposeHandler: true);
    private static readonly HttpClient SharedDownloadHttp = CreateHttpClient(allowAutoRedirect: false, TimeSpan.FromMinutes(5), disposeHandler: true);

    private readonly IPathService _paths;
    private readonly HttpClient _apiHttp;
    private readonly HttpClient _downloadHttp;

    public UpdateService(IPathService paths)
    {
        _paths = paths;
        _paths.EnsureDirectoriesExist();
        _apiHttp = SharedHttp;
        _downloadHttp = SharedDownloadHttp;
    }

    public UpdateService(IPathService paths, HttpMessageHandler testHandler)
    {
        ArgumentNullException.ThrowIfNull(testHandler);
        _paths = paths;
        _paths.EnsureDirectoriesExist();
        _apiHttp = new HttpClient(testHandler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(15) };
        _downloadHttp = new HttpClient(testHandler, disposeHandler: false) { Timeout = TimeSpan.FromMinutes(5) };
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
    public bool CanOverwriteCurrentExecutable(out string? targetExePath)
    {
        targetExePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(targetExePath) ||
            !targetExePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var fileName = Path.GetFileName(targetExePath);
        if (string.Equals(fileName, "dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var dir = Path.GetDirectoryName(targetExePath);
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            return false;
        }

        var probePath = Path.Combine(dir, $".chassis_write_probe_{Guid.NewGuid():N}.tmp");
        try
        {
            using (File.Create(probePath, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch
        {
            return false;
        }
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

        // Prefer releases.json asset when present so SHA256 and version match the build manifest directly.
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

        // Fallback to .exe + checksum.sha256 assets if releases.json was not attached or omitted an external GitHub URL.
        var exeAsset = FindPreferredExeAsset(best.Assets);
        if (manifest is null || !IsValidGitHubHttps(manifest.DownloadUrl))
        {
            var checksumAsset = best.Assets.FirstOrDefault(a =>
                string.Equals(a.Name, "checksum.sha256", StringComparison.OrdinalIgnoreCase));
            var sha256 = manifest?.Sha256 ?? string.Empty;

            if (string.IsNullOrWhiteSpace(sha256) &&
                !string.IsNullOrEmpty(checksumAsset.Url) &&
                Uri.TryCreate(checksumAsset.Url, UriKind.Absolute, out var shaUri) &&
                IsAllowedGitHubUri(shaUri) &&
                !string.IsNullOrEmpty(exeAsset.Name))
            {
                var checksumText = await DownloadGitHubStringAsync(shaUri, 64 * 1024, ct).ConfigureAwait(false);
                sha256 = ParseChecksumForFile(checksumText, exeAsset.Name) ?? string.Empty;
            }

            if (!string.IsNullOrEmpty(exeAsset.Url))
            {
                manifest = new UpdateManifest(
                    Version: manifest?.Version ?? latestVersion,
                    DownloadUrl: exeAsset.Url,
                    Sha256: sha256,
                    ReleaseNotesUrl: IsValidGitHubHttps(best.HtmlUrl) ? best.HtmlUrl : null,
                    IsPreRelease: best.IsPreRelease);
            }
        }

        if (manifest is null || !IsValidGitHubHttps(manifest.DownloadUrl))
        {
            return new UpdateCheckResult(false, currentVersion, latestVersion, null, "Release is missing a valid executable asset.");
        }

        return new UpdateCheckResult(true, currentVersion, manifest.Version, manifest);
    }

    /// <inheritdoc />
    public async Task<string> DownloadAndVerifyUpdateAsync(
        UpdateManifest manifest,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!Uri.TryCreate(manifest.DownloadUrl, UriKind.Absolute, out var initialUri) ||
            !IsAllowedGitHubUri(initialUri))
        {
            throw new InvalidDataException("Update download URL must point to an HTTPS GitHub release host.");
        }

        if (!IsValidSha256Hex(manifest.Sha256))
        {
            throw new InvalidDataException("Update manifest does not contain a valid SHA256 checksum.");
        }

        _paths.EnsureDirectoriesExist();
        var stagedPath = Path.Combine(_paths.PendingUpdatesDirectory, $"{AppVersion.EntryAssemblyName}.staged.exe");
        var partPath = stagedPath + ".part";

        try
        {
            using var response = await SendWithGitHubRedirectValidationAsync(initialUri, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength is > MaxPayloadBytes)
            {
                throw new InvalidDataException("Update binary exceeds maximum allowed size.");
            }

            await using var remoteStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using (var localStream = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 81_920, useAsync: true))
            {
                var buffer = new byte[81_920];
                long totalRead = 0;
                int read;
                while ((read = await remoteStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    totalRead += read;
                    if (totalRead > MaxPayloadBytes)
                    {
                        throw new InvalidDataException("Update binary exceeds maximum allowed size.");
                    }

                    await localStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    if (contentLength is > 0)
                    {
                        progress?.Report(Math.Clamp((double)totalRead / contentLength.Value, 0.0, 1.0));
                    }
                    else
                    {
                        progress?.Report(-1);
                    }
                }

                await localStream.FlushAsync(ct).ConfigureAwait(false);
            }

            File.Move(partPath, stagedPath, overwrite: true);

            if (!VerifyPayloadHash(stagedPath, manifest.Sha256))
            {
                try { File.Delete(stagedPath); } catch { }
                throw new InvalidDataException("SHA256 verification failed for downloaded update.");
            }

            progress?.Report(1.0);
            return stagedPath;
        }
        catch
        {
            try { if (File.Exists(partPath)) File.Delete(partPath); } catch { }
            throw;
        }
    }

    private async Task<HttpResponseMessage> SendWithGitHubRedirectValidationAsync(Uri initialUri, CancellationToken ct)
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
            var response = await _downloadHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            var status = (int)response.StatusCode;
            if (status is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location)
            {
                var nextUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                response.Dispose();
                currentUri = nextUri;
                continue;
            }

            return response;
        }

        throw new HttpRequestException("Too many redirects while downloading update.");
    }

    private async Task<string> DownloadGitHubStringAsync(Uri initialUri, int maxBytes, CancellationToken ct)
    {
        using var response = await SendWithGitHubRedirectValidationAsync(initialUri, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await ReadLimitedStringAsync(response, maxBytes, ct).ConfigureAwait(false);
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
        if (item.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (TryReadBool(item, "draft"))
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

    private static (string Name, string Url) FindPreferredExeAsset(List<(string Name, string Url)> assets)
    {
        var preferredName = $"{AppVersion.EntryAssemblyName}.exe";
        var preferred = assets.FirstOrDefault(a => string.Equals(a.Name, preferredName, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(preferred.Url))
        {
            return preferred;
        }

        var releaseExe = assets.FirstOrDefault(a => string.Equals(a.Name, "release.exe", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(releaseExe.Url))
        {
            return releaseExe;
        }

        return assets.FirstOrDefault(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
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
            !File.Exists(filePath))
        {
            return false;
        }

        using var stream = File.OpenRead(filePath);
        var hash = SHA256.HashData(stream);
        var actualHex = Convert.ToHexString(hash);
        return string.Equals(actualHex, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public string StageUpdateScript(string stagedExePath, string targetExePath, string expectedSha256)
    {
        if (!VerifyPayloadHash(stagedExePath, expectedSha256))
        {
            throw new InvalidOperationException("SHA256 verification failed for staged update binary.");
        }

        var safeStagedPath = SanitizeCmdPath(stagedExePath, nameof(stagedExePath));
        var safeTargetPath = SanitizeCmdPath(targetExePath, nameof(targetExePath));

        _paths.EnsureDirectoriesExist();
        var scriptPath = Path.Combine(_paths.PendingUpdatesDirectory, "apply_update.cmd");
        var pid = Environment.ProcessId;

        var script = new StringBuilder();
        script.AppendLine("@echo off");
        script.AppendLine("chcp 65001 >NUL");
        script.AppendLine("setlocal");
        script.AppendLine($"set PID={pid}");
        script.AppendLine(":waitloop");
        script.AppendLine("tasklist /FI \"PID eq %PID%\" 2>NUL | find /I \"%PID%\" >NUL");
        script.AppendLine("if \"%ERRORLEVEL%\"==\"0\" (");
        script.AppendLine("    timeout /t 1 /nobreak >NUL");
        script.AppendLine("    goto waitloop");
        script.AppendLine(")");
        script.AppendLine("set /a RETRIES=0");
        script.AppendLine(":copyloop");
        script.AppendLine($"copy /Y \"{EscapeBatchPercent(safeStagedPath)}\" \"{EscapeBatchPercent(safeTargetPath)}\" >NUL 2>&1");
        script.AppendLine("if \"%ERRORLEVEL%\"==\"0\" goto copydone");
        script.AppendLine("set /a RETRIES+=1");
        script.AppendLine("if %RETRIES% GEQ 5 goto copydone");
        script.AppendLine("timeout /t 1 /nobreak >NUL");
        script.AppendLine("goto copyloop");
        script.AppendLine(":copydone");
        script.AppendLine($"del /F /Q \"{EscapeBatchPercent(safeStagedPath)}\" >NUL 2>&1");
        script.AppendLine($"start \"\" \"{EscapeBatchPercent(safeTargetPath)}\"");
        script.AppendLine("del /F /Q \"%~f0\" >NUL 2>&1");

        File.WriteAllText(scriptPath, script.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return scriptPath;
    }

    private static string SanitizeCmdPath(string path, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, paramName);
        if (path.IndexOfAny(['"', '\r', '\n', '\0']) >= 0)
        {
            throw new ArgumentException("Update path contains invalid characters.", paramName);
        }

        return Path.GetFullPath(path);
    }

    private static string EscapeBatchPercent(string value)
        => value.Replace("%", "%%", StringComparison.Ordinal);

    /// <inheritdoc />
    public bool ApplyUpdateAndRestart(string stagedExePath, string expectedSha256, bool launchDetached = true)
    {
        var currentExe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExe))
        {
            return false;
        }

        var scriptPath = StageUpdateScript(stagedExePath, currentExe, expectedSha256);
        if (!launchDetached)
        {
            return true;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{scriptPath}\"",
            CreateNoWindow = true,
            UseShellExecute = false
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
