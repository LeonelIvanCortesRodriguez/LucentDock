using System.Runtime.InteropServices;
using Strata.Native;

namespace Strata.Core;

/// <summary>Canonical identity shared by Explorer folder windows and the shell-only taskbar pin.</summary>
public static class ExplorerPinIdentity
{
    public const string Key = "shell:explorer-folder";
    private const string ExplorerHome = "::{52205FD8-5DFB-447D-801A-D0B52F2E83E1}";

    public static AppIdentityKind FromShellTarget(string? parsingName) =>
        string.Equals(parsingName, ExplorerHome, StringComparison.OrdinalIgnoreCase)
            ? AppIdentityKind.ExplorerFolder : AppIdentityKind.Standard;

    /// <summary>
    /// Upgrade pins saved by older Strata builds from the actual system executable identity. This deliberately
    /// does not use the localized/displayed pin name and does not classify an unrelated explorer.exe elsewhere.
    /// </summary>
    public static AppIdentityKind FromExecutable(string? executable, string? windowsDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(executable)) return AppIdentityKind.Standard;
        windowsDirectory ??= Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        try
        {
            string candidate = Path.GetFullPath(Environment.ExpandEnvironmentVariables(executable));
            string systemExplorer = Path.GetFullPath(Path.Combine(windowsDirectory, "explorer.exe"));
            return string.Equals(candidate, systemExplorer, StringComparison.OrdinalIgnoreCase)
                ? AppIdentityKind.ExplorerFolder : AppIdentityKind.Standard;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return AppIdentityKind.Standard;
        }
    }
}

/// <summary>Documented Shell Windows enumeration, isolated so tests never require a live Explorer process.</summary>
public sealed record ExplorerFolderWindowSnapshot(bool Available, IReadOnlyCollection<IntPtr> Windows);

public interface IExplorerFolderWindowSource
{
    ExplorerFolderWindowSnapshot Enumerate();
}

public sealed class ShellExplorerFolderWindowSource : IExplorerFolderWindowSource
{
    private static readonly Guid ShellWindowsClass = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private readonly Func<object?> _create;

    public ShellExplorerFolderWindowSource(Func<object?>? create = null) => _create = create ?? (() =>
    {
        Type? type = Type.GetTypeFromCLSID(ShellWindowsClass, throwOnError: false);
        return type is null ? null : Activator.CreateInstance(type);
    });

    public ExplorerFolderWindowSnapshot Enumerate()
    {
        if (PrivacyGuard.PreviewActive) return new(true, []);
        object? shellWindows = null;
        var result = new HashSet<IntPtr>();
        bool available = true;
        try
        {
            if (_create() is not { } instance) return new(false, []);
            shellWindows = instance;
            dynamic windows = instance;
            int count = windows.Count;
            for (int i = 0; i < count; i++)
            {
                object? item = null;
                try
                {
                    item = windows.Item(i);
                    // A registered shell window that reads back as nothing, or reports no handle, is a window this
                    // snapshot does not have. That is the same rule as the catch below, and for the same reason:
                    // counting it as "nothing is open" would let the dock report no folder window while one is open,
                    // and a normal click would then open a second one beside it.
                    if (item is null) { available = false; continue; }
                    dynamic browser = item;
                    long raw = browser.HWND;
                    if (raw != 0) { result.Add(new IntPtr(raw)); } else { available = false; }
                }
                catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException)
                {
                    // A skipped item could be the only live folder window. A partial snapshot is useful, but never
                    // authoritative: retain validated prior HWNDs and suppress zero-window normal-click launch.
                    available = false;
                }
                finally { if (item is not null && Marshal.IsComObject(item)) Marshal.FinalReleaseComObject(item); }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException)
        {
            available = false;
            // Mark the snapshot unavailable so the tracker retains validated prior folder HWNDs and the dock never
            // mistakes uncertainty for zero windows. Never log folder titles or paths.
            Log.Write($"Explorer folder enumeration unavailable ({ex.GetType().Name}).");
        }
        finally { if (shellWindows is not null && Marshal.IsComObject(shellWindows)) Marshal.FinalReleaseComObject(shellWindows); }
        return new(available, result);
    }
}

/// <summary>Top-level facts needed by the tracker. Production uses Win32; tests inject deterministic windows.</summary>
public interface ITopLevelWindowSystem
{
    IReadOnlyList<IntPtr> Enumerate();
    bool IsVisible(IntPtr window);
    long ExStyle(IntPtr window);
    IntPtr Owner(IntPtr window);
    int TitleLength(IntPtr window);
    bool IsCloaked(IntPtr window);
    string ClassName(IntPtr window);
    uint ProcessId(IntPtr window);
    string Title(IntPtr window);
    bool IsMinimized(IntPtr window);
}

public sealed class Win32TopLevelWindowSystem : ITopLevelWindowSystem
{
    public IReadOnlyList<IntPtr> Enumerate()
    {
        var windows = new List<IntPtr>();
        Win32.EnumWindows((window, _) => { windows.Add(window); return true; }, IntPtr.Zero);
        return windows;
    }

    public bool IsVisible(IntPtr window) => Win32.IsWindowVisible(window);
    public long ExStyle(IntPtr window) => Win32.GetExStyle(window);
    public IntPtr Owner(IntPtr window) => Win32.GetWindow(window, Win32.GW_OWNER);
    public int TitleLength(IntPtr window) => Win32.GetWindowTextLength(window);
    public bool IsCloaked(IntPtr window) => Win32.IsCloaked(window);
    public string ClassName(IntPtr window) => Win32.GetClassNameOf(window);
    public uint ProcessId(IntPtr window) { Win32.GetWindowThreadProcessId(window, out uint pid); return pid; }
    public string Title(IntPtr window) => Win32.GetWindowTitle(window);
    public bool IsMinimized(IntPtr window) => Win32.IsIconic(window);
}

public static class ExplorerFolderWindowPolicy
{
    private static readonly HashSet<string> FolderClasses = new(StringComparer.Ordinal)
    {
        "CabinetWClass", "ExploreWClass",
    };

    /// <summary>IShellWindows membership plus native class/process facts prevents shell surfaces and dialogs matching.</summary>
    public static bool IsExplorerProcess(string? executable) =>
        ExplorerPinIdentity.FromExecutable(executable) == AppIdentityKind.ExplorerFolder;

    public static bool IsEligible(bool inShellWindows, string windowClass, string? executable) =>
        inShellWindows && FolderClasses.Contains(windowClass) && IsExplorerProcess(executable);
}
