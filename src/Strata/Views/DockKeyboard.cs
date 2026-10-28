using System.Windows.Input;

namespace Strata.Views;

/// <summary>Key gestures for dock keyboard mode.</summary>
public static class DockKeyboard
{
    /// <summary>
    /// Shift+F10 or the Menu key. WPF reports F10 as <see cref="Key.System"/> (the real key is in SystemKey), so
    /// checking <c>e.Key == Key.F10</c> never matched and Windows entered system-menu mode instead.
    /// </summary>
    public static bool IsContextMenu(Key key, Key systemKey, ModifierKeys modifiers)
    {
        var actual = key == Key.System ? systemKey : key;
        return actual == Key.Apps || (actual == Key.F10 && modifiers.HasFlag(ModifierKeys.Shift));
    }
}
