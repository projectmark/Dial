using Chassis.Core.Data;

namespace Dial.Modules.Radio;

/// <summary>
/// Tracks live session playback duration and total per-station listening statistics.
/// </summary>
public interface IListeningStatsService : IDisposable
{
    TimeSpan CurrentSessionElapsed { get; }
    event Action<TimeSpan>? SessionElapsedChanged;

    Task InitializeAsync(CancellationToken ct = default);
    IReadOnlyList<StationListeningStat> GetStats();
    void OnPlaybackStateChanged(RadioStateSnapshot snapshot);
    Task FlushAsync(CancellationToken ct = default);
}

/// <summary>
/// Persistent listening tracker storing station play-time metrics via <see cref="IDataStore{T}"/>.
/// </summary>
public sealed class ListeningStatsService : IListeningStatsService
{
    private const string StatsCollectionName = "listening_stats";

    private readonly IDataStore<StationListeningStat> _store;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, StationListeningStat> _stats = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _tickTimer;

    private string? _activeStationId;
    private string? _activeStationName;
    private bool _isPlaying;
    private TimeSpan _sessionElapsed;
    private int _ticksSinceFlush;
    private bool _dirty;
    private bool _disposed;

    public ListeningStatsService(IDataStore<StationListeningStat> store)
    {
        _store = store;
        _tickTimer = new Timer(OnTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public TimeSpan CurrentSessionElapsed
    {
        get
        {
            lock (_gate)
            {
                return _sessionElapsed;
            }
        }
    }

    public event Action<TimeSpan>? SessionElapsedChanged;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        try
        {
            var loaded = await _store.LoadAllAsync(StatsCollectionName, ct).ConfigureAwait(false);
            lock (_gate)
            {
                foreach (var stat in loaded)
                {
                    if (!string.IsNullOrWhiteSpace(stat.StationId))
                    {
                        _stats[stat.StationId] = stat;
                    }
                }
            }
        }
        catch
        {
            // Continue cleanly even if historical stats cannot be loaded.
        }
    }

    public IReadOnlyList<StationListeningStat> GetStats()
    {
        lock (_gate)
        {
            return _stats.Values
                .OrderByDescending(s => s.TotalSeconds)
                .ThenBy(s => s.StationName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public void OnPlaybackStateChanged(RadioStateSnapshot snapshot)
    {
        bool flushNeeded = false;
        TimeSpan? notifySession = null;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (snapshot.State == RadioPlaybackState.Playing && snapshot.ActiveStation is not null)
            {
                var station = snapshot.ActiveStation;
                if (!string.Equals(_activeStationId, station.Id, StringComparison.OrdinalIgnoreCase))
                {
                    if (_dirty)
                    {
                        flushNeeded = true;
                    }

                    _activeStationId = station.Id;
                    _activeStationName = station.Name;
                    _sessionElapsed = TimeSpan.Zero;
                    notifySession = TimeSpan.Zero;
                }
                else
                {
                    _activeStationName = station.Name;
                }

                var wasPlaying = _isPlaying;
                _isPlaying = true;
                if (!wasPlaying)
                {
                    _tickTimer.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
                }
            }
            else if (snapshot.State is RadioPlaybackState.Stopped or RadioPlaybackState.Error)
            {
                var wasActive = _isPlaying || _sessionElapsed > TimeSpan.Zero;
                _isPlaying = false;
                _tickTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                _sessionElapsed = TimeSpan.Zero;
                if (wasActive)
                {
                    notifySession = TimeSpan.Zero;
                }

                if (_dirty)
                {
                    flushNeeded = true;
                }
            }
            else
            {
                _isPlaying = false;
                _tickTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
        }

        if (notifySession.HasValue)
        {
            SessionElapsedChanged?.Invoke(notifySession.Value);
        }

        if (flushNeeded)
        {
            _ = FlushAsync();
        }
    }

    public async Task FlushAsync(CancellationToken ct = default)
    {
        List<StationListeningStat>? snapshot = null;
        lock (_gate)
        {
            if (!_dirty)
            {
                return;
            }

            snapshot = _stats.Values
                .OrderByDescending(s => s.TotalSeconds)
                .ToList();
            _dirty = false;
            _ticksSinceFlush = 0;
        }

        try
        {
            await _store.SaveAllAsync(StatsCollectionName, snapshot, ct).ConfigureAwait(false);
        }
        catch
        {
            lock (_gate)
            {
                _dirty = true;
            }
        }
    }

    private void OnTick(object? state)
    {
        TimeSpan elapsedSnapshot;
        bool shouldNotify = false;
        bool shouldFlush = false;

        lock (_gate)
        {
            if (_disposed || !_isPlaying || string.IsNullOrWhiteSpace(_activeStationId))
            {
                return;
            }

            _sessionElapsed += TimeSpan.FromSeconds(1);
            elapsedSnapshot = _sessionElapsed;
            shouldNotify = true;

            var name = string.IsNullOrWhiteSpace(_activeStationName) ? _activeStationId : _activeStationName;
            if (_stats.TryGetValue(_activeStationId, out var existing))
            {
                _stats[_activeStationId] = existing with
                {
                    StationName = name,
                    TotalSeconds = existing.TotalSeconds + 1,
                    LastPlayedUtc = DateTimeOffset.UtcNow
                };
            }
            else
            {
                _stats[_activeStationId] = new StationListeningStat(
                    _activeStationId,
                    name,
                    1,
                    DateTimeOffset.UtcNow);
            }

            _dirty = true;
            _ticksSinceFlush++;
            if (_ticksSinceFlush >= 30)
            {
                shouldFlush = true;
            }
        }

        if (shouldNotify)
        {
            SessionElapsedChanged?.Invoke(elapsedSnapshot);
        }

        if (shouldFlush)
        {
            _ = FlushAsync();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _isPlaying = false;
        _tickTimer.Dispose();
        try
        {
            FlushAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // Best-effort flush during shutdown.
        }
    }
}
