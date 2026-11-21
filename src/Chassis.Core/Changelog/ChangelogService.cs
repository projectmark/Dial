using Chassis.Core.Settings;
using Chassis.Core.Versioning;

namespace Chassis.Core.Changelog;

public sealed record ChangelogChange(string ChangeType, string DescriptionKey);

public sealed record ChangelogRelease(
    string Version,
    DateTime ReleaseDate,
    IReadOnlyList<ChangelogChange> Changes,
    bool IsLatest = false);

/// <summary>
/// Manages versioned release notes and tracks unread status relative to persistent LastSeenVersion.
/// </summary>
public interface IChangelogService
{
    bool HasNewVersion { get; }
    event Action<bool>? UnreadStateChanged;
    void RegisterRelease(ChangelogRelease release);
    IReadOnlyList<ChangelogRelease> GetReleases();
    void MarkCurrentAsViewed();
}

/// <summary>
/// In-memory changelog registry synchronized with <see cref="ISettingsService"/>.
/// </summary>
public sealed class ChangelogService : IChangelogService
{
    private readonly ISettingsService _settings;
    private readonly List<ChangelogRelease> _releases = [];
    private readonly Lock _gate = new();

    public ChangelogService(ISettingsService settings)
    {
        _settings = settings;
        HasNewVersion = EvaluateHasNewVersion();
    }

    public bool HasNewVersion { get; private set; }
    public event Action<bool>? UnreadStateChanged;

    /// <inheritdoc />
    public void RegisterRelease(ChangelogRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);
        lock (_gate)
        {
            _releases.RemoveAll(r => string.Equals(r.Version, release.Version, StringComparison.OrdinalIgnoreCase));
            _releases.Add(release);
        }

        UpdateUnreadState();
    }

    /// <inheritdoc />
    public IReadOnlyList<ChangelogRelease> GetReleases()
    {
        lock (_gate)
        {
            return _releases
                .OrderByDescending(r => r.ReleaseDate)
                .ToList();
        }
    }

    /// <inheritdoc />
    public void MarkCurrentAsViewed()
    {
        var current = AppVersion.InformationalVersion;
        if (!string.Equals(_settings.Current.LastSeenVersion, current, StringComparison.OrdinalIgnoreCase))
        {
            _settings.Update(s => s.LastSeenVersion = current);
        }

        UpdateUnreadState();
    }

    private void UpdateUnreadState()
    {
        var next = EvaluateHasNewVersion();
        if (next != HasNewVersion)
        {
            HasNewVersion = next;
            UnreadStateChanged?.Invoke(next);
        }
    }

    private bool EvaluateHasNewVersion()
    {
        var lastSeen = _settings.Current.LastSeenVersion;
        if (string.IsNullOrWhiteSpace(lastSeen))
        {
            return true;
        }

        if (SemVersion.TryParse(AppVersion.InformationalVersion, out var currentSem) &&
            SemVersion.TryParse(lastSeen, out var seenSem))
        {
            return currentSem > seenSem;
        }

        return !string.Equals(AppVersion.InformationalVersion, lastSeen, StringComparison.OrdinalIgnoreCase);
    }
}
