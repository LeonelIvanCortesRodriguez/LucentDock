using System.Diagnostics;
using Strata.Native;

namespace Strata.Core;

/// <summary>Which app a window or pin belongs to.</summary>
public sealed record AppIdentity(string? Aumid, string? ExePath, string DisplayName)
{
    public AppIdentityKind IdentityKind { get; init; }

    public string Key => IdentityKind == AppIdentityKind.ExplorerFolder ? ExplorerPinIdentity.Key
        : Aumid is not null ? "aumid:" + Aumid.ToLowerInvariant()
        : "exe:" + (ExePath ?? "").ToLowerInvariant();

    public bool Matches(PinnedApp pin)
    {
        if (IdentityKind == AppIdentityKind.ExplorerFolder || pin.IdentityKind == AppIdentityKind.ExplorerFolder)
            return IdentityKind == pin.IdentityKind;
        if (pin.Aumid is not null && Aumid is not null && string.Equals(pin.Aumid, Aumid, StringComparison.OrdinalIgnoreCase))
            return true;
        return pin.ExePath is not null && ExePath is not null
               && string.Equals(pin.ExePath, ExePath, StringComparison.OrdinalIgnoreCase);
    }

    public static string NameFromExe(string? exe)
    {
        if (string.IsNullOrEmpty(exe)) return "App";
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exe);
            if (!string.IsNullOrWhiteSpace(info.FileDescription)) return info.FileDescription.Trim();
            if (!string.IsNullOrWhiteSpace(info.ProductName)) return info.ProductName.Trim();
        }
        catch { }
        return System.IO.Path.GetFileNameWithoutExtension(exe);
    }
}

public sealed class TrackedWindow
{
    public required IntPtr Handle { get; init; }
    public required AppIdentity App { get; init; }
    public required long Order { get; init; }
    public uint ProcessId { get; init; }
    public long? ProcessStart { get; init; }
    public string Title { get; set; } = "";
    public bool IsMinimized { get; set; }
}

/// <summary>Where the dock gets its windows from: the live desktop, or synthetic fixtures (previews, audits).</summary>
public interface IWindowSource
{
    /// <summary>Re-read the window list. True if it changed.</summary>
    bool Refresh();
    IReadOnlyList<TrackedWindow> Windows { get; }
    IntPtr Foreground { get; }
    void Activate(IntPtr window);
    void Minimize(IntPtr window);
    void Close(IntPtr window);
    bool IsMinimized(IntPtr window);
    /// <summary>False when File Explorer folder membership cannot currently be established safely.</summary>
    bool ExplorerEnumerationAvailable => true;
}

/// <summary>Enumerates the windows the Windows taskbar would show, using the same rules as Alt+Tab.</summary>
public sealed class WindowTracker : IWindowSource
{
    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
        "XamlExplorerHostIslandWindow", "TopLevelWindowForOverflowXamlIsland", "Shell_InputSwitchTopLevelWindow",
        "MSCTFIME UI", "IME", "ForegroundStaging", "MultitaskingViewFrame",
    };

    private readonly Dictionary<IntPtr, TrackedWindow> _known = new();
    private readonly uint _ownPid = (uint)Environment.ProcessId;
    private readonly WindowIdentityCache _identities;
    private readonly ITopLevelWindowSystem _windowSystem;
    private readonly IExplorerFolderWindowSource _explorerWindows;
    private long _order;

    public WindowTracker(IProcessInfo? processes = null, ITopLevelWindowSystem? windowSystem = null,
        IExplorerFolderWindowSource? explorerWindows = null, Func<IntPtr, AppIdentity?>? identify = null)
    {
        _windowSystem = windowSystem ?? new Win32TopLevelWindowSystem();
        _explorerWindows = explorerWindows ?? new ShellExplorerFolderWindowSource();
        _identities = new WindowIdentityCache(processes ?? new Win32ProcessInfo(), identify ?? Identify);
    }

    public IReadOnlyList<TrackedWindow> Windows { get; private set; } = [];
    public IntPtr Foreground => Win32.GetForegroundWindow();
    public bool ExplorerEnumerationAvailable { get; private set; } = true;

    /// <summary>Re-enumerate. Returns true if the set of windows, their owners or their titles changed.</summary>
    public bool Refresh()
    {
        PrivacyGuard.ThrowIfPreview("the live window list");
        var seen = new HashSet<IntPtr>();
        bool changed = false;

        ExplorerFolderWindowSnapshot explorerSnapshot = _explorerWindows.Enumerate();
        ExplorerEnumerationAvailable = explorerSnapshot.Available;
        HashSet<IntPtr> explorerFolders = explorerSnapshot.Windows.ToHashSet();
        foreach (IntPtr h in _windowSystem.Enumerate())
        {
            if (!IsTaskbarWindow(h)) continue;
            string title = _windowSystem.Title(h);
            bool minimized = _windowSystem.IsMinimized(h);
            var (pid, start) = _identities.Owner(h);
            string windowClass = _windowSystem.ClassName(h);
            bool explorerMember = explorerFolders.Contains(h);
            if (!explorerSnapshot.Available && _known.TryGetValue(h, out var priorExplorer)
                && priorExplorer.ProcessId == pid && priorExplorer.ProcessStart == start
                && priorExplorer.App.IdentityKind == AppIdentityKind.ExplorerFolder)
                explorerMember = true;
            if (_known.TryGetValue(h, out var w) && w.ProcessId == pid && w.ProcessStart == start
                && !ExplorerFolderWindowPolicy.IsExplorerProcess(w.App.ExePath))
            {
                seen.Add(h);
                if (w.Title != title || w.IsMinimized != minimized) { w.Title = title; w.IsMinimized = minimized; changed = true; }
                continue;
            }

            if (_known.TryGetValue(h, out w) && w.ProcessId == pid && w.ProcessStart == start
                && ExplorerFolderWindowPolicy.IsEligible(explorerMember, windowClass, w.App.ExePath))
            {
                seen.Add(h);
                if (w.Title != title || w.IsMinimized != minimized) { w.Title = title; w.IsMinimized = minimized; changed = true; }
                continue;
            }

            // New window, or a recycled handle now owned by a different process: identify it afresh.
            var app = _identities.Resolve(h);
            if (app is null) continue;
            bool explorerFolder = ExplorerFolderWindowPolicy.IsEligible(explorerMember, windowClass, app.ExePath);
            // explorer.exe also owns the desktop, taskbar, invisible hosts and occasional dialogs. Only a
            // documented IShellWindows folder HWND belongs on the dock.
            if (ExplorerFolderWindowPolicy.IsExplorerProcess(app.ExePath) && !explorerFolder) continue;
            if (explorerFolder)
                app = app with { IdentityKind = AppIdentityKind.ExplorerFolder, DisplayName = "File Explorer" };
            seen.Add(h);
            _known[h] = new TrackedWindow
            {
                Handle = h, App = app, Order = _order++, ProcessId = pid, ProcessStart = start, Title = title, IsMinimized = minimized,
            };
            changed = true;
        }

        foreach (var gone in _known.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _known.Remove(gone);
            changed = true;
        }
        if (changed)
        {
            Windows = _known.Values.OrderBy(w => w.Order).ToList();
            _identities.Evict();
        }
        return changed;
    }

    private bool IsTaskbarWindow(IntPtr h)
    {
        if (!_windowSystem.IsVisible(h)) return false;
        long ex = _windowSystem.ExStyle(h);
        bool appWindow = (ex & Win32.WS_EX_APPWINDOW) != 0;
        if (!appWindow)
        {
            if ((ex & Win32.WS_EX_TOOLWINDOW) != 0) return false;
            if ((ex & Win32.WS_EX_NOACTIVATE) != 0) return false;
            if (_windowSystem.Owner(h) != IntPtr.Zero) return false;
        }
        if (_windowSystem.TitleLength(h) == 0) return false;
        if (_windowSystem.IsCloaked(h)) return false;
        if (IgnoredClasses.Contains(_windowSystem.ClassName(h))) return false;
        uint pid = _windowSystem.ProcessId(h);
        return pid != _ownPid;
    }

    public static AppIdentity? Identify(IntPtr h)
    {
        PrivacyGuard.ThrowIfPreview("live window identities");
        Win32.GetWindowThreadProcessId(h, out uint pid);
        string? exe = Win32.GetProcessPath(pid);
        string? aumid = ShellInterop.GetWindowAppUserModelId(h);

        // Store apps are hosted by ApplicationFrameHost; the real app is a child CoreWindow in another process.
        if (exe is not null && exe.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
        {
            string? inner = null;
            Win32.EnumChildWindows(h, (child, _) =>
            {
                Win32.GetWindowThreadProcessId(child, out uint cpid);
                if (cpid != pid) { inner = Win32.GetProcessPath(cpid); return false; }
                return true;
            }, IntPtr.Zero);
            if (inner is not null) exe = inner;
            else if (aumid is null) return null; // a suspended frame with no app inside
        }

        string name = AppIdentity.NameFromExe(exe);
        return new AppIdentity(aumid, exe, name);
    }

    public TrackedWindow? Find(IntPtr h) => _known.TryGetValue(h, out var w) ? w : null;

    public void Activate(IntPtr window) => Win32.ActivateWindow(window);
    public void Minimize(IntPtr window) => Win32.ShowWindow(window, Win32.SW_MINIMIZE);
    public void Close(IntPtr window) => Win32.PostMessage(window, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    public bool IsMinimized(IntPtr window) => Win32.IsIconic(window);
}

/// <summary>Synthetic windows for previews and audit runs: no desktop access at all.</summary>
public sealed class FixtureWindowSource(IEnumerable<(AppIdentity App, string Title)> windows, int activeIndex = 0) : IWindowSource
{
    private readonly List<TrackedWindow> _windows = windows.Select((w, i) => new TrackedWindow
    {
        Handle = new IntPtr(0x10000 + i), App = w.App, Order = i, Title = w.Title,
    }).ToList();
    private bool _first = true;

    public IReadOnlyList<TrackedWindow> Windows => _windows;
    public IntPtr Foreground { get; private set; } = new(0x10000 + activeIndex);
    public bool Refresh() { bool f = _first; _first = false; return f; }
    public void Activate(IntPtr window) => Foreground = window;
    public void Minimize(IntPtr window) { var w = _windows.FirstOrDefault(x => x.Handle == window); if (w is not null) w.IsMinimized = true; }
    public void Close(IntPtr window) => _windows.RemoveAll(w => w.Handle == window);
    public bool IsMinimized(IntPtr window) => _windows.FirstOrDefault(x => x.Handle == window)?.IsMinimized ?? false;
}
