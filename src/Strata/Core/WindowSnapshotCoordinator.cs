namespace Strata.Core;

/// <summary>
/// One bounded desktop enumeration shared by every dock. Subscribers only regroup the immutable snapshot; they
/// never start their own EnumWindows/IShellWindows poller. Monitor-sensitive mode publishes even when the HWND
/// facts are unchanged because a window can move to another monitor without changing those facts.
/// </summary>
public sealed class WindowSnapshotCoordinator(IWindowSource source)
{
    private event Action? Updated;
    public IWindowSource Source { get; } = source;
    public int SubscriberCount => Updated?.GetInvocationList().Length ?? 0;

    public IDisposable Subscribe(Action update)
    {
        Updated += update;
        return new Subscription(() => Updated -= update);
    }

    public bool Refresh(bool force = false, bool monitorSensitive = false)
    {
        bool changed = Source.Refresh();
        if (changed || force || monitorSensitive) Updated?.Invoke();
        return changed;
    }

    private sealed class Subscription(Action remove) : IDisposable
    {
        private Action? _remove = remove;
        public void Dispose() => Interlocked.Exchange(ref _remove, null)?.Invoke();
    }
}
