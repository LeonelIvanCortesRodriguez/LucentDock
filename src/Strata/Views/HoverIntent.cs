namespace Strata.Views;

/// <summary>
/// When to show an app's window previews, as the taskbar does: after the pointer rests on an icon for
/// <see cref="ShowDelay"/>; at once when moving to another icon while previews are up; kept while the pointer is
/// over the previews; hidden <see cref="HideDelay"/> after it leaves both. A click hides them until the pointer
/// leaves that icon. Pure, so it can be tested; the dock drives it with a short timer while the pointer is near.
/// </summary>
public sealed class HoverIntent<T> where T : class
{
    public TimeSpan ShowDelay { get; init; } = TimeSpan.FromMilliseconds(450);
    public TimeSpan HideDelay { get; init; } = TimeSpan.FromMilliseconds(300);

    private T? _over, _suppressed;
    private DateTime _overSince;
    private bool _overPreview;
    private DateTime? _leftAt;

    /// <summary>The item whose previews are showing, or null.</summary>
    public T? Shown { get; private set; }

    /// <summary>True while something may still change without new pointer events (a timer should keep ticking).</summary>
    public bool Pending => _over is not null && Shown != _over && _suppressed != _over || Shown is not null && _leftAt is not null;

    public void Enter(T item, DateTime now)
    {
        _over = item;
        _overSince = now;
        _leftAt = null;
    }

    public void Leave(T item, DateTime now)
    {
        if (!ReferenceEquals(_over, item)) return;
        _over = null;
        if (ReferenceEquals(_suppressed, item)) _suppressed = null;
        if (!_overPreview) _leftAt = now;
    }

    public void EnterPreview() { _overPreview = true; _leftAt = null; }

    public void LeavePreview(DateTime now) { _overPreview = false; if (_over is null) _leftAt = now; }

    /// <summary>A click (or a menu) on the item: hide its previews until the pointer leaves it.</summary>
    public void Dismiss()
    {
        _suppressed = _over;
        Shown = null;
        _overPreview = false;
        _leftAt = null;
    }

    /// <summary>What should be showing now.</summary>
    public T? Update(DateTime now)
    {
        if (_over is not null && !ReferenceEquals(_over, _suppressed))
        {
            if (Shown is not null || now - _overSince >= ShowDelay) Shown = _over;
            return Shown;
        }
        if (_overPreview) return Shown;
        if (Shown is not null && (_leftAt is null || now - _leftAt.Value >= HideDelay)) { Shown = null; _leftAt = null; }
        return Shown;
    }
}
