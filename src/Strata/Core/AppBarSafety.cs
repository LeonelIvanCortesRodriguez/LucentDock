using Strata.Native;

namespace Strata.Core;

/// <summary>
/// Connects AppBar registration/reservation to taskbar safety. A failed register or reserve restores the Windows
/// taskbar with <see cref="TaskbarSafetyBlockers.AppBarUnavailable"/>; only a verified register *and* reserve
/// clears that blocker. A visible dock window never clears it (that is the dock-loss lifecycle's own blocker).
/// The dock window calls this; tests drive it with the real controllers and fake native boundaries.
/// </summary>
public sealed class AppBarSafety(AppBarController appBar, TaskbarController taskbar)
{
    public AppBarController AppBar { get; } = appBar;

    /// <summary>Register (if needed) and reserve. Returns true only when the reservation is verified.</summary>
    public bool Ensure(IntPtr hwnd, uint callbackMessage, bool reserveSpace, bool bottom, Win32.RECT monitor, int heightPx)
    {
        if (!reserveSpace)
        {
            AppBar.Unregister(hwnd);
            // Without a reservation there is no AppBar dependency, so a past AppBar failure no longer applies.
            taskbar.ResumeAfterAppBarRecovery();
            return true;
        }
        if (!AppBar.Registered && !AppBar.Register(hwnd, callbackMessage))
        {
            // A deferred backoff sample is not another failure. Restore once per actual native attempt/failure.
            if (AppBar.LastRegisterAttempted)
                taskbar.RestoreForSafety(TaskbarSafetyBlockers.AppBarUnavailable);
            return false;
        }
        return Reserve(hwnd, reserveSpace, bottom, monitor, heightPx);
    }

    /// <summary>Re-reserve after a position/DPI/display change.</summary>
    public bool Reserve(IntPtr hwnd, bool reserveSpace, bool bottom, Win32.RECT monitor, int heightPx)
    {
        if (!reserveSpace || !AppBar.Registered) return false;
        if (AppBar.Reserve(hwnd, bottom, monitor, heightPx) is null)
        {
            taskbar.RestoreForSafety(TaskbarSafetyBlockers.AppBarUnavailable);
            return false;
        }
        taskbar.ResumeAfterAppBarRecovery();
        return true;
    }

    /// <summary>Explorer restarted: its AppBar list is gone. Register again, then let the taskbar controller react.</summary>
    public bool OnExplorerRestarted(IntPtr hwnd, uint callbackMessage, bool reserveSpace, bool bottom, Win32.RECT monitor, int heightPx)
    {
        AppBar.ForgetRegistration();
        bool ok = Ensure(hwnd, callbackMessage, reserveSpace, bottom, monitor, heightPx);
        taskbar.OnExplorerRestarted();
        return ok;
    }

    public void RearmRegistration(AppBarReadinessTransition transition) => AppBar.RearmRegistration(transition);
}
