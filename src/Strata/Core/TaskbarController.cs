using System.Runtime.InteropServices;
using Strata.Native;

namespace Strata.Core;

/// <summary>Native taskbar operations, behind an interface so failure paths can be tested.</summary>
public interface ITaskbarNative
{
    IntPtr FindPrimary();
    IEnumerable<IntPtr> FindAll();
    int GetState(IntPtr tray);
    void SetState(IntPtr tray, int state);
    bool IsVisible(IntPtr window);
    void Show(IntPtr window);
    void Hide(IntPtr window);
    int LastError { get; }
}

public sealed class Win32TaskbarNative : ITaskbarNative
{
    public int LastError => Marshal.GetLastPInvokeError();

    public IntPtr FindPrimary() => Win32.FindWindow("Shell_TrayWnd", null);

    public IEnumerable<IntPtr> FindAll()
    {
        IntPtr primary = FindPrimary();
        if (primary != IntPtr.Zero) yield return primary;
        IntPtr after = IntPtr.Zero;
        while ((after = Win32.FindWindowEx(IntPtr.Zero, after, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
            yield return after;
    }

    public int GetState(IntPtr tray)
    {
        var abd = new Win32.APPBARDATA { cbSize = Marshal.SizeOf<Win32.APPBARDATA>(), hWnd = tray };
        return (int)Win32.SHAppBarMessage(Win32.ABM_GETSTATE, ref abd);
    }

    public void SetState(IntPtr tray, int state)
    {
        var abd = new Win32.APPBARDATA { cbSize = Marshal.SizeOf<Win32.APPBARDATA>(), hWnd = tray, lParam = new IntPtr(state) };
        Win32.SHAppBarMessage(Win32.ABM_SETSTATE, ref abd);
    }

    public bool IsVisible(IntPtr window) => Win32.IsWindowVisible(window);
    public void Show(IntPtr window) => Win32.ShowWindow(window, Win32.SW_SHOWNA);
    public void Hide(IntPtr window) => Win32.ShowWindow(window, Win32.SW_HIDE);
}

public enum TaskbarApplyResult { Applied, Left, NotFound, VerificationFailed, SuspendedForSafety }

/// <summary>
/// Why the configured hide/auto-hide is currently withheld. Blockers compose: each origin sets and clears only
/// its own flag, and the configured mode is applied again only when none remain.
/// </summary>
[Flags]
public enum TaskbarSafetyBlockers
{
    None = 0,
    /// <summary>The dock was not visible for the guard's grace period (set and cleared by the dock-loss lifecycle).</summary>
    DockUnavailable = 1,
    /// <summary>AppBar registration or space reservation failed (cleared only by a verified register/reserve).</summary>
    AppBarUnavailable = 2,
    /// <summary>Startup did not reach stable native dock geometry before its bounded timeout.</summary>
    StartupNotReady = 4,
    /// <summary>Explorer recreated the taskbar; hiding waits for the replacement AppBar and dock geometry.</summary>
    ExplorerReinitializing = 8,
}

/// <summary>
/// Hides or auto-hides the Windows taskbar while Strata runs, and always puts it back.
/// The original state is written to disk *before* anything changes, so even after a crash the next start
/// (or `Strata.exe --restore-taskbar`) restores it. Every change is verified by reading the state back;
/// if Windows doesn't accept it, the taskbar is restored and the failure is logged, never left half-done.
/// </summary>
public sealed class TaskbarController(ITaskbarNative native)
{
    private sealed class SavedState
    {
        public bool HasState { get; set; }
        public int OriginalState { get; set; }
    }

    private const string StateFile = "taskbar-state.json";
    private TaskbarMode _configuredMode = TaskbarMode.Leave;
    private bool _applied;
    private bool _restoreUnconfirmed;
    private TaskbarSafetyBlockers _blockers;
    private int _hideFailures;

    public TaskbarController() : this(new Win32TaskbarNative()) { }

    public TaskbarMode ConfiguredMode => _configuredMode;
    public TaskbarSafetyBlockers Blockers => _blockers;
    public bool SafetySuspended => _blockers != TaskbarSafetyBlockers.None;
    /// <summary>A verified restore failed; the safety lifecycle keeps retrying and nothing re-hides meanwhile.</summary>
    public bool RestoreUnconfirmed => _restoreUnconfirmed;
    public bool IsHidingOrAutoHiding => _restoreUnconfirmed || (_applied && _configuredMode != TaskbarMode.Leave);
    public string? LastFailure { get; private set; }

    /// <summary>
    /// Automatic application of the saved preference (startup, settings reload). Records the preference but
    /// never ends a safety suspension: while any blocker is active the taskbar stays (or becomes) visible.
    /// </summary>
    public TaskbarApplyResult ApplyConfigured(TaskbarMode mode)
    {
        bool sameMode = mode == _configuredMode;
        _configuredMode = mode;
        if (SafetySuspended)
        {
            if (_applied || _restoreUnconfirmed) RestoreCore();
            return TaskbarApplyResult.SuspendedForSafety;
        }
        return ApplyChange(sameMode);
    }

    /// <summary>
    /// An explicit user action (the "Hide/Show Windows taskbar" command). Showing always ends a suspension;
    /// hiding records the preference but cannot override a known readiness/AppBar/dock safety failure.
    /// </summary>
    public TaskbarApplyResult ApplyUserRequested(TaskbarMode mode)
    {
        bool sameMode = mode == _configuredMode;
        _configuredMode = mode;
        if (mode == TaskbarMode.Leave)
        {
            if (_blockers != TaskbarSafetyBlockers.None) Log.Write($"User restore ends taskbar safety suspension ({_blockers}).");
            _blockers = TaskbarSafetyBlockers.None;
        }
        else if (SafetySuspended)
        {
            RestoreCore();
            return TaskbarApplyResult.SuspendedForSafety;
        }
        return ApplyChange(sameMode);
    }

    private TaskbarApplyResult ApplyChange(bool sameMode)
    {
        if (_restoreUnconfirmed && !RestoreCore()) return TaskbarApplyResult.VerificationFailed;
        if (_applied && sameMode) { Enforce(); return TaskbarApplyResult.Applied; }
        if (_applied && !RestoreCore()) return TaskbarApplyResult.VerificationFailed;
        return ApplyEffectiveMode();
    }

    private TaskbarApplyResult ApplyEffectiveMode()
    {
        if (SafetySuspended) return TaskbarApplyResult.SuspendedForSafety;
        if (_restoreUnconfirmed) return TaskbarApplyResult.VerificationFailed;
        if (_applied) { Enforce(); return TaskbarApplyResult.Applied; }

        if (_configuredMode == TaskbarMode.Leave) return TaskbarApplyResult.Left;

        IntPtr tray = native.FindPrimary();
        if (tray == IntPtr.Zero)
        {
            Log.Write("Taskbar not found yet (Explorer starting?); will retry.");
            return TaskbarApplyResult.NotFound;
        }

        // Only record the original state if one is not already saved (a previous crash left it there).
        var saved = Storage.Load<SavedState>(StateFile);
        if (!saved.HasState)
        {
            saved = new SavedState { HasState = true, OriginalState = native.GetState(tray) };
            if (!Storage.TrySave(StateFile, saved))
            {
                LastFailure = "Could not durably save the original Windows taskbar state; the taskbar was not changed.";
                Log.Write(LastFailure);
                foreach (var window in native.FindAll()) native.Show(window);
                _applied = false;
                return TaskbarApplyResult.VerificationFailed;
            }
        }

        // Auto-hide releases the screen space the taskbar reserves; hiding the window removes it entirely.
        int wanted = Win32.ABS_AUTOHIDE | (saved.OriginalState & Win32.ABS_ALWAYSONTOP);
        native.SetState(tray, wanted);
        int actual = native.GetState(tray);
        if ((actual & Win32.ABS_AUTOHIDE) == 0)
        {
            LastFailure = $"Windows did not accept taskbar auto-hide (asked {wanted}, got {actual}, error {native.LastError}).";
            Log.Write(LastFailure + " Restoring the taskbar.");
            _restoreUnconfirmed = !RestoreWith(native);
            _applied = false;
            return TaskbarApplyResult.VerificationFailed;
        }
        _applied = true;
        _restoreUnconfirmed = false;
        Enforce();
        return TaskbarApplyResult.Applied;
    }

    /// <summary>Called on a timer: Explorer sometimes re-shows the taskbar, or restarts.</summary>
    public void Enforce()
    {
        // Each timer call makes one bounded native retry until visibility and the saved state are both
        // confirmed. This must not depend on the dock being hidden: AppBar and explicit restores can fail
        // while the dock itself remains perfectly visible.
        if (_restoreUnconfirmed) { RestoreCore(); return; }
        if (SafetySuspended) return;
        if (_configuredMode == TaskbarMode.Leave) return;
        if (!_applied) { ApplyEffectiveMode(); return; }
        if (_configuredMode != TaskbarMode.Hide) return;
        // Only the primary taskbar is hidden: the dock lives on the primary monitor, so taskbars on
        // other monitors stay available in auto-hide (they slide in when the pointer reaches the edge).
        IntPtr primary = native.FindPrimary();
        if (primary == IntPtr.Zero || !native.IsVisible(primary)) return;
        native.Hide(primary);
        if (native.IsVisible(primary))
        {
            // Harmless (the taskbar simply stays visible) but worth knowing; log once per streak.
            if (_hideFailures++ == 0) Log.Write($"Could not hide the taskbar window (error {native.LastError}); it stays visible.");
        }
        else _hideFailures = 0;
    }

    /// <summary>Explorer restarted: the taskbar is a new window, so start over.</summary>
    public void OnExplorerRestarted()
    {
        _applied = false;
        if (SafetySuspended)
        {
            // Explorer created a new taskbar window. Keep it accessible and keep every blocker;
            // only the blocker's own origin (or the user) can end the suspension.
            RestoreCore();
            return;
        }
        if (_restoreUnconfirmed) return;
        ApplyEffectiveMode();
    }

    public bool Restore() => RestoreCore();

    /// <summary>Give the taskbar back because of <paramref name="reason"/>; it stays suspended until that origin clears.</summary>
    public bool RestoreForSafety(TaskbarSafetyBlockers reason)
    {
        if (reason == TaskbarSafetyBlockers.None) throw new ArgumentException("A safety restore needs a reason.", nameof(reason));
        if ((_blockers & reason) != reason) Log.Write($"Taskbar safety suspension: {reason}.");
        _blockers |= reason;
        return RestoreCore();
    }

    /// <summary>
    /// Clear one blocker after its origin confirmed recovery. The configured mode is reapplied only when no other
    /// blocker remains. Returns true if the blocker was set (i.e. this call changed something).
    /// </summary>
    public bool ClearSafetyBlocker(TaskbarSafetyBlockers reason)
    {
        if ((_blockers & reason) == TaskbarSafetyBlockers.None) return false;
        _blockers &= ~reason;
        Log.Write($"Taskbar safety blocker cleared: {reason}; remaining: {_blockers}.");
        if (_blockers == TaskbarSafetyBlockers.None)
        {
            // Never erase an unverified native state. Enforce keeps making one bounded restore attempt per
            // heartbeat and may reapply the configured mode only on a later heartbeat after restoration verifies.
            if (_restoreUnconfirmed)
            {
                RestoreCore();
                return true;
            }
            ApplyEffectiveMode();
        }
        return true;
    }

    /// <summary>The dock is confirmed visible again: clears only the dock-loss blocker.</summary>
    public bool ResumeAfterDockRecovery() => ClearSafetyBlocker(TaskbarSafetyBlockers.DockUnavailable);

    /// <summary>AppBar registration and reservation were verified: clears only the AppBar blocker.</summary>
    public bool ResumeAfterAppBarRecovery() => ClearSafetyBlocker(TaskbarSafetyBlockers.AppBarUnavailable);

    private bool RestoreCore()
    {
        bool restored = RestoreWith(native);
        _applied = false;
        // A failed verified restore is still unsafe state. Keep reporting it so the safety
        // lifecycle can retry, while Enforce remains suspended and cannot hide the taskbar again.
        _restoreUnconfirmed = !restored;
        return restored;
    }

    /// <summary>Show every taskbar and put back the saved auto-hide state. Safe to call any number of times.</summary>
    public static bool RestoreFromDisk() => RestoreWith(new Win32TaskbarNative());

    private static bool RestoreWith(ITaskbarNative native)
    {
        bool ok = true;
        foreach (var h in native.FindAll()) { native.Show(h); ok &= native.IsVisible(h); }
        var saved = Storage.Load<SavedState>(StateFile);
        IntPtr tray = native.FindPrimary();
        if (saved.HasState && tray != IntPtr.Zero)
        {
            native.SetState(tray, saved.OriginalState);
            int actual = native.GetState(tray);
            if (actual != saved.OriginalState)
            {
                ok = false;
                Log.Write($"Taskbar state restore not confirmed (wanted {saved.OriginalState}, got {actual}).");
            }
            else Storage.Save(StateFile, new SavedState());
        }
        else if (saved.HasState) ok = false;
        return ok;
    }
}

/// <summary>
/// Safety net: if the taskbar is hidden but the dock has not been visible for a while (and isn't hidden on
/// purpose for a fullscreen app), give the taskbar back. The user is never left with neither.
/// </summary>
public sealed class TaskbarGuard(TimeSpan grace)
{
    private DateTime? _unhealthySince;

    /// <returns>True when a restore attempt is due. A continuing unsafe episode becomes due again after another grace period.</returns>
    public bool Check(DateTime now, bool taskbarHidden, bool dockVisible, bool dockHiddenOnPurpose)
    {
        if (!taskbarHidden || dockVisible || dockHiddenOnPurpose)
        {
            _unhealthySince = null;
            return false;
        }
        _unhealthySince ??= now;
        if (now - _unhealthySince < grace) return false;
        _unhealthySince = now; // bound retries: a failed recovery gets a fresh grace period
        return true;
    }

    public void Reset()
    {
        _unhealthySince = null;
    }
}

/// <summary>
/// Connects <see cref="TaskbarGuard"/> to the live dock heartbeat. State probes are callbacks so the
/// safety behavior is deterministic in tests while production supplies native taskbar/dock visibility.
/// <c>restoreTaskbar</c> should restore with <see cref="TaskbarSafetyBlockers.DockUnavailable"/>, and
/// <c>dockConfirmedHealthy</c> should clear only that blocker. Seeing the dock visible again ends only a
/// dock-loss episode this lifecycle started itself.
/// </summary>
public sealed class TaskbarSafetyLifecycle(
    TaskbarGuard guard,
    Func<bool> taskbarHidden,
    Func<bool> dockVisible,
    Func<bool> dockHiddenOnPurpose,
    Func<bool> restoreTaskbar,
    Action? dockConfirmedHealthy = null)
{
    public long Checks { get; private set; }
    public bool? LastRestoreSucceeded { get; private set; }
    /// <summary>True from this lifecycle's own dock-loss restore until the dock is seen visible again.</summary>
    public bool DockLossEpisodeActive { get; private set; }

    public bool Heartbeat(DateTime now)
    {
        Checks++;
        bool visible = dockVisible();
        // A visible dock says nothing about other safety blockers (e.g. a failed AppBar), so it can only
        // end a dock-loss episode that this lifecycle started.
        if (visible && DockLossEpisodeActive)
        {
            DockLossEpisodeActive = false;
            dockConfirmedHealthy?.Invoke();
        }
        if (!guard.Check(now, taskbarHidden(), visible, dockHiddenOnPurpose())) return false;
        DockLossEpisodeActive = true;
        try { LastRestoreSucceeded = restoreTaskbar(); }
        catch (Exception ex)
        {
            LastRestoreSucceeded = false;
            Log.Write($"Taskbar safety restore failed: {ex.Message}");
        }
        return true;
    }

    public void Rearm()
    {
        LastRestoreSucceeded = null;
        guard.Reset();
    }
}
