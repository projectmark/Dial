using System.Text;
using Chassis.Core.Paths;
using Chassis.Core.Versioning;

namespace Chassis.Core.Diagnostics;

/// <summary>
/// Writes structured unhandled-exception crash logs with automatic rotation under %AppData%/&lt;App&gt;/logs/.
/// </summary>
public interface ICrashReportService
{
    int MaxRetainedLogs { get; }
    string WriteCrashReport(string source, Exception exception);
    IReadOnlyList<FileInfo> GetRecentLogFiles();
}

/// <summary>
/// File-backed crash logger retaining the newest 20 crash logs ordered by UTC creation timestamp.
/// </summary>
public sealed class CrashReportService : ICrashReportService
{
    private readonly IPathService _paths;
    private readonly Lock _gate = new();

    public CrashReportService(IPathService paths)
    {
        _paths = paths;
        _paths.EnsureDirectoriesExist();
    }

    public int MaxRetainedLogs => 20;

    /// <inheritdoc />
    public string WriteCrashReport(string source, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        _paths.EnsureDirectoriesExist();

        var now = DateTime.UtcNow;
        var fileName = $"crash_{now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}"[..36] + ".log";
        var fullPath = Path.Combine(_paths.LogsDirectory, fileName);

        var sb = new StringBuilder();
        sb.AppendLine($"=== {AppVersion.ProductTitle} Crash Report ===");
        sb.AppendLine($"Timestamp UTC : {now:O}");
        sb.AppendLine($"Assembly      : {AppVersion.EntryAssemblyName}");
        sb.AppendLine($"Version       : {AppVersion.InformationalVersion}");
        sb.AppendLine($"Source        : {source}");
        sb.AppendLine($"OS            : {Environment.OSVersion}");
        sb.AppendLine($"Runtime       : {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        sb.AppendLine();
        sb.AppendLine(exception.ToString());

        lock (_gate)
        {
            File.WriteAllText(fullPath, sb.ToString());
            PruneOldLogsLocked();
        }

        return fullPath;
    }

    /// <inheritdoc />
    public IReadOnlyList<FileInfo> GetRecentLogFiles()
    {
        if (!Directory.Exists(_paths.LogsDirectory))
        {
            return Array.Empty<FileInfo>();
        }

        return new DirectoryInfo(_paths.LogsDirectory)
            .GetFiles("crash_*.log", SearchOption.TopDirectoryOnly)
            .OrderByDescending(f => f.CreationTimeUtc)
            .ThenByDescending(f => f.Name, StringComparer.Ordinal)
            .ToList();
    }

    private void PruneOldLogsLocked()
    {
        var files = GetRecentLogFiles();
        for (var i = MaxRetainedLogs; i < files.Count; i++)
        {
            try
            {
                files[i].Delete();
            }
            catch
            {
                // Ignore files locked by external editors.
            }
        }
    }
}
