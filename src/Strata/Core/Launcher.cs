using System.Diagnostics;
using Strata.Native;

namespace Strata.Core;

public static class Launcher
{
    private static readonly string TaskbarPins = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");

    /// <summary>First run: copy the apps already pinned to the Windows taskbar.</summary>
    public static List<PinnedApp> ImportWindowsTaskbarPins()
    {
        var pins = new List<PinnedApp>();
        if (!Directory.Exists(TaskbarPins)) return pins;
        foreach (var lnk in Directory.GetFiles(TaskbarPins, "*.lnk").OrderBy(File.GetCreationTime))
        {
            var pin = FromShortcut(lnk);
            if (pin is not null) pins.Add(pin);
        }
        return pins;
    }

    public static PinnedApp? FromShortcut(string lnk)
    {
        var resolved = ShellInterop.ResolveShortcut(lnk);
        string target = resolved?.Target ?? "";
        return new PinnedApp
        {
            Name = Path.GetFileNameWithoutExtension(lnk),
            Path = lnk,
            ExePath = target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? target : null,
            IdentityKind = ExplorerPinIdentity.FromShellTarget(ShellInterop.GetShortcutShellTarget(lnk)),
        };
    }

    /// <summary>Upgrade legacy Explorer pins in memory. Loading settings never writes them back by itself.</summary>
    public static void RefreshShellIdentities(IEnumerable<PinnedApp> pins)
    {
        foreach (var pin in pins)
        {
            if (pin.IdentityKind != AppIdentityKind.Standard || pin.Aumid is not null) continue;
            string? executable = pin.ExePath;
            if (executable is null && pin.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) executable = pin.Path;
            AppIdentityKind kind = ExplorerPinIdentity.FromExecutable(executable);
            if (kind == AppIdentityKind.Standard && pin.Path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                kind = ExplorerPinIdentity.FromShellTarget(ShellInterop.GetShortcutShellTarget(pin.Path));
            pin.IdentityKind = kind;
        }
    }

    public static PinnedApp FromFile(string path)
    {
        if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && FromShortcut(path) is { } fromLink) return fromLink;
        bool isExe = path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        return new PinnedApp
        {
            Name = isExe ? AppIdentity.NameFromExe(path) : Path.GetFileName(path.TrimEnd('\\')),
            Path = path,
            ExePath = isExe ? path : null,
        };
    }

    public static PinnedApp FromRunning(AppIdentity app) => new()
    {
        Name = app.DisplayName,
        Path = app.Aumid is null ? app.ExePath ?? "" : "",
        Aumid = app.Aumid,
        ExePath = app.ExePath,
        IdentityKind = app.IdentityKind,
    };

    public static void Launch(PinnedApp pin)
    {
        try
        {
            if (!string.IsNullOrEmpty(pin.Path))
                Start(pin.Path, pin.Arguments);
            else if (pin.Aumid is not null)
                Start("explorer.exe", @"shell:AppsFolder\" + pin.Aumid);
        }
        catch (Exception ex) { Log.Write($"Launch failed for {pin.Name}: {ex.Message}"); }
    }

    /// <summary>Start another instance of a running app.</summary>
    public static void LaunchNew(AppIdentity app)
    {
        try
        {
            if (app.Aumid is not null && (app.ExePath is null || app.ExePath.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase)))
                Start("explorer.exe", @"shell:AppsFolder\" + app.Aumid);
            else if (app.ExePath is not null)
                Start(app.ExePath, "");
        }
        catch (Exception ex) { Log.Write($"Launch failed for {app.DisplayName}: {ex.Message}"); }
    }

    public enum OpenKind { None, Exe, Packaged, Shortcut }

    /// <summary>How files dropped on (or picked from the menu of) a dock app open with it: its executable with the
    /// paths as arguments; a Store app by its AppUserModelID; or, failing both, its shortcut with the paths.</summary>
    public static (OpenKind Kind, string Target) OpenTarget(PinnedApp? pin, AppIdentity? app)
    {
        string? exe = pin?.ExePath ?? (pin?.Path is { } p && p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p : null) ?? app?.ExePath;
        bool packaged = exe is not null && exe.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);
        string? aumid = pin?.Aumid ?? app?.Aumid;
        if (exe is not null && !packaged) return (OpenKind.Exe, exe);
        if (aumid is not null) return (OpenKind.Packaged, aumid);
        if (pin?.Path is { Length: > 0 } lnk && lnk.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return (OpenKind.Shortcut, lnk);
        return (OpenKind.None, "");
    }

    /// <summary>Paths as one command line, each quoted. A trailing backslash is doubled so it can't escape the quote.</summary>
    public static string CommandLineFor(IEnumerable<string> files) =>
        string.Join(" ", files.Select(f => "\"" + (f.EndsWith('\\') ? f + "\\" : f) + "\""));

    /// <summary>Open <paramref name="files"/> with this dock app. False if it can't take files.</summary>
    public static bool OpenWith(PinnedApp? pin, AppIdentity? app, IReadOnlyList<string> files)
    {
        if (files.Count == 0) return false;
        var (kind, target) = OpenTarget(pin, app);
        try
        {
            switch (kind)
            {
                case OpenKind.Exe:
                case OpenKind.Shortcut:
                    Start(target, CommandLineFor(files));
                    return true;
                case OpenKind.Packaged:
                    return ShellDocuments.ActivateForFiles(target, files);
                default:
                    return false;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Opening {files.Count} file(s) with {pin?.Name ?? app?.DisplayName} failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>The IDs an app's jump list may be under, most specific first: its own AppUserModelID (from its
    /// windows or pin), the one its shortcut declares, then the implicit one Windows derives from its path.</summary>
    public static IEnumerable<string> RecentAppIds(PinnedApp? pin, AppIdentity? app)
    {
        if (app?.Aumid is { } a) yield return a;
        if (pin?.Aumid is { } b) yield return b;
        if (ShellDocuments.ShortcutAppId(pin?.Path) is { } c) yield return c;
        string? exe = pin?.ExePath ?? app?.ExePath;
        if (ShellDocuments.ImplicitAppId(exe, ShellDocuments.KnownFolders()) is { } d) yield return d;
        if (exe is not null) yield return exe;
    }

    public static void Start(string file, string args)
    {
        Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true })?.Dispose();
    }

    public static void OpenStartMenu() => Win32.PressKeys(Win32.VK_LWIN);
    public static void OpenSearch() => Win32.PressKeys(Win32.VK_LWIN, (byte)'S');
}
