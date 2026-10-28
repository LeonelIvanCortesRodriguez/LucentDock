namespace Strata.Widgets;

/// <summary>
/// Serializes refresh requests, coalesces bursts into one follow-up refresh, and supplies a bounded
/// periodic fallback for event sources that can occasionally miss a notification.
/// </summary>
public sealed class RefreshCoordinator : IDisposable
{
    private readonly object _gate = new();
    private readonly Func<Task> _refresh;
    private readonly Action<Exception> _onError;
    private readonly TimeSpan _fallbackInterval;
    private bool _running;
    private bool _pending;
    private bool _disposed;
    private DateTime? _lastPoll;
    private TaskCompletionSource _idle = Completed();

    public RefreshCoordinator(Func<Task> refresh, TimeSpan fallbackInterval, Action<Exception>? onError = null)
    {
        if (fallbackInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(fallbackInterval));
        _refresh = refresh;
        _fallbackInterval = fallbackInterval;
        _onError = onError ?? (_ => { });
    }

    public Task WhenIdle
    {
        get { lock (_gate) return _idle.Task; }
    }

    public void Request()
    {
        bool start = false;
        lock (_gate)
        {
            if (_disposed) return;
            _pending = true;
            if (!_running)
            {
                _running = true;
                _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                start = true;
            }
        }
        if (start) _ = PumpAsync();
    }

    public void Poll(DateTime now)
    {
        bool due;
        lock (_gate)
        {
            if (_disposed) return;
            if (_lastPoll is null)
            {
                _lastPoll = now;
                return;
            }
            due = now - _lastPoll.Value >= _fallbackInterval;
            if (due) _lastPoll = now;
        }
        if (due) Request();
    }

    private async Task PumpAsync()
    {
        while (true)
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    FinishLocked();
                    return;
                }
                _pending = false;
            }

            try { await _refresh().ConfigureAwait(false); }
            catch (Exception ex)
            {
                try { _onError(ex); } catch { }
            }

            lock (_gate)
            {
                if (!_pending || _disposed)
                {
                    FinishLocked();
                    return;
                }
            }
        }
    }

    private void FinishLocked()
    {
        _running = false;
        _pending = false;
        _idle.TrySetResult();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _pending = false;
            if (!_running) _idle.TrySetResult();
        }
    }

    private static TaskCompletionSource Completed()
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        result.SetResult();
        return result;
    }
}
