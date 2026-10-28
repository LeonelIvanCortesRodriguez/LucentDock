using System.Security.Cryptography;
using System.Text.Json;

namespace Strata.Core;

public enum UpdateInstallResult
{
    Success = 0,
    NoSource = 20,
    NoInstalledCopy = 21,
    StillRunning = 22,
    StageCopyFailed = 23,
    StageHashMismatch = 24,
    BackupFailed = 25,
    JournalFailed = 26,
    ReplaceFailed = 27,
    LaunchFailed = 28,
    HealthTimeout = 29,
    RolledBack = 30,
    RollbackFailed = 31,
    NoPendingUpdate = 32,
    RecoveryStarted = 33,
    InvalidJournal = 34,
    PreflightFailed = 35,
}

public sealed record PendingUpdate(string Token, string PreviousSha256, string CandidateSha256, DateTimeOffset StartedUtc);

public interface IUpdateTransactionIO
{
    string? CurrentExe { get; }
    string InstallDir { get; }
    string InstalledExe { get; }
    string IncomingExe { get; }
    string RollbackExe { get; }
    string RecoveryExe { get; }
    string JournalPath { get; }
    bool FileExists(string path);
    void CreateDirectory(string path);
    void CopyFile(string from, string to, bool overwrite);
    void ReplaceFile(string source, string destination);
    void DeleteFile(string path);
    string Sha256(string path);
    void WriteJournal(PendingUpdate pending);
    PendingUpdate? ReadJournal();
    void Launch(string exe, string args);
    void RestoreTaskbar();
    void Sleep(TimeSpan time);
    void ScheduleStageCleanup() { }
}

public sealed class RealUpdateTransactionIO : IUpdateTransactionIO
{
    public string? CurrentExe => Environment.ProcessPath;
    public string InstallDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Strata");
    public string InstalledExe => Path.Combine(InstallDir, "Strata.exe");
    public string IncomingExe => Path.Combine(InstallDir, "Strata.update-incoming.exe");
    public string RollbackExe => Path.Combine(InstallDir, "Strata.last-known-good.exe");
    public string RecoveryExe => Path.Combine(InstallDir, "Strata.update-recovery.exe");
    public string JournalPath => Path.Combine(InstallDir, "update-pending.json");
    public bool FileExists(string path) => File.Exists(path);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public void CopyFile(string from, string to, bool overwrite) => File.Copy(from, to, overwrite);
    public void ReplaceFile(string source, string destination)
    {
        if (File.Exists(destination)) File.Replace(source, destination, null, ignoreMetadataErrors: true);
        else File.Move(source, destination);
    }
    public void DeleteFile(string path) { if (File.Exists(path)) File.Delete(path); }
    public string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    public void WriteJournal(PendingUpdate pending)
    {
        DurableFile.WriteAllTextAtomic(JournalPath, JsonSerializer.Serialize(pending, Storage.Json));
    }
    public PendingUpdate? ReadJournal() => JsonSerializer.Deserialize<PendingUpdate>(File.ReadAllText(JournalPath), Storage.Json);
    public void Launch(string exe, string args) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, args) { UseShellExecute = false })?.Dispose();
    public void RestoreTaskbar() => TaskbarController.RestoreFromDisk();
    public void Sleep(TimeSpan time) => Thread.Sleep(time);
    public void ScheduleStageCleanup()
    {
        string? source = CurrentExe;
        string? stage = source is null ? null : Path.GetDirectoryName(source);
        if (stage is null || !Updater.IsOwnedStageDirectory(stage, Path.GetTempPath())) return;
        try { Launch(InstalledExe, $"--cleanup-update-stage {Environment.ProcessId} \"{stage}\""); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Log.Write("Update stage cleanup handoff could not start; bounded next-start cleanup remains available.");
        }
    }
}

/// <summary>
/// A fail-closed, bounded update transaction. The old executable is copied and verified before atomic replacement.
/// It remains available after success and is restored after a launch or readiness failure.
/// </summary>
public sealed class UpdateTransaction(IUpdateTransactionIO io)
{
    public const int HealthAttempts = 120;
    public static readonly TimeSpan HealthPollInterval = TimeSpan.FromMilliseconds(250);
    public static UpdateTransaction Default => new(new RealUpdateTransactionIO());
    public bool HasPending => io.FileExists(io.JournalPath);
    public List<string> Messages { get; } = [];

    private UpdateInstallResult Report(UpdateInstallResult result, string message)
    {
        Messages.Add(message);
        Log.Write($"Update transaction: {result}: {message}");
        // When Apply runs from a verified temp stage, hand its directory to the installed executable. The helper
        // waits for this mapped process to exit before deleting; non-stage recovery helpers safely no-op here.
        io.ScheduleStageCleanup();
        return result;
    }

    public UpdateInstallResult Apply(Func<bool> stopRunning)
    {
        string? source = io.CurrentExe;
        if (source is null || !io.FileExists(source))
            return Report(UpdateInstallResult.NoSource, "The staged update executable is unavailable; the installed copy was not changed.");
        if (!io.FileExists(io.InstalledExe))
            return Report(UpdateInstallResult.NoInstalledCopy, "There is no installed copy to update; use Install instead.");
        string candidateHash, previousHash;
        try
        {
            // Do every fallible disk/hash preflight before stopping the healthy installed dock.
            io.CreateDirectory(io.InstallDir);
            candidateHash = io.Sha256(source);
            previousHash = io.Sha256(io.InstalledExe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Report(UpdateInstallResult.PreflightFailed,
                "The update preflight could not read or prepare the install location; the running and installed copies were not changed.");
        }
        if (!stopRunning())
            return Report(UpdateInstallResult.StillRunning, "The running dock did not exit; the installed copy was not changed.");
        try
        {
            io.DeleteFile(io.IncomingExe);
            io.CopyFile(source, io.IncomingExe, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return AbortBeforeReplacement(UpdateInstallResult.StageCopyFailed,
                "The update could not be staged (for example, disk space or access was unavailable). The installed copy was not changed.");
        }
        try
        {
            if (!HashEquals(candidateHash, io.Sha256(io.IncomingExe)))
                return AbortBeforeReplacement(UpdateInstallResult.StageHashMismatch,
                    "The local staged copy failed its SHA-256 check; the installed copy was not changed.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return AbortBeforeReplacement(UpdateInstallResult.StageCopyFailed,
                "The local staged copy could not be read for SHA-256 verification; the installed copy was not changed.");
        }

        try
        {
            io.CopyFile(io.InstalledExe, io.RollbackExe, overwrite: true);
            if (!HashEquals(previousHash, io.Sha256(io.RollbackExe))) throw new IOException("rollback hash mismatch");
            // Keep a candidate helper outside InstalledExe. If power is lost after replacement, the installed
            // image cannot safely overwrite itself when Windows maps it on the next start.
            io.CopyFile(source, io.RecoveryExe, overwrite: true);
            if (!HashEquals(candidateHash, io.Sha256(io.RecoveryExe))) throw new IOException("recovery helper hash mismatch");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return AbortBeforeReplacement(UpdateInstallResult.BackupFailed,
                "A verified last-known-good copy could not be created; the installed copy was not changed.");
        }

        string token = Guid.NewGuid().ToString("N");
        try { io.WriteJournal(new(token, previousHash, candidateHash, DateTimeOffset.UtcNow)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return AbortBeforeReplacement(UpdateInstallResult.JournalFailed,
                "The durable update journal could not be written; the installed copy was not changed.");
        }
        try
        {
            io.ReplaceFile(io.IncomingExe, io.InstalledExe);
            if (!HashEquals(candidateHash, io.Sha256(io.InstalledExe))) throw new IOException("installed hash mismatch");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return RollBack(stopRunning, previousHash, token, UpdateInstallResult.ReplaceFailed,
                "The atomic replacement was interrupted or failed.");
        }

        try { io.Launch(io.InstalledExe, $"--update-health {token}"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return RollBack(stopRunning, previousHash, token, UpdateInstallResult.LaunchFailed, "The updated dock could not start.");
        }

        string health = UpdateHealth.MarkerPath(token);
        for (int attempt = 0; attempt < HealthAttempts; attempt++)
        {
            if (io.FileExists(health))
            {
                try { io.DeleteFile(io.JournalPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Keep the health marker with the pending journal. The already-running healthy candidate may
                    // continue, and the next bounded recovery can finalize without a false rollback.
                    return Report(UpdateInstallResult.Success,
                        "The updated dock passed its health check; durable finalization remains pending for the next start.");
                }
                TryDeleteAfterFinalization(health);
                TryDeleteAfterFinalization(io.RecoveryExe);
                return Report(UpdateInstallResult.Success, "The updated dock passed its bounded startup health check; the last-known-good copy was retained.");
            }
            io.Sleep(HealthPollInterval);
        }
        return RollBack(stopRunning, previousHash, token, UpdateInstallResult.HealthTimeout,
            "The updated dock did not report healthy before the bounded timeout.");
    }

    private UpdateInstallResult AbortBeforeReplacement(UpdateInstallResult cause, string reason)
    {
        // stopRunning has already succeeded, but InstalledExe has not been replaced. Cleanup must never
        // prevent us from restoring the shell or restarting that unchanged known-good executable.
        try { io.DeleteFile(io.IncomingExe); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        try { io.RestoreTaskbar(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }

        try
        {
            io.Launch(io.InstalledExe, "");
            return Report(cause, $"{reason} The unchanged installed copy was restarted.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return Report(UpdateInstallResult.LaunchFailed,
                $"{reason} The unchanged installed copy could not be restarted; the Windows taskbar restoration was requested.");
        }
    }

    /// <summary>True only for the candidate instance deliberately launched by the active transaction.</summary>
    public bool PendingTokenMatches(string? token)
    {
        if (token is null || !Guid.TryParseExact(token, "N", out _) || !io.FileExists(io.JournalPath)) return false;
        try
        {
            PendingUpdate? pending = io.ReadJournal();
            return ValidPending(pending) && string.Equals(pending!.Token, token, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }

    /// <summary>
    /// Hand an interrupted transaction to a separate, hash-verified candidate copy. The caller exits immediately,
    /// allowing that helper to replace the installed executable without racing its mapped image.
    /// </summary>
    public UpdateInstallResult StartPendingRecovery(int parentProcessId)
    {
        io.RestoreTaskbar();
        if (!io.FileExists(io.JournalPath)) return Report(UpdateInstallResult.NoPendingUpdate, "No interrupted update requires recovery.");
        try
        {
            PendingUpdate? pending = io.ReadJournal();
            if (!ValidPending(pending) || !io.FileExists(io.RecoveryExe) ||
                !HashEquals(pending!.CandidateSha256, io.Sha256(io.RecoveryExe)))
                return Report(UpdateInstallResult.InvalidJournal, "Interrupted-update recovery could not be authenticated. The taskbar is visible and the last-known-good copy was retained.");
            io.Launch(io.RecoveryExe, $"--recover-update {parentProcessId}");
            return Report(UpdateInstallResult.RecoveryStarted, "A separate verified helper will recover the interrupted update.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or JsonException)
        {
            return Report(UpdateInstallResult.InvalidJournal, "Interrupted-update recovery could not start. The taskbar is visible and the last-known-good copy was retained.");
        }
    }

    /// <summary>Complete recovery from RecoveryExe after the installed candidate process has exited.</summary>
    public UpdateInstallResult RecoverPending()
    {
        io.RestoreTaskbar();
        if (!io.FileExists(io.JournalPath)) return Report(UpdateInstallResult.NoPendingUpdate, "No interrupted update requires recovery.");
        PendingUpdate? pending;
        try { pending = io.ReadJournal(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Report(UpdateInstallResult.InvalidJournal, "The interrupted-update journal is unreadable. The taskbar is visible and the last-known-good copy was retained.");
        }
        if (!ValidPending(pending))
            return Report(UpdateInstallResult.InvalidJournal, "The interrupted-update journal is invalid. The taskbar is visible and the last-known-good copy was retained.");

        string marker = UpdateHealth.MarkerPath(pending!.Token);
        try
        {
            // The candidate may have reached readiness before its updater parent died during cleanup.
            if (io.FileExists(marker) && io.FileExists(io.InstalledExe) &&
                HashEquals(pending.CandidateSha256, io.Sha256(io.InstalledExe)))
            {
                io.DeleteFile(io.JournalPath);
                TryDeleteAfterFinalization(marker);
                TryDeleteAfterFinalization(io.RecoveryExe);
                io.Launch(io.InstalledExe, "");
                return Report(UpdateInstallResult.Success, "The recovered candidate had already passed its health check.");
            }

            // A crash before replacement leaves the previous binary intact. Every later crash point restores LKG.
            if (!io.FileExists(io.InstalledExe) || !HashEquals(pending.PreviousSha256, io.Sha256(io.InstalledExe)))
            {
                if (!io.FileExists(io.RollbackExe) || !HashEquals(pending.PreviousSha256, io.Sha256(io.RollbackExe)))
                    throw new IOException("verified rollback copy unavailable");
                io.DeleteFile(io.IncomingExe);
                io.CopyFile(io.RollbackExe, io.IncomingExe, overwrite: true);
                if (!HashEquals(pending.PreviousSha256, io.Sha256(io.IncomingExe))) throw new IOException("rollback staging hash mismatch");
                io.ReplaceFile(io.IncomingExe, io.InstalledExe);
                if (!HashEquals(pending.PreviousSha256, io.Sha256(io.InstalledExe))) throw new IOException("restored hash mismatch");
            }
            io.DeleteFile(marker);
            io.DeleteFile(io.JournalPath);
            io.Launch(io.InstalledExe, "");
            return Report(UpdateInstallResult.RolledBack, "The interrupted update was recovered and the verified previous version was started.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return Report(UpdateInstallResult.RollbackFailed, "Interrupted-update recovery failed. The taskbar is visible and the verified last-known-good file was retained.");
        }
    }

    private UpdateInstallResult RollBack(Func<bool> stopRunning, string previousHash, string token,
        UpdateInstallResult cause, string reason)
    {
        io.RestoreTaskbar();
        stopRunning();
        try
        {
            if (!io.FileExists(io.RollbackExe) || !HashEquals(previousHash, io.Sha256(io.RollbackExe)))
                throw new IOException("verified rollback copy unavailable");
            io.DeleteFile(io.IncomingExe);
            io.CopyFile(io.RollbackExe, io.IncomingExe, overwrite: true);
            if (!HashEquals(previousHash, io.Sha256(io.IncomingExe))) throw new IOException("rollback staging hash mismatch");
            io.ReplaceFile(io.IncomingExe, io.InstalledExe);
            if (!HashEquals(previousHash, io.Sha256(io.InstalledExe))) throw new IOException("restored hash mismatch");
            io.DeleteFile(UpdateHealth.MarkerPath(token));
            io.DeleteFile(io.JournalPath);
            io.Launch(io.InstalledExe, "");
            return Report(UpdateInstallResult.RolledBack, $"{reason} The verified previous version was restored automatically ({cause}).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return Report(UpdateInstallResult.RollbackFailed,
                $"{reason} Automatic restoration could not complete. The Windows taskbar was restored and the verified last-known-good file was retained.");
        }
    }

    private static bool HashEquals(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private void TryDeleteAfterFinalization(string path)
    {
        try { io.DeleteFile(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static bool ValidPending(PendingUpdate? pending) => pending is not null &&
        Guid.TryParseExact(pending.Token, "N", out _) &&
        pending.PreviousSha256.Length == 64 && pending.CandidateSha256.Length == 64 &&
        pending.PreviousSha256.All(Uri.IsHexDigit) && pending.CandidateSha256.All(Uri.IsHexDigit);
}

public static class UpdateHealth
{
    public static string MarkerPath(string token)
    {
        if (!Guid.TryParseExact(token, "N", out _)) throw new ArgumentException("Invalid update health token.", nameof(token));
        return Path.Combine(Path.GetTempPath(), $"Strata-update-health-{token}.ok");
    }

    public static bool Acknowledge(string? token)
    {
        if (token is null || !Guid.TryParseExact(token, "N", out _)) return false;
        try
        {
            File.WriteAllText(MarkerPath(token), "healthy", new System.Text.UTF8Encoding(false));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
