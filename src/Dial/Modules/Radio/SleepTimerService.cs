namespace Dial.Modules.Radio;

/// <summary>
/// Configurable sleep timer up to 6 hours that stops playback automatically upon expiration.
/// </summary>
public interface ISleepTimerService : IDisposable
{
    bool IsActive { get; }
    TimeSpan Remaining { get; }
    event Action<TimeSpan?>? RemainingChanged;
    event Action? Expired;

    void Start(TimeSpan duration);
    void Cancel();
}

/// <summary>
/// 1-second resolution sleep timer backed by <see cref="System.Threading.Timer"/>.
/// </summary>
public sealed class SleepTimerService : ISleepTimerService
{
    public static readonly TimeSpan MinDuration = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(6);

    private readonly Lock _gate = new();
    private Timer? _timer;
    private DateTimeOffset? _endsAtUtc;
    private bool _disposed;

    public bool IsActive
    {
        get
        {
            lock (_gate)
            {
                return _endsAtUtc.HasValue;
            }
        }
    }

    public TimeSpan Remaining
    {
        get
        {
            lock (_gate)
            {
                if (!_endsAtUtc.HasValue)
                {
                    return TimeSpan.Zero;
                }

                var diff = _endsAtUtc.Value - DateTimeOffset.UtcNow;
                return diff > TimeSpan.Zero ? diff : TimeSpan.Zero;
            }
        }
    }

    public event Action<TimeSpan?>? RemainingChanged;
    public event Action? Expired;

    public void Start(TimeSpan duration)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var clamped = duration < MinDuration
            ? MinDuration
            : duration > MaxDuration ? MaxDuration : duration;

        lock (_gate)
        {
            _timer?.Dispose();
            _endsAtUtc = DateTimeOffset.UtcNow + clamped;
            _timer = new Timer(OnTick, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        RemainingChanged?.Invoke(clamped);
    }

    public void Cancel()
    {
        bool wasActive;
        lock (_gate)
        {
            wasActive = _endsAtUtc.HasValue;
            _endsAtUtc = null;
            _timer?.Dispose();
            _timer = null;
        }

        if (wasActive)
        {
            RemainingChanged?.Invoke(null);
        }
    }

    private void OnTick(object? state)
    {
        TimeSpan? remainingSnapshot = null;
        var expired = false;

        lock (_gate)
        {
            if (_disposed || !_endsAtUtc.HasValue)
            {
                return;
            }

            var diff = _endsAtUtc.Value - DateTimeOffset.UtcNow;
            if (diff <= TimeSpan.Zero)
            {
                _endsAtUtc = null;
                _timer?.Dispose();
                _timer = null;
                expired = true;
            }
            else
            {
                remainingSnapshot = diff;
            }
        }

        if (expired)
        {
            RemainingChanged?.Invoke(null);
            Expired?.Invoke();
        }
        else if (remainingSnapshot.HasValue)
        {
            RemainingChanged?.Invoke(remainingSnapshot);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _endsAtUtc = null;
            _timer?.Dispose();
            _timer = null;
        }
    }
}
