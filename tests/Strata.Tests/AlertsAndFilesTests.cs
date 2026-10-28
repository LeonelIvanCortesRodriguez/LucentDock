using Strata.Core;
using Strata.Native;
using Strata.Quota;

namespace Strata.Tests;

/// <summary>
/// The upgrades asked for after aed2ca0: AI usage notifications, files dropped on dock apps, and recent files in
/// an app's menu.
/// </summary>
[Trait("Category", "Unit")]
public class AlertsAndFilesTests
{
    private static readonly DateTimeOffset Now = QuotaFixtures.Now;

    private static ProviderState Codex(double used, DateTimeOffset resets, QuotaStatus status = QuotaStatus.Fresh, bool sample = false) =>
        new(QuotaProvider.Codex, status, QuotaFixtures.Snapshot(QuotaProvider.Codex, Now,
            QuotaFixtures.Window("codex/weekly", used, resets, QuotaWindowKind.Weekly, 10080) with { Label = "Weekly" }) with { IsSample = sample });

    private static List<QuotaAlert> Eval(ProviderState st, QuotaAlertLog log, DateTimeOffset? at = null) =>
        QuotaAlerts.Evaluate(st, at ?? Now, warnRemainingPercent: 20, log, use24Hour: true);

    [Fact]
    public void A_limit_warns_once_at_the_warning_level()
    {
        var log = new QuotaAlertLog();
        var resets = Now.AddDays(3);
        Assert.Empty(Eval(Codex(70, resets), log));                          // 30% remaining: no alert
        var first = Eval(Codex(82, resets), log);
        Assert.Equal(["Codex · Weekly: 82% used"], first.Select(a => a.Title));
        Assert.StartsWith("18% remaining. Resets in 3d", first[0].Text);
        Assert.Empty(Eval(Codex(90, resets), log));                          // still warned: not again
    }

    [Fact]
    public void Reaching_the_limit_alerts_once_and_skips_a_late_warning()
    {
        var log = new QuotaAlertLog();
        var resets = Now.AddDays(5);
        var reached = Eval(Codex(100, resets), log);
        Assert.Equal(["Codex · Weekly: limit reached"], reached.Select(a => a.Title));
        Assert.Empty(Eval(Codex(100, resets), log));
        Assert.Empty(Eval(Codex(95, resets), log));                          // no warning after the fact
    }

    [Fact]
    public void A_limit_that_warned_says_so_once_when_it_resets()
    {
        var log = new QuotaAlertLog();
        Eval(Codex(100, Now.AddHours(2)), log);
        var later = Now.AddHours(3);
        var reset = Eval(Codex(4, later.AddDays(7)), log, later);
        Assert.Equal(["Codex · Weekly has reset"], reset.Select(a => a.Title));
        Assert.Equal("4% used now: available again.", reset[0].Text);
        Assert.Empty(Eval(Codex(5, later.AddDays(7)), log, later));
    }

    [Fact]
    public void A_reset_time_that_moves_by_minutes_is_not_a_reset_or_a_new_warning()
    {
        var log = new QuotaAlertLog();
        var resets = Now.AddDays(2);
        Eval(Codex(85, resets), log);
        Assert.Empty(Eval(Codex(86, resets.AddMinutes(3)), log));            // same period, reported slightly differently
        Assert.Empty(Eval(Codex(50, resets.AddMinutes(4)), log));            // and below the warning: still no "reset"
    }

    [Fact]
    public void Stale_sample_or_past_reset_readings_never_alert()
    {
        Assert.Empty(Eval(Codex(100, Now.AddDays(1), QuotaStatus.Stale), new QuotaAlertLog()));
        Assert.Empty(Eval(Codex(100, Now.AddDays(1), sample: true), new QuotaAlertLog()));
        Assert.Empty(Eval(Codex(100, Now.AddMinutes(-5)), new QuotaAlertLog()));   // past its reset: waits for a new reading
    }

    [Fact]
    public void Old_periods_are_forgotten()
    {
        var log = new QuotaAlertLog();
        Eval(Codex(100, Now.AddDays(1)), log);
        QuotaAlerts.Prune(log, Now.AddDays(10));
        Assert.NotEmpty(log.Levels);
        QuotaAlerts.Prune(log, Now.AddDays(16));
        Assert.Empty(log.Levels);
        Assert.Empty(log.Sent);
    }

    // ---- Opening files with a dock app ----

    [Fact]
    public void Dropped_files_open_with_the_apps_executable_store_id_or_shortcut()
    {
        var excel = new PinnedApp { Name = "Excel", Path = @"C:\Links\Excel.lnk", ExePath = @"C:\Program Files\Microsoft Office\root\Office16\EXCEL.EXE" };
        Assert.Equal((Launcher.OpenKind.Exe, excel.ExePath), Launcher.OpenTarget(excel, null));
        var store = new AppIdentity("Microsoft.WindowsNotepad_8wekyb3d8bbwe!App", @"C:\Program Files\WindowsApps\Microsoft.WindowsNotepad\Notepad.exe", "Notepad");
        Assert.Equal((Launcher.OpenKind.Packaged, store.Aumid!), Launcher.OpenTarget(null, store));
        var linkOnly = new PinnedApp { Name = "Tool", Path = @"C:\Links\Tool.lnk" };
        Assert.Equal((Launcher.OpenKind.Shortcut, linkOnly.Path), Launcher.OpenTarget(linkOnly, null));
        Assert.Equal(Launcher.OpenKind.None, Launcher.OpenTarget(new PinnedApp { Name = "?" }, null).Kind);
        Assert.False(Launcher.OpenWith(excel, null, []));
    }

    [Fact]
    public void Paths_are_quoted_so_spaces_and_trailing_backslashes_stay_intact()
    {
        Assert.Equal("\"C:\\My Files\\a b.xlsx\" \"D:\\x.csv\"", Launcher.CommandLineFor([@"C:\My Files\a b.xlsx", @"D:\x.csv"]));
        Assert.Equal("\"C:\\\\\"", Launcher.CommandLineFor([@"C:\"]));       // C:\ → "C:\\" (the quote isn't escaped)
    }

    // ---- Recent files: which jump list ----

    [Fact]
    public void Apps_without_their_own_id_get_the_implicit_one_from_the_deepest_known_folder()
    {
        var local = new Guid("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");
        var programs = new Guid("5CD7AEE2-2219-4A67-B85D-6C9CE15660CB");
        var system = new Guid("1AC14E77-02E7-4E5D-B744-2EB1AE5198B7");
        (Guid, string)[] folders = [(local, @"C:\Users\Fixture\AppData\Local"), (programs, @"C:\Users\Fixture\AppData\Local\Programs"), (system, @"C:\Windows\System32")];
        Assert.Equal("{5CD7AEE2-2219-4A67-B85D-6C9CE15660CB}\\Editor\\editor.exe",
            ShellDocuments.ImplicitAppId(@"C:\Users\Fixture\AppData\Local\Programs\Editor\editor.exe", folders));
        Assert.Equal("{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\\notepad.exe", ShellDocuments.ImplicitAppId(@"c:\windows\system32\notepad.exe", folders));
        Assert.Null(ShellDocuments.ImplicitAppId(@"D:\Portable\tool.exe", folders));
        Assert.Null(ShellDocuments.ImplicitAppId(null, folders));
    }

    [Fact]
    public void Jump_list_ids_are_tried_most_specific_first()
    {
        var pin = new PinnedApp { Name = "Notepad", ExePath = Path.Combine(Environment.SystemDirectory, "notepad.exe") };
        var ids = Launcher.RecentAppIds(pin, new AppIdentity("Fixture.Explicit!App", pin.ExePath, "Notepad")).ToList();
        Assert.Equal("Fixture.Explicit!App", ids[0]);
        Assert.Equal("{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\\notepad.exe", ids[1]);
        Assert.Equal(pin.ExePath, ids[^1]);
    }

    /// <summary>The shell's jump-list interface answers for an id with no list, and for nonsense, without throwing.</summary>
    [Fact]
    public void Reading_a_jump_list_never_throws() => OnSta(() =>
    {
        Assert.Empty(ShellDocuments.Recent(["Strata.Fixture.NoSuchApp!App"]));
        Assert.Empty(ShellDocuments.Recent(["", "   "]));
        Assert.Null(ShellDocuments.ShortcutAppId(@"C:\does-not-exist.lnk"));
    });

    private static void OnSta(Action action)
    {
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? error = null;
        var t = new Thread(() => { try { action(); } catch (Exception ex) { error = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex); } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(10)), "STA test timed out.");
        error?.Throw();
    }
}
