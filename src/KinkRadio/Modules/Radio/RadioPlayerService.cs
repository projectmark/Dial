using Chassis.Core.Data;
using Chassis.Core.Localization;
using Chassis.Core.Settings;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace KinkRadio.Modules.Radio;

/// <summary>
/// Manages live radio playback, station selection, volume/mute persistence, ICY track metadata, and Windows SMTC integration.
/// </summary>
public interface IRadioPlayerService : IDisposable
{
    IReadOnlyList<RadioStation> Stations { get; }
    RadioStateSnapshot Current { get; }
    event Action<RadioStateSnapshot>? StateChanged;

    Task InitializeAsync(CancellationToken ct = default);
    Task PlayStationAsync(string stationId, CancellationToken ct = default);
    Task TogglePlayPauseAsync(CancellationToken ct = default);
    void Stop();
    Task NextStationAsync(CancellationToken ct = default);
    Task PreviousStationAsync(CancellationToken ct = default);
    void SetVolume(double volume);
    void ToggleMute();
}

/// <summary>
/// Windows MediaPlayer-backed radio service for KINK live streams.
/// </summary>
public sealed class RadioPlayerService : IRadioPlayerService
{
    private const string StationsCollectionName = "stations";
    private const string LastStationOptionKey = "radio.last_station_id";
    private const string VolumeOptionKey = "radio.volume";
    private const string MuteOptionKey = "radio.muted";

    private readonly IDataStore<RadioStation> _stationStore;
    private readonly ISettingsService _settings;
    private readonly ILocalizationService _localization;
    private readonly IIcyMetadataReader _icyReader;
    private readonly MediaPlayer _mediaPlayer;
    private readonly Lock _gate = new();

    private List<RadioStation> _stations;
    private RadioStation _activeStation;
    private RadioPlaybackState _state = RadioPlaybackState.Stopped;
    private string? _nowPlaying;
    private string? _errorMessage;
    private double _volume;
    private bool _isMuted;
    private bool _usingFallback;
    private long _playbackGeneration;
    private MediaSource? _currentSource;
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

        _mediaPlayer = new MediaPlayer
        {
            AudioCategory = MediaPlayerAudioCategory.Media,
            AutoPlay = true,
            Volume = _volume,
            IsMuted = _isMuted
        };

        _mediaPlayer.MediaOpened += OnMediaOpened;
        _mediaPlayer.MediaFailed += OnMediaFailed;
        _mediaPlayer.PlaybackSession.PlaybackStateChanged += OnPlaybackStateChanged;
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
                var merged = stored.ToList();
                var addedMissing = false;
                foreach (var fallback in KinkStationCatalog.DefaultStations)
                {
                    if (!merged.Any(s => string.Equals(s.Id, fallback.Id, StringComparison.OrdinalIgnoreCase)))
                    {
                        merged.Add(fallback);
                        addedMissing = true;
                    }
                }

                if (addedMissing)
                {
                    await _stationStore.SaveAllAsync(StationsCollectionName, merged, ct).ConfigureAwait(false);
                }

                lock (_gate)
                {
                    _stations = merged;
                    var savedId = _settings.GetOption(LastStationOptionKey, _stations[0].Id);
                    _activeStation = _stations.FirstOrDefault(s => string.Equals(s.Id, savedId, StringComparison.OrdinalIgnoreCase))
                        ?? _stations[0];
                }
            }
        }
        catch
        {
            // Keep default catalog if storage cannot be initialized.
        }

        UpdateSmtcDisplay();
        NotifyStateChanged();
    }

    public Task PlayStationAsync(string stationId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        RadioStation station;
        long generation;
        lock (_gate)
        {
            station = _stations.FirstOrDefault(s => string.Equals(s.Id, stationId, StringComparison.OrdinalIgnoreCase))
                ?? _activeStation;
            _activeStation = station;
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

    public Task TogglePlayPauseAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        bool shouldStop;
        string stationId;
        lock (_gate)
        {
            shouldStop = _state is RadioPlaybackState.Playing or RadioPlaybackState.Buffering;
            stationId = _activeStation.Id;
        }

        if (shouldStop)
        {
            Stop();
            return Task.CompletedTask;
        }

        return PlayStationAsync(stationId, ct);
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

    private Task StepStationAsync(int delta, CancellationToken ct)
    {
        string targetId;
        lock (_gate)
        {
            if (_stations.Count == 0)
            {
                return Task.CompletedTask;
            }

            var currentIdx = _stations.FindIndex(s => string.Equals(s.Id, _activeStation.Id, StringComparison.OrdinalIgnoreCase));
            var nextIdx = currentIdx < 0 ? 0 : (currentIdx + delta + _stations.Count) % _stations.Count;
            targetId = _stations[nextIdx].Id;
        }

        return PlayStationAsync(targetId, ct);
    }

    private void StartStream(string streamUrl, long expectedGeneration)
    {
        lock (_gate)
        {
            if (expectedGeneration != _playbackGeneration || _disposed)
            {
                return;
            }
        }

        _icyReader.Stop();
        DisposeCurrentSource();

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

            if (!_usingFallback && !string.IsNullOrWhiteSpace(_activeStation.FallbackStreamUrl))
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
        UpdateSmtcDisplay();
        NotifyStateChanged();
    }

    private void OnPlaybackStateChanged(MediaPlaybackSession sender, object args)
    {
        var mapped = sender.PlaybackState switch
        {
            MediaPlaybackState.Opening or MediaPlaybackState.Buffering => RadioPlaybackState.Buffering,
            MediaPlaybackState.Playing => RadioPlaybackState.Playing,
            _ => RadioPlaybackState.Stopped
        };

        var changed = false;
        lock (_gate)
        {
            if (_state == RadioPlaybackState.Error && mapped == RadioPlaybackState.Stopped)
            {
                return;
            }

            if (_state != mapped)
            {
                _state = mapped;
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
                _ = PlayStationAsync(Current.ActiveStation?.Id ?? KinkStationCatalog.DefaultStations[0].Id);
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
                ? snapshot.ActiveStation?.Name ?? "KINK Radio"
                : snapshot.NowPlaying;
            updater.MusicProperties.Artist = snapshot.ActiveStation?.Name ?? "KINK";
            updater.Update();
        }
        catch
        {
            // SMTC updates are best-effort for OS media integration.
        }
    }

    private void DisposeCurrentSource()
    {
        MediaSource? old;
        lock (_gate)
        {
            old = _currentSource;
            _currentSource = null;
        }

        old?.Dispose();
    }

    private RadioStateSnapshot CreateSnapshotUnsafe()
        => new(_activeStation, _state, _nowPlaying, _volume, _isMuted, _errorMessage);

    private void NotifyStateChanged()
        => StateChanged?.Invoke(Current);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _icyReader.NowPlayingChanged -= OnNowPlayingChanged;
        _icyReader.Dispose();

        if (_smtc is not null)
        {
            _smtc.ButtonPressed -= OnSmtcButtonPressed;
            _smtc = null;
        }

        _mediaPlayer.MediaOpened -= OnMediaOpened;
        _mediaPlayer.MediaFailed -= OnMediaFailed;
        _mediaPlayer.PlaybackSession.PlaybackStateChanged -= OnPlaybackStateChanged;
        _mediaPlayer.Pause();
        _mediaPlayer.Source = null;
        DisposeCurrentSource();
        _mediaPlayer.Dispose();
    }
}
