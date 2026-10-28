using Strata.Core;
using Strata.Native;
using Strata.Smart;

namespace Strata.Tests;

[Trait("Category", "Unit")]
public class DockHotkeyTests
{
    private static (DockHotkey Hotkey, FakeNotifier Notifier, NotificationCenter Center) Create(Func<uint, uint, bool> register, bool quietNow = false)
    {
        var notifier = new FakeNotifier();
        var clock = new ManualClock(new DateTime(2026, 10, 23, quietNow ? 23 : 12, 0, 0));
        var center = new NotificationCenter(notifier, clock, new QuietHours { Enabled = true });
        return (new DockHotkey(register, center), notifier, center);
    }

    [Fact]
    public void Shortcut_avoids_combinations_Windows_11_reserves()
    {
        // Win+Alt+D (date/time), K (mic mute), and the Game Bar's B, G, R, T are taken by Windows 11.
        Assert.Equal(Win32.MOD_WIN | Win32.MOD_ALT | Win32.MOD_NOREPEAT, DockHotkey.Modifiers);
        Assert.DoesNotContain(DockHotkey.VirtualKey, "DKBGRT".Select(c => (uint)c));
        Assert.Equal((uint)'S', DockHotkey.VirtualKey);
        Assert.Equal("Win+Alt+S", DockHotkey.Gesture);
    }

    [Fact]
    public void Successful_registration_shows_no_warning()
    {
        uint mods = 0, vk = 0;
        var (hotkey, notifier, _) = Create((m, k) => { mods = m; vk = k; return true; });
        Assert.True(hotkey.Register());
        Assert.True(hotkey.Registered);
        Assert.Equal(DockHotkey.Modifiers, mods);
        Assert.Equal(DockHotkey.VirtualKey, vk);
        Assert.Empty(notifier.Shown);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)] // quiet hours never hide this warning
    public void Failed_registration_shows_a_visible_warning(bool quietNow)
    {
        var (hotkey, notifier, center) = Create((_, _) => false, quietNow);
        Assert.False(hotkey.Register());
        Assert.False(hotkey.Registered);
        var shown = Assert.Single(notifier.Shown);
        Assert.Equal(DockHotkey.WarningTitle, shown.Title);
        Assert.Contains(DockHotkey.Gesture, shown.Body);
        Assert.Equal(NotifyOutcome.Sent, Assert.Single(center.Recent).Outcome);
    }

    [Fact]
    public void Registration_that_throws_is_treated_as_failed_and_warned()
    {
        var (hotkey, notifier, _) = Create((_, _) => throw new InvalidOperationException("no window"));
        Assert.False(hotkey.Register());
        Assert.Single(notifier.Shown);
    }

    [Fact]
    public void Warning_sent_before_the_engine_starts_keeps_its_dedup_key_and_is_persisted()
    {
        // The dock registers its shortcut while it initializes, before RuleEngine.Start loads saved state.
        var h = new SmartHarness();
        new DockHotkey((_, _) => false, h.Notifications).Register();
        h.Start();
        Assert.True(h.Notifications.SentKeys.ContainsKey(DockHotkey.WarningKey));
        Assert.True(h.Persistence.State!.NotificationKeys.ContainsKey(DockHotkey.WarningKey));
        Assert.Single(h.Notifier.Shown);
    }

    internal static (DockHotkey Hotkey, FakeNotifier Notifier) CreateForIntegration(Func<uint, uint, bool> register)
    {
        var (hotkey, notifier, _) = Create(register);
        return (hotkey, notifier);
    }
}

/// <summary>The real RegisterHotKey path: the dock warns when its shortcut is already taken.</summary>
[Trait("Category", "Integration")]
public class DockHotkeyIntegrationTests
{
    [Fact]
    public void Real_registration_of_an_occupied_shortcut_fails_and_warns()
    {
        // Occupy the shortcut first (if something else already owns it, that is the same situation).
        const int occupierId = 0x7A01, dockId = 0x7A02;
        bool occupied = Win32.RegisterHotKey(IntPtr.Zero, occupierId, DockHotkey.Modifiers, DockHotkey.VirtualKey);
        try
        {
            var (hotkey, notifier) = DockHotkeyTests.CreateForIntegration((m, k) => Win32.RegisterHotKey(IntPtr.Zero, dockId, m, k));
            bool registered = hotkey.Register();
            if (registered) Win32.UnregisterHotKey(IntPtr.Zero, dockId);
            Assert.False(registered);
            Assert.Equal(DockHotkey.WarningTitle, Assert.Single(notifier.Shown).Title);
        }
        finally
        {
            if (occupied) Win32.UnregisterHotKey(IntPtr.Zero, occupierId);
        }
    }
}
