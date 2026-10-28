namespace Strata.Core;

/// <summary>
/// App groups on the dock (one app dropped onto another, LumaBar-style folders), as pure list transforms so the
/// semantics are pinned by tests. Groups are flat: merging a group into anything pours its members out, so a
/// group can never contain a group.
/// </summary>
public static class DockGroups
{
    public static bool IsGroup(PinnedApp? pin) => pin?.Group is { Count: > 0 };

    /// <summary>
    /// The drop target when a dragged button is released at <paramref name="x"/>: the index (within
    /// <paramref name="centres"/>, which exclude the dragged button) whose central half contains the point, or
    /// null when the point lies between buttons — which stays a reorder. The central-half rule is what separates
    /// "onto" from "next to" with a finger's worth of tolerance.
    /// </summary>
    public static int? GroupTarget(IReadOnlyList<double> centres, IReadOnlyList<double> widths, double x)
    {
        for (int i = 0; i < centres.Count && i < widths.Count; i++)
            if (Math.Abs(x - centres[i]) <= widths[i] * 0.25) return i;
        return null;
    }

    /// <summary>
    /// The full drop decision. Grouping arms only while the drag has never left its own slot: the moment
    /// InsertIndex moves the slot, the neighbours animate aside and the static centres stop describing the
    /// screen, so a slot-crossing drag is always the reorder the user is watching — a release over a neighbour's
    /// old position must never silently make a group (audit F1).
    /// </summary>
    public static int? DropOnto(IReadOnlyList<double> centres, IReadOnlyList<double> widths, double x, int fromSlot, int currentSlot)
        => currentSlot == fromSlot ? GroupTarget(centres, widths, x) : null;

    /// <summary>
    /// Drop <paramref name="dragged"/> onto <paramref name="target"/>: an ordinary target becomes a group of the
    /// two at its own slot; a group target absorbs the dragged pin (or the dragged group's members). True when
    /// the list changed.
    /// </summary>
    public static bool MergeInto(List<PinnedApp> pins, PinnedApp dragged, PinnedApp target, string groupName)
    {
        if (ReferenceEquals(dragged, target)) return false;
        int targetIndex = pins.IndexOf(target);
        if (targetIndex < 0) return false;
        var arriving = IsGroup(dragged) ? dragged.Group! : [dragged];
        pins.Remove(dragged); // a running, unpinned drag is simply not in the list; that is fine
        if (IsGroup(target))
        {
            target.Group!.AddRange(arriving);
            return true;
        }
        int slot = pins.IndexOf(target);
        pins[slot] = new PinnedApp { Name = groupName, Group = [target, .. arriving] };
        return true;
    }

    /// <summary>A new group's name: its two founders, readably short — two groups must never be
    /// indistinguishable in tooltips, the screen reader, the palette or Settings (audit F6).</summary>
    public static string NameFor(PinnedApp target, PinnedApp dragged)
    {
        static string N(PinnedApp p) => p.Name is { Length: > 0 } n ? (n.Length > 14 ? n[..13] + "…" : n) : "App";
        return IsGroup(target) ? target.Name : $"{N(target)} & {N(dragged)}";
    }

    /// <summary>
    /// Repair a loaded or imported pin list: drop null members, pour nested groups flat, and turn an empty
    /// group back into nothing. Imported files are data, not invariants (audit F10).
    /// </summary>
    public static void Sanitize(List<PinnedApp> pins)
    {
        for (int i = pins.Count - 1; i >= 0; i--)
        {
            var pin = pins[i];
            if (pin is null) { pins.RemoveAt(i); continue; }
            if (pin.Group is null) continue;
            var flat = new List<PinnedApp>();
            foreach (var member in pin.Group)
            {
                if (member is null) continue;
                if (member.Group is { Count: > 0 } nested) flat.AddRange(nested.Where(m => m is not null));
                else if (member.Group is null) flat.Add(member);
            }
            pin.Group.Clear();
            pin.Group.AddRange(flat);
            if (pin.Group.Count == 0) pins.RemoveAt(i);
        }
    }

    /// <summary>Dissolve the group in place: its members take its slot, in their order.</summary>
    public static bool Ungroup(List<PinnedApp> pins, PinnedApp group)
    {
        if (!IsGroup(group)) return false;
        int index = pins.IndexOf(group);
        if (index < 0) return false;
        pins.RemoveAt(index);
        pins.InsertRange(index, group.Group!);
        return true;
    }
}
