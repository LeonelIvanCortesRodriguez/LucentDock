using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Strata.Core;

public enum InstallResult
{
    Success = 0,
    NoSource = 1,
    StillRunning = 3,
    CopyFailed = 4,
    HashMismatch = 5,
    ShortcutsFailed = 6,
    LaunchFailed = 7,
    UninstallIncomplete = 8,
    HandedOff = 9,
}

/// <summary>File-system and shell operations the installer needs, so every failure path can be tested.</summary>
public interface IInstallerIO
{
    string? CurrentExe { get; }
    string InstallDir { get; }
    string StartMenuDir { get; }
    string TempDir { get; }
    bool FileExists(string path);
    bool DirectoryExists(string path);
    void CreateDirectory(string path);
    void CopyFile(string from, string to);
    string Sha256(string path);
    void DeleteDirectory(string path);
    void CreateShortcut(string lnk, string target, string args, string description);
    void Launch(string exe, string args);
    void SetRunKey(string? exe);
    bool RunKeyPresent();
    void RestoreTaskbar();
    void Sleep(TimeSpan t);
}

public sealed class RealInstallerIO : IInstallerIO
{
    public string? CurrentExe => Environment.ProcessPath;
    public string InstallDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Strata");
    public string StartMenuDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Strata");
    public string TempDir => Path.GetTempPath();
    public bool FileExists(string path) => File.Exists(path);
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public void CopyFile(string from, string to) => File.Copy(from, to, overwrite: true);

    public string Sha256(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s));
    }

    public void DeleteDirectory(string path) => Directory.Delete(path, recursive: true);

    public void CreateShortcut(string lnk, string target, string args, string description)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell is unavailable");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic link = shell.CreateShortcut(lnk);
            link.TargetPath = target;
            link.Arguments = args;
            link.Description = description;
            link.WorkingDirectory = Path.GetDirectoryName(target);
            link.IconLocation = target + ",0";
            link.Save();
            Marshal.FinalReleaseComObject(link);
        }
        finally { Marshal.FinalReleaseComObject(shell); }
        if (!File.Exists(lnk)) throw new IOException($"Shortcut was not created: {Path.GetFileName(lnk)}");
    }

    public void Launch(string exe, string args) =>
        Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true })?.Dispose();

    public void SetRunKey(string? exe)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        if (key is null) return;
        if (exe is null) key.DeleteValue("Strata", throwOnMissingValue: false);
        else key.SetValue("Strata", $"\"{exe}\"");
    }

    public bool RunKeyPresent()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        return key?.GetValue("Strata") is not null;
    }

    public void RestoreTaskbar() => TaskbarController.RestoreFromDisk();
    public void Sleep(TimeSpan t) => Thread.Sleep(t);
}

/// <summary>
/// Per-user install without admin rights: %LOCALAPPDATA%\Programs\Strata plus Start menu shortcuts.
/// Every step is verified; a failure returns a specific nonzero <see cref="InstallResult"/> and is logged
/// with what to do next. User data in the Strata data folder is kept unless --purge is given.
/// </summary>
public sealed class Installer(IInstallerIO io)
{
    public const string ShortcutMain = "Strata.lnk";
    public const string ShortcutRestore = "Strata - Restore Windows taskbar.lnk";
    public const string ShortcutUninstall = "Uninstall Strata.lnk";

    public string InstalledExe => Path.Combine(io.InstallDir, "Strata.exe");
    public List<string> Messages { get; } = [];

    public static Installer Default => new(new RealInstallerIO());

    public bool IsInstalledCopy => io.CurrentExe is { } p &&
        string.Equals(Path.GetFullPath(p), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);

    private InstallResult Report(InstallResult result, string message)
    {
        Messages.Add(message);
        Log.Write($"Installer: {result}: {message}");
        return result;
    }

    public InstallResult Install(Func<bool> stopRunning, bool launch = true)
    {
        string? source = io.CurrentExe;
        if (source is null) return Report(InstallResult.NoSource, "Could not find the running Strata.exe.");
        if (!stopRunning())
            return Report(InstallResult.StillRunning,
                "Strata is still running and did not exit. Close it from its tray icon (Exit Strata) and run the installer again.");

        try { io.CreateDirectory(io.InstallDir); }
        catch (Exception ex) { return Report(InstallResult.CopyFailed, $"Could not create {io.InstallDir}: {ex.Message}"); }

        if (!IsInstalledCopy)
        {
            Exception? last = null;
            for (int attempt = 0; attempt < 20; attempt++)
            {
                try { io.CopyFile(source, InstalledExe); last = null; break; }
                catch (IOException ex) { last = ex; io.Sleep(TimeSpan.FromMilliseconds(250)); } // file still locked by the old dock
                catch (UnauthorizedAccessException ex) { last = ex; break; }
            }
            if (last is not null)
                return Report(InstallResult.CopyFailed, $"Copying Strata.exe failed: {last.Message}. Nothing was launched.");

            string expected = io.Sha256(source);
            string actual = io.Sha256(InstalledExe);
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                return Report(InstallResult.HashMismatch, $"The installed copy does not match (expected {expected}, found {actual}). Nothing was launched.");
        }

        try
        {
            io.CreateDirectory(io.StartMenuDir);
            io.CreateShortcut(Path.Combine(io.StartMenuDir, ShortcutMain), InstalledExe, "", "Strata dock");
            io.CreateShortcut(Path.Combine(io.StartMenuDir, ShortcutRestore), InstalledExe, "--restore-taskbar",
                "Bring the Windows taskbar back if it stays hidden");
            io.CreateShortcut(Path.Combine(io.StartMenuDir, ShortcutUninstall), InstalledExe, "--uninstall", "Remove Strata");
        }
        catch (Exception ex)
        {
            return Report(InstallResult.ShortcutsFailed, $"Strata was copied, but Start menu shortcuts failed: {ex.Message}");
        }

        // Point "start with Windows" at the installed copy if it was on.
        if (io.RunKeyPresent()) io.SetRunKey(InstalledExe);

        if (launch)
        {
            try { io.Launch(InstalledExe, ""); }
            catch (Exception ex) { return Report(InstallResult.LaunchFailed, $"Installed, but starting Strata failed: {ex.Message}"); }
        }
        return Report(InstallResult.Success, $"Installed to {io.InstallDir} (SHA-256 {io.Sha256(InstalledExe)}).");
    }

    /// <summary>
    /// Remove the program, shortcuts and startup entry, then verify nothing is left. A running exe can't delete
    /// itself, so the installed copy hands the job to a temporary copy of itself first.
    /// </summary>
    public InstallResult Uninstall(Func<bool> stopRunning, bool purgeData, bool handoffAllowed = true)
    {
        if (!stopRunning())
            return Report(InstallResult.StillRunning,
                "Strata is still running and did not exit. Close it from its tray icon and try again.");

        if (IsInstalledCopy && handoffAllowed && io.CurrentExe is { } self)
        {
            string temp = Path.Combine(io.TempDir, $"Strata-uninstall-{Guid.NewGuid():N}.exe");
            try
            {
                io.CopyFile(self, temp);
                io.Launch(temp, "--uninstall --handoff" + (purgeData ? " --purge" : ""));
                return Report(InstallResult.HandedOff, "Uninstall continues in a temporary copy.");
            }
            catch (Exception ex)
            {
                return Report(InstallResult.UninstallIncomplete, $"Could not start the temporary uninstaller: {ex.Message}");
            }
        }

        io.RestoreTaskbar();
        var problems = new List<string>();
        try { io.SetRunKey(null); } catch (Exception ex) { problems.Add($"startup entry: {ex.Message}"); }
        TryDelete(io.StartMenuDir, problems);
        if (purgeData) TryDelete(Storage.Root, problems);

        // The old dock may take a moment to release its exe after it exits.
        for (int attempt = 0; attempt < 20 && io.DirectoryExists(io.InstallDir); attempt++)
        {
            try { io.DeleteDirectory(io.InstallDir); }
            catch (IOException) { io.Sleep(TimeSpan.FromMilliseconds(250)); }
            catch (UnauthorizedAccessException) { io.Sleep(TimeSpan.FromMilliseconds(250)); }
        }

        if (io.DirectoryExists(io.InstallDir)) problems.Add($"{io.InstallDir} still exists");
        if (io.DirectoryExists(io.StartMenuDir)) problems.Add("Start menu shortcuts still exist");
        if (io.RunKeyPresent()) problems.Add("startup entry still present");
        return problems.Count > 0
            ? Report(InstallResult.UninstallIncomplete, "Uninstall incomplete: " + string.Join("; ", problems))
            : Report(InstallResult.Success, purgeData ? "Strata and its data were removed." : "Strata was removed. Your data was kept.");
    }

    private void TryDelete(string dir, List<string> problems)
    {
        try { if (io.DirectoryExists(dir)) io.DeleteDirectory(dir); }
        catch (Exception ex) { problems.Add($"{Path.GetFileName(dir)}: {ex.Message}"); }
    }
}
