using Strata.Core;
using Strata.Native;

namespace Strata.Tests;

/// <summary>
/// Integration tests for origin-aware taskbar safety: the real TaskbarController, AppBarController,
/// AppBarSafety and TaskbarSafetyLifecycle wired as DockWindow wires them, with fakes only at the native boundary.
/// </summary>
[Trait("Category", "Integration")]
public class AppBarSafetyTests : IDisposable
{
    private sealed class FakeTaskbar : ITaskbarNative
    {
        public int State;
        public bool Visible = true;
        public bool IgnoreShow, IgnoreSetState;
        public int SetCalls, HideCalls;
        public IntPtr FindPrimary() => new(42);
        public IEnumerable<IntPtr> FindAll() { yield return new IntPtr(42); }
        public int GetState(IntPtr tray) => State;
        public void SetState(IntPtr tray, int state) { SetCalls++; if (!IgnoreSetState) State = state; }
        public bool IsVisible(IntPtr w) => Visible;
        public void Show(IntPtr w) { if (!IgnoreShow) Visible = true; }
        public void Hide(IntPtr w) { HideCalls++; Visible = false; }
        public int LastError => 0;
    }

    private sealed class FakeAppBar : IAppBarNative
    {
        public bool NewWorks = true, ReserveWorks = true;
        public int NewCalls;
        public int LastError => 1400;
        public bool New(IntPtr hwnd, uint msg) { NewCalls++; return NewWorks; }
        public bool Remove(IntPtr hwnd) => true;
        public bool QueryPos(IntPtr hwnd, uint edge, ref Win32.RECT r) => ReserveWorks;
        public bool SetPos(IntPtr hwnd, uint edge, ref Win32.RECT r) => ReserveWorks;
    }

    private static readonly IntPtr Dock = new(7);
    private static readonly Win32.RECT Monitor = new() { Left = 0, Top = 0, Right = 3840, Bottom = 2160 };
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0);

    private readonly FakeTaskbar _native = new();
    private readonly FakeAppBar _appBarNative = new();
    private readonly TaskbarController _taskbar;
    private readonly AppBarSafety _safety;
    private readonly TaskbarSafetyLifecycle _lifecycle;
    private DateTime _now = T0;
    private bool _dockVisible = true;
    private int _dockRecoveries;

    public AppBarSafetyTests()
    {
        File.Delete(Storage.PathFor("taskbar-state.json"));
        _taskbar = new TaskbarController(_native);
        _safety = new AppBarSafety(new AppBarController(_appBarNative, () => _now), _taskbar);
        _lifecycle = new TaskbarSafetyLifecycle(new TaskbarGuard(TimeSpan.FromSeconds(10)),
            () => _taskbar.IsHidingOrAutoHiding,
            () => _dockVisible,
            () => false,
            () => _taskbar.RestoreForSafety(TaskbarSafetyBlockers.DockUnavailable),
            () => { _dockRecoveries++; _taskbar.ResumeAfterDockRecovery(); });
    }

    public void Dispose()
    {
        _taskbar.Restore();
        File.Delete(Storage.PathFor("taskbar-state.json"));
    }

    private bool Ensure() => _safety.Ensure(Dock, 0xC123, reserveSpace: true, bottom: true, Monitor, 72);
    private bool Reserve() => _safety.Reserve(Dock, reserveSpace: true, bottom: true, Monitor, 72);
    private bool TaskbarAccessible => _native.Visible && (_native.State & Win32.ABS_AUTOHIDE) == 0;

    /// <summary>Starts like production: AppBar first (dock Show), then the automatic configured mode.</summary>
    private void StartHealthyHidden()
    {
        Assert.True(Ensure());
        Assert.Equal(TaskbarApplyResult.Applied, _taskbar.ApplyConfigured(TaskbarMode.Hide));
        Assert.False(_native.Visible);
        Assert.True(_taskbar.IsHidingOrAutoHiding);
    }

    [Fact]
    public void AppBarFailureBeforeStartupIsNotClearedByTheAutomaticConfiguredMode()
    {
        _appBarNative.NewWorks = false;
        Assert.False(Ensure());                                    // during dock Show()
        Assert.Equal(TaskbarSafetyBlockers.AppBarUnavailable, _taskbar.Blockers);

        var result = _taskbar.ApplyConfigured(TaskbarMode.Hide);  // App.OnStartup after Show()
        Assert.Equal(TaskbarApplyResult.SuspendedForSafety, result);
        Assert.Equal(TaskbarSafetyBlockers.AppBarUnavailable, _taskbar.Blockers);
        Assert.Equal(TaskbarMode.Hide, _taskbar.ConfiguredMode);  // preference recorded, not effective
        Assert.False(_taskbar.IsHidingOrAutoHiding);
        Assert.True(TaskbarAccessible);
        Assert.Equal(0, _native.HideCalls);
    }

    [Fact]
    public void RepeatedFailedReadinessObservationsStayBoundedAndKeepTaskbarAccessible()
    {
        _appBarNative.NewWorks = false;
        for (int observation = 0; observation < 100; observation++)
        {
            Assert.False(Ensure());
            _now = _now.AddMilliseconds(250);
            Assert.True(TaskbarAccessible);
        }

        Assert.Equal(AppBarController.MaxAttempts, _appBarNative.NewCalls);
        Assert.True(_safety.AppBar.RegistrationSuspended);
        Assert.Equal(TaskbarSafetyBlockers.AppBarUnavailable, _taskbar.Blockers);
        Assert.Equal(0, _native.HideCalls);
    }

    [Fact]
    public void RuntimeAppBarFailureWithAVisibleDockStaysSuspended()
    {
        StartHealthyHidden();
        _appBarNative.ReserveWorks = false;
        Assert.False(Reserve());                                   // e.g. ABN_POSCHANGED after another appbar appeared
        Assert.True(TaskbarAccessible);
        int sets = _native.SetCalls, hides = _native.HideCalls;

        for (int second = 1; second <= 30; second++)
        {
            Assert.False(_lifecycle.Heartbeat(T0.AddSeconds(second))); // dock is visible the whole time
            _taskbar.Enforce();
        }
        Assert.Equal(TaskbarSafetyBlockers.AppBarUnavailable, _taskbar.Blockers);
        Assert.False(_lifecycle.DockLossEpisodeActive);
        Assert.Equal(0, _dockRecoveries);                          // a visible dock is not an AppBar recovery
        Assert.Equal(sets, _native.SetCalls);
        Assert.Equal(hides, _native.HideCalls);
        Assert.True(TaskbarAccessible);
    }

    [Fact]
    public void ExplorerRestartDuringAppBarSuspensionKeepsTheTaskbarAndTheBlocker()
    {
        StartHealthyHidden();
        _appBarNative.NewWorks = false;
        _appBarNative.ReserveWorks = false;
        Assert.False(Reserve());
        Assert.False(_safety.OnExplorerRestarted(Dock, 0xC123, true, true, Monitor, 72)); // re-register fails too
        _lifecycle.Rearm();
        for (int second = 1; second <= 15; second++) { _lifecycle.Heartbeat(T0.AddSeconds(second)); _taskbar.Enforce(); }

        Assert.Equal(TaskbarSafetyBlockers.AppBarUnavailable, _taskbar.Blockers);
        Assert.True(TaskbarAccessible);

        _taskbar.OnExplorerRestarted();                            // a second restart, controller-only path
        Assert.True(_taskbar.SafetySuspended);
        Assert.True(TaskbarAccessible);
    }

    [Fact]
    public void DockRecoveryClearsOnlyTheDockLossBlocker()
    {
        StartHealthyHidden();
        _dockVisible = false;
        Assert.False(_lifecycle.Heartbeat(T0));
        Assert.True(_lifecycle.Heartbeat(T0.AddSeconds(10)));      // dock lost for the grace period
        Assert.Equal(TaskbarSafetyBlockers.DockUnavailable, _taskbar.Blockers);

        _appBarNative.ReserveWorks = false;
        Assert.False(Reserve());                                   // AppBar also fails while the dock is gone
        Assert.Equal(TaskbarSafetyBlockers.DockUnavailable | TaskbarSafetyBlockers.AppBarUnavailable, _taskbar.Blockers);

        _dockVisible = true;
        Assert.False(_lifecycle.Heartbeat(T0.AddSeconds(11)));
        Assert.Equal(1, _dockRecoveries);
        Assert.Equal(TaskbarSafetyBlockers.AppBarUnavailable, _taskbar.Blockers);
        Assert.True(TaskbarAccessible);                            // still blocked: AppBar not recovered
        Assert.False(_lifecycle.DockLossEpisodeActive);
    }

    [Fact]
    public void VerifiedAppBarRecoveryClearsOnlyItsBlockerAndReappliesWhenNoneRemain()
    {
        StartHealthyHidden();
        _appBarNative.NewWorks = false;
        _appBarNative.ReserveWorks = false;
        _safety.AppBar.ForgetRegistration();
        Assert.False(Ensure());
        _dockVisible = false;
        _taskbar.RestoreForSafety(TaskbarSafetyBlockers.DockUnavailable);
        Assert.Equal(TaskbarSafetyBlockers.DockUnavailable | TaskbarSafetyBlockers.AppBarUnavailable, _taskbar.Blockers);

        _appBarNative.NewWorks = true;                             // registration alone is not recovery
        _now += AppBarController.RetryBackoff;
        Assert.False(Ensure());
        Assert.True(_taskbar.Blockers.HasFlag(TaskbarSafetyBlockers.AppBarUnavailable));

        _appBarNative.ReserveWorks = true;                         // verified register + reserve
        Assert.True(Ensure());
        Assert.Equal(TaskbarSafetyBlockers.DockUnavailable, _taskbar.Blockers);
        Assert.True(TaskbarAccessible);                            // dock still lost: not reapplied

        Assert.True(_taskbar.ResumeAfterDockRecovery());
        Assert.Equal(TaskbarSafetyBlockers.None, _taskbar.Blockers);
        Assert.True(_taskbar.IsHidingOrAutoHiding);                // configured mode back only now
        Assert.False(_native.Visible);
    }

    [Fact]
    public void ExplicitUserRehideCannotOverrideKnownAppBarFailure()
    {
        _appBarNative.NewWorks = false;
        Assert.False(Ensure());
        Assert.Equal(TaskbarApplyResult.SuspendedForSafety, _taskbar.ApplyConfigured(TaskbarMode.Hide));

        Assert.Equal(TaskbarApplyResult.SuspendedForSafety, _taskbar.ApplyUserRequested(TaskbarMode.Hide));
        Assert.Equal(TaskbarSafetyBlockers.AppBarUnavailable, _taskbar.Blockers);
        Assert.True(TaskbarAccessible);
        Assert.Equal(0, _native.HideCalls);
    }

    [Fact]
    public void TurningOffSpaceReservationRemovesTheAppBarDependency()
    {
        StartHealthyHidden();
        _appBarNative.ReserveWorks = false;
        Assert.False(Reserve());
        Assert.True(TaskbarAccessible);
        Assert.True(_safety.Ensure(Dock, 0xC123, reserveSpace: false, bottom: true, Monitor, 72));
        Assert.Equal(TaskbarSafetyBlockers.None, _taskbar.Blockers);
        Assert.True(_taskbar.IsHidingOrAutoHiding);
    }

    [Fact]
    public void SettingsReloadUsesTheAutomaticPathAndRespectsBlockers()
    {
        StartHealthyHidden();
        _appBarNative.ReserveWorks = false;
        Assert.False(Reserve());
        Assert.Equal(TaskbarApplyResult.SuspendedForSafety, _taskbar.ApplyConfigured(TaskbarMode.AutoHide));
        Assert.Equal(TaskbarMode.AutoHide, _taskbar.ConfiguredMode);
        Assert.True(TaskbarAccessible);
        Assert.Equal(TaskbarApplyResult.SuspendedForSafety, _taskbar.ApplyConfigured(TaskbarMode.Leave));
        Assert.True(TaskbarAccessible);

        _appBarNative.ReserveWorks = true;
        Assert.True(Reserve());
        Assert.Equal(TaskbarSafetyBlockers.None, _taskbar.Blockers);
        Assert.False(_taskbar.IsHidingOrAutoHiding);               // the configured Leave is what applies now
    }

    [Fact]
    public void SafetyRestoreRequiresAReason() =>
        Assert.Throws<ArgumentException>(() => _taskbar.RestoreForSafety(TaskbarSafetyBlockers.None));

    [Fact]
    public void FailedRestoreRetriesEvenWhileDockRemainsVisibleUntilNativeStateIsConfirmed()
    {
        StartHealthyHidden();
        _native.IgnoreShow = true;
        _native.IgnoreSetState = true;

        Assert.False(_taskbar.RestoreForSafety(TaskbarSafetyBlockers.AppBarUnavailable));
        Assert.True(_taskbar.RestoreUnconfirmed);
        Assert.False(TaskbarAccessible);

        _native.IgnoreShow = false;
        _native.IgnoreSetState = false;
        _taskbar.Enforce();

        Assert.False(_taskbar.RestoreUnconfirmed);
        Assert.True(TaskbarAccessible);
        Assert.Equal(TaskbarSafetyBlockers.AppBarUnavailable, _taskbar.Blockers);
    }

    [Fact]
    public void ClearingLastBlockerCannotEraseAnUnconfirmedRestoreOrRehideTaskbar()
    {
        StartHealthyHidden();
        _native.IgnoreShow = true;
        _native.IgnoreSetState = true;
        Assert.False(_taskbar.RestoreForSafety(TaskbarSafetyBlockers.AppBarUnavailable));
        int hides = _native.HideCalls;

        Assert.True(_taskbar.ResumeAfterAppBarRecovery());
        Assert.Equal(TaskbarSafetyBlockers.None, _taskbar.Blockers);
        Assert.True(_taskbar.RestoreUnconfirmed);
        Assert.Equal(hides, _native.HideCalls);

        _taskbar.Enforce();
        Assert.True(_taskbar.RestoreUnconfirmed);
        Assert.Equal(hides, _native.HideCalls);

        _native.IgnoreShow = false;
        _native.IgnoreSetState = false;
        _taskbar.Enforce();
        Assert.False(_taskbar.RestoreUnconfirmed);
        Assert.True(TaskbarAccessible);
        Assert.Equal(hides, _native.HideCalls);

        _taskbar.Enforce();
        Assert.False(_native.Visible);
        Assert.True(_taskbar.IsHidingOrAutoHiding);
    }
}
