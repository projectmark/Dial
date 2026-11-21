using Chassis.Core.Data;
using Chassis.Core.Localization;
using Chassis.Core.Settings;
using Chassis.Core.Versioning;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace Dial.Modules.Radio;

/// <summary>
/// Manages live radio playback, favorites CRUD, preview playback, volume persistence, ICY metadata, and Windows SMTC.
/// </summary>
public interface IRadioPlayerService : IDisposable
{
    IReadOnlyList<RadioStation> Stations { get; }
    RadioStateSnapshot Current { get; }
    event Action<RadioStateSnapshot>? StateChanged;
    event Action? StationsChanged;

    Task InitializeAsync(CancellationToken ct = default);
    Task PlayStationAsync(string stationId, CancellationToken ct = default);
    Task PreviewStationAsync(RadioStation station, CancellationToken ct = default);
    Task TogglePlayPauseAsync(CancellationToken ct = default);
    void Stop();
    Task NextStationAsync(CancellationToken ct = default);
    Task PreviousStationAsync(CancellationToken ct = default);
    void SetVolume(double volume);
    void ToggleMute();

    Task AddStationAsync(RadioStation station, CancellationToken ct = default);
    Task UpdateStationAsync(RadioStation station, CancellationToken ct = default);
    Task RemoveStationAsync(string stationId, CancellationToken ct = default);
    Task RemoveStationsAsync(IReadOnlyCollection<string> stationIds, CancellationToken ct = default);
    Task MoveStationAsync(string stationId, int delta, CancellationToken ct = default);
    Task MoveStationsAsync(IReadOnlyCollection<string> stationIds, int targetInsertionIndex, CancellationToken ct = default);
    Task ImportStationsAsync(IEnumerable<RadioStation> stations, bool replaceExisting = false, CancellationToken ct = default);
    bool IsFavorite(string streamUrlOrId);
}

/// <summary>
/// Windows MediaPlayer-backed radio service for Dial live streams.
/// </summary>
public sealed class RadioPlayerService : IRadioPlayerService
{
    private const string StationsCollectionName = "stations";
    private const string LastStationOptionKey = RadioOptions.LastStationId;
    private const string VolumeOptionKey = RadioOptions.Volume;
    private const string MuteOptionKey = RadioOptions.Muted;

    private readonly IDataStore<RadioStation> _stationStore;
    private readonly ISettingsService _settings;
    private readonly ILocalizationService _localization;
    private readonly IIcyMetadataReader _icyReader;
    private readonly MediaPlayer _mediaPlayer;
    private readonly Lock _gate = new();

    private List<RadioStation> _stations;
    private RadioStation? _activeStation;
    private RadioPlaybackState _state = RadioPlaybackState.Stopped;
    private string? _nowPlaying;
    private string? _errorMessage;
    private double _volume;
    private bool _isMuted;
    private bool _isPreview;
    private bool _usingFallback;
    private long _playbackGeneration;
    private MediaSource? _currentSource;
    private BufferedFrameMediaSource? _activeFeed;
    private string? _activeUpstreamUrl;
    private int _bufferSeconds;
    private double _bufferedSeconds;
    private int _unexpectedEndRecoveryCount;
    private SystemMediaTransportControls? _smtc;
    private bool _disposed;

    public RadioPlayerService(
        IDataStore<RadioStation> stationStore,
        ISettingsService settings,
        ILocalizationService localization,
        IIcyMetadataReader icyReader)
    {
        _stationStore = stationStore;
        _settings = settings;
        _localization = localization;
        _icyReader = icyReader;

        _stations = KinkStationCatalog.DefaultStations.ToList();
        var savedId = _settings.GetOption(LastStationOptionKey, _stations[0].Id);
        _activeStation = _stations.FirstOrDefault(s => string.Equals(s.Id, savedId, StringComparison.OrdinalIgnoreCase))
            ?? _stations[0];
        _volume = Math.Clamp(_settings.GetOption(VolumeOptionKey, 0.8), 0.0, 1.0);
        _isMuted = _settings.GetOption(MuteOptionKey, false);

        _bufferSeconds = AudioBufferSizing.NormalizeSeconds(
            _settings.GetOption(AudioBufferSizing.OptionKey, AudioBufferSizing.DefaultBufferSeconds));

        _mediaPlayer = new MediaPlayer
        {
            AudioCategory = MediaPlayerAudioCategory.Media,
            AutoPlay = true,
            Volume = _volume,
            IsMuted = _isMuted
        };

        _settings.SettingsChanged += OnSettingsChanged;
        _mediaPlayer.MediaOpened += OnMediaOpened;
        _mediaPlayer.MediaFailed += OnMediaFailed;
        _mediaPlayer.MediaEnded += OnMediaEnded;
        _mediaPlayer.PlaybackSession.PlaybackStateChanged += OnPlaybackStateChanged;
        _mediaPlayer.PlaybackSession.BufferingStarted += OnBufferingStarted;
        _mediaPlayer.PlaybackSession.BufferingEnded += OnBufferingEnded;
        _icyReader.NowPlayingChanged += OnNowPlayingChanged;

        ConfigureSmtc();
        UpdateSmtcDisplay();
    }

    public IReadOnlyList<RadioStation> Stations
    {
        get
        {
            lock (_gate)
            {
                return _stations.ToList();
            }
        }
    }

    public RadioStateSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return CreateSnapshotUnsafe();
            }
        }
    }

    public event Action<RadioStateSnapshot>? StateChanged;
    public event Action? StationsChanged;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        try
        {
            var stored = await _stationStore.LoadAllAsync(StationsCollectionName, ct).ConfigureAwait(false);
            if (stored.Count == 0)
            {
                await _stationStore.SaveAllAsync(StationsCollectionName, KinkStationCatalog.DefaultStations, ct)
                    .ConfigureAwait(false);
            }
            else
            {
                var sanitized = stored.Select(SanitizeStation).ToList();
                lock (_gate)
                {
                    _stations = sanitized;
                    var savedId = _settings.GetOption(LastStationOptionKey, _stations[0].Id);
                    _activeStation = _stations.FirstOrDefault(s => string.Equals(s.Id, savedId, StringComparison.OrdinalIgnoreCase))
                        ?? _stations.FirstOrDefault();
                }
            }
        }
        catch
        {
            // Keep default catalog if storage cannot be initialized.
        }

        UpdateSmtcDisplay();
        StationsChanged?.Invoke();
        NotifyStateChanged();
    }

    public Task PlayStationAsync(string stationId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        RadioStation? station;
        long generation;
        lock (_gate)
        {
            station = _stations.FirstOrDefault(s => string.Equals(s.Id, stationId, StringComparison.OrdinalIgnoreCase))
                ?? _activeStation
                ?? _stations.FirstOrDefault();
            if (station is null)
            {
                return Task.CompletedTask;
            }

            _activeStation = station;
            _isPreview = false;
            _usingFallback = false;
            _state = RadioPlaybackState.Buffering;
            _nowPlaying = null;
            _errorMessage = null;
            generation = ++_playbackGeneration;
        }

        _settings.SetOption(LastStationOptionKey, station.Id);
        StartStream(station.StreamUrl, generation);
        UpdateSmtcDisplay();
        NotifyStateChanged();
        return Task.CompletedTask;
    }

    public Task PreviewStationAsync(RadioStation station, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(station);

        var normalized = SanitizeStation(station);
        long generation;
        bool isAlreadyFavorite;

        lock (_gate)
        {
            isAlreadyFavorite = _stations.Any(s =>
                string.Equals(s.Id, normalized.Id, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.StreamUrl, normalized.StreamUrl, StringComparison.OrdinalIgnoreCase));

            _activeStation = normalized;
            _isPreview = !isAlreadyFavorite;
            _usingFallback = false;
            _state = RadioPlaybackState.Buffering;
            _nowPlaying = null;
            _errorMessage = null;
            generation = ++_playbackGeneration;
        }

        StartStream(normalized.StreamUrl, generation);
        UpdateSmtcDisplay();
        NotifyStateChanged();
        return Task.CompletedTask;
    }

    public Task TogglePlayPauseAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        bool shouldStop;
        RadioStation? active;
        bool isPreview;
        lock (_gate)
        {
            shouldStop = _state is RadioPlaybackState.Playing or RadioPlaybackState.Buffering;
            active = _activeStation ?? _stations.FirstOrDefault();
            isPreview = _isPreview;
        }

        if (shouldStop)
        {
            Stop();
            return Task.CompletedTask;
        }

        if (active is null)
        {
            return Task.CompletedTask;
        }

        return isPreview ? PreviewStationAsync(active, ct) : PlayStationAsync(active.Id, ct);
    }

    public void Stop()
    {
        if (_disposed)
        {
            return;
        }

        lock (_gate)
        {
            _playbackGeneration++;
            _state = RadioPlaybackState.Stopped;
            _nowPlaying = null;
            _errorMessage = null;
        }

        _icyReader.Stop();

        try
        {
            _mediaPlayer.Pause();
            _mediaPlayer.Source = null;
        }
        catch
        {
            // Ignore player teardown races.
        }

        DisposeCurrentSource();
        UpdateSmtcDisplay();
        NotifyStateChanged();
    }

    public Task NextStationAsync(CancellationToken ct = default)
        => StepStationAsync(1, ct);

    public Task PreviousStationAsync(CancellationToken ct = default)
        => StepStationAsync(-1, ct);

    public void SetVolume(double volume)
    {
        if (_disposed)
        {
            return;
        }

        var clamped = Math.Clamp(volume, 0.0, 1.0);
        lock (_gate)
        {
            _volume = clamped;
            if (clamped > 0 && _isMuted)
            {
                _isMuted = false;
            }
        }

        _mediaPlayer.Volume = clamped;
        _mediaPlayer.IsMuted = _isMuted;
        _settings.SetOption(VolumeOptionKey, clamped, autoSave: false);
        _settings.SetOption(MuteOptionKey, _isMuted, autoSave: true);
        NotifyStateChanged();
    }

    public void ToggleMute()
    {
        if (_disposed)
        {
            return;
        }

        bool muted;
        lock (_gate)
        {
            _isMuted = !_isMuted;
            muted = _isMuted;
        }

        _mediaPlayer.IsMuted = muted;
        _settings.SetOption(MuteOptionKey, muted);
        NotifyStateChanged();
    }

    public async Task AddStationAsync(RadioStation station, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(station);

        var normalized = SanitizeStation(station);
        List<RadioStation> snapshot;
        bool activePromoted = false;

        lock (_gate)
        {
            var existingIdx = _stations.FindIndex(s =>
                string.Equals(s.Id, normalized.Id, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.StreamUrl, normalized.StreamUrl, StringComparison.OrdinalIgnoreCase));

            if (existingIdx >= 0)
            {
                return;
            }

            _stations.Add(normalized);
            if (_activeStation is not null &&
                string.Equals(_activeStation.StreamUrl, normalized.StreamUrl, StringComparison.OrdinalIgnoreCase))
            {
                _activeStation = normalized;
                _isPreview = false;
                activePromoted = true;
            }
            else if (_activeStation is null)
            {
                _activeStation = normalized;
            }

            snapshot = _stations.ToList();
        }

        await _stationStore.SaveAllAsync(StationsCollectionName, snapshot, ct).ConfigureAwait(false);
        StationsChanged?.Invoke();
        if (activePromoted)
        {
            UpdateSmtcDisplay();
            NotifyStateChanged();
        }
    }

    public async Task UpdateStationAsync(RadioStation station, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(station);

        var normalized = SanitizeStation(station);
        List<RadioStation> snapshot;
        bool activeUpdated = false;

        lock (_gate)
        {
            var idx = _stations.FindIndex(s => string.Equals(s.Id, normalized.Id, StringComparison.OrdinalIgnoreCase));
            if (idx < 0)
            {
                return;
            }

            _stations[idx] = normalized;
            if (_activeStation is not null && string.Equals(_activeStation.Id, normalized.Id, StringComparison.OrdinalIgnoreCase))
            {
                _activeStation = normalized;
                activeUpdated = true;
            }

            snapshot = _stations.ToList();
        }

        await _stationStore.SaveAllAsync(StationsCollectionName, snapshot, ct).ConfigureAwait(false);
        StationsChanged?.Invoke();
        if (activeUpdated)
        {
            UpdateSmtcDisplay();
            NotifyStateChanged();
        }
    }

    public async Task RemoveStationAsync(string stationId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(stationId))
        {
            return;
        }

        await RemoveStationsAsync(new[] { stationId }, ct).ConfigureAwait(false);
    }

    public async Task RemoveStationsAsync(IReadOnlyCollection<string> stationIds, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (stationIds is null || stationIds.Count == 0)
        {
            return;
        }

        List<RadioStation> snapshot;
        bool stateChanged = false;

        lock (_gate)
        {
            var next = StationListOperations.RemoveStations(_stations, stationIds);
            if (next.Count == _stations.Count)
            {
                return;
            }

            _stations = next;

            if (_activeStation is not null &&
                !_stations.Any(s => string.Equals(s.Id, _activeStation.Id, StringComparison.OrdinalIgnoreCase)))
            {
                if (_state is RadioPlaybackState.Playing or RadioPlaybackState.Buffering)
                {
                    _isPreview = true;
                }
                else
                {
                    _activeStation = _stations.FirstOrDefault();
                }

                stateChanged = true;
            }

            snapshot = _stations.ToList();
        }

        await _stationStore.SaveAllAsync(StationsCollectionName, snapshot, ct).ConfigureAwait(false);
        StationsChanged?.Invoke();
        if (stateChanged)
        {
            UpdateSmtcDisplay();
            NotifyStateChanged();
        }
    }

    public async Task MoveStationAsync(string stationId, int delta, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(stationId) || delta == 0)
        {
            return;
        }

        List<RadioStation> snapshot;
        lock (_gate)
        {
            var idx = _stations.FindIndex(s => string.Equals(s.Id, stationId, StringComparison.OrdinalIgnoreCase));
            if (idx < 0)
            {
                return;
            }

            var targetIdx = Math.Clamp(idx + delta, 0, _stations.Count - 1);
            if (targetIdx == idx)
            {
                return;
            }

            var item = _stations[idx];
            _stations.RemoveAt(idx);
            _stations.Insert(targetIdx, item);
            snapshot = _stations.ToList();
        }

        await _stationStore.SaveAllAsync(StationsCollectionName, snapshot, ct).ConfigureAwait(false);
        StationsChanged?.Invoke();
    }

    public async Task MoveStationsAsync(IReadOnlyCollection<string> stationIds, int targetInsertionIndex, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (stationIds is null || stationIds.Count == 0)
        {
            return;
        }

        List<RadioStation> snapshot;
        lock (_gate)
        {
            var reordered = StationListOperations.ReorderStations(_stations, stationIds, targetInsertionIndex);
            if (reordered.SequenceEqual(_stations))
            {
                return;
            }

            _stations = reordered;
            snapshot = _stations.ToList();
        }

        await _stationStore.SaveAllAsync(StationsCollectionName, snapshot, ct).ConfigureAwait(false);
        StationsChanged?.Invoke();
    }

    public async Task ImportStationsAsync(IEnumerable<RadioStation> stations, bool replaceExisting = false, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(stations);

        var incoming = stations
            .Where(s => s is not null && !string.IsNullOrWhiteSpace(s.Name) && !string.IsNullOrWhiteSpace(s.StreamUrl))
            .Select(SanitizeStation)
            .ToList();

        if (incoming.Count == 0)
        {
            return;
        }

        List<RadioStation> snapshot;
        lock (_gate)
        {
            if (replaceExisting)
            {
                _stations = incoming;
            }
            else
            {
                var existingUrls = new HashSet<string>(_stations.Select(s => s.StreamUrl), StringComparer.OrdinalIgnoreCase);
                var existingIds = new HashSet<string>(_stations.Select(s => s.Id), StringComparer.OrdinalIgnoreCase);
                foreach (var item in incoming)
                {
                    if (existingUrls.Add(item.StreamUrl) && existingIds.Add(item.Id))
                    {
                        _stations.Add(item);
                    }
                }
            }

            if (_activeStation is null)
            {
                _activeStation = _stations.FirstOrDefault();
            }

            snapshot = _stations.ToList();
        }

        await _stationStore.SaveAllAsync(StationsCollectionName, snapshot, ct).ConfigureAwait(false);
        StationsChanged?.Invoke();
        NotifyStateChanged();
    }

    public bool IsFavorite(string streamUrlOrId)
    {
        if (string.IsNullOrWhiteSpace(streamUrlOrId))
        {
            return false;
        }

        lock (_gate)
        {
            return _stations.Any(s =>
                string.Equals(s.Id, streamUrlOrId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.StreamUrl, streamUrlOrId, StringComparison.OrdinalIgnoreCase));
        }
    }

    private Task StepStationAsync(int delta, CancellationToken ct)
    {
        string? targetId;
        lock (_gate)
        {
            if (_stations.Count == 0)
            {
                return Task.CompletedTask;
            }

            var currentIdx = _activeStation is null
                ? -1
                : _stations.FindIndex(s => string.Equals(s.Id, _activeStation.Id, StringComparison.OrdinalIgnoreCase));
            var nextIdx = currentIdx < 0 ? 0 : (currentIdx + delta + _stations.Count) % _stations.Count;
            targetId = _stations[nextIdx].Id;
        }

        return PlayStationAsync(targetId, ct);
    }

    private void StartStream(string streamUrl, long expectedGeneration)
        => _ = StartStreamAsync(streamUrl, expectedGeneration, isRecovery: false);

    private async Task StartStreamAsync(string streamUrl, long expectedGeneration, bool isRecovery)
    {
        int bufferSeconds;
        lock (_gate)
        {
            if (expectedGeneration != _playbackGeneration || _disposed)
            {
                return;
            }

            _activeUpstreamUrl = streamUrl;
            if (!isRecovery)
            {
                _unexpectedEndRecoveryCount = 0;
            }

            bufferSeconds = _bufferSeconds;
        }

        _icyReader.Stop();
        DisposeCurrentSource();

        if (!AudioBufferSizing.ShouldBypassProxy(streamUrl))
        {
            BufferedFrameMediaSource? feed = null;
            try
            {
                feed = new BufferedFrameMediaSource(streamUrl, bufferSeconds);
                var capturedFeed = feed;
                feed.NowPlayingChanged += title => OnFeedNowPlayingChanged(capturedFeed, title);
                feed.BufferingStateChanged += isBuf => OnFeedBufferingChanged(capturedFeed, isBuf);
                feed.BufferLevelChanged += (buffered, target) => OnFeedBufferLevelChanged(capturedFeed, buffered, target);
                feed.UpstreamFailed += () => OnFeedUpstreamFailed(expectedGeneration);

                lock (_gate)
                {
                    if (expectedGeneration != _playbackGeneration || _disposed)
                    {
                        feed.Dispose();
                        return;
                    }

                    _activeFeed = feed;
                }

                var startResult = await feed.StartAsync().ConfigureAwait(false);

                lock (_gate)
                {
                    if (expectedGeneration != _playbackGeneration || _disposed || !ReferenceEquals(_activeFeed, feed))
                    {
                        startResult.Source?.Dispose();
                        feed.Dispose();
                        return;
                    }
                }

                if (startResult.Status == BufferedMediaStartStatus.Started && startResult.Source is not null)
                {
                    lock (_gate)
                    {
                        if (expectedGeneration != _playbackGeneration || _disposed || !ReferenceEquals(_activeFeed, feed))
                        {
                            startResult.Source.Dispose();
                            feed.Dispose();
                            return;
                        }

                        _currentSource = startResult.Source;
                        _bufferedSeconds = startResult.BufferedSeconds;
                    }

                    _mediaPlayer.Source = startResult.Source;
                    _mediaPlayer.Play();
                    NotifyStateChanged();
                    return;
                }

                lock (_gate)
                {
                    if (ReferenceEquals(_activeFeed, feed))
                    {
                        _activeFeed = null;
                    }
                }

                startResult.Source?.Dispose();
                feed.Dispose();
                feed = null;

                if (startResult.Status == BufferedMediaStartStatus.Failed)
                {
                    HandlePlaybackError(expectedGeneration);
                    return;
                }
            }
            catch
            {
                if (feed is not null)
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(_activeFeed, feed))
                        {
                            _activeFeed = null;
                        }
                    }

                    feed.Dispose();
                }

                HandlePlaybackError(expectedGeneration);
                return;
            }
        }

        try
        {
            var source = MediaSource.CreateFromUri(new Uri(streamUrl));
            lock (_gate)
            {
                if (expectedGeneration != _playbackGeneration || _disposed)
                {
                    source.Dispose();
                    return;
                }

                _currentSource = source;
            }

            _mediaPlayer.Source = source;
            _mediaPlayer.Play();
            _icyReader.Start(streamUrl);
        }
        catch
        {
            HandlePlaybackError(expectedGeneration);
        }
    }

    private void OnSettingsChanged(AppSettings _)
    {
        var configured = AudioBufferSizing.NormalizeSeconds(
            _settings.GetOption(AudioBufferSizing.OptionKey, AudioBufferSizing.DefaultBufferSeconds));

        BufferedFrameMediaSource? feed;
        lock (_gate)
        {
            if (configured == _bufferSeconds)
            {
                return;
            }

            _bufferSeconds = configured;
            feed = _activeFeed;
        }

        feed?.UpdateBufferSeconds(configured);
        NotifyStateChanged();
    }

    private void OnFeedNowPlayingChanged(BufferedFrameMediaSource sourceFeed, string? title)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(sourceFeed, _activeFeed))
            {
                return;
            }
        }

        OnNowPlayingChanged(title);
    }

    private void OnFeedBufferingChanged(BufferedFrameMediaSource sourceFeed, bool isBuffering)
    {
        var changed = false;
        lock (_gate)
        {
            if (!ReferenceEquals(sourceFeed, _activeFeed) || _state == RadioPlaybackState.Stopped)
            {
                return;
            }

            var desired = isBuffering ? RadioPlaybackState.Buffering : RadioPlaybackState.Playing;
            if (_state != desired)
            {
                _state = desired;
                changed = true;
            }
        }

        if (changed)
        {
            UpdateSmtcDisplay();
            NotifyStateChanged();
        }
    }

    private void OnFeedBufferLevelChanged(BufferedFrameMediaSource sourceFeed, double bufferedSeconds, int targetSeconds)
    {
        var shouldNotify = false;
        lock (_gate)
        {
            if (!ReferenceEquals(sourceFeed, _activeFeed) || _state == RadioPlaybackState.Stopped)
            {
                return;
            }

            var delta = Math.Abs(bufferedSeconds - _bufferedSeconds);
            if (_state == RadioPlaybackState.Buffering || delta >= 0.4 || (bufferedSeconds == 0 && _bufferedSeconds > 0))
            {
                _bufferedSeconds = bufferedSeconds;
                shouldNotify = true;
            }
        }

        if (shouldNotify)
        {
            NotifyStateChanged();
        }
    }

    private void OnFeedUpstreamFailed(long expectedGeneration)
        => HandlePlaybackError(expectedGeneration);

    private void OnMediaEnded(MediaPlayer sender, object args)
    {
        long gen;
        string? url;
        bool shouldRecover;
        lock (_gate)
        {
            if (_disposed || _state == RadioPlaybackState.Stopped)
            {
                return;
            }

            gen = _playbackGeneration;
            url = _activeUpstreamUrl;
            if (!string.IsNullOrWhiteSpace(url) && _unexpectedEndRecoveryCount < 2)
            {
                _unexpectedEndRecoveryCount++;
                _state = RadioPlaybackState.Buffering;
                shouldRecover = true;
            }
            else
            {
                shouldRecover = false;
            }
        }

        if (shouldRecover && url is not null)
        {
            NotifyStateChanged();
            _ = RecoverUnexpectedEndAsync(url, gen);
            return;
        }

        HandlePlaybackError(gen);
    }

    private async Task RecoverUnexpectedEndAsync(string url, long expectedGeneration)
    {
        try
        {
            await Task.Delay(400).ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        await StartStreamAsync(url, expectedGeneration, isRecovery: true).ConfigureAwait(false);
    }

    private void OnBufferingStarted(MediaPlaybackSession sender, object args)
    {
        var changed = false;
        lock (_gate)
        {
            if (_state == RadioPlaybackState.Playing)
            {
                _state = RadioPlaybackState.Buffering;
                changed = true;
            }
        }

        if (changed)
        {
            UpdateSmtcDisplay();
            NotifyStateChanged();
        }
    }

    private void OnBufferingEnded(MediaPlaybackSession sender, object args)
    {
        var changed = false;
        lock (_gate)
        {
            if (_state == RadioPlaybackState.Buffering)
            {
                _state = RadioPlaybackState.Playing;
                changed = true;
            }
        }

        if (changed)
        {
            UpdateSmtcDisplay();
            NotifyStateChanged();
        }
    }

    private void OnMediaOpened(MediaPlayer sender, object args)
    {
        lock (_gate)
        {
            if (_state != RadioPlaybackState.Stopped)
            {
                _state = RadioPlaybackState.Playing;
                _errorMessage = null;
            }
        }

        UpdateSmtcDisplay();
        NotifyStateChanged();
    }

    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        long gen;
        lock (_gate)
        {
            gen = _playbackGeneration;
        }

        HandlePlaybackError(gen);
    }

    private void HandlePlaybackError(long generation)
    {
        string? fallbackUrl = null;
        lock (_gate)
        {
            if (generation != _playbackGeneration || _disposed)
            {
                return;
            }

            if (!_usingFallback &&
                _activeStation is not null &&
                !string.IsNullOrWhiteSpace(_activeStation.FallbackStreamUrl))
            {
                _usingFallback = true;
                _state = RadioPlaybackState.Buffering;
                fallbackUrl = _activeStation.FallbackStreamUrl;
            }
            else
            {
                _state = RadioPlaybackState.Error;
                _errorMessage = _localization["Radio.Error.StreamFailed"];
            }
        }

        if (fallbackUrl is not null)
        {
            StartStream(fallbackUrl, generation);
            NotifyStateChanged();
            return;
        }

        _icyReader.Stop();
        DisposeCurrentSource();
        UpdateSmtcDisplay();
        NotifyStateChanged();
    }

    private void OnPlaybackStateChanged(MediaPlaybackSession sender, object args)
    {
        RadioPlaybackState? mapped = sender.PlaybackState switch
        {
            MediaPlaybackState.Opening or MediaPlaybackState.Buffering => RadioPlaybackState.Buffering,
            MediaPlaybackState.Playing => RadioPlaybackState.Playing,
            _ => null
        };

        if (!mapped.HasValue)
        {
            return;
        }

        var changed = false;
        lock (_gate)
        {
            if (_state is RadioPlaybackState.Stopped or RadioPlaybackState.Error)
            {
                return;
            }

            if (_state != mapped.Value)
            {
                _state = mapped.Value;
                changed = true;
            }
        }

        if (changed)
        {
            UpdateSmtcDisplay();
            NotifyStateChanged();
        }
    }

    private void OnNowPlayingChanged(string? title)
    {
        var changed = false;
        lock (_gate)
        {
            if (_state is RadioPlaybackState.Playing or RadioPlaybackState.Buffering &&
                !string.Equals(_nowPlaying, title, StringComparison.Ordinal))
            {
                _nowPlaying = title;
                changed = true;
            }
        }

        if (changed)
        {
            UpdateSmtcDisplay();
            NotifyStateChanged();
        }
    }

    private void ConfigureSmtc()
    {
        try
        {
            _mediaPlayer.CommandManager.IsEnabled = false;
            _smtc = _mediaPlayer.SystemMediaTransportControls;
            _smtc.IsEnabled = true;
            _smtc.IsPlayEnabled = true;
            _smtc.IsPauseEnabled = true;
            _smtc.IsStopEnabled = true;
            _smtc.IsNextEnabled = true;
            _smtc.IsPreviousEnabled = true;
            _smtc.ButtonPressed += OnSmtcButtonPressed;
        }
        catch
        {
            _smtc = null;
        }
    }

    private void OnSmtcButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        switch (args.Button)
        {
            case SystemMediaTransportControlsButton.Play:
                _ = TogglePlayPauseAsync();
                break;
            case SystemMediaTransportControlsButton.Pause:
            case SystemMediaTransportControlsButton.Stop:
                Stop();
                break;
            case SystemMediaTransportControlsButton.Next:
                _ = NextStationAsync();
                break;
            case SystemMediaTransportControlsButton.Previous:
                _ = PreviousStationAsync();
                break;
        }
    }

    private void UpdateSmtcDisplay()
    {
        if (_smtc is null)
        {
            return;
        }

        try
        {
            var snapshot = Current;
            _smtc.PlaybackStatus = snapshot.State switch
            {
                RadioPlaybackState.Playing => MediaPlaybackStatus.Playing,
                RadioPlaybackState.Buffering => MediaPlaybackStatus.Changing,
                _ => MediaPlaybackStatus.Stopped
            };

            var updater = _smtc.DisplayUpdater;
            updater.Type = MediaPlaybackType.Music;
            updater.MusicProperties.Title = string.IsNullOrWhiteSpace(snapshot.NowPlaying)
                ? snapshot.ActiveStation?.Name ?? AppVersion.ProductTitle
                : snapshot.NowPlaying;
            updater.MusicProperties.Artist = snapshot.ActiveStation?.Name ?? AppVersion.ProductTitle;
            updater.Update();
        }
        catch
        {
            // SMTC updates are best-effort for OS media integration.
        }
    }

    private static RadioStation SanitizeStation(RadioStation station)
        => station with
        {
            Id = string.IsNullOrWhiteSpace(station.Id) ? $"custom-{Guid.NewGuid():N}" : station.Id.Trim(),
            Name = station.Name.Trim(),
            Tagline = station.Tagline ?? string.Empty,
            StreamUrl = station.StreamUrl.Trim(),
            FallbackStreamUrl = station.FallbackStreamUrl ?? string.Empty,
            IconGlyph = string.IsNullOrWhiteSpace(station.IconGlyph) ? "\uE7F6" : station.IconGlyph,
            AccentHex = string.IsNullOrWhiteSpace(station.AccentHex) ? "#0078D4" : station.AccentHex,
            CountryCode = string.IsNullOrWhiteSpace(station.CountryCode) ? "INT" : station.CountryCode.Trim().ToUpperInvariant(),
            Codec = string.IsNullOrWhiteSpace(station.Codec) ? "MP3" : station.Codec.Trim().ToUpperInvariant(),
            Tags = station.Tags ?? string.Empty
        };

    private void DisposeCurrentSource()
    {
        MediaSource? oldSource;
        BufferedFrameMediaSource? oldFeed;
        lock (_gate)
        {
            oldSource = _currentSource;
            _currentSource = null;
            oldFeed = _activeFeed;
            _activeFeed = null;
            _bufferedSeconds = 0;
        }

        oldSource?.Dispose();
        oldFeed?.Dispose();
    }

    private RadioStateSnapshot CreateSnapshotUnsafe()
        => new(_activeStation, _state, _nowPlaying, _volume, _isMuted, _errorMessage, _isPreview, _bufferedSeconds, _bufferSeconds);

    private void NotifyStateChanged()
        => StateChanged?.Invoke(Current);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _settings.SettingsChanged -= OnSettingsChanged;
        _icyReader.NowPlayingChanged -= OnNowPlayingChanged;
        _icyReader.Dispose();

        if (_smtc is not null)
        {
            _smtc.ButtonPressed -= OnSmtcButtonPressed;
            _smtc = null;
        }

        _mediaPlayer.MediaOpened -= OnMediaOpened;
        _mediaPlayer.MediaFailed -= OnMediaFailed;
        _mediaPlayer.MediaEnded -= OnMediaEnded;
        _mediaPlayer.PlaybackSession.PlaybackStateChanged -= OnPlaybackStateChanged;
        _mediaPlayer.PlaybackSession.BufferingStarted -= OnBufferingStarted;
        _mediaPlayer.PlaybackSession.BufferingEnded -= OnBufferingEnded;
        _mediaPlayer.Pause();
        _mediaPlayer.Source = null;
        DisposeCurrentSource();
        _mediaPlayer.Dispose();
    }
}
