namespace Strata.Core;

public readonly record struct DockOverflowCandidate(string Key, double Width, int Priority);

public static class DockOverflowPlanner
{
    /// <summary>Lowest-priority content moves to More first; wider peers go first to restore useful space quickly.</summary>
    public static IReadOnlyList<string> Select(double pixelsToFree, IEnumerable<DockOverflowCandidate> candidates)
    {
        if (pixelsToFree <= 0) return [];
        double freed = 0;
        var selected = new List<string>();
        foreach (var item in candidates.OrderBy(c => c.Priority).ThenByDescending(c => c.Width).ThenBy(c => c.Key, StringComparer.Ordinal))
        {
            selected.Add(item.Key);
            freed += Math.Max(0, item.Width);
            if (freed >= pixelsToFree) break;
        }
        return selected;
    }
}
