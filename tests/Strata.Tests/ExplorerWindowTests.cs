using Strata.Core;
using Strata.Views;

namespace Strata.Tests;

[Trait("Category", "Unit")]
public class ExplorerWindowTests
{
    public sealed class ThrowingShellWindows
    {
        public int Count => 1;
        public object Item(int index) => new ThrowingShellBrowser();
    }

    public sealed class ThrowingShellBrowser
    {
        public long HWND => throw new System.Runtime.InteropServices.COMException("fixture");
    }

    private sealed class FakeProcesses : IProcessInfo
    {
        public Dictionary<IntPtr, uint> Owners { get; } = [];
        public Dictionary<uint, long?> Starts { get; } = [];
        public HashSet<IntPtr> Alive { get; } = [];
        public uint ProcessIdOf(IntPtr window) => Owners.GetValueOrDefault(window);
        public long? StartTimeOf(uint pid) => Starts.GetValueOrDefault(pid);
        public bool WindowExists(IntPtr window) => Alive.Contains(window);
    }

    private sealed class FakeExplorerSource(params IntPtr[] windows) : IExplorerFolderWindowSource
    {
        public List<IntPtr> Windows { get; } = [.. windows];
        public bool Available { get; set; } = true;
        public ExplorerFolderWindowSnapshot Enumerate() => new(Available, Windows);
    }

    private sealed class FakeWindows : ITopLevelWindowSystem
    {
        public sealed record Facts(uint Pid, string Class = "CabinetWClass", bool Visible = true,
            bool Cloaked = false, bool Minimized = false, long ExStyle = 0x100, IntPtr Owner = default,
            string Title = "fixture-folder");

        public Dictionary<IntPtr, Facts> Items { get; } = [];
        public IReadOnlyList<IntPtr> Enumerate() => [.. Items.Keys];
        public bool IsVisible(IntPtr window) => Items[window].Visible;
        public long ExStyle(IntPtr window) => Items[window].ExStyle;
        public IntPtr Owner(IntPtr window) => Items[window].Owner;
        public int TitleLength(IntPtr window) => Items[window].Title.Length;
        public bool IsCloaked(IntPtr window) => Items[window].Cloaked;
        public string ClassName(IntPtr window) => Items[window].Class;
        public uint ProcessId(IntPtr window) => Items[window].Pid;
        public string Title(IntPtr window) => Items[window].Title;
        public bool IsMinimized(IntPtr window) => Items[window].Minimized;
    }

    [Fact]
    public void ExplorerPinUsesCanonicalShellTargetNotItsDisplayedName()
    {
        Assert.Equal(AppIdentityKind.ExplorerFolder,
            ExplorerPinIdentity.FromShellTarget("::{52205FD8-5DFB-447D-801A-D0B52F2E83E1}"));
        Assert.Equal(AppIdentityKind.Standard, ExplorerPinIdentity.FromShellTarget("File Explorer"));

        var window = new AppIdentity(null, @"C:\Windows\explorer.exe", "anything")
            { IdentityKind = AppIdentityKind.ExplorerFolder };
        Assert.True(window.Matches(new PinnedApp { Name = "anything else", IdentityKind = AppIdentityKind.ExplorerFolder }));
        Assert.False(window.Matches(new PinnedApp { Name = "File Explorer" }));
    }

    [Fact]
    public void LegacySystemExplorerExecutablePinIsUpgradedWithoutUsingItsDisplayedName()
    {
        Assert.Equal(AppIdentityKind.ExplorerFolder,
            ExplorerPinIdentity.FromExecutable(@"C:\Windows\explorer.exe", @"C:\Windows"));
        Assert.Equal(AppIdentityKind.Standard,
            ExplorerPinIdentity.FromExecutable(@"C:\Fixture\explorer.exe", @"C:\Windows"));

        string systemExplorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        var legacy = new PinnedApp { Name = "Unrelated localized label", Path = systemExplorer, ExePath = systemExplorer };
        Launcher.RefreshShellIdentities([legacy]);

        Assert.Equal(AppIdentityKind.ExplorerFolder, legacy.IdentityKind);
        var folderWindow = new AppIdentity(null, systemExplorer, "File Explorer")
            { IdentityKind = AppIdentityKind.ExplorerFolder };
        Assert.True(folderWindow.Matches(legacy));
    }

    [Fact]
    public void ExplorerWindowCountCannotBeReplacedByATitleDerivedUnreadBadge()
    {
        Assert.False(AppButton.UseUnreadBadge(AppIdentityKind.ExplorerFolder, configured: true));
        Assert.True(AppButton.UseUnreadBadge(AppIdentityKind.Standard, configured: true));
        Assert.False(AppButton.UseUnreadBadge(AppIdentityKind.Standard, configured: false));
    }

    [Fact]
    public void ShellEnumerationAccessDenialFailsClosedWithoutCrashingDockRefresh()
    {
        var source = new ShellExplorerFolderWindowSource(() => throw new UnauthorizedAccessException("fixture"));
        ExplorerFolderWindowSnapshot snapshot = source.Enumerate();
        Assert.False(snapshot.Available);
        Assert.Empty(snapshot.Windows);
    }

    [Fact]
    public void PerItemShellComFailureMakesPartialSnapshotNonAuthoritativeAndSuppressesLaunch()
    {
        var source = new ShellExplorerFolderWindowSource(() => new ThrowingShellWindows());

        ExplorerFolderWindowSnapshot snapshot = source.Enumerate();

        Assert.False(snapshot.Available);
        Assert.Empty(snapshot.Windows);
        Assert.Equal(DockWindow.MissingPinClick.ExplorerTrackingUnavailable, Click(AppIdentityKind.ExplorerFolder, snapshot.Available));
    }

    [Theory]
    [InlineData(true, "CabinetWClass", @"C:\Windows\explorer.exe", true)]
    [InlineData(true, "ExploreWClass", @"C:\Windows\EXPLORER.EXE", true)]
    [InlineData(false, "CabinetWClass", @"C:\Windows\explorer.exe", false)]
    [InlineData(true, "#32770", @"C:\Windows\explorer.exe", false)]
    [InlineData(true, "Progman", @"C:\Windows\explorer.exe", false)]
    [InlineData(true, "CabinetWClass", @"C:\Apps\other.exe", false)]
    [InlineData(true, "CabinetWClass", @"C:\Fixture\explorer.exe", false)]
    public void FolderPolicyRequiresShellMembershipClassAndExplorerProcess(
        bool member, string windowClass, string executable, bool expected) =>
        Assert.Equal(expected, ExplorerFolderWindowPolicy.IsEligible(member, windowClass, executable));

    [Fact]
    public void ExistingFolderWindowsAreGroupedBeforeTrackerStartsAndCountWindowsNotDuplicates()
    {
        var a = new IntPtr(0x101);
        var b = new IntPtr(0x102);
        var c = new IntPtr(0x103);
        var windows = new FakeWindows();
        var processes = new FakeProcesses();
        foreach (var (handle, pid, minimized) in new[] { (a, 10u, false), (b, 10u, true), (c, 10u, false) })
        {
            windows.Items[handle] = new FakeWindows.Facts(pid, Minimized: minimized);
            processes.Owners[handle] = pid;
            processes.Starts[pid] = 500;
            processes.Alive.Add(handle);
        }
        // A repeated COM item can represent the same top-level folder window; the HWND set is authoritative.
        var explorer = new FakeExplorerSource(a, b, b, c);
        var tracker = new WindowTracker(processes, windows, explorer,
            _ => new AppIdentity(null, @"C:\Windows\explorer.exe", "Windows Explorer"));

        Assert.True(tracker.Refresh());
        Assert.Equal(3, tracker.Windows.Count);
        Assert.All(tracker.Windows, item => Assert.Equal(AppIdentityKind.ExplorerFolder, item.App.IdentityKind));
        Assert.True(tracker.Windows.Single(item => item.Handle == b).IsMinimized);
        Assert.Single(tracker.Windows.Select(item => item.App.Key).Distinct());
    }

    [Fact]
    public void LegacyExplorerPinGroupsThreeExistingWindowsAndNormalClickNeverLaunches()
    {
        IntPtr[] handles = [new(0x301), new(0x302), new(0x303)];
        var windows = new FakeWindows();
        var processes = new FakeProcesses();
        foreach (IntPtr handle in handles)
        {
            windows.Items[handle] = new FakeWindows.Facts(42);
            processes.Owners[handle] = 42;
            processes.Starts[42] = 900;
            processes.Alive.Add(handle);
        }
        string systemExplorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        var pin = new PinnedApp { Name = "Localized fixture", Path = systemExplorer, ExePath = systemExplorer };
        Launcher.RefreshShellIdentities([pin]);
        var tracker = new WindowTracker(processes, windows, new FakeExplorerSource(handles),
            _ => new AppIdentity(null, systemExplorer, "File Explorer"));
        tracker.Refresh();

        var grouped = tracker.Windows.Where(window => window.App.Matches(pin)).ToList();
        Assert.Equal(3, grouped.Count);
        AppClickDecision decision = DockWindow.DecideNormalAppClick(grouped, IntPtr.Zero, _ => false);
        Assert.Equal(AppClickKind.ActivateWindow, decision.Kind);
        Assert.Contains(decision.Window, handles);
    }

    [Fact]
    public void ShellSurfacesDialogsInvisibleAndCloakedWindowsAreExcluded()
    {
        var windows = new FakeWindows();
        var processes = new FakeProcesses();
        var handles = Enumerable.Range(1, 6).Select(i => new IntPtr(i)).ToArray();
        windows.Items[handles[0]] = new(20, "CabinetWClass");
        windows.Items[handles[1]] = new(20, "Progman");
        windows.Items[handles[2]] = new(20, "WorkerW");
        windows.Items[handles[3]] = new(20, "#32770");
        windows.Items[handles[4]] = new(20, "CabinetWClass", Visible: false);
        windows.Items[handles[5]] = new(20, "CabinetWClass", Cloaked: true);
        foreach (var handle in handles)
        {
            processes.Owners[handle] = 20;
            processes.Starts[20] = 700;
            processes.Alive.Add(handle);
        }
        var tracker = new WindowTracker(processes, windows, new FakeExplorerSource(handles),
            _ => new AppIdentity(null, @"C:\Windows\explorer.exe", "Explorer"));

        tracker.Refresh();
        Assert.Equal(handles[0], Assert.Single(tracker.Windows).Handle);
    }

    [Fact]
    public void ClosingAndHandleReuseRemoveStaleExplorerIdentity()
    {
        var handle = new IntPtr(0x222);
        var windows = new FakeWindows();
        var processes = new FakeProcesses();
        var explorer = new FakeExplorerSource(handle);
        windows.Items[handle] = new(30);
        processes.Owners[handle] = 30;
        processes.Starts[30] = 1000;
        processes.Alive.Add(handle);
        var tracker = new WindowTracker(processes, windows, explorer, h =>
            new AppIdentity(null, processes.Owners[h] == 30 ? @"C:\Windows\explorer.exe" : @"C:\Apps\fixture.exe", "fixture"));

        tracker.Refresh();
        Assert.Equal(AppIdentityKind.ExplorerFolder, Assert.Single(tracker.Windows).App.IdentityKind);

        windows.Items.Remove(handle);
        processes.Alive.Remove(handle);
        explorer.Windows.Clear();
        Assert.True(tracker.Refresh());
        Assert.Empty(tracker.Windows);

        windows.Items[handle] = new(31, "FixtureWindow");
        processes.Owners[handle] = 31;
        processes.Starts[31] = 2000;
        processes.Alive.Add(handle);
        tracker.Refresh();
        Assert.Equal(AppIdentityKind.Standard, Assert.Single(tracker.Windows).App.IdentityKind);
    }

    [Fact]
    public void UnavailableShellEnumerationRetainsValidatedPriorFoldersAndSuppressesNormalLaunch()
    {
        var handle = new IntPtr(0x401);
        var windows = new FakeWindows();
        var processes = new FakeProcesses();
        var explorer = new FakeExplorerSource(handle);
        windows.Items[handle] = new(50);
        processes.Owners[handle] = 50;
        processes.Starts[50] = 3000;
        processes.Alive.Add(handle);
        var tracker = new WindowTracker(processes, windows, explorer,
            _ => new AppIdentity(null, @"C:\Windows\explorer.exe", "Explorer"));

        tracker.Refresh();
        explorer.Windows.Clear();
        explorer.Available = false;
        tracker.Refresh();

        Assert.False(tracker.ExplorerEnumerationAvailable);
        Assert.Equal(handle, Assert.Single(tracker.Windows).Handle);
        Assert.Equal(DockWindow.MissingPinClick.ExplorerTrackingUnavailable,
            Click(AppIdentityKind.ExplorerFolder, tracker.ExplorerEnumerationAvailable));
    }

    [Fact]
    public void InitialUnavailableShellEnumerationDoesNotGuessExplorerFolderMembership()
    {
        var handle = new IntPtr(0x402);
        var windows = new FakeWindows();
        var processes = new FakeProcesses();
        var explorer = new FakeExplorerSource { Available = false };
        windows.Items[handle] = new(51);
        processes.Owners[handle] = 51;
        processes.Starts[51] = 3001;
        processes.Alive.Add(handle);
        var tracker = new WindowTracker(processes, windows, explorer,
            _ => new AppIdentity(null, @"C:\Windows\explorer.exe", "Explorer"));

        tracker.Refresh();

        Assert.False(tracker.ExplorerEnumerationAvailable);
        Assert.Empty(tracker.Windows);
    }

    /// <summary>The decision the click itself makes. It is reached only when this dock shows no window for the pin.</summary>
    private static DockWindow.MissingPinClick Click(AppIdentityKind kind, bool enumerationAvailable,
        int folderWindowsAnywhere = 0, double secondsSinceLaunch = 60) =>
        DockWindow.DecideMissingPinClick(kind, enumerationAvailable, folderWindowsAnywhere, TimeSpan.FromSeconds(secondsSinceLaunch));

    [Fact]
    public void AuthoritativeZeroWindowExplorerSnapshotOpensAFolderWindowOnNormalClick()
    {
        // Nothing to activate and the snapshot proves it: the click opens a folder window, as the taskbar does.
        Assert.Equal(DockWindow.MissingPinClick.Launch, Click(AppIdentityKind.ExplorerFolder, true));
        // Uncertainty is a reason to refuse: "none open" is indistinguishable from "cannot see them".
        Assert.Equal(DockWindow.MissingPinClick.ExplorerTrackingUnavailable, Click(AppIdentityKind.ExplorerFolder, false));
        // Every other pin launches, whatever the shell enumeration is doing.
        Assert.Equal(DockWindow.MissingPinClick.Launch, Click(AppIdentityKind.Standard, true));
        Assert.Equal(DockWindow.MissingPinClick.Launch, Click(AppIdentityKind.Standard, false));
        Assert.Equal("New window", DockWindow.NewInstanceCommandLabel(AppIdentityKind.ExplorerFolder, 0));
        Assert.Equal("Open", DockWindow.NewInstanceCommandLabel(AppIdentityKind.Standard, 0));
        Assert.Equal("New window", DockWindow.NewInstanceCommandLabel(AppIdentityKind.Standard, 1));
    }

    /// <summary>
    /// A dock shows only the windows on its own screen, so with a dock on every screen the click decision would
    /// otherwise read "no folder window" from a per-screen list while the owner has one on the other screen. It
    /// counts folder windows across every screen, and a minimized window attributed to the nearest monitor rather
    /// than its own cannot cause a duplicate either.
    /// </summary>
    [Fact]
    public void AFolderWindowOnAnotherScreenStopsTheClickOpeningASecondOne()
    {
        Assert.Equal(DockWindow.MissingPinClick.ExplorerWindowElsewhere, Click(AppIdentityKind.ExplorerFolder, true, folderWindowsAnywhere: 1));
        Assert.Equal(DockWindow.MissingPinClick.ExplorerWindowElsewhere, Click(AppIdentityKind.ExplorerFolder, true, folderWindowsAnywhere: 7));
        Assert.Equal(DockWindow.MissingPinClick.Launch, Click(AppIdentityKind.ExplorerFolder, true, folderWindowsAnywhere: 0));
        // Another app's windows are its own business: only File Explorer is counted this way.
        Assert.Equal(DockWindow.MissingPinClick.Launch, Click(AppIdentityKind.Standard, true, folderWindowsAnywhere: 3));
    }

    /// <summary>
    /// Explorer takes longer to show a window than the 400 ms window-list refresh, so for a moment after the click
    /// the dock still sees no folder window. Without a settling time a double-click, or one impatient second click,
    /// opens two.
    /// </summary>
    [Fact]
    public void ASecondClickWhileTheFolderWindowIsStillOpeningDoesNotOpenAnother()
    {
        var settle = DockWindow.ExplorerLaunchSettle;
        Assert.True(settle >= TimeSpan.FromMilliseconds(800), "the settling time must outlast the 400 ms window-list refresh");
        foreach (double ms in new[] { 0.0, 150, 400, settle.TotalMilliseconds - 1 })
            Assert.Equal(DockWindow.MissingPinClick.ExplorerLaunchPending,
                DockWindow.DecideMissingPinClick(AppIdentityKind.ExplorerFolder, true, 0, TimeSpan.FromMilliseconds(ms)));
        // Once it has settled and still nothing opened, the owner may ask again.
        Assert.Equal(DockWindow.MissingPinClick.Launch, DockWindow.DecideMissingPinClick(AppIdentityKind.ExplorerFolder, true, 0, settle));
        // The settling time never holds back anything but File Explorer.
        Assert.Equal(DockWindow.MissingPinClick.Launch, DockWindow.DecideMissingPinClick(AppIdentityKind.Standard, true, 0, TimeSpan.Zero));
    }

    /// <summary>
    /// The shell's own enumeration can skip an entry without throwing: a registered window that reads back as null,
    /// or one reporting handle 0. A skipped entry could be the only open folder window, so the snapshot has to say
    /// it is not authoritative -- otherwise the dock reports no folder window while one is open, and the click that
    /// now opens one would open a second beside it.
    /// </summary>
    [Fact]
    public void AShellWindowThatCannotBeReadMakesTheSnapshotUnauthoritativeRatherThanEmpty()
    {
        foreach (object items in new object[] { new NullItemShellWindows(), new ZeroHandleShellWindows() })
        {
            ExplorerFolderWindowSnapshot snapshot = new ShellExplorerFolderWindowSource(() => items).Enumerate();
            Assert.False(snapshot.Available);
            Assert.Empty(snapshot.Windows);
            Assert.Equal(DockWindow.MissingPinClick.ExplorerTrackingUnavailable,
                Click(AppIdentityKind.ExplorerFolder, snapshot.Available));
        }
    }

    // Public, like the other shell fixtures: the dynamic call site in the product cannot bind to a private type.
    public sealed class NullItemShellWindows
    {
        public int Count => 1;
        public object? Item(int index) => null;
    }

    public sealed class ZeroHandleShellWindows
    {
        public int Count => 1;
        public object Item(int index) => new ZeroHandleBrowser();
    }

    public sealed class ZeroHandleBrowser { public long HWND => 0; }
}
