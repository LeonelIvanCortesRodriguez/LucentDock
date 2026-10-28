namespace Strata.Core;

/// <summary>
/// Where a dragged app lands. The dock lists the pins first (in <see cref="AppSettings.Pinned"/> order), then the
/// running apps that aren't pinned. Dragging a pin moves it among the pins; dragging a running app in among (or just
/// after) the pins pins it there. Dropping a running app further right changes nothing: running apps keep the order
/// they were opened in.
/// </summary>
public static class DockOrder
{
    /// <summary>The slot a drag at <paramref name="x"/> points to: how many of the other buttons' centres lie to its left.</summary>
    public static int InsertIndex(IReadOnlyList<double> otherCentres, double x) => otherCentres.Count(c => c < x);

    /// <param name="pinCount">How many pins the dock has.</param>
    /// <param name="pinIndex">The dragged button's pin index, or -1 for a running app that isn't pinned.</param>
    /// <param name="landed">Where the button sits after the drag, counting every button on the dock.</param>
    /// <returns>The pin index it should take, or null when the drop changes nothing.</returns>
    public static int? PinTarget(int pinCount, int pinIndex, int landed)
    {
        if (landed < 0) return null;
        if (pinIndex >= 0)
        {
            int target = Math.Min(landed, pinCount - 1);
            return target == pinIndex ? null : target;
        }
        return landed <= pinCount ? landed : null;
    }

    /// <summary>Apply a drop to the pin list: move the pin, or insert the newly pinned app. True when the list changed.</summary>
    public static bool Apply(List<PinnedApp> pins, PinnedApp? pin, Func<PinnedApp>? newPin, int landed)
    {
        int index = pin is null ? -1 : pins.IndexOf(pin);
        if (pin is not null && index < 0) return false;
        if (pin is null && newPin is null) return false;
        if (PinTarget(pins.Count, index, landed) is not int target) return false;
        if (pin is not null)
        {
            pins.RemoveAt(index);
            pins.Insert(target, pin);
        }
        else pins.Insert(target, newPin!());
        return true;
    }
}
