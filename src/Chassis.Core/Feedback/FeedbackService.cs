using System.Runtime.InteropServices;
using System.Text.Json;
using Chassis.Core.Diagnostics;
using Chassis.Core.Paths;
using Chassis.Core.Settings;
using Chassis.Core.Versioning;

namespace Chassis.Core.Feedback;

/// <summary>
/// Generates local feedback and bug reports with optional diagnostics snapshots under %AppData%/&lt;App&gt;/feedback/.
/// </summary>
public interface IFeedbackService
{
    Task<string> SaveFeedbackAsync(
        string category,
        string subject,
        string description,
        bool includeDiagnostics = true,
        string? attachmentPath = null,
        CancellationToken ct = default);
}

/// <summary>
/// File-backed feedback bundle writer saving structured JSON reports for developer triage.
/// </summary>
public sealed class FeedbackService : IFeedbackService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly IPathService _paths;
    private readonly ISettingsService _settings;
    private readonly ICrashReportService _crashReports;

    public FeedbackService(
        IPathService paths,
        ISettingsService settings,
        ICrashReportService crashReports)
    {
        _paths = paths;
        _settings = settings;
        _crashReports = crashReports;
    }

    /// <inheritdoc />
    public async Task<string> SaveFeedbackAsync(
        string category,
        string subject,
        string description,
        bool includeDiagnostics = true,
        string? attachmentPath = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        _paths.EnsureDirectoriesExist();

        var timestamp = DateTime.UtcNow;
        var reportPath = Path.Combine(
            _paths.FeedbackDirectory,
            $"feedback_{timestamp:yyyyMMdd_HHmmss_fff}.json");

        string? copiedAttachment = null;
        if (!string.IsNullOrWhiteSpace(attachmentPath) && File.Exists(attachmentPath))
        {
            var destName = $"attach_{timestamp:yyyyMMdd_HHmmss}_{Path.GetFileName(attachmentPath)}";
            copiedAttachment = Path.Combine(_paths.FeedbackDirectory, destName);
            File.Copy(attachmentPath, copiedAttachment, overwrite: true);
        }

        string? recentCrashExcerpt = null;
        if (includeDiagnostics)
        {
            var latestCrash = _crashReports.GetRecentLogFiles().FirstOrDefault();
            if (latestCrash is not null && File.Exists(latestCrash.FullName))
            {
                var text = await File.ReadAllTextAsync(latestCrash.FullName, ct).ConfigureAwait(false);
                recentCrashExcerpt = text.Length > 4096 ? text[..4096] : text;
            }
        }

        var payload = new
        {
            TimestampUtc = timestamp,
            AppName = AppVersion.ProductTitle,
            Assembly = AppVersion.EntryAssemblyName,
            Version = AppVersion.InformationalVersion,
            Category = category,
            Subject = subject.Trim(),
            Description = description.Trim(),
            Attachment = copiedAttachment,
            Diagnostics = includeDiagnostics
                ? new
                {
                    OS = Environment.OSVersion.ToString(),
                    Runtime = RuntimeInformation.FrameworkDescription,
                    Language = _settings.Current.Language,
                    Theme = _settings.Current.Theme.ToString(),
                    RecentCrashLog = recentCrashExcerpt
                }
                : null
        };

        var tmp = reportPath + ".tmp";
        await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(fs, payload, SerializerOptions, ct).ConfigureAwait(false);
            await fs.FlushAsync(ct).ConfigureAwait(false);
        }

        File.Move(tmp, reportPath, overwrite: true);
        return reportPath;
    }
}
