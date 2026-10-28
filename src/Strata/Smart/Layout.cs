using Strata.Core;

namespace Strata.Smart;

/// <summary>
/// A temporary change to the dock layout requested by an automation. Layers never edit the user's saved
/// layout: the effective layout is recomputed from the saved layout plus the active layers, so removing a
/// layer restores the previous layout exactly.
/// </summary>
public sealed record LayoutLayer(
    string Owner,
    int Priority,
    IReadOnlyList<string> Promote,
    IReadOnlyList<string> Collapse,
    IReadOnlyList<string> Emphasize,
    DateTime? ExpiresAt = null)
{
    public bool IsActive(DateTime now) => ExpiresAt is null || now < ExpiresAt;
}

public sealed record EffectiveLayout(
    IReadOnlyList<string> Left,
    IReadOnlyList<string> Right,
    IReadOnlySet<string> Emphasized,
    IReadOnlyList<string> AppliedBy)
{
    public bool SameAs(EffectiveLayout other) =>
        Left.SequenceEqual(other.Left) && Right.SequenceEqual(other.Right) &&
        Emphasized.SetEquals(other.Emphasized);
}

public static class LayoutOverlay
{
    /// <summary>
    /// Apply active layers in ascending priority, so a higher-priority layer wins any conflict
    /// (e.g. a promote from "low battery" beats a collapse from "focus mode").
    /// Promoted widgets move to the front of the right-hand group; collapsed widgets are hidden.
    /// </summary>
    public static EffectiveLayout Compose(IReadOnlyList<string> baseLeft, IReadOnlyList<string> baseRight,
        IEnumerable<LayoutLayer> layers, DateTime now)
    {
        var left = baseLeft.ToList();
        var right = baseRight.ToList();
        var emphasized = new HashSet<string>();
        var applied = new List<string>();
        var active = layers.Where(l => l.IsActive(now)).Select((l, i) => (l, i))
            .OrderBy(x => x.l.Priority).ThenBy(x => x.i).Select(x => x.l).ToList();

        foreach (var layer in active)
        {
            applied.Add(layer.Owner);
            foreach (var id in layer.Collapse)
            {
                left.Remove(id);
                right.Remove(id);
                emphasized.Remove(id);
            }
            // Insert promoted widgets in the order given, at the front of the right group.
            for (int i = layer.Promote.Count - 1; i >= 0; i--)
            {
                var id = layer.Promote[i];
                left.Remove(id);
                right.Remove(id);
                right.Insert(0, id);
            }
            foreach (var id in layer.Emphasize) emphasized.Add(id);
        }
        emphasized.IntersectWith(left.Concat(right));
        return new EffectiveLayout(left, right, emphasized, applied);
    }
}

/// <summary>The set of active layers, with handles that remove exactly the layer they added.</summary>
public sealed class LayoutStack
{
    private readonly List<LayoutLayer> _layers = [];
    public event Action? Changed;
    public IReadOnlyList<LayoutLayer> Layers => _layers;

    public IDisposable Push(LayoutLayer layer)
    {
        _layers.Add(layer);
        Changed?.Invoke();
        return new Handle(() =>
        {
            if (_layers.Remove(layer)) Changed?.Invoke();
        });
    }

    /// <summary>Drop expired layers; returns true if anything changed.</summary>
    public bool PruneExpired(DateTime now)
    {
        int removed = _layers.RemoveAll(l => !l.IsActive(now));
        if (removed > 0) Changed?.Invoke();
        return removed > 0;
    }

    public DateTime? NextExpiry => _layers.Where(l => l.ExpiresAt is not null).Select(l => l.ExpiresAt).Min();
}

/// <summary>
/// Decides the dock style: the user's chosen style, unless an automation (sunset) asks for another one.
/// If the user picks a style while an automation's style is showing, that is a manual override: the
/// automation steps back until it ends, then applies again next time (e.g. tomorrow evening).
/// </summary>
public sealed class ThemeController
{
    private (string Owner, DockStyle Style)? _request;
    public event Action? Changed;

    public DockStyle UserStyle { get; private set; }
    public string? OverriddenOwner { get; set; }

    public ThemeController(DockStyle userStyle) => UserStyle = userStyle;

    public DockStyle Effective => _request is { } r && OverriddenOwner != r.Owner ? r.Style : UserStyle;
    public string? ActiveOwner => _request is { } r && OverriddenOwner != r.Owner ? r.Owner : null;

    public IDisposable Request(string owner, DockStyle style)
    {
        var before = Effective;
        _request = (owner, style);
        if (Effective != before) Changed?.Invoke();
        return new Handle(() =>
        {
            if (_request?.Owner != owner) return;
            var prev = Effective;
            _request = null;
            if (OverriddenOwner == owner) OverriddenOwner = null; // the override lasts until the automation ends
            if (Effective != prev) Changed?.Invoke();
        });
    }

    /// <summary>The user chose a style (settings). Counts as an override if an automation style is showing.</summary>
    public void UserChose(DockStyle style)
    {
        var before = Effective;
        var beforeOwner = OverriddenOwner;
        var beforeUser = UserStyle;
        if (_request is { } r && OverriddenOwner != r.Owner) OverriddenOwner = r.Owner;
        UserStyle = style;
        if (Effective != before || beforeOwner != OverriddenOwner || beforeUser != UserStyle) Changed?.Invoke();
    }

    /// <summary>Settings were reloaded without the user touching the style (keeps any override).</summary>
    public void SyncUserStyle(DockStyle style)
    {
        var before = Effective;
        UserStyle = style;
        if (Effective != before) Changed?.Invoke();
    }
}

internal sealed class Handle(Action dispose) : IDisposable
{
    private Action? _dispose = dispose;
    public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
}
