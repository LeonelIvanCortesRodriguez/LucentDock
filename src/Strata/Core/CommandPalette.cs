using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using Strata.Native;
using Strata.Smart;

namespace Strata.Core;

public enum PaletteCommandKind
{
    Application,
    Window,
    Setting,
    Widget,
    Action,
    Automation,
    RecentFile,
}

/// <summary>A typed palette operation. User text is used only for ranking and is never interpreted as a command.</summary>
public sealed record PaletteCommand(
    string Id,
    string Title,
    string Subtitle,
    string Keywords,
    PaletteCommandKind Kind,
    Action Execute,
    string? Confirmation = null);

public sealed record PaletteMatch(PaletteCommand Command, int Score);

public static class PaletteMatcher
{
    public static IReadOnlyList<PaletteMatch> Search(IEnumerable<PaletteCommand> commands, string query, int limit = 12)
    {
        string[] terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant()).ToArray();

        return commands.Select(command => (Command: command, Score: Score(command, terms)))
            .Where(x => x.Score >= 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Command.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(Math.Max(0, limit))
            .Select(x => new PaletteMatch(x.Command, x.Score))
            .ToList();
    }

    private static int Score(PaletteCommand command, IReadOnlyList<string> terms)
    {
        if (terms.Count == 0) return KindPriority(command.Kind);
        string title = command.Title.ToLowerInvariant();
        string subtitle = command.Subtitle.ToLowerInvariant();
        string searchable = title + " " + subtitle + " " + command.Keywords.ToLowerInvariant();
        int total = KindPriority(command.Kind);

        foreach (string term in terms)
        {
            int best = TextScore(title, term, titleWeight: true);
            best = Math.Max(best, TextScore(searchable, term, titleWeight: false));
            if (best < 0) return -1;
            total += best;
        }
        return total;
    }

    private static int TextScore(string text, string term, bool titleWeight)
    {
        if (text == term) return titleWeight ? 1000 : 620;
        if (text.StartsWith(term, StringComparison.Ordinal)) return titleWeight ? 800 : 500;
        int word = text.IndexOf(" " + term, StringComparison.Ordinal);
        if (word >= 0) return titleWeight ? 650 : 420;
        int contiguous = text.IndexOf(term, StringComparison.Ordinal);
        if (contiguous >= 0) return (titleWeight ? 520 : 300) - Math.Min(contiguous, 80);

        int ti = 0, gap = 0, first = -1, last = -1;
        for (int i = 0; i < text.Length && ti < term.Length; i++)
        {
            if (text[i] != term[ti]) continue;
            if (first < 0) first = i;
            if (last >= 0) gap += i - last - 1;
            last = i;
            ti++;
        }
        if (ti != term.Length) return -1;
        return (titleWeight ? 350 : 180) - Math.Min(gap * 3 + first, 150);
    }

    private static int KindPriority(PaletteCommandKind kind) => kind switch
    {
        PaletteCommandKind.Window => 70,
        PaletteCommandKind.Application => 60,
        PaletteCommandKind.Action => 50,
        PaletteCommandKind.Setting => 40,
        PaletteCommandKind.Widget => 30,
        PaletteCommandKind.Automation => 20,
        _ => 10,
    };
}

/// <summary>Reads launchable .lnk entries from the user's and machine's Start Menu without executing them.</summary>
public sealed class StartMenuApplicationSource
{
    private readonly IReadOnlyList<string> _roots;
    private readonly Func<string, PinnedApp?> _resolve;
    private readonly Func<IReadOnlyList<PinnedApp>> _shellApps;

    public StartMenuApplicationSource(IReadOnlyList<string>? roots = null, Func<string, PinnedApp?>? resolve = null,
        Func<IReadOnlyList<PinnedApp>>? shellApps = null)
    {
        _roots = roots ??
        [
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
        ];
        _resolve = resolve ?? Launcher.FromShortcut;
        _shellApps = shellApps ?? EnumerateShellApps;
    }

    public IReadOnlyList<PinnedApp> Enumerate()
    {
        var apps = new List<PinnedApp>();
        try { apps.AddRange(_shellApps()); }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException) { }
        foreach (string root in _roots.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                };
                foreach (string shortcut in Directory.EnumerateFiles(root, "*.lnk", options))
                    if (_resolve(shortcut) is { } app && IsWithin(root, shortcut)) apps.Add(app);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return apps.GroupBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => g.First()).OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Documented AppsFolder enumeration: names plus AUMIDs for every app Windows exposes in Start.</summary>
    private static IReadOnlyList<PinnedApp> EnumerateShellApps()
    {
        const string appsFolder = "shell:::{4234d49b-0245-4df3-b780-3893943456e1}";
        object? shellObject = null, folderObject = null, itemsObject = null;
        var result = new List<PinnedApp>();
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type is null) return result;
            shellObject = Activator.CreateInstance(type);
            if (shellObject is null) return result;
            dynamic shell = shellObject;
            folderObject = shell.NameSpace(appsFolder);
            if (folderObject is null) return result;
            dynamic folder = folderObject;
            itemsObject = folder.Items();
            dynamic items = itemsObject;
            int count = items.Count;
            for (int i = 0; i < count; i++)
            {
                object? itemObject = null;
                try
                {
                    itemObject = items.Item(i);
                    if (itemObject is null) continue;
                    dynamic item = itemObject;
                    string name = item.Name ?? "";
                    string aumid = item.Path ?? "";
                    if (name.Length > 0 && aumid.Length > 0)
                        result.Add(new PinnedApp { Name = name, Aumid = aumid, Path = "" });
                }
                catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException) { }
                finally { Release(itemObject); }
            }
            return result;
        }
        finally
        {
            Release(itemsObject);
            Release(folderObject);
            Release(shellObject);
        }
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }

    private static bool IsWithin(string root, string file)
    {
        string canonicalRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string canonicalFile = Path.GetFullPath(file);
        return canonicalFile.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase);
    }

    public static string OpaqueId(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }
}

/// <summary>Win+Alt+Space opens the palette. Failure is urgent and never silently steals another app's gesture.</summary>
public sealed class CommandPaletteHotkey(Func<uint, uint, bool> register, NotificationCenter? notifications)
{
    public const string Gesture = "Win+Alt+Space";
    public const uint Modifiers = Win32.MOD_WIN | Win32.MOD_ALT | Win32.MOD_NOREPEAT;
    public const uint VirtualKey = 0x20;
    public const string WarningKey = "command-palette-hotkey-unavailable";
    public const string WarningTitle = "Strata command palette shortcut unavailable";
    public const string WarningBody = Gesture + " is already used by Windows or another app. Open the palette from Strata's dock menu instead.";

    public bool? Registered { get; private set; }

    public bool Register()
    {
        try { Registered = register(Modifiers, VirtualKey); }
        catch (Exception ex) { Log.Write($"Registering {Gesture} threw: {ex.Message}"); Registered = false; }
        if (Registered != true)
        {
            Log.Write($"Could not register {Gesture} command palette shortcut.");
            notifications?.Send(WarningKey, WarningTitle, WarningBody, "command-palette", TimeSpan.FromHours(12), urgent: true);
        }
        return Registered == true;
    }
}
