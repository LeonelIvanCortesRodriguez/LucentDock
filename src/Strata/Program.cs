using Strata.Core;

namespace Strata;

public static class Program
{
    public const string ExitEventName = @"Local\Strata.Dock.Exit";
    public const string SettingsEventName = @"Local\Strata.Dock.OpenSettings";

    [STAThread]
    public static int Main(string[] args)
    {
        bool Has(string flag) => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

        // Escape hatch: put the Windows taskbar back without starting the dock.
        if (Has("--restore-taskbar"))
        {
            return TaskbarController.RestoreFromDisk() ? 0 : 2;
        }

        // Ask a running Strata to quit cleanly (it restores the taskbar itself).
        if (Has("--exit"))
        {
            return Signal(ExitEventName) ? 0 : 1;
        }

        // Claude Code's status-line command (Settings > AI usage > Connect): record Claude's quota windows and print a line.
        if (Has(Quota.ClaudeStatusLine.BridgeFlag))
        {
            int rootIndex = Array.FindIndex(args, a => a.Equals("--data-root", StringComparison.OrdinalIgnoreCase));
            if (rootIndex >= 0 && rootIndex + 1 < args.Length) Storage.Configure(args[rootIndex + 1]);
            using var stdin = new StreamReader(Console.OpenStandardInput(), System.Text.Encoding.UTF8);
            using var stdout = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false));
            return Quota.ClaudeStatusLine.RunBridge(stdin, stdout, () => DateTimeOffset.Now);
        }

        // The same as Settings > AI usage > Connect / Disconnect, for scripts (tools\claude-terminal.ps1).
        // Exit codes: 0 done, 3 another status line is set, 4 Claude Code's settings file can't be read.
        if (Has("--connect-claude") || Has("--disconnect-claude"))
        {
            int rootIndex = Array.FindIndex(args, a => a.Equals("--data-root", StringComparison.OrdinalIgnoreCase));
            string? root = rootIndex >= 0 && rootIndex + 1 < args.Length ? Path.GetFullPath(args[rootIndex + 1]) : null;
            var connector = Quota.ClaudeCodeConnector.ForCurrentUser();
            using var stdout = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false));
            var before = connector.Status();
            bool ok = Has("--connect-claude")
                ? connector.Connect(Quota.ClaudeCodeConnector.BridgeCommand(Environment.ProcessPath ?? "Strata.exe", root))
                : connector.Disconnect();
            var after = connector.Status();
            stdout.WriteLine($"Claude Code status line: {before} -> {after} ({connector.SettingsPath})");
            return ok ? 0 : after == Quota.ClaudeConnection.OtherStatusLine ? 3 : 4;
        }

        if (Has("--install")) return ExitCode(Installer.Default.Install(StopRunningInstance));
        if (Has("--apply-update")) return (int)UpdateTransaction.Default.Apply(StopRunningInstance);
        if (Has("--uninstall")) return ExitCode(Installer.Default.Uninstall(StopRunningInstance, purgeData: Has("--purge"),
            handoffAllowed: !Has("--handoff")));

        if (Has("--recover-update"))
        {
            int index = Array.FindIndex(args, a => a.Equals("--recover-update", StringComparison.OrdinalIgnoreCase));
            if (index < 0 || index + 1 >= args.Length || !int.TryParse(args[index + 1], out int parentId) || parentId <= 0)
                return (int)UpdateInstallResult.InvalidJournal;
            try
            {
                using var parent = System.Diagnostics.Process.GetProcessById(parentId);
                if (!parent.WaitForExit(15000)) { TaskbarController.RestoreFromDisk(); return (int)UpdateInstallResult.RollbackFailed; }
            }
            catch (ArgumentException) { } // The parent has already exited, which is the required state.
            return (int)UpdateTransaction.Default.RecoverPending();
        }

        if (Has("--cleanup-update-stage"))
        {
            int index = Array.FindIndex(args, a => a.Equals("--cleanup-update-stage", StringComparison.OrdinalIgnoreCase));
            if (index < 0 || index + 2 >= args.Length || !int.TryParse(args[index + 1], out int parentId)) return 2;
            return Updater.RunStageCleanup(parentId, args[index + 2]) ? 0 : 2;
        }

        // Render every widget, flyout and settings page to PNG files without touching the desktop.
        int previewIndex = Array.FindIndex(args, a => a.Equals("--render-previews", StringComparison.OrdinalIgnoreCase));
        if (previewIndex >= 0)
        {
            string dir = previewIndex + 1 < args.Length ? args[previewIndex + 1] : Path.Combine(Environment.CurrentDirectory, "previews");
            return new App { PreviewDirectory = dir }.Run();
        }

        Updater.ScavengeStaleStages(Path.GetTempPath(), DateTime.UtcNow);

        int healthIndex = Array.FindIndex(args, a => a.Equals("--update-health", StringComparison.OrdinalIgnoreCase));
        string? healthToken = healthIndex >= 0 && healthIndex + 1 < args.Length ? args[healthIndex + 1] : null;
        var update = UpdateTransaction.Default;
        if (update.HasPending && !update.PendingTokenMatches(healthToken))
            return (int)update.StartPendingRecovery(Environment.ProcessId);

        using var mutex = new Mutex(true, @"Local\Strata.Dock.SingleInstance", out bool first);
        if (!first)
        {
            // Already running: starting it again opens Settings, like most tray apps.
            Signal(SettingsEventName);
            return 0;
        }

        return new App { UpdateHealthToken = healthToken }.Run();
    }

    private static int ExitCode(InstallResult result) =>
        result is InstallResult.Success or InstallResult.HandedOff ? 0 : (int)result;

    /// <summary>Ask a running dock to exit and wait (up to 10 s) until it has let go of the single-instance lock.</summary>
    private static bool StopRunningInstance()
    {
        if (!Signal(ExitEventName)) return true; // nothing running
        for (int i = 0; i < 40; i++)
        {
            try
            {
                using var probe = new Mutex(false, @"Local\Strata.Dock.SingleInstance");
                if (probe.WaitOne(0)) { probe.ReleaseMutex(); return true; }
            }
            catch (AbandonedMutexException) { return true; }
            Thread.Sleep(250);
        }
        return false;
    }

    private static bool Signal(string name)
    {
        try
        {
            using var handle = EventWaitHandle.OpenExisting(name);
            return handle.Set();
        }
        catch (WaitHandleCannotBeOpenedException) { return false; }
    }
}
