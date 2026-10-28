using Strata.Native;
using Strata.Smart;

namespace Strata.Core;

/// <summary>
/// The global shortcut that makes the dock keyboard-focusable. Windows 11 owns Win+Alt+D (show/hide date and
/// time), so the shortcut is Win+Alt+S. A failed registration is never silent: it is logged and an urgent
/// notification says the shortcut is unavailable and how else to reach Strata.
/// </summary>
public sealed class DockHotkey(Func<uint, uint, bool> register, NotificationCenter? notifications)
{
    public const string Gesture = "Win+Alt+S";
    public const uint Modifiers = Win32.MOD_WIN | Win32.MOD_ALT | Win32.MOD_NOREPEAT;
    public const uint VirtualKey = 0x53; // 'S'
    public const string WarningKey = "dock-hotkey-unavailable";
    public const string WarningTitle = "Strata keyboard shortcut unavailable";
    public const string WarningBody = Gesture + " is already used by Windows or another app, so it can't focus the dock. " +
        "To reach Strata from the keyboard, open it again from Start (Win, type Strata, Enter): that opens its settings.";

    /// <summary>Null until <see cref="Register"/> runs.</summary>
    public bool? Registered { get; private set; }

    public bool Register()
    {
        bool ok;
        try { ok = register(Modifiers, VirtualKey); }
        catch (Exception ex) { Log.Write($"Registering {Gesture} threw: {ex.Message}"); ok = false; }
        Registered = ok;
        if (!ok)
        {
            Log.Write($"Could not register {Gesture} dock keyboard shortcut.");
            notifications?.Send(WarningKey, WarningTitle, WarningBody, "dock", TimeSpan.FromHours(12), urgent: true);
        }
        return ok;
    }
}
