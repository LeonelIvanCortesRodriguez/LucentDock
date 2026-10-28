namespace Strata.Smart;

/// <summary>Identifies a signal and its value type. <see cref="Ttl"/> is how long a published value stays fresh.</summary>
public abstract class SignalKey(string id, TimeSpan ttl, string description)
{
    public string Id { get; } = id;
    public TimeSpan Ttl { get; } = ttl;
    public string Description { get; } = description;
    public abstract Type ValueType { get; }
    public override string ToString() => Id;
}

public sealed class SignalKey<T>(string id, TimeSpan ttl, string description) : SignalKey(id, ttl, description)
{
    public override Type ValueType => typeof(T);
}

/// <summary>One published reading: value, where it came from, when, how long it is good for, and how sure we are.</summary>
public sealed record SignalSnapshot(
    string Id,
    object? Value,
    string Source,
    DateTime Timestamp,
    TimeSpan Ttl,
    double Confidence,
    string? Error,
    bool Pinned = false)
{
    public bool IsFresh(DateTime now) => Error is null && (Pinned || now - Timestamp <= Ttl);
    public TimeSpan Age(DateTime now) => now - Timestamp;
}

public sealed class SignalChangedEventArgs(string id, SignalSnapshot? previous, SignalSnapshot current) : EventArgs
{
    public string Id { get; } = id;
    public SignalSnapshot? Previous { get; } = previous;
    public SignalSnapshot Current { get; } = current;
}

/// <summary>
/// The shared, typed blackboard the widgets and automations exchange information through.
/// Publishers never talk to consumers directly: they publish here, and <see cref="Changed"/> fires only
/// when a value actually changes (or a value goes stale / recovers), so subscribers are event-driven.
/// Pinned values (used by the audit test-control channel and by previews) override live publishers.
/// All access is expected on the UI thread; a lock keeps reads safe from background callers anyway.
/// </summary>
public sealed class SignalStore(IClock clock)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, SignalSnapshot> _values = new();
    private readonly HashSet<string> _staleNotified = new();

    public IClock Clock { get; } = clock;
    public event EventHandler<SignalChangedEventArgs>? Changed;

    /// <summary>Counts every publish call, changed or not (for performance evidence).</summary>
    public long PublishCount { get; private set; }
    public long ChangeCount { get; private set; }

    public void Publish<T>(SignalKey<T> key, T value, string source, double confidence = 1.0) =>
        Set(key, value, source, confidence, error: null, pinned: false);

    /// <summary>Record that a source failed; consumers see the signal as unknown until it recovers.</summary>
    public void PublishError(SignalKey key, string source, string error) =>
        Set(key, null, source, 0, error, pinned: false);

    /// <summary>Force a value (test control and previews). Live publishers are ignored until <see cref="Unpin"/>.</summary>
    public void Pin<T>(SignalKey<T> key, T value, string source) => Set(key, value, source, 1.0, null, pinned: true);

    public void Unpin(SignalKey key)
    {
        SignalSnapshot? old;
        lock (_gate)
        {
            if (!_values.TryGetValue(key.Id, out old) || !old.Pinned) return;
            // Keep the last value but mark it stale so the next live publish takes over immediately.
            _values[key.Id] = old with { Pinned = false, Timestamp = Clock.Now - key.Ttl - TimeSpan.FromSeconds(1) };
        }
        Raise(key.Id, old, _values[key.Id]);
    }

    private void Set(SignalKey key, object? value, string source, double confidence, string? error, bool pinned)
    {
        var now = Clock.Now;
        SignalSnapshot? old;
        SignalSnapshot next;
        bool changed;
        lock (_gate)
        {
            PublishCount++;
            _values.TryGetValue(key.Id, out old);
            if (old is { Pinned: true } && !pinned) return; // a pinned value wins over live publishers
            next = new SignalSnapshot(key.Id, value, source, now, key.Ttl, confidence, error, pinned);
            bool wasFresh = old is not null && old.IsFresh(now);
            changed = old is null
                      || !Equals(old.Value, value)
                      || old.Error != error
                      || old.Pinned != pinned
                      || !wasFresh; // recovered from stale
            _values[key.Id] = next;
            _staleNotified.Remove(key.Id);
        }
        if (changed) Raise(key.Id, old, next);
    }

    private void Raise(string id, SignalSnapshot? old, SignalSnapshot current)
    {
        ChangeCount++;
        Changed?.Invoke(this, new SignalChangedEventArgs(id, old, current));
    }

    public SignalSnapshot? Get(SignalKey key)
    {
        lock (_gate) return _values.TryGetValue(key.Id, out var s) ? s : null;
    }

    public SignalSnapshot? Get(string id)
    {
        lock (_gate) return _values.TryGetValue(id, out var s) ? s : null;
    }

    /// <summary>The value if it is present, error-free and within its TTL.</summary>
    public bool TryGetFresh<T>(SignalKey<T> key, out T value)
    {
        var s = Get(key);
        if (s is not null && s.IsFresh(Clock.Now) && s.Value is T v)
        {
            value = v;
            return true;
        }
        value = default!;
        return false;
    }

    public T? ValueOrDefault<T>(SignalKey<T> key) where T : class => TryGetFresh(key, out var v) ? v : null;

    public T? StructOrNull<T>(SignalKey<T> key) where T : struct => TryGetFresh(key, out var v) ? v : null;

    public IReadOnlyList<SignalSnapshot> All
    {
        get { lock (_gate) return _values.Values.OrderBy(v => v.Id).ToList(); }
    }

    /// <summary>
    /// Raise one change notification for each signal that has just passed its TTL, so rules that depend on it
    /// re-evaluate it as unknown. Called from the single shared heartbeat; costs one pass over a small dictionary.
    /// </summary>
    public void CheckExpiry()
    {
        var now = Clock.Now;
        List<SignalSnapshot> expired = [];
        lock (_gate)
        {
            foreach (var s in _values.Values)
                if (!s.IsFresh(now) && s.Error is null && _staleNotified.Add(s.Id)) expired.Add(s);
        }
        foreach (var s in expired) Raise(s.Id, s, s);
    }
}
