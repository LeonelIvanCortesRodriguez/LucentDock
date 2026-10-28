using Strata.Core;
using Strata.Native;
using Strata.Views;
using Strata.Widgets;

namespace Strata.Tests;

[Trait("Category", "Unit")]
public class StorageIsolationTests
{
    [Fact]
    public void TestsNeverUseTheRealDataFolder()
    {
        Assert.False(Storage.UsingDefaultRoot);
        Assert.StartsWith(Path.GetTempPath().TrimEnd('\\'), Storage.Root, StringComparison.OrdinalIgnoreCase);
    }
}

[Trait("Category", "Integration")]
public class TaskbarDurabilityTests
{
    private sealed class Native : ITaskbarNative
    {
        public bool Visible = true;
        public int SetCalls, HideCalls;
        public IntPtr FindPrimary() => new(1);
        public IEnumerable<IntPtr> FindAll() { yield return new IntPtr(1); }
        public int GetState(IntPtr tray) => Win32.ABS_ALWAYSONTOP;
        public void SetState(IntPtr tray, int state) => SetCalls++;
        public bool IsVisible(IntPtr window) => Visible;
        public void Show(IntPtr window) => Visible = true;
        public void Hide(IntPtr window) { HideCalls++; Visible = false; }
        public int LastError => 0;
    }

    [Fact]
    public void FailedOriginalStatePersistenceLeavesTaskbarVisibleAndUnchanged()
    {
        string blockingFile = Path.Combine(Path.GetTempPath(), "strata-storage-block-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blockingFile, "fixture");
        try
        {
            Storage.Configure(blockingFile);
            var native = new Native();
            var controller = new TaskbarController(native);
            Assert.Equal(TaskbarApplyResult.VerificationFailed, controller.ApplyUserRequested(TaskbarMode.Hide));
            Assert.True(native.Visible);
            Assert.Equal(0, native.SetCalls);
            Assert.Equal(0, native.HideCalls);
            Assert.Contains("durably save", controller.LastFailure);
        }
        finally
        {
            Storage.Configure(TestSetup.DataRoot);
            File.Delete(blockingFile);
        }
    }
}

[Trait("Category", "Unit")]
public class AppBarControllerTests
{
    private sealed class FakeAppBar : IAppBarNative
    {
        public bool NewResult = true, QueryResult = true, SetResult = true;
        public int NewCalls, QueryCalls, SetCalls, RemoveCalls;
        public Win32.RECT? ForceSetRect;
        public int LastError => 1400;
        public bool New(IntPtr hwnd, uint msg) { NewCalls++; return NewResult; }
        public bool Remove(IntPtr hwnd) { RemoveCalls++; return true; }
        public bool QueryPos(IntPtr hwnd, uint edge, ref Win32.RECT r) { QueryCalls++; return QueryResult; }
        public bool SetPos(IntPtr hwnd, uint edge, ref Win32.RECT r) { SetCalls++; if (ForceSetRect is { } f) r = f; return SetResult; }
    }

    private static readonly Win32.RECT Monitor = new() { Left = 0, Top = 0, Right = 3840, Bottom = 2160 };

    [Fact]
    public void FailedRegistrationUsesOneBoundedBackoffCycleAndNeverStorms()
    {
        DateTime now = new(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);
        var native = new FakeAppBar { NewResult = false };
        var bar = new AppBarController(native, () => now);

        for (int observation = 0; observation < 100; observation++)
        {
            Assert.False(bar.Register(new IntPtr(1), 0xC000));
            now = now.AddMilliseconds(250);
        }

        Assert.False(bar.Registered);
        Assert.Equal(AppBarController.MaxAttempts, native.NewCalls);
        Assert.True(bar.RegistrationSuspended);
        Assert.Contains("ABM_NEW", bar.LastFailure);
        Assert.Null(bar.Reserve(new IntPtr(1), true, Monitor, 72));
        Assert.Equal(0, native.QueryCalls); // nothing reserved without registration
    }

    [Fact]
    public void MeaningfulTransitionRearmsExactlyOneBoundedRecoveryCycle()
    {
        DateTime now = new(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);
        var native = new FakeAppBar { NewResult = false };
        var bar = new AppBarController(native, () => now);
        for (int attempt = 0; attempt < AppBarController.MaxAttempts; attempt++)
        {
            Assert.False(bar.Register(new IntPtr(1), 0xC000));
            now += AppBarController.RetryBackoff;
        }
        Assert.True(bar.RegistrationSuspended);

        for (int observation = 0; observation < 20; observation++)
            Assert.False(bar.Register(new IntPtr(1), 0xC000));
        Assert.Equal(AppBarController.MaxAttempts, native.NewCalls);

        bar.RearmRegistration(AppBarReadinessTransition.DpiOrDisplayChanged);
        native.NewResult = true;
        Assert.True(bar.Register(new IntPtr(1), 0xC000));
        Assert.Equal(AppBarController.MaxAttempts + 1, native.NewCalls);
        Assert.False(bar.RegistrationSuspended);
    }

    [Fact]
    public void ReservationFailuresAreDetected()
    {
        var native = new FakeAppBar { QueryResult = false };
        var bar = new AppBarController(native);
        Assert.True(bar.Register(new IntPtr(1), 0xC000));
        Assert.Null(bar.Reserve(new IntPtr(1), true, Monitor, 72));
        Assert.Contains("ABM_QUERYPOS", bar.LastFailure);

        native.QueryResult = true;
        native.ForceSetRect = new Win32.RECT { Left = 0, Top = 2000, Right = 3840, Bottom = 2000 };
        Assert.Null(bar.Reserve(new IntPtr(1), true, Monitor, 72));
        Assert.Contains("empty rect", bar.LastFailure);
    }

    [Fact]
    public void SuccessfulReservationAndRemoval()
    {
        var native = new FakeAppBar();
        var bar = new AppBarController(native);
        bar.Register(new IntPtr(1), 0xC000);
        var rc = bar.Reserve(new IntPtr(1), bottom: true, Monitor, 72);
        Assert.NotNull(rc);
        Assert.Equal(2160 - 72, rc!.Value.Top);
        Assert.True(bar.Unregister(new IntPtr(1)));
        Assert.False(bar.Registered);
        Assert.Equal(1, native.RemoveCalls);
        Assert.True(bar.Unregister(new IntPtr(1))); // idempotent
        Assert.Equal(1, native.RemoveCalls);
    }
}

[Trait("Category", "Unit")]
public class TaskbarSafetyTests
{
    private sealed class FakeTaskbar : ITaskbarNative
    {
        public int State = 0;
        public bool AcceptState = true;
        public bool Visible = true;
        public bool AcceptHide = true;
        public int Shows, SetCalls, HideCalls;
        public IntPtr FindPrimary() => new(42);
        public IEnumerable<IntPtr> FindAll() { yield return new IntPtr(42); yield return new IntPtr(43); }
        public int GetState(IntPtr tray) => State;
        public void SetState(IntPtr tray, int state) { SetCalls++; if (AcceptState) State = state; }
        public bool IsVisible(IntPtr w) => Visible;
        public void Show(IntPtr w) { Shows++; Visible = true; }
        public void Hide(IntPtr w) { HideCalls++; if (AcceptHide) Visible = false; }
        public int LastError => 5;
    }

    [Fact]
    public void HideThenRestoreReturnsTheOriginalState()
    {
        var native = new FakeTaskbar { State = Win32.ABS_ALWAYSONTOP };
        var tb = new TaskbarController(native);
        Assert.Equal(TaskbarApplyResult.Applied, tb.ApplyUserRequested(TaskbarMode.Hide));
        Assert.True((native.State & Win32.ABS_AUTOHIDE) != 0);
        Assert.False(native.Visible);
        tb.Restore();
        Assert.Equal(Win32.ABS_ALWAYSONTOP, native.State);
        Assert.True(native.Visible);
    }

    [Fact]
    public void RejectedStateChangeRestoresTheTaskbarInsteadOfLeavingItHalfDone()
    {
        var native = new FakeTaskbar { AcceptState = false };
        var tb = new TaskbarController(native);
        Assert.Equal(TaskbarApplyResult.VerificationFailed, tb.ApplyUserRequested(TaskbarMode.Hide));
        Assert.True(native.Visible);
        Assert.True(native.Shows >= 2); // every taskbar shown again
        Assert.False(tb.IsHidingOrAutoHiding);
        Assert.Contains("did not accept", tb.LastFailure);
    }

    [Fact]
    public void FailingToHideLeavesTheTaskbarVisible()
    {
        var native = new FakeTaskbar { AcceptHide = false };
        var tb = new TaskbarController(native);
        tb.ApplyUserRequested(TaskbarMode.Hide);
        tb.Enforce();
        Assert.True(native.Visible); // safe: the user still has the Windows taskbar
        tb.Restore();
    }

    [Fact]
    public void FailedRestoreIsReportedAndSavedStateIsRetained()
    {
        File.Delete(Storage.PathFor("taskbar-state.json"));
        var native = new FakeTaskbar { State = Win32.ABS_ALWAYSONTOP };
        var tb = new TaskbarController(native);
        Assert.Equal(TaskbarApplyResult.Applied, tb.ApplyUserRequested(TaskbarMode.Hide));
        native.AcceptState = false;
        Assert.False(tb.Restore());
        Assert.True(tb.IsHidingOrAutoHiding); // keep the safety probe armed for a bounded retry
        Assert.True(native.Visible);
        int setCallsAfterFailure = native.SetCalls;
        int hideCallsAfterFailure = native.HideCalls;
        tb.Enforce();
        Assert.Equal(setCallsAfterFailure + 1, native.SetCalls); // one bounded restore retry, never auto-hide
        Assert.Equal(hideCallsAfterFailure, native.HideCalls);
        Assert.Contains("hasState", File.ReadAllText(Storage.PathFor("taskbar-state.json")), StringComparison.OrdinalIgnoreCase);
        native.AcceptState = true;
        tb.Enforce();
        Assert.False(tb.RestoreUnconfirmed);
    }

    [Fact]
    public void GuardRetriesOnlyAfterAnotherGracePeriod()
    {
        var guard = new TaskbarGuard(TimeSpan.FromSeconds(10));
        var t = new DateTime(2026, 1, 1, 12, 0, 0);
        Assert.False(guard.Check(t, taskbarHidden: true, dockVisible: false, dockHiddenOnPurpose: false));
        Assert.False(guard.Check(t.AddSeconds(5), true, false, false));
        Assert.True(guard.Check(t.AddSeconds(11), true, false, false));
        Assert.False(guard.Check(t.AddSeconds(12), true, false, false));
        Assert.False(guard.Check(t.AddSeconds(20), true, false, false));
        Assert.True(guard.Check(t.AddSeconds(21), true, false, false));
    }

    [Fact]
    public void FirstUnsafeEpisodeRestoresOnce()
    {
        bool taskbarHidden = true;
        int restores = 0;
        var lifecycle = new TaskbarSafetyLifecycle(new TaskbarGuard(TimeSpan.FromSeconds(10)),
            () => taskbarHidden, () => false, () => false, () => { restores++; taskbarHidden = false; return true; });
        var t = new DateTime(2026, 1, 1, 12, 0, 0);

        Assert.False(lifecycle.Heartbeat(t));
        Assert.True(lifecycle.Heartbeat(t.AddSeconds(11)));
        Assert.False(lifecycle.Heartbeat(t.AddSeconds(12)));
        Assert.Equal(1, restores);
        Assert.True(lifecycle.LastRestoreSucceeded);
    }

    [Fact]
    public void FailedRestoreRetriesAfterAnotherBoundedGracePeriod()
    {
        int restores = 0;
        var lifecycle = new TaskbarSafetyLifecycle(new TaskbarGuard(TimeSpan.FromSeconds(10)),
            () => true, () => false, () => false, () => { restores++; return false; });
        var t = new DateTime(2026, 1, 1, 12, 0, 0);

        Assert.False(lifecycle.Heartbeat(t));
        Assert.True(lifecycle.Heartbeat(t.AddSeconds(10)));
        Assert.False(lifecycle.Heartbeat(t.AddSeconds(11)));
        Assert.False(lifecycle.Heartbeat(t.AddSeconds(19)));
        Assert.True(lifecycle.Heartbeat(t.AddSeconds(20)));
        Assert.Equal(2, restores);
        Assert.False(lifecycle.LastRestoreSucceeded);
    }

    [Fact]
    public void HealthyRecoveryRearmsASecondUnsafeEpisode()
    {
        bool hidden = true;
        int restores = 0;
        var lifecycle = new TaskbarSafetyLifecycle(new TaskbarGuard(TimeSpan.FromSeconds(10)),
            () => hidden, () => false, () => false, () => { restores++; hidden = false; return true; });
        var t = new DateTime(2026, 1, 1, 12, 0, 0);

        lifecycle.Heartbeat(t);
        Assert.True(lifecycle.Heartbeat(t.AddSeconds(10)));
        Assert.False(lifecycle.Heartbeat(t.AddSeconds(11))); // observed healthy, automatically rearmed
        hidden = true;
        Assert.False(lifecycle.Heartbeat(t.AddSeconds(20)));
        Assert.True(lifecycle.Heartbeat(t.AddSeconds(30)));
        Assert.Equal(2, restores);
    }

    [Fact]
    public void ExplorerRestartAndRehideRearmsAfterFirstRecovery()
    {
        bool hidden = true;
        int restores = 0;
        var lifecycle = new TaskbarSafetyLifecycle(new TaskbarGuard(TimeSpan.FromSeconds(10)),
            () => hidden, () => false, () => false, () => { restores++; hidden = false; return true; });
        var t = new DateTime(2026, 1, 1, 12, 0, 0);

        lifecycle.Heartbeat(t);
        Assert.True(lifecycle.Heartbeat(t.AddSeconds(10)));
        lifecycle.Rearm(); // the production TaskbarCreated path does this after reapplying configured mode
        hidden = true;
        Assert.False(lifecycle.Heartbeat(t.AddSeconds(20)));
        Assert.True(lifecycle.Heartbeat(t.AddSeconds(30)));
        Assert.Equal(2, restores);
    }

    [Fact]
    public void DeliberateFullscreenHidingNeverRestores()
    {
        int restores = 0;
        var lifecycle = new TaskbarSafetyLifecycle(new TaskbarGuard(TimeSpan.FromSeconds(10)),
            () => true, () => false, () => true, () => { restores++; return true; });
        var t = new DateTime(2026, 1, 1, 12, 0, 0);

        Assert.False(lifecycle.Heartbeat(t));
        Assert.False(lifecycle.Heartbeat(t.AddMinutes(1)));
        Assert.False(lifecycle.Heartbeat(t.AddHours(1)));
        Assert.Equal(0, restores);
    }

    [Fact]
    public void VisibleDockOperationNeverRestores()
    {
        int restores = 0;
        var lifecycle = new TaskbarSafetyLifecycle(new TaskbarGuard(TimeSpan.FromSeconds(10)),
            () => true, () => true, () => false, () => { restores++; return true; });
        var t = new DateTime(2026, 1, 1, 12, 0, 0);

        Assert.False(lifecycle.Heartbeat(t));
        Assert.False(lifecycle.Heartbeat(t.AddMinutes(1)));
        Assert.False(lifecycle.Heartbeat(t.AddHours(1)));
        Assert.Equal(0, restores);
    }

    [Fact]
    public void SuccessfulSafetyRecoveryStaysVisibleThroughEnforcementAndExplorerRestart()
    {
        File.Delete(Storage.PathFor("taskbar-state.json"));
        var native = new FakeTaskbar { State = Win32.ABS_ALWAYSONTOP };
        var controller = new TaskbarController(native);
        bool dockVisible = false;
        var lifecycle = new TaskbarSafetyLifecycle(new TaskbarGuard(TimeSpan.FromSeconds(10)),
            () => controller.IsHidingOrAutoHiding,
            () => dockVisible,
            () => false,
            () => controller.RestoreForSafety(TaskbarSafetyBlockers.DockUnavailable),
            () => controller.ResumeAfterDockRecovery());
        var t = new DateTime(2026, 1, 1, 12, 0, 0);

        Assert.Equal(TaskbarApplyResult.Applied, controller.ApplyUserRequested(TaskbarMode.Hide));
        Assert.False(native.Visible);
        Assert.False(lifecycle.Heartbeat(t));
        Assert.True(lifecycle.Heartbeat(t.AddSeconds(10)));
        Assert.True(controller.SafetySuspended);
        Assert.Equal(TaskbarMode.Hide, controller.ConfiguredMode); // preference is retained, not effective
        Assert.False(controller.IsHidingOrAutoHiding);
        Assert.True(native.Visible);

        int setCallsAfterRecovery = native.SetCalls;
        int hideCallsAfterRecovery = native.HideCalls;
        for (int second = 11; second <= 40; second++)
        {
            controller.Enforce();
            lifecycle.Heartbeat(t.AddSeconds(second));
        }
        Assert.Equal(setCallsAfterRecovery, native.SetCalls);
        Assert.Equal(hideCallsAfterRecovery, native.HideCalls);
        Assert.True(native.Visible);

        controller.OnExplorerRestarted();
        lifecycle.Rearm();
        Assert.True(controller.SafetySuspended);
        Assert.Equal(setCallsAfterRecovery, native.SetCalls);
        Assert.Equal(hideCallsAfterRecovery, native.HideCalls);
        Assert.True(native.Visible);

        // A user hide request records intent but cannot bypass the known dock-loss suspension.
        Assert.Equal(TaskbarApplyResult.SuspendedForSafety, controller.ApplyUserRequested(TaskbarMode.Hide));
        Assert.True(controller.SafetySuspended);
        Assert.False(controller.IsHidingOrAutoHiding);
        Assert.True(native.Visible);
        dockVisible = true;
        Assert.False(lifecycle.Heartbeat(t.AddSeconds(41)));
        Assert.False(controller.SafetySuspended);
        Assert.False(native.Visible); // verified dock recovery applies the retained preference

        // A later safety episode can also end only when the native dock is confirmed visible.
        dockVisible = false;
        lifecycle.Rearm();
        Assert.False(lifecycle.Heartbeat(t.AddSeconds(50)));
        Assert.True(lifecycle.Heartbeat(t.AddSeconds(60)));
        Assert.True(controller.SafetySuspended);
        Assert.True(native.Visible);
        dockVisible = true;
        Assert.False(lifecycle.Heartbeat(t.AddSeconds(61)));
        Assert.False(controller.SafetySuspended);
        Assert.False(native.Visible); // confirmed health reapplies the retained preference immediately
        controller.Enforce();
        Assert.False(native.Visible);

        Assert.True(controller.Restore());
        File.Delete(Storage.PathFor("taskbar-state.json"));
    }

    [Fact]
    public void DockWindowHeartbeatCallsTheLiveTaskbarSafetyLifecycle()
    {
        const System.Reflection.BindingFlags privateInstance =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        Assert.Contains(typeof(DockWindow).GetFields(privateInstance),
            field => field.FieldType == typeof(TaskbarSafetyLifecycle));

        var onSecond = typeof(DockWindow).GetMethod("OnSecond", privateInstance);
        var heartbeat = typeof(TaskbarSafetyLifecycle).GetMethod(nameof(TaskbarSafetyLifecycle.Heartbeat));
        Assert.NotNull(onSecond);
        Assert.NotNull(heartbeat);
        byte[] il = onSecond!.GetMethodBody()!.GetILAsByteArray()!;
        Assert.True(il.AsSpan().IndexOf(BitConverter.GetBytes(heartbeat!.MetadataToken)) >= 0,
            "DockWindow.OnSecond must invoke TaskbarSafetyLifecycle.Heartbeat.");

        var wndProc = typeof(DockWindow).GetMethod("WndProc", privateInstance);
        var rearm = typeof(TaskbarSafetyLifecycle).GetMethod(nameof(TaskbarSafetyLifecycle.Rearm));
        Assert.NotNull(wndProc);
        Assert.NotNull(rearm);
        byte[] wndProcIl = wndProc!.GetMethodBody()!.GetILAsByteArray()!;
        Assert.True(wndProcIl.AsSpan().IndexOf(BitConverter.GetBytes(rearm!.MetadataToken)) >= 0,
            "The Explorer-restart path must rearm TaskbarSafetyLifecycle.");
    }

    [Fact]
    public void ApplicationStartupArmsTaskbarSafetyBeforeDockReadiness()
    {
        const System.Reflection.BindingFlags instance =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var startup = typeof(App).GetMethod("OnStartup", instance);
        var restoreForSafety = typeof(TaskbarController).GetMethod(nameof(TaskbarController.RestoreForSafety));
        Assert.NotNull(startup);
        Assert.NotNull(restoreForSafety);
        byte[] il = startup!.GetMethodBody()!.GetILAsByteArray()!;
        Assert.True(il.AsSpan().IndexOf(BitConverter.GetBytes(restoreForSafety!.MetadataToken)) >= 0,
            "App startup must arm StartupNotReady before any taskbar hide request can be honored.");
    }
}

[Trait("Category", "Unit")]
public class InstallerTests
{
    private sealed class FakeIO : IInstallerIO
    {
        public string? CurrentExe { get; set; } = @"X:\build\Strata.exe";
        public string InstallDir { get; set; } = @"X:\Programs\Strata";
        public string StartMenuDir { get; set; } = @"X:\StartMenu\Strata";
        public string TempDir => @"X:\Temp\";
        public HashSet<string> Files = new(StringComparer.OrdinalIgnoreCase) { @"X:\build\Strata.exe" };
        public HashSet<string> Dirs = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Hashes = new(StringComparer.OrdinalIgnoreCase);
        public int CopyFailures;
        public bool ShortcutThrows, DeleteThrows, RunKey;
        public List<(string Exe, string Args)> Launches = [];

        public bool FileExists(string p) => Files.Contains(p);
        public bool DirectoryExists(string p) => Dirs.Contains(p);
        public void CreateDirectory(string p) => Dirs.Add(p);
        public void CopyFile(string from, string to)
        {
            if (CopyFailures-- > 0) throw new IOException("file in use");
            Files.Add(to);
            Hashes[to] = Hashes.GetValueOrDefault(from, "AAAA");
        }
        public string Sha256(string p) => Hashes.GetValueOrDefault(p, "AAAA");
        public void DeleteDirectory(string p) { if (DeleteThrows) throw new IOException("locked"); Dirs.Remove(p); }
        public void CreateShortcut(string lnk, string target, string args, string d) { if (ShortcutThrows) throw new IOException("no shell"); Files.Add(lnk); }
        public void Launch(string exe, string args) => Launches.Add((exe, args));
        public void SetRunKey(string? exe) => RunKey = exe is not null;
        public bool RunKeyPresent() => RunKey;
        public void RestoreTaskbar() { }
        public void Sleep(TimeSpan t) { }
    }

    [Fact]
    public void AbortsWhenTheRunningCopyWontStop()
    {
        var io = new FakeIO();
        var r = new Installer(io).Install(stopRunning: () => false);
        Assert.Equal(InstallResult.StillRunning, r);
        Assert.Empty(io.Launches);
        Assert.DoesNotContain(@"X:\Programs\Strata\Strata.exe", io.Files);
    }

    [Fact]
    public void CopyFailureIsReportedAndNothingLaunches()
    {
        var io = new FakeIO { CopyFailures = 100 };
        var installer = new Installer(io);
        Assert.Equal(InstallResult.CopyFailed, installer.Install(() => true));
        Assert.Empty(io.Launches);
        Assert.Contains("Copying Strata.exe failed", installer.Messages.Last());
    }

    [Fact]
    public void TransientLockIsRetried()
    {
        var io = new FakeIO { CopyFailures = 3 };
        Assert.Equal(InstallResult.Success, new Installer(io).Install(() => true));
    }

    [Fact]
    public void HashMismatchBlocksLaunch()
    {
        var io = new FakeIO();
        io.Hashes[@"X:\build\Strata.exe"] = "GOOD";
        var installer = new Installer(io);
        // Simulate a corrupted copy: the installed file hashes differently.
        var corrupt = new FakeIO { CopyFailures = 0 };
        corrupt.Hashes[@"X:\build\Strata.exe"] = "GOOD";
        var wrapped = new CorruptingIO(corrupt);
        Assert.Equal(InstallResult.HashMismatch, new Installer(wrapped).Install(() => true));
        Assert.Empty(corrupt.Launches);
    }

    private sealed class CorruptingIO(FakeIO inner) : IInstallerIO
    {
        public string? CurrentExe => inner.CurrentExe;
        public string InstallDir => inner.InstallDir;
        public string StartMenuDir => inner.StartMenuDir;
        public string TempDir => inner.TempDir;
        public bool FileExists(string p) => inner.FileExists(p);
        public bool DirectoryExists(string p) => inner.DirectoryExists(p);
        public void CreateDirectory(string p) => inner.CreateDirectory(p);
        public void CopyFile(string f, string t) { inner.CopyFile(f, t); inner.Hashes[t] = "BAD"; }
        public string Sha256(string p) => inner.Sha256(p);
        public void DeleteDirectory(string p) => inner.DeleteDirectory(p);
        public void CreateShortcut(string l, string t, string a, string d) => inner.CreateShortcut(l, t, a, d);
        public void Launch(string e, string a) => inner.Launch(e, a);
        public void SetRunKey(string? e) => inner.SetRunKey(e);
        public bool RunKeyPresent() => inner.RunKeyPresent();
        public void RestoreTaskbar() => inner.RestoreTaskbar();
        public void Sleep(TimeSpan t) { }
    }

    [Fact]
    public void ShortcutFailureIsNotReportedAsSuccess()
    {
        var io = new FakeIO { ShortcutThrows = true };
        Assert.Equal(InstallResult.ShortcutsFailed, new Installer(io).Install(() => true));
    }

    [Fact]
    public void SuccessfulInstallLaunchesTheInstalledCopyOnce()
    {
        var io = new FakeIO();
        var installer = new Installer(io);
        Assert.Equal(InstallResult.Success, installer.Install(() => true));
        Assert.Single(io.Launches);
        Assert.Equal(@"X:\Programs\Strata\Strata.exe", io.Launches[0].Exe);
        Assert.Contains(@"X:\StartMenu\Strata\Strata - Restore Windows taskbar.lnk", io.Files);
    }

    [Fact]
    public void UninstallReportsLeftovers()
    {
        var io = new FakeIO { CurrentExe = @"X:\Temp\Strata-uninstall.exe", DeleteThrows = true };
        io.Dirs.Add(io.InstallDir);
        io.Dirs.Add(io.StartMenuDir);
        var installer = new Installer(io);
        Assert.Equal(InstallResult.UninstallIncomplete, installer.Uninstall(() => true, purgeData: false));
        Assert.Contains("still exists", installer.Messages.Last());
    }

    [Fact]
    public void UninstallSucceedsOnlyWhenEverythingIsGone()
    {
        var io = new FakeIO { CurrentExe = @"X:\Temp\Strata-uninstall.exe", RunKey = true };
        io.Dirs.Add(io.InstallDir);
        io.Dirs.Add(io.StartMenuDir);
        var installer = new Installer(io);
        Assert.Equal(InstallResult.Success, installer.Uninstall(() => true, purgeData: false));
        Assert.False(io.RunKey);
        Assert.Contains("data was kept", installer.Messages.Last());
    }

    [Fact]
    public void InstalledCopyHandsUninstallToATemporaryCopy()
    {
        var io = new FakeIO { CurrentExe = @"X:\Programs\Strata\Strata.exe" };
        io.Files.Add(io.CurrentExe);
        var r = new Installer(io).Uninstall(() => true, purgeData: true);
        Assert.Equal(InstallResult.HandedOff, r);
        Assert.Single(io.Launches);
        Assert.StartsWith(@"X:\Temp\Strata-uninstall-", io.Launches[0].Exe);
        Assert.Equal("--uninstall --handoff --purge", io.Launches[0].Args);
    }

    [Fact]
    public void UninstallAbortsWhenTheDockWontStop() =>
        Assert.Equal(InstallResult.StillRunning, new Installer(new FakeIO()).Uninstall(() => false, purgeData: false));
}

[Trait("Category", "Unit")]
public class WindowIdentityTests
{
    private sealed class FakeProcesses : IProcessInfo
    {
        public Dictionary<IntPtr, uint> Owners = new();
        public Dictionary<uint, long?> Starts = new();
        public HashSet<IntPtr> Alive = new();
        public uint ProcessIdOf(IntPtr w) => Owners.GetValueOrDefault(w);
        public long? StartTimeOf(uint pid) => Starts.GetValueOrDefault(pid);
        public bool WindowExists(IntPtr w) => Alive.Contains(w);
    }

    [Fact]
    public void RecycledHandlesAndPidsAreReidentified()
    {
        var procs = new FakeProcesses();
        var hwnd = new IntPtr(0x1234);
        procs.Owners[hwnd] = 100;
        procs.Starts[100] = 5;
        procs.Alive.Add(hwnd);
        int identifyCalls = 0;
        var cache = new WindowIdentityCache(procs, _ =>
        {
            identifyCalls++;
            return new AppIdentity(null, $@"C:\app{procs.Owners[hwnd]}-{procs.Starts[procs.Owners[hwnd]]}.exe", "App");
        });

        var first = cache.Resolve(hwnd);
        Assert.Same(first, cache.Resolve(hwnd));          // cached while the owner is unchanged
        Assert.Equal(1, identifyCalls);

        procs.Owners[hwnd] = 200;                          // handle reused by another process
        procs.Starts[200] = 9;
        var second = cache.Resolve(hwnd);
        Assert.Equal(2, identifyCalls);
        Assert.NotEqual(first!.ExePath, second!.ExePath);

        procs.Starts[200] = 10;                            // same PID, but a newer process (PID recycled)
        cache.Resolve(hwnd);
        Assert.Equal(3, identifyCalls);

        procs.Owners[hwnd] = 0;                            // window gone
        Assert.Null(cache.Resolve(hwnd));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void DeadWindowsAreEvicted()
    {
        var procs = new FakeProcesses();
        var cache = new WindowIdentityCache(procs, _ => new AppIdentity(null, "x", "x"), capacity: 2);
        for (int i = 1; i <= 3; i++)
        {
            var h = new IntPtr(i);
            procs.Owners[h] = (uint)i;
            procs.Starts[(uint)i] = i * 100;
            procs.Alive.Add(h);
            cache.Resolve(h);
        }
        Assert.Equal(2, cache.Count);                       // capacity enforced
        procs.Alive.Clear();
        cache.Evict();
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void UnknownProcessStartTimeIsNeverCached()
    {
        var procs = new FakeProcesses();
        var hwnd = new IntPtr(77);
        procs.Owners[hwnd] = 7;
        procs.Alive.Add(hwnd);
        int calls = 0;
        var cache = new WindowIdentityCache(procs, _ => { calls++; return new AppIdentity(null, "x", "x"); });
        cache.Resolve(hwnd);
        cache.Resolve(hwnd);
        Assert.Equal(2, calls);
        Assert.Equal(0, cache.Count);
        Assert.False(cache.StillSameProcess(hwnd, 7, null));
    }
}

[Trait("Category", "Unit")]
public class WeatherCacheTests
{
    private static WeatherReport Report(double t) => new(t, 0, true, 0, 50, [], new DateTime(2026, 1, 1, 12, 0, 0));

    [Fact]
    public void OutOfOrderResponsesNeverShowUnderTheWrongCity()
    {
        var cache = new WeatherCache();
        var paris = new WeatherKey(48.857, 2.352, false);
        var lima = new WeatherKey(-12.046, -77.043, false);
        var now = new DateTime(2026, 1, 1, 12, 0, 0);
        long t1 = cache.Begin(paris, now);
        long t2 = cache.Begin(lima, now);                   // user switched city before Paris answered
        Assert.True(cache.Complete(t2, lima, Report(24)));
        Assert.False(cache.Complete(t1, paris, Report(8))); // late Paris answer is discarded
        Assert.Equal(24, cache.Get(lima)!.Temperature);
        Assert.Null(cache.Get(paris));
    }

    [Fact]
    public void UnitsArePartOfTheKey()
    {
        var s = new AppSettings { WeatherLatitude = 40.4168, WeatherLongitude = -3.7038, Fahrenheit = false };
        var celsius = WeatherKey.For(s)!;
        s.Fahrenheit = true;
        var fahrenheit = WeatherKey.For(s)!;
        Assert.NotEqual(celsius, fahrenheit);
        var cache = new WeatherCache();
        cache.Put(celsius, Report(20));
        Assert.Null(cache.Get(fahrenheit));                 // shows "loading", never 20° labelled °F
        Assert.Null(WeatherKey.For(new AppSettings()));
    }

    [Fact]
    public async Task NewestSelectionWinsThroughTheService()
    {
        var pending = new Dictionary<WeatherKey, TaskCompletionSource<WeatherReport>>();
        var original = WeatherService.Fetch;
        WeatherService.Fetch = key => (pending[key] = new TaskCompletionSource<WeatherReport>()).Task;
        try
        {
            var s = new AppSettings { WeatherCity = "Oslo", WeatherLatitude = 59.913, WeatherLongitude = 10.752 };
            var oslo = WeatherKey.For(s)!;
            var first = WeatherService.RefreshIfStale(s, force: true);
            s.WeatherCity = "Cairo"; s.WeatherLatitude = 30.044; s.WeatherLongitude = 31.236;
            var cairo = WeatherKey.For(s)!;
            var second = WeatherService.RefreshIfStale(s, force: true);

            pending[cairo].SetResult(Report(31));
            await second;
            pending[oslo].SetResult(Report(-4));
            await first;

            Assert.Equal(31, WeatherService.For(s)!.Temperature);
            Assert.Null(WeatherService.Cache.Get(oslo));
        }
        finally { WeatherService.Fetch = original; }
    }
}

[Trait("Category", "Unit")]
public class FailureTrackerTests
{
    [Fact]
    public void RepeatedFailuresQuarantineOnce()
    {
        var t = new FailureTracker(threshold: 3, window: TimeSpan.FromMinutes(5));
        var now = new DateTime(2026, 1, 1, 12, 0, 0);
        var ex = new InvalidOperationException("boom");
        Assert.False(t.Record("widget:cpu", ex, now));
        Assert.False(t.Record("widget:cpu", ex, now.AddSeconds(1)));
        Assert.True(t.Record("widget:cpu", ex, now.AddSeconds(2)));
        Assert.False(t.Record("widget:cpu", ex, now.AddSeconds(3))); // already quarantined
        Assert.True(t.IsQuarantined("widget:cpu"));
        Assert.False(t.IsQuarantined("widget:ram"));
        t.Release("widget:cpu");
        Assert.False(t.IsQuarantined("widget:cpu"));
    }

    [Fact]
    public void FailuresOutsideTheWindowDontAccumulate()
    {
        var t = new FailureTracker(threshold: 3, window: TimeSpan.FromMinutes(5));
        var now = new DateTime(2026, 1, 1, 12, 0, 0);
        var ex = new Exception("x");
        t.Record("w", ex, now);
        t.Record("w", ex, now.AddMinutes(6));
        Assert.False(t.Record("w", ex, now.AddMinutes(12)));
    }
}

[Trait("Category", "Unit")]
public class PrivacyGuardUnitTests
{
    [Fact]
    public void RealRootIsRefusedOnlyInPreviewMode()
    {
        // Outside preview mode the guard is inert.
        if (!PrivacyGuard.PreviewActive)
        {
            PrivacyGuard.ThrowIfPreview("anything");
            PrivacyGuard.ThrowIfPreviewTouches(Storage.DefaultRoot);
        }
        Assert.False(Storage.UsingDefaultRoot);
    }
}
