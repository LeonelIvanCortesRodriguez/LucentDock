using Strata.Core;

namespace Strata.Tests;

[Trait("Category", "Unit")]
public class UpdateTransactionTests
{
    private sealed class FakeIO : IUpdateTransactionIO
    {
        public static readonly string OldHash = new('A', 64);
        public static readonly string NewHash = new('B', 64);
        public string? CurrentExe { get; set; } = @"X:\stage\Strata.exe";
        public string InstallDir => @"X:\Programs\Strata";
        public string InstalledExe => @"X:\Programs\Strata\Strata.exe";
        public string IncomingExe => @"X:\Programs\Strata\Strata.update-incoming.exe";
        public string RollbackExe => @"X:\Programs\Strata\Strata.last-known-good.exe";
        public string RecoveryExe => @"X:\Programs\Strata\Strata.update-recovery.exe";
        public string JournalPath => @"X:\Programs\Strata\update-pending.json";
        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Hashes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<(string Exe, string Args)> Launches { get; } = [];
        public List<string> Deletes { get; } = [];
        public PendingUpdate? Pending { get; private set; }
        public bool RestoreCalled, HealthOnLaunch = true, ThrowOnCopy, ThrowOnJournal, ThrowOnJournalWin32, ThrowOnCreate, ThrowOnHash,
            ThrowOnIncomingDelete, CorruptIncoming;
        public string? ThrowOnCopyTo, ThrowOnHashPath, ThrowOnDeletePath;
        public int ReplaceFailures, Sleeps;

        public FakeIO()
        {
            Files.Add(CurrentExe!);
            Files.Add(InstalledExe);
            Hashes[CurrentExe!] = NewHash;
            Hashes[InstalledExe] = OldHash;
        }

        public bool FileExists(string path) => Files.Contains(path);
        public void CreateDirectory(string path) { if (ThrowOnCreate) throw new UnauthorizedAccessException("denied"); }
        public void CopyFile(string from, string to, bool overwrite)
        {
            if (ThrowOnCopy || string.Equals(ThrowOnCopyTo, to, StringComparison.OrdinalIgnoreCase))
                throw new IOException("disk full");
            Files.Add(to);
            Hashes[to] = CorruptIncoming && to.Equals(IncomingExe, StringComparison.OrdinalIgnoreCase)
                ? "CORRUPT" : Hashes[from];
        }
        public void ReplaceFile(string source, string destination)
        {
            if (ReplaceFailures-- > 0) throw new IOException("interrupted");
            Files.Add(destination);
            Hashes[destination] = Hashes[source];
            Files.Remove(source);
            Hashes.Remove(source);
        }
        public void DeleteFile(string path)
        {
            Deletes.Add(path);
            if (string.Equals(ThrowOnDeletePath, path, StringComparison.OrdinalIgnoreCase))
                throw new IOException("delete failed");
            if (ThrowOnIncomingDelete && string.Equals(path, IncomingExe, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("delete denied");
            Files.Remove(path); Hashes.Remove(path); if (path == JournalPath) Pending = null;
        }
        public string Sha256(string path) => ThrowOnHash || string.Equals(ThrowOnHashPath, path, StringComparison.OrdinalIgnoreCase)
            ? throw new IOException("read failed") : Hashes[path];
        public void WriteJournal(PendingUpdate pending)
        {
            if (ThrowOnJournalWin32) throw new System.ComponentModel.Win32Exception(5, "write-through rename denied");
            if (ThrowOnJournal) throw new IOException("disk full");
            Pending = pending;
            Files.Add(JournalPath);
            Hashes[JournalPath] = "JOURNAL";
        }
        public PendingUpdate? ReadJournal() => Pending;
        public void SeedPending(bool replaced, bool healthy = false)
        {
            var pending = new PendingUpdate(Guid.NewGuid().ToString("N"), OldHash, NewHash, DateTimeOffset.UtcNow);
            WriteJournal(pending);
            Files.Add(RollbackExe); Hashes[RollbackExe] = OldHash;
            Files.Add(RecoveryExe); Hashes[RecoveryExe] = NewHash;
            Hashes[InstalledExe] = replaced ? NewHash : OldHash;
            if (healthy)
            {
                string marker = UpdateHealth.MarkerPath(pending.Token);
                Files.Add(marker); Hashes[marker] = "HEALTH";
            }
        }
        public void Launch(string exe, string args)
        {
            Launches.Add((exe, args));
            if (HealthOnLaunch && args.StartsWith("--update-health ", StringComparison.Ordinal))
            {
                string token = args.Split(' ')[1];
                string marker = UpdateHealth.MarkerPath(token);
                Files.Add(marker);
                Hashes[marker] = "HEALTH";
            }
        }
        public void RestoreTaskbar() => RestoreCalled = true;
        public void Sleep(TimeSpan time) => Sleeps++;
    }

    [Fact]
    public void Successful_update_is_atomic_health_checked_and_keeps_last_known_good()
    {
        var io = new FakeIO();
        var result = new UpdateTransaction(io).Apply(() => true);

        Assert.Equal(UpdateInstallResult.Success, result);
        Assert.Equal(FakeIO.NewHash, io.Hashes[io.InstalledExe]);
        Assert.Equal(FakeIO.OldHash, io.Hashes[io.RollbackExe]);
        Assert.DoesNotContain(io.JournalPath, io.Files);
        Assert.DoesNotContain(io.RecoveryExe, io.Files);
        Assert.Single(io.Launches);
        Assert.StartsWith("--update-health ", io.Launches[0].Args);
        Assert.True(io.Deletes.IndexOf(io.JournalPath) < io.Deletes.FindIndex(path => path.Contains("Strata-update-health-")));
    }

    [Fact]
    public void Healthy_candidate_keeps_marker_and_journal_for_next_start_when_finalization_fails()
    {
        var io = new FakeIO { ThrowOnDeletePath = @"X:\Programs\Strata\update-pending.json" };

        Assert.Equal(UpdateInstallResult.Success, new UpdateTransaction(io).Apply(() => true));

        Assert.Contains(io.JournalPath, io.Files);
        Assert.Contains(io.Files, path => path.Contains("Strata-update-health-", StringComparison.Ordinal));
        Assert.Equal(FakeIO.NewHash, io.Hashes[io.InstalledExe]);
    }

    [Fact]
    public void Disk_full_while_staging_does_not_touch_installed_copy()
    {
        var io = new FakeIO { ThrowOnCopy = true };
        Assert.Equal(UpdateInstallResult.StageCopyFailed, new UpdateTransaction(io).Apply(() => true));
        Assert.Equal(FakeIO.OldHash, io.Hashes[io.InstalledExe]);
        Assert.Equal((io.InstalledExe, ""), Assert.Single(io.Launches));
        Assert.True(io.RestoreCalled);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Preflight_disk_or_hash_failure_does_not_stop_or_change_healthy_install(bool createFails, bool hashFails)
    {
        var io = new FakeIO { ThrowOnCreate = createFails, ThrowOnHash = hashFails };
        bool stopped = false;
        Assert.Equal(UpdateInstallResult.PreflightFailed, new UpdateTransaction(io).Apply(() => { stopped = true; return true; }));
        Assert.False(stopped);
        Assert.Equal(FakeIO.OldHash, io.Hashes[io.InstalledExe]);
        Assert.Empty(io.Launches);
    }

    [Fact]
    public void Corrupt_local_stage_is_rejected_before_replacement()
    {
        var io = new FakeIO { CorruptIncoming = true };
        Assert.Equal(UpdateInstallResult.StageHashMismatch, new UpdateTransaction(io).Apply(() => true));
        Assert.Equal(FakeIO.OldHash, io.Hashes[io.InstalledExe]);
        Assert.Equal((io.InstalledExe, ""), Assert.Single(io.Launches));
        Assert.True(io.RestoreCalled);
    }

    [Fact]
    public void Staged_copy_hash_read_failure_restarts_unchanged_installed_copy()
    {
        var io = new FakeIO { ThrowOnHashPath = @"X:\Programs\Strata\Strata.update-incoming.exe" };

        Assert.Equal(UpdateInstallResult.StageCopyFailed, new UpdateTransaction(io).Apply(() => true));
        Assert.Equal(FakeIO.OldHash, io.Hashes[io.InstalledExe]);
        Assert.DoesNotContain(io.IncomingExe, io.Files);
        Assert.Equal((io.InstalledExe, ""), Assert.Single(io.Launches));
        Assert.True(io.RestoreCalled);
    }

    [Fact]
    public void Failed_incoming_cleanup_does_not_prevent_unchanged_installed_copy_restart()
    {
        var io = new FakeIO { CorruptIncoming = true, ThrowOnIncomingDelete = true };

        Assert.Equal(UpdateInstallResult.StageCopyFailed, new UpdateTransaction(io).Apply(() => true));
        Assert.Equal(FakeIO.OldHash, io.Hashes[io.InstalledExe]);
        Assert.Equal((io.InstalledExe, ""), Assert.Single(io.Launches));
        Assert.True(io.RestoreCalled);
    }

    [Fact]
    public void Backup_failure_restarts_unchanged_installed_copy()
    {
        var io = new FakeIO { ThrowOnCopyTo = @"X:\Programs\Strata\Strata.last-known-good.exe" };

        Assert.Equal(UpdateInstallResult.BackupFailed, new UpdateTransaction(io).Apply(() => true));
        Assert.Equal(FakeIO.OldHash, io.Hashes[io.InstalledExe]);
        Assert.Equal((io.InstalledExe, ""), Assert.Single(io.Launches));
        Assert.True(io.RestoreCalled);
    }

    [Fact]
    public void Journal_failure_does_not_replace_the_installed_copy()
    {
        var io = new FakeIO { ThrowOnJournal = true };
        Assert.Equal(UpdateInstallResult.JournalFailed, new UpdateTransaction(io).Apply(() => true));
        Assert.Equal(FakeIO.OldHash, io.Hashes[io.InstalledExe]);
        Assert.Equal(FakeIO.OldHash, io.Hashes[io.RollbackExe]);
        Assert.Equal((io.InstalledExe, ""), Assert.Single(io.Launches));
        Assert.True(io.RestoreCalled);
    }

    [Fact]
    public void Native_durable_journal_failure_restarts_unchanged_installed_copy()
    {
        var io = new FakeIO { ThrowOnJournalWin32 = true };

        Assert.Equal(UpdateInstallResult.JournalFailed, new UpdateTransaction(io).Apply(() => true));
        Assert.Equal(FakeIO.OldHash, io.Hashes[io.InstalledExe]);
        Assert.Equal((io.InstalledExe, ""), Assert.Single(io.Launches));
        Assert.True(io.RestoreCalled);
    }

    [Fact]
    public void Interrupted_atomic_replace_restores_or_retains_verified_old_build()
    {
        var io = new FakeIO { ReplaceFailures = 1 };
        Assert.Equal(UpdateInstallResult.RolledBack, new UpdateTransaction(io).Apply(() => true));
        Assert.True(io.RestoreCalled);
        Assert.Equal(FakeIO.OldHash, io.Hashes[io.InstalledExe]);
        Assert.Equal(FakeIO.OldHash, io.Hashes[io.RollbackExe]);
    }

    [Fact]
    public void Failed_start_rolls_back_and_relaunches_previous_build()
    {
        var io = new FakeIO();
        int launches = 0;
        var throwing = new LaunchFailureIO(io, () => launches++ == 0);
        Assert.Equal(UpdateInstallResult.RolledBack, new UpdateTransaction(throwing).Apply(() => true));
        Assert.True(io.RestoreCalled);
        Assert.Equal(FakeIO.OldHash, io.Hashes[io.InstalledExe]);
        Assert.Equal(2, io.Launches.Count);
        Assert.Equal("", io.Launches[^1].Args);
    }

    [Fact]
    public void Native_process_start_failure_rolls_back_and_relaunches_previous_build()
    {
        var io = new FakeIO();
        int launches = 0;
        var throwing = new LaunchFailureIO(io, () => launches++ == 0, throwWin32: true);

        Assert.Equal(UpdateInstallResult.RolledBack, new UpdateTransaction(throwing).Apply(() => true));
        Assert.True(io.RestoreCalled);
        Assert.Equal(FakeIO.OldHash, io.Hashes[io.InstalledExe]);
        Assert.Equal(2, io.Launches.Count);
        Assert.Equal("", io.Launches[^1].Args);
    }

    [Fact]
    public void Failed_health_check_is_bounded_then_rolls_back()
    {
        var io = new FakeIO { HealthOnLaunch = false };
        Assert.Equal(UpdateInstallResult.RolledBack, new UpdateTransaction(io).Apply(() => true));
        Assert.Equal(UpdateTransaction.HealthAttempts, io.Sleeps);
        Assert.Equal(FakeIO.OldHash, io.Hashes[io.InstalledExe]);
        Assert.True(io.RestoreCalled);
    }

    [Fact]
    public void Rollback_failure_leaves_taskbar_and_verified_last_known_good_available()
    {
        var io = new FakeIO { ReplaceFailures = 2 };
        Assert.Equal(UpdateInstallResult.RollbackFailed, new UpdateTransaction(io).Apply(() => true));
        Assert.True(io.RestoreCalled);
        Assert.Equal(FakeIO.OldHash, io.Hashes[io.RollbackExe]);
    }

    [Fact]
    public void Process_loss_after_replacement_launches_verified_helper_then_restores_previous_build()
    {
        var io = new FakeIO();
        io.SeedPending(replaced: true);
        string token = io.Pending!.Token;
        var transaction = new UpdateTransaction(io);

        Assert.Equal(UpdateInstallResult.RecoveryStarted, transaction.StartPendingRecovery(4321));
        Assert.True(io.RestoreCalled);
        Assert.Equal((io.RecoveryExe, "--recover-update 4321"), Assert.Single(io.Launches));

        io.Launches.Clear();
        Assert.Equal(UpdateInstallResult.RolledBack, transaction.RecoverPending());
        Assert.Equal(FakeIO.OldHash, io.Hashes[io.InstalledExe]);
        Assert.Equal(FakeIO.OldHash, io.Hashes[io.RollbackExe]);
        Assert.DoesNotContain(io.JournalPath, io.Files);
        Assert.DoesNotContain(UpdateHealth.MarkerPath(token), io.Files);
        Assert.Equal((io.InstalledExe, ""), Assert.Single(io.Launches));
    }

    [Fact]
    public void Process_loss_before_replacement_keeps_and_restarts_previous_build()
    {
        var io = new FakeIO();
        io.SeedPending(replaced: false);

        Assert.Equal(UpdateInstallResult.RolledBack, new UpdateTransaction(io).RecoverPending());
        Assert.Equal(FakeIO.OldHash, io.Hashes[io.InstalledExe]);
        Assert.Equal((io.InstalledExe, ""), Assert.Single(io.Launches));
    }

    [Fact]
    public void Health_marker_survives_updater_loss_and_finalizes_candidate_without_rollback()
    {
        var io = new FakeIO();
        io.SeedPending(replaced: true, healthy: true);

        Assert.Equal(UpdateInstallResult.Success, new UpdateTransaction(io).RecoverPending());
        Assert.Equal(FakeIO.NewHash, io.Hashes[io.InstalledExe]);
        Assert.DoesNotContain(io.JournalPath, io.Files);
        Assert.Equal((io.InstalledExe, ""), Assert.Single(io.Launches));
        Assert.True(io.Deletes.IndexOf(io.JournalPath) < io.Deletes.FindIndex(path => path.Contains("Strata-update-health-")));
    }

    [Fact]
    public void Recovery_finalization_failure_retains_health_authorization_for_retry()
    {
        var io = new FakeIO();
        io.SeedPending(replaced: true, healthy: true);
        string marker = UpdateHealth.MarkerPath(io.Pending!.Token);
        io.ThrowOnDeletePath = io.JournalPath;

        Assert.Equal(UpdateInstallResult.RollbackFailed, new UpdateTransaction(io).RecoverPending());
        Assert.Contains(io.JournalPath, io.Files);
        Assert.Contains(marker, io.Files);
        Assert.Empty(io.Launches);

        io.ThrowOnDeletePath = null;
        Assert.Equal(UpdateInstallResult.Success, new UpdateTransaction(io).RecoverPending());
        Assert.DoesNotContain(io.JournalPath, io.Files);
        Assert.DoesNotContain(marker, io.Files);
    }

    [Fact]
    public void Unverified_recovery_helper_fails_closed_with_taskbar_restored()
    {
        var io = new FakeIO();
        io.SeedPending(replaced: true);
        io.Hashes[io.RecoveryExe] = new string('C', 64);

        Assert.Equal(UpdateInstallResult.InvalidJournal, new UpdateTransaction(io).StartPendingRecovery(4321));
        Assert.True(io.RestoreCalled);
        Assert.Empty(io.Launches);
        Assert.Contains(io.RollbackExe, io.Files);
    }

    [Fact]
    public void Only_the_journal_token_authorizes_health_mode()
    {
        var io = new FakeIO();
        io.SeedPending(replaced: true);
        var transaction = new UpdateTransaction(io);

        Assert.True(transaction.PendingTokenMatches(io.Pending!.Token));
        Assert.False(transaction.PendingTokenMatches(Guid.NewGuid().ToString("N")));
        Assert.False(transaction.PendingTokenMatches("not-a-token"));
    }

    [Theory]
    [InlineData(null, null, false, true)]
    [InlineData("ABC", "ABC", true, true)]
    [InlineData("ABC", "DEF", true, false)]
    [InlineData("ABC", "ABC", false, false)]
    [InlineData("ABC", null, true, false)]
    public void Same_publisher_policy_is_fail_closed_for_signed_builds(
        string? current, string? candidate, bool valid, bool accepted) =>
        Assert.Equal(accepted, Updater.SignerAccepted(current, candidate, valid));

    [Fact]
    public void Health_tokens_are_strict_and_do_not_contain_user_data()
    {
        Assert.Throws<ArgumentException>(() => UpdateHealth.MarkerPath("..\\owner"));
        string token = Guid.NewGuid().ToString("N");
        Assert.EndsWith($"Strata-update-health-{token}.ok", UpdateHealth.MarkerPath(token), StringComparison.Ordinal);
    }

    private sealed class LaunchFailureIO(FakeIO inner, Func<bool> fail, bool throwWin32 = false) : IUpdateTransactionIO
    {
        public string? CurrentExe => inner.CurrentExe;
        public string InstallDir => inner.InstallDir;
        public string InstalledExe => inner.InstalledExe;
        public string IncomingExe => inner.IncomingExe;
        public string RollbackExe => inner.RollbackExe;
        public string RecoveryExe => inner.RecoveryExe;
        public string JournalPath => inner.JournalPath;
        public bool FileExists(string path) => inner.FileExists(path);
        public void CreateDirectory(string path) => inner.CreateDirectory(path);
        public void CopyFile(string from, string to, bool overwrite) => inner.CopyFile(from, to, overwrite);
        public void ReplaceFile(string source, string destination) => inner.ReplaceFile(source, destination);
        public void DeleteFile(string path) => inner.DeleteFile(path);
        public string Sha256(string path) => inner.Sha256(path);
        public void WriteJournal(PendingUpdate pending) => inner.WriteJournal(pending);
        public PendingUpdate? ReadJournal() => inner.ReadJournal();
        public void Launch(string exe, string args)
        {
            inner.Launches.Add((exe, args));
            if (fail())
            {
                if (throwWin32) throw new System.ComponentModel.Win32Exception(2, "start failed");
                throw new IOException("start failed");
            }
            inner.Launch(exe, args);
            inner.Launches.RemoveAt(inner.Launches.Count - 1);
        }
        public void RestoreTaskbar() => inner.RestoreTaskbar();
        public void Sleep(TimeSpan time) => inner.Sleep(time);
    }
}
