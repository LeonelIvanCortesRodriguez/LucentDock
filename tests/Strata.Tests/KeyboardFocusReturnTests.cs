using System.Runtime.ExceptionServices;
using System.Windows.Input;
using Strata.Views;

namespace Strata.Tests;

/// <summary>
/// Guards the focus fix found in the Phase B keyboard pass: closing a flyout opened from dock keyboard mode
/// left keyboard focus nowhere, so Shift+F10 and the rest of the dock were unreachable without Win+Alt+S again.
/// </summary>
[Trait("Category", "Unit")]
public class KeyboardFocusReturnTests
{
    private static readonly string[] Dock = ["Start", "Search", "Now playing", "Calendar"];

    [Fact]
    public void Escape_returns_focus_to_the_item_that_opened_it()
    {
        Assert.Equal("Calendar", KeyboardFocusReturn.Target(true, FlyoutCloseReason.Escape, false, "Calendar", Dock));
        // Escape is a dismissal inside Strata: it returns even if Windows has already picked another window.
        Assert.Equal("Calendar", KeyboardFocusReturn.Target(true, FlyoutCloseReason.Escape, true, "Calendar", Dock));
    }

    [Fact]
    public void Menu_action_returns_unless_it_moved_the_foreground_elsewhere()
    {
        Assert.Equal("Search", KeyboardFocusReturn.Target(true, FlyoutCloseReason.Programmatic, false, "Search", Dock));
        Assert.Null(KeyboardFocusReturn.Target(true, FlyoutCloseReason.Programmatic, true, "Search", Dock));
    }

    [Fact]
    public void Losing_activation_never_pulls_focus_back()
    {
        Assert.Null(KeyboardFocusReturn.Target(true, FlyoutCloseReason.LostFocus, false, "Calendar", Dock));
        Assert.Null(KeyboardFocusReturn.Target(true, FlyoutCloseReason.LostFocus, true, "Calendar", Dock));
    }

    [Theory]
    [InlineData(FlyoutCloseReason.Escape)]
    [InlineData(FlyoutCloseReason.Programmatic)]
    public void Flyouts_opened_by_mouse_leave_focus_alone(FlyoutCloseReason reason) =>
        Assert.Null(KeyboardFocusReturn.Target(false, reason, false, "Calendar", Dock));

    [Fact]
    public void Removed_opener_falls_back_to_the_first_dock_item()
    {
        // e.g. "Unpin" rebuilt the dock, so the button that opened the menu no longer exists.
        Assert.Equal("Start", KeyboardFocusReturn.Target(true, FlyoutCloseReason.Programmatic, false, "Paint", Dock));
        Assert.Equal("Start", KeyboardFocusReturn.Target<string>(true, FlyoutCloseReason.Escape, false, null, Dock));
        Assert.Null(KeyboardFocusReturn.Target(true, FlyoutCloseReason.Escape, false, "Paint", Array.Empty<string>()));
    }

    [Fact]
    public void Flyout_reports_the_first_close_reason_once() => OnSta(() =>
    {
        // Escape starts the close; the deactivation that follows while it fades must not relabel it as LostFocus.
        var flyout = new FlyoutWindow(new System.Windows.Controls.Button(), new System.Windows.Controls.TextBlock(), 200, below: false);
        var started = new List<FlyoutCloseReason>();
        flyout.CloseStarted += started.Add;
        flyout.CloseAnimated(FlyoutCloseReason.Escape);
        flyout.CloseAnimated(FlyoutCloseReason.LostFocus);
        Assert.Equal([FlyoutCloseReason.Escape], started);
        Assert.Equal(FlyoutCloseReason.Escape, flyout.CloseReason);
        Assert.True(flyout.IsClosing);
    });

    [Theory]
    [InlineData(Key.System, Key.F10, ModifierKeys.Shift, true)]  // how WPF actually delivers Shift+F10
    [InlineData(Key.F10, Key.None, ModifierKeys.Shift, true)]
    [InlineData(Key.Apps, Key.None, ModifierKeys.None, true)]    // the Menu key
    [InlineData(Key.System, Key.F10, ModifierKeys.None, false)]  // plain F10 stays Windows' menu-bar key
    [InlineData(Key.F10, Key.None, ModifierKeys.None, false)]
    [InlineData(Key.System, Key.F4, ModifierKeys.Shift, false)]
    public void Context_menu_gesture_is_recognized_as_WPF_delivers_it(Key key, Key systemKey, ModifierKeys modifiers, bool expected) =>
        Assert.Equal(expected, DockKeyboard.IsContextMenu(key, systemKey, modifiers));

    private static void OnSta(Action action)
    {
        ExceptionDispatchInfo? error = null;
        var t = new Thread(() => { try { action(); } catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(10)), "STA keyboard test timed out.");
        error?.Throw();
    }
}
