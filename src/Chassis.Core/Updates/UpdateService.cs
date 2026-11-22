using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chassis.Core.Paths;
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
/// Checks release manifests (local JSON or HTTP), verifies SHA256 hashes, and stages clean self-replacing updates.
/// </summary>
public interface IUpdateService
{
    Task<UpdateCheckResult> CheckForUpdatesAsync(string manifestPathOrUrl, CancellationToken ct = default);
    bool VerifyPayloadHash(string filePath, string expectedSha256);
    string StageUpdateScript(string stagedExePath, string targetExePath, string expectedSha256);
    bool ApplyUpdateAndRestart(string stagedExePath, string expectedSha256, bool launchDetached = true);
}

/// <summary>
/// Default SHA256-verified self-updater staging batch swaps inside %AppData%/&lt;App&gt;/pending/.
/// </summary>
public sealed class UpdateService : IUpdateService
{
    private const int MaxManifestBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly HttpClient SharedHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private readonly IPathService _paths;

    public UpdateService(IPathService paths)
    {
        _paths = paths;
        _paths.EnsureDirectoriesExist();
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
                using var response = await SharedHttp.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is > MaxManifestBytes)
                {
                    return new UpdateCheckResult(false, current, null, null, "Update manifest exceeds maximum allowed size.");
                }

                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var ms = new MemoryStream();
                var buffer = new byte[8192];
                var total = 0;
                int read;
                while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > MaxManifestBytes)
                    {
                        return new UpdateCheckResult(false, current, null, null, "Update manifest exceeds maximum allowed size.");
                    }

                    ms.Write(buffer, 0, read);
                }

                rawJson = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
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
        catch (Exception ex)
        {
            return new UpdateCheckResult(false, current, null, null, ex.Message);
        }
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
        script.AppendLine($"copy /Y \"{EscapeBatchPercent(safeStagedPath)}\" \"{EscapeBatchPercent(safeTargetPath)}\" >NUL");
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
