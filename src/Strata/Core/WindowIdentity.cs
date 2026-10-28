using Strata.Native;

namespace Strata.Core;

/// <summary>Process facts used to tell whether a window handle still belongs to the same process.</summary>
public interface IProcessInfo
{
    uint ProcessIdOf(IntPtr window);
    /// <summary>Process creation time (FILETIME ticks), or null when it can't be read.</summary>
    long? StartTimeOf(uint pid);
    bool WindowExists(IntPtr window);
}

public sealed class Win32ProcessInfo : IProcessInfo
{
    private readonly Dictionary<uint, (long? Start, DateTime Read)> _starts = new();

    public uint ProcessIdOf(IntPtr window)
    {
        Win32.GetWindowThreadProcessId(window, out uint pid);
        return pid;
    }

    public long? StartTimeOf(uint pid)
    {
        // Cached briefly: the tracker asks for every window several times a second.
        var now = DateTime.UtcNow;
        if (_starts.TryGetValue(pid, out var c) && now - c.Read < TimeSpan.FromSeconds(30)) return c.Start;
        long? start = Win32.GetProcessStartTime(pid);
        if (_starts.Count > 2000) _starts.Clear();
        _starts[pid] = (start, now);
        return start;
    }

    public bool WindowExists(IntPtr window) => Win32.IsWindow(window);
}

/// <summary>
/// Caches "which app owns this window". Window handles are recycled by Windows, so a cached entry is only
/// trusted while the handle still belongs to the same process ID *and* that process has the same start
/// time (PIDs are recycled too). Dead handles are evicted.
/// </summary>
public sealed class WindowIdentityCache(IProcessInfo processes, Func<IntPtr, AppIdentity?> identify, int capacity = 1000)
{
    private sealed record Entry(uint Pid, long? Start, AppIdentity? App, long LastUsed);

    private readonly Dictionary<IntPtr, Entry> _entries = new();
    private long _clock;

    public int Count => _entries.Count;
    public int Misses { get; private set; }

    public AppIdentity? Resolve(IntPtr window)
    {
        uint pid = processes.ProcessIdOf(window);
        if (pid == 0)
        {
            _entries.Remove(window);
            return null;
        }
        long? start = processes.StartTimeOf(pid);
        if (start is null)
        {
            // Without a creation identity a recycled PID cannot be distinguished safely; do not cache it.
            _entries.Remove(window);
            Misses++;
            return identify(window);
        }
        if (_entries.TryGetValue(window, out var e) && e.Pid == pid && e.Start == start)
        {
            _entries[window] = e with { LastUsed = ++_clock };
            return e.App;
        }

        Misses++;
        var app = identify(window);
        _entries[window] = new Entry(pid, start, app, ++_clock);
        if (_entries.Count > capacity) Evict();
        return app;
    }

    /// <summary>True if <paramref name="window"/> is still owned by the process it was first seen with.</summary>
    public bool StillSameProcess(IntPtr window, uint pid, long? start) =>
        start is not null && processes.ProcessIdOf(window) == pid && processes.StartTimeOf(pid) == start;

    public (uint Pid, long? Start) Owner(IntPtr window)
    {
        uint pid = processes.ProcessIdOf(window);
        return (pid, pid == 0 ? null : processes.StartTimeOf(pid));
    }

    /// <summary>Drop dead windows, then the least recently used entries above capacity.</summary>
    public void Evict()
    {
        foreach (var dead in _entries.Keys.Where(h => !processes.WindowExists(h)).ToList()) _entries.Remove(dead);
        if (_entries.Count <= capacity) return;
        foreach (var old in _entries.OrderBy(kv => kv.Value.LastUsed).Take(_entries.Count - capacity).Select(kv => kv.Key).ToList())
            _entries.Remove(old);
    }

    public void Clear() => _entries.Clear();
}
