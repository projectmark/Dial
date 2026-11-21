using Chassis.Core.Versioning;

namespace Chassis.Core.Paths;

/// <summary>
/// Resolves application-scoped %AppData% directories and ensures folder structure initialization.
/// </summary>
public interface IPathService
{
    string RootDirectory { get; }
    string ConfigDirectory { get; }
    string DataDirectory { get; }
    string ExportsDirectory { get; }
    string LogsDirectory { get; }
    string FeedbackDirectory { get; }
    string PendingUpdatesDirectory { get; }
    string SettingsFilePath { get; }
    void EnsureDirectoriesExist();
}

/// <summary>
/// Default %AppData% path provider supporting test isolation via an optional custom root path.
/// </summary>
public sealed class PathService : IPathService
{
    public PathService(string? customRoot = null)
    {
        if (!string.IsNullOrWhiteSpace(customRoot))
        {
            RootDirectory = Path.GetFullPath(customRoot);
        }
        else
        {
            var invalid = Path.GetInvalidFileNameChars();
            var safeFolder = new string(AppVersion.EntryAssemblyName.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            if (string.IsNullOrWhiteSpace(safeFolder))
            {
                safeFolder = "ChassisApp";
            }

            RootDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                safeFolder);
        }

        ConfigDirectory = Path.Combine(RootDirectory, "config");
        DataDirectory = Path.Combine(RootDirectory, "data");
        ExportsDirectory = Path.Combine(RootDirectory, "exports");
        LogsDirectory = Path.Combine(RootDirectory, "logs");
        FeedbackDirectory = Path.Combine(RootDirectory, "feedback");
        PendingUpdatesDirectory = Path.Combine(RootDirectory, "pending");
        SettingsFilePath = Path.Combine(ConfigDirectory, "settings.json");
    }

    public string RootDirectory { get; }
    public string ConfigDirectory { get; }
    public string DataDirectory { get; }
    public string ExportsDirectory { get; }
    public string LogsDirectory { get; }
    public string FeedbackDirectory { get; }
    public string PendingUpdatesDirectory { get; }
    public string SettingsFilePath { get; }

    /// <inheritdoc />
    public void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(ExportsDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(FeedbackDirectory);
        Directory.CreateDirectory(PendingUpdatesDirectory);
    }
}
