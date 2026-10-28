using System.Text.Json;
using Strata.Core;
using Strata.Quota;

namespace Strata.Tests;

/// <summary>Fixture helpers for the AI usage (quota) tests. All fixture values are invented.</summary>
internal static class QuotaFixtures
{
    /// <summary>Fixed "now" for the fixtures: their reset times are relative to it.</summary>
    public static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1793300000);

    public static string Text(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Quota", name));
    public static JsonElement Json(string name) => JsonDocument.Parse(Text(name)).RootElement.Clone();
    public static QuotaSnapshot Codex(string name) => CodexRateLimits.Parse(Json(name), Now);

    /// <summary>Strings planted in fixtures that must never reach Strata's storage or output.</summary>
    public static readonly string[] Secrets =
    [
        "acct-FIXTURE-SECRET", "Fixture upsell", "Fixture reset", "rc-fixture", "example.invalid", "accountId", "upsell",
        "fixture-person", "secret-project", "session-FIXTURE-SECRET", "Fixture Model", "claude-fixture-model",
        "transcript", "cwd", "workspace", "total_cost", "codexHome",
    ];

    public static QuotaWindow Window(string id, double? used, DateTimeOffset? resets, QuotaWindowKind kind = QuotaWindowKind.Session, int? minutes = 300) =>
        new() { Id = id, Label = id, Kind = kind, ReportedUsedPercent = used, ResetsAt = resets, WindowMinutes = minutes, Reached = used >= 100 };

    public static QuotaSnapshot Snapshot(QuotaProvider p, DateTimeOffset observed, params QuotaWindow[] windows) =>
        new() { Provider = p, Source = "fixture", Windows = windows, ObservedAt = observed };
}

[Trait("Category", "Unit")]
public class QuotaFormatTests
{
    private static readonly DateTimeOffset Now = QuotaFixtures.Now;

    [Theory]
    [InlineData(64.5, "64.5% used", "35.5% remaining")]
    [InlineData(0, "0% used", "100% remaining")]
    [InlineData(100, "100% used", "0% remaining")]
    [InlineData(23, "23% used", "77% remaining")]
    [InlineData(99.96, "99.9% used", "<0.1% remaining")]   // never shows a limit as hit before it is
    [InlineData(0.04, "<0.1% used", "99.9% remaining")]    // never shows usage as zero when it isn't
    [InlineData(112.4, "112.4% used", "0% remaining (over limit)")]
    public void Every_value_says_used_or_remaining_and_remaining_is_100_minus_used(double used, string usedText, string remainingText)
    {
        var w = QuotaFixtures.Window("w", used, Now.AddHours(1));
        Assert.Equal(used, w.ReportedUsedPercent);   // the provider's figure is preserved exactly
        Assert.Equal(100 - used, w.RemainingPercent);
        Assert.Equal(usedText, QuotaFormat.Value(w, Now, remaining: false));
        Assert.Equal(remainingText, QuotaFormat.Value(w, Now, remaining: true));
    }

    [Fact]
    public void A_provider_that_reports_remaining_keeps_it_and_derives_used()
    {
        var w = new QuotaWindow { Id = "s", Label = "Spend limit", ReportedRemainingPercent = 75 };
        Assert.Null(w.ReportedUsedPercent);
        Assert.Equal(25, w.UsedPercent);
        Assert.Equal(75, w.RemainingPercent);
    }

    [Fact]
    public void Missing_data_is_unavailable_never_zero()
    {
        var w = QuotaFixtures.Window("w", null, Now.AddHours(1));
        Assert.Equal(WindowPhase.Unavailable, QuotaFormat.Phase(w, Now));
        Assert.Equal("Unavailable", QuotaFormat.Value(w, Now, remaining: true));
        Assert.Equal("Unavailable", QuotaFormat.Value(w, Now, remaining: false));
        Assert.Equal(QuotaSeverity.Normal, QuotaFormat.Severity(w, Now, 20, 5));
        Assert.Null(QuotaFormat.Countdown(null, Now));
    }

    [Fact]
    public void After_its_reset_time_a_window_waits_for_a_new_sample_instead_of_guessing()
    {
        var w = QuotaFixtures.Window("w", 100, Now.AddMinutes(-1));
        Assert.Equal(WindowPhase.ResetPassed, QuotaFormat.Phase(w, Now));
        Assert.Equal(QuotaFormat.WaitingAfterReset, QuotaFormat.Value(w, Now, remaining: true));
        Assert.DoesNotContain("%", QuotaFormat.Value(w, Now, remaining: false));
        Assert.Equal(QuotaSeverity.Normal, QuotaFormat.Severity(w, Now, 20, 5));
    }

    [Theory]
    [InlineData(68 * 60, "1h 8m")]
    [InlineData(45 * 60, "45m")]
    [InlineData(30, "<1m")]
    [InlineData((3 * 24 + 5) * 3600, "3d 5h")]
    [InlineData(-10, "now")]
    public void Countdown(int seconds, string expected) =>
        Assert.Equal(expected, QuotaFormat.Countdown(Now.AddSeconds(seconds), Now));

    [Fact]
    public void Absolute_reset_time_is_local_and_says_which_day()
    {
        var localNoon = new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 28, 12, 0, 0)));
        Assert.Equal("today 14:05", QuotaFormat.ResetLocal(localNoon.AddHours(2).AddMinutes(5), localNoon, use24Hour: true));
        Assert.StartsWith("tomorrow ", QuotaFormat.ResetLocal(localNoon.AddDays(1), localNoon, true));
        Assert.Matches(@"^[A-Z][a-z]{2} 12:00$", QuotaFormat.ResetLocal(localNoon.AddDays(3), localNoon, true));
        Assert.Matches(@"^\d{1,2} [A-Z][a-z]{2} 12:00$", QuotaFormat.ResetLocal(localNoon.AddDays(10), localNoon, true));
        Assert.EndsWith(" PM", QuotaFormat.ResetLocal(localNoon.AddHours(2), localNoon, use24Hour: false));
    }

    [Theory]
    [InlineData(20, "just now")]
    [InlineData(3 * 60, "3m ago")]
    [InlineData(2 * 3600, "2h ago")]
    [InlineData(3 * 86400, "3d ago")]
    public void Age(int seconds, string expected) => Assert.Equal(expected, QuotaFormat.Age(Now.AddSeconds(-seconds), Now));

    [Fact]
    public void Clock_skew_is_detected_when_the_reading_is_from_the_future_or_the_reset_is_too_far()
    {
        var normal = QuotaFixtures.Window("w", 50, Now.AddMinutes(200));
        Assert.False(QuotaFormat.ClockSkewSuspected(QuotaFixtures.Snapshot(QuotaProvider.Codex, Now, normal), normal, Now));
        Assert.True(QuotaFormat.ClockSkewSuspected(QuotaFixtures.Snapshot(QuotaProvider.Codex, Now.AddMinutes(5), normal), normal, Now));
        var tooFar = QuotaFixtures.Window("w", 50, Now.AddMinutes(400)); // a 5-hour window can't reset in 6h40m
        Assert.True(QuotaFormat.ClockSkewSuspected(QuotaFixtures.Snapshot(QuotaProvider.Codex, Now, tooFar), tooFar, Now));
    }

    [Theory]
    [InlineData(70, QuotaSeverity.Normal)]   // 30% remaining
    [InlineData(80, QuotaSeverity.Warning)]  // 20% remaining: at the threshold
    [InlineData(85, QuotaSeverity.Warning)]
    [InlineData(95, QuotaSeverity.Critical)] // 5% remaining
    [InlineData(100, QuotaSeverity.Critical)]
    public void Warning_colours_apply_only_at_the_configured_thresholds(double used, QuotaSeverity expected) =>
        Assert.Equal(expected, QuotaFormat.Severity(QuotaFixtures.Window("w", used, Now.AddHours(1)), Now, warnRemaining: 20, criticalRemaining: 5));

    [Theory]
    [InlineData(300, "Session (5h)", QuotaWindowKind.Session)]
    [InlineData(10080, "Weekly", QuotaWindowKind.Weekly)]
    [InlineData(120, "2-hour window", QuotaWindowKind.Session)]
    [InlineData(1440, "1-day window", QuotaWindowKind.Other)]
    public void Window_labels_come_from_the_window_length(int minutes, string label, QuotaWindowKind kind) =>
        Assert.Equal((label, kind), QuotaFormat.DurationLabel(minutes));
}

[Trait("Category", "Unit")]
public class QuotaSelectorTests
{
    private static readonly DateTimeOffset Now = QuotaFixtures.Now;

    private static ProviderState Fresh(QuotaSnapshot s) => new(s.Provider, QuotaStatus.Fresh, s);

    [Fact]
    public void Most_constrained_is_one_providers_one_window_never_a_blend()
    {
        var claudeSession = QuotaFixtures.Window("five_hour", 23.5, Now.AddHours(1));
        var claudeWeekly = QuotaFixtures.Window("seven_day", 41.2, Now.AddDays(3), QuotaWindowKind.Weekly, 10080);
        var codexSession = QuotaFixtures.Window("codex/primary", 64.5, Now.AddHours(1));
        var claude = Fresh(QuotaFixtures.Snapshot(QuotaProvider.Claude, Now, claudeSession, claudeWeekly));
        var codex = Fresh(QuotaFixtures.Snapshot(QuotaProvider.Codex, Now, codexSession));

        var pick = QuotaSelector.MostConstrained([claude, codex], Now)!;
        Assert.Equal(QuotaProvider.Codex, pick.Provider);
        Assert.Same(codexSession, pick.Window); // the provider's own window object, not a computed mixture
    }

    [Fact]
    public void Most_constrained_ignores_stale_providers_and_windows_past_their_reset()
    {
        var staleCodex = new ProviderState(QuotaProvider.Codex, QuotaStatus.Stale,
            QuotaFixtures.Snapshot(QuotaProvider.Codex, Now.AddHours(-3), QuotaFixtures.Window("codex/primary", 99, Now.AddHours(1))));
        var claude = Fresh(QuotaFixtures.Snapshot(QuotaProvider.Claude, Now,
            QuotaFixtures.Window("five_hour", 100, Now.AddMinutes(-5)),   // reset already passed
            QuotaFixtures.Window("seven_day", 40, Now.AddDays(2), QuotaWindowKind.Weekly, 10080)));
        var pick = QuotaSelector.MostConstrained([claude, staleCodex], Now)!;
        Assert.Equal(("seven_day", QuotaProvider.Claude), (pick.Window!.Id, pick.Provider));
        Assert.Null(QuotaSelector.MostConstrained([staleCodex], Now));
    }

    [Fact]
    public void Ties_go_to_the_window_that_resets_first()
    {
        var later = QuotaFixtures.Window("later", 50, Now.AddHours(3));
        var sooner = QuotaFixtures.Window("sooner", 50, Now.AddHours(1));
        var pick = QuotaSelector.MostConstrained([Fresh(QuotaFixtures.Snapshot(QuotaProvider.Claude, Now, later, sooner))], Now)!;
        Assert.Same(sooner, pick.Window);
    }

    [Fact]
    public void Tile_modes()
    {
        var claude = Fresh(QuotaFixtures.Snapshot(QuotaProvider.Claude, Now, QuotaFixtures.Window("five_hour", 10, Now.AddHours(1))));
        var codex = Fresh(QuotaFixtures.Snapshot(QuotaProvider.Codex, Now, QuotaFixtures.Window("codex/primary", 90, Now.AddHours(1))));
        var off = new ProviderState(QuotaProvider.Codex, QuotaStatus.Disabled, null);
        Assert.Equal([QuotaProvider.Claude], QuotaSelector.TileItems(QuotaTileMode.Claude, claude, codex, QuotaWindowKind.Session, QuotaWindowKind.Session, Now).Select(i => i.Provider));
        Assert.Equal([QuotaProvider.Codex], QuotaSelector.TileItems(QuotaTileMode.Codex, claude, codex, QuotaWindowKind.Session, QuotaWindowKind.Session, Now).Select(i => i.Provider));
        Assert.Equal([QuotaProvider.Codex], QuotaSelector.TileItems(QuotaTileMode.MostConstrained, claude, codex, QuotaWindowKind.Session, QuotaWindowKind.Session, Now).Select(i => i.Provider));
        Assert.Equal([QuotaProvider.Claude, QuotaProvider.Codex], QuotaSelector.TileItems(QuotaTileMode.Rotate, claude, codex, QuotaWindowKind.Session, QuotaWindowKind.Session, Now).Select(i => i.Provider));
        Assert.Equal([QuotaProvider.Claude], QuotaSelector.TileItems(QuotaTileMode.Rotate, claude, off, QuotaWindowKind.Session, QuotaWindowKind.Session, Now).Select(i => i.Provider));
    }

    [Fact]
    public void A_missing_preferred_window_falls_back_to_one_the_provider_has_and_keeps_its_own_label()
    {
        var weeklyOnly = QuotaFixtures.Codex("codex-weekly-reached.json");
        var w = QuotaSelector.Preferred(weeklyOnly, QuotaWindowKind.Session, QuotaFixtures.Now)!;
        Assert.Equal(("Weekly", QuotaWindowKind.Weekly), (w.Label, w.Kind));
        Assert.Null(QuotaSelector.Preferred(QuotaFixtures.Codex("codex-missing-buckets.json"), QuotaWindowKind.Session, QuotaFixtures.Now));
    }
}

[Trait("Category", "Unit")]
public class CodexRateLimitsTests
{
    private static readonly DateTimeOffset Now = QuotaFixtures.Now;

    [Fact]
    public void Multiple_buckets_and_windows_are_all_kept()
    {
        var s = QuotaFixtures.Codex("codex-multi-bucket.json");
        Assert.Equal(["codex/primary", "codex/secondary", "codex/spend", "gpt-5.5-pro/primary"], s.Windows.Select(w => w.Id));
        Assert.Equal(["Session (5h)", "Weekly", "Spend limit", "GPT-5.5 Pro · weekly"], s.Windows.Select(w => w.Label));
        Assert.Equal([QuotaWindowKind.Session, QuotaWindowKind.Weekly, QuotaWindowKind.Spend, QuotaWindowKind.Model], s.Windows.Select(w => w.Kind));
        Assert.Equal(64.5, s.Windows[0].ReportedUsedPercent);
        Assert.Equal(Now.AddMinutes(68), s.Windows[0].ResetsAt);
        var spend = s.Windows[2];
        Assert.Equal((75d, 25d, "25 of 100 used"), (spend.ReportedRemainingPercent!.Value, spend.UsedPercent!.Value, spend.Detail));
        Assert.Equal("Pro", s.Plan);
        Assert.False(s.Blocked);
    }

    [Fact]
    public void Credits_and_reset_credit_expiry_come_from_the_provider()
    {
        var s = QuotaFixtures.Codex("codex-multi-bucket.json");
        Assert.Equal(new QuotaCredits(true, false, "12.50", "credits", null, null), s.Credits);
        // Next expiry among *available* reset credits only (the redeemed one expires sooner but doesn't count).
        Assert.Equal(new ResetCreditsInfo(2, DateTimeOffset.FromUnixTimeSeconds(1794000000)), s.ResetCredits);
    }

    [Fact]
    public void A_reached_limit_is_reported_as_blocked_with_its_reason()
    {
        var s = QuotaFixtures.Codex("codex-weekly-reached.json");
        var w = Assert.Single(s.Windows);
        Assert.Equal(("Weekly", 100d, true), (w.Label, w.ReportedUsedPercent!.Value, w.Reached));
        Assert.Equal("0% remaining", QuotaFormat.Value(w, Now, remaining: true));
        Assert.Equal((true, "Usage limit reached"), (s.Blocked!.Value, s.BlockedReason));
        Assert.Equal("Pro Lite", s.Plan);
        Assert.Equal(new QuotaCredits(false, false, "0", "credits", null, null), s.Credits);
        Assert.Equal(new ResetCreditsInfo(0, null), s.ResetCredits);
    }

    [Fact]
    public void Missing_buckets_mean_no_windows_and_unknown_blocking_not_zero_or_false()
    {
        var s = QuotaFixtures.Codex("codex-missing-buckets.json");
        Assert.Empty(s.Windows);
        Assert.Null(s.Blocked);
        Assert.Null(s.Plan);
        Assert.Null(s.Credits);
        Assert.Null(s.ResetCredits);
    }

    [Theory]
    [InlineData("""{ "rateLimits": "not an object" }""")]
    [InlineData("""{ "ordinaryUsageAllowed": true }""")]
    [InlineData("""{ "rateLimits": { "limitId": "codex", "primary": { "usedPercent": "64", "windowDurationMins": 300, "resetsAt": 1 } } }""")]
    [InlineData("""[1, 2, 3]""")]
    public void A_malformed_response_is_a_bad_response_error(string json)
    {
        var ex = Assert.Throws<QuotaSourceException>(() => CodexRateLimits.Parse(JsonDocument.Parse(json).RootElement, Now));
        Assert.Equal(QuotaErrorKind.BadResponse, ex.Kind);
    }

    [Fact]
    public void A_sparse_update_replaces_only_what_it_carries_and_never_clears()
    {
        var read = QuotaFixtures.Codex("codex-multi-bucket.json");
        var merged = CodexRateLimits.Merge(read, QuotaFixtures.Json("codex-update-sparse.json"), Now.AddMinutes(1));
        Assert.Equal(70, merged.Windows.Single(w => w.Id == "codex/primary").ReportedUsedPercent);
        Assert.Equal(30, merged.Windows.Single(w => w.Id == "codex/secondary").ReportedUsedPercent);      // null in the update: kept
        Assert.Equal(12, merged.Windows.Single(w => w.Id == "gpt-5.5-pro/primary").ReportedUsedPercent);   // other bucket: kept
        Assert.Equal(read.Credits, merged.Credits);                                                       // null credits: kept
        Assert.Equal(read.ResetCredits, merged.ResetCredits);
        Assert.Equal("Plus", merged.Plan);                                                                // plan change applied
        Assert.Equal(Now.AddMinutes(1), merged.ObservedAt);
    }

    [Fact]
    public void Plan_changes_between_reads_are_picked_up()
    {
        Assert.Equal("Pro", QuotaFixtures.Codex("codex-multi-bucket.json").Plan);
        Assert.Equal("Pro Lite", QuotaFixtures.Codex("codex-weekly-reached.json").Plan);
    }

    [Theory]
    [InlineData("prolite", "Pro Lite")]
    [InlineData("promax", "Pro Max")]
    [InlineData("self_serve_business_usage_based", "Self Serve Business Usage Based")]
    [InlineData("unknown", null)]
    [InlineData(null, null)]
    public void Plan_names(string? raw, string? expected) => Assert.Equal(expected, CodexRateLimits.PlanName(raw));

    [Theory]
    [InlineData("gpt-5.5-pro", "GPT-5.5 Pro")]
    [InlineData("gpt-5.1-codex-max", "GPT-5.1 Codex Max")]
    [InlineData("o4-mini", "O4 Mini")]
    [InlineData("", null)]
    public void Model_names(string slug, string? expected) => Assert.Equal(expected, CodexRateLimits.ModelName(slug));

    [Theory]
    [InlineData("unexpected status 401 Unauthorized", QuotaErrorKind.SignInRequired)]
    [InlineData("Not logged in. Run codex login", QuotaErrorKind.SignInRequired)]
    [InlineData("unexpected status 429 Too Many Requests", QuotaErrorKind.RateLimited)]
    [InlineData("error sending request for url (https://example.invalid/usage)", QuotaErrorKind.Offline)]
    [InlineData("dns error: failed to lookup address", QuotaErrorKind.Offline)]
    [InlineData("something else entirely", QuotaErrorKind.Failed)]
    [InlineData(null, QuotaErrorKind.Failed)]
    public void Errors_are_classified_for_the_ui(string? message, QuotaErrorKind expected) =>
        Assert.Equal(expected, CodexRateLimits.Classify(message));
}

[Trait("Category", "Unit")]
public class CodexRpcSessionTests
{
    private static string Init => """{"id":1,"result":{"userAgent":"fixture","codexHome":"C:\\Users\\fixture-person\\.codex","platformFamily":"windows","platformOs":"windows"}}""";
    private static string Compact(string fixture) => JsonSerializer.Serialize(QuotaFixtures.Json(fixture));

    private static async Task<(QuotaSnapshot? Snapshot, QuotaSourceException? Error, string[] Methods)> Run(params string[] serverLines)
    {
        var toServer = new StringWriter();
        var session = new CodexRpcSession(new StringReader(string.Join("\n", serverLines) + "\n"), toServer);
        QuotaSnapshot? snapshot = null;
        QuotaSourceException? error = null;
        try { snapshot = await session.ReadRateLimitsAsync(() => QuotaFixtures.Now, "1.2.0", TimeSpan.FromMilliseconds(200), CancellationToken.None); }
        catch (QuotaSourceException ex) { error = ex; }
        string[] methods = toServer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement.GetProperty("method").GetString()!).ToArray();
        return (snapshot, error, methods);
    }

    [Fact]
    public async Task Handshake_then_one_read_only_request_and_later_updates_are_merged()
    {
        var (s, error, methods) = await Run(Init,
            """{"method":"account/updated","params":{}}""",
            "{\"id\":2,\"result\":" + Compact("codex-multi-bucket.json") + "}",
            "{\"method\":\"account/rateLimits/updated\",\"params\":" + Compact("codex-update-sparse.json") + "}");
        Assert.Null(error);
        Assert.Equal(["initialize", "initialized", "account/rateLimits/read"], methods); // nothing that changes the account
        Assert.Equal(70, s!.Windows.Single(w => w.Id == "codex/primary").ReportedUsedPercent);
        Assert.Equal("Plus", s.Plan);
    }

    [Theory]
    [InlineData("unexpected status 401 Unauthorized: C:\\\\Users\\\\fixture-person\\\\.codex\\\\auth.json", QuotaErrorKind.SignInRequired)]
    [InlineData("unexpected status 429 Too Many Requests", QuotaErrorKind.RateLimited)]
    [InlineData("error sending request for url", QuotaErrorKind.Offline)]
    [InlineData("internal", QuotaErrorKind.Failed)]
    public async Task Error_responses_become_explained_errors_without_the_raw_text(string message, QuotaErrorKind kind)
    {
        var (s, error, _) = await Run(Init, "{\"id\":2,\"error\":{\"code\":-32603,\"message\":\"" + message + "\"}}");
        Assert.Null(s);
        Assert.Equal(kind, error!.Kind);
        Assert.DoesNotContain("fixture-person", error.Message);
        Assert.DoesNotContain("auth.json", error.Message);
    }

    [Fact]
    public async Task A_server_that_closes_early_or_sends_garbage_is_an_error()
    {
        Assert.Equal(QuotaErrorKind.Failed, (await Run(Init)).Error!.Kind);
        Assert.Equal(QuotaErrorKind.BadResponse, (await Run(Init, "this is not json")).Error!.Kind);
        Assert.Equal(QuotaErrorKind.BadResponse, (await Run(Init, """{"id":2,"result":{"rateLimits":7}}""")).Error!.Kind);
    }
}

[Trait("Category", "Unit")]
public class ClaudeStatusLineTests
{
    private static readonly DateTimeOffset Now = QuotaFixtures.Now;

    [Fact]
    public void Five_hour_weekly_and_spend_windows_are_read_from_the_documented_fields()
    {
        var s = ClaudeStatusLine.Normalize(QuotaFixtures.Json("claude-statusline-full.json"), Now);
        Assert.Equal(["five_hour", "seven_day", "spend_limit"], s.Windows.Select(w => w.Id));
        Assert.Equal(["Session (5h)", "Weekly", "Spend limit (monthly)"], s.Windows.Select(w => w.Label));
        Assert.Equal([23.5, 41.2, 112.4], s.Windows.Select(w => w.ReportedUsedPercent!.Value));
        Assert.Equal(Now.AddMinutes(68), s.Windows[0].ResetsAt);
        Assert.Equal("$562.00 of $500 (monthly) (estimated)", s.Windows[2].Detail);
        Assert.Equal("0% remaining (over limit)", QuotaFormat.Value(s.Windows[2], Now, remaining: true));
    }

    [Fact]
    public void Absent_or_malformed_windows_are_missing_not_zero()
    {
        var partial = ClaudeStatusLine.Normalize(QuotaFixtures.Json("claude-statusline-partial.json"), Now);
        Assert.Equal(["seven_day"], partial.Windows.Select(w => w.Id)); // five_hour had a string percentage: skipped
        var none = ClaudeStatusLine.Normalize(QuotaFixtures.Json("claude-statusline-no-ratelimits.json"), Now);
        Assert.Empty(none.Windows);
        Assert.Throws<QuotaSourceException>(() => ClaudeStatusLine.Normalize(JsonDocument.Parse("[1]").RootElement, Now));
    }

    [Fact]
    public void The_bridge_stores_only_normalized_figures_and_prints_used()
    {
        string file = Storage.PathFor(ClaudeStatusLine.FileName);
        File.Delete(file);
        var output = new StringWriter();
        int code = ClaudeStatusLine.RunBridge(new StringReader(QuotaFixtures.Text("claude-statusline-full.json")), output, () => Now);

        Assert.Equal(0, code);
        Assert.Equal("5h 23.5% used, resets in 1h 8m  ·  7d 41.2% used, resets in 3d 0h  ·  spend 112.4% used, resets in 19d 16h", output.ToString());
        string stored = File.ReadAllText(file);
        foreach (string secret in QuotaFixtures.Secrets) Assert.DoesNotContain(secret, stored, StringComparison.OrdinalIgnoreCase);
        var back = ClaudeStatusLine.ReadLatest()!;
        Assert.Equal(3, back.Windows.Count);
        Assert.Equal(Now, back.ObservedAt);
        File.Delete(file);
    }

    [Fact]
    public void The_bridge_never_fails_claude_code()
    {
        var output = new StringWriter();
        Assert.Equal(0, ClaudeStatusLine.RunBridge(new StringReader("{ not json"), output, () => Now));
        Assert.Equal("Strata: Claude usage unavailable", output.ToString());
        var waiting = new StringWriter();
        ClaudeStatusLine.RunBridge(new StringReader(QuotaFixtures.Text("claude-statusline-no-ratelimits.json")), waiting, () => Now);
        Assert.Equal("Strata: waiting for Claude usage", waiting.ToString());
        File.Delete(Storage.PathFor(ClaudeStatusLine.FileName));
    }
}

[Trait("Category", "Unit")]
public class ClaudeCodeConnectorTests
{
    private static (ClaudeCodeConnector Connector, string Path) Temp(string? content)
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "strata-claude-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, "settings.json");
        if (content is not null) File.WriteAllText(path, content);
        return (new ClaudeCodeConnector(path), path);
    }

    private const string Command = "\"C:\\Program Files\\Strata\\Strata.exe\" --claude-statusline";

    [Fact]
    public void Connect_adds_only_the_status_line_keeps_everything_else_and_backs_up()
    {
        var (c, path) = Temp("""{ "model": "fixture", "permissions": { "allow": ["Bash(ls)"] } }""");
        Assert.Equal(ClaudeConnection.NotConnected, c.Status());
        Assert.True(c.Connect(Command));
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("fixture", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal("Bash(ls)", doc.RootElement.GetProperty("permissions").GetProperty("allow")[0].GetString());
        Assert.Equal(Command, doc.RootElement.GetProperty("statusLine").GetProperty("command").GetString());
        Assert.Equal("command", doc.RootElement.GetProperty("statusLine").GetProperty("type").GetString());
        Assert.True(File.Exists(path + ".strata-backup"));
        Assert.Equal(ClaudeConnection.Connected, c.Status());
    }

    [Fact]
    public void Connect_keeps_the_users_own_characters_unescaped()
    {
        var (c, path) = Temp("""{ "permissions": { "allow": ["Bash(git log --format=<%h> && echo 'ok' + more)"] }, "note": "café ñandú 日本" }""");
        Assert.True(c.Connect(Command));
        string text = File.ReadAllText(path);
        Assert.Contains("Bash(git log --format=<%h> && echo 'ok' + more)", text);
        Assert.Contains("café ñandú 日本", text);
        Assert.DoesNotContain("\\u00", text);
        Assert.True(c.Disconnect());
        Assert.Contains("café ñandú 日本", File.ReadAllText(path));
    }

    [Fact]
    public void Disconnect_restores_the_exact_original_file()
    {
        string original = "{\r\n    \"model\": \"fixture\",\r\n    \"effortLevel\": \"high\"\r\n}\r\n"; // 4-space indent, CRLF
        var (c, path) = Temp(original);
        Assert.True(c.Connect(Command));
        Assert.True(c.Connect(Command));                  // reconnecting must not back up the connected state
        Assert.NotEqual(original, File.ReadAllText(path));
        Assert.True(c.Disconnect());
        Assert.Equal(original, File.ReadAllText(path));   // byte for byte, formatting included
        Assert.False(File.Exists(path + ".strata-backup"));
    }

    [Fact]
    public void Disconnect_keeps_changes_made_while_connected()
    {
        var (c, path) = Temp("""{ "model": "fixture" }""");
        c.Connect(Command);
        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        root["effortLevel"] = "max";                      // the user (or Claude Code) changed something meanwhile
        File.WriteAllText(path, root.ToJsonString());
        Assert.True(c.Disconnect());
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("max", doc.RootElement.GetProperty("effortLevel").GetString());
        Assert.False(doc.RootElement.TryGetProperty("statusLine", out _));
        Assert.True(File.Exists(path + ".strata-backup")); // the pre-connect original stays available
    }

    [Fact]
    public void A_settings_file_created_by_connect_is_removed_by_disconnect()
    {
        var (c, path) = Temp(null);
        Assert.True(c.Connect(Command));
        Assert.True(c.Disconnect());
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Another_status_line_is_never_replaced_or_removed()
    {
        string original = """{ "statusLine": { "type": "command", "command": "~/.claude/statusline.sh" } }""";
        var (c, path) = Temp(original);
        Assert.Equal(ClaudeConnection.OtherStatusLine, c.Status());
        Assert.False(c.Connect(Command));
        Assert.True(c.Disconnect());
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public void Disconnect_removes_only_strata()
    {
        var (c, path) = Temp("""{ "model": "fixture" }""");
        c.Connect(Command);
        Assert.True(c.Disconnect());
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.False(doc.RootElement.TryGetProperty("statusLine", out _));
        Assert.Equal("fixture", doc.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public void A_missing_file_is_created_and_an_unreadable_one_is_left_alone()
    {
        var (fresh, freshPath) = Temp(null);
        Assert.True(fresh.Connect(Command));
        Assert.True(File.Exists(freshPath));
        var (broken, brokenPath) = Temp("{ this is not json");
        Assert.Equal(ClaudeConnection.SettingsUnreadable, broken.Status());
        Assert.False(broken.Connect(Command));
        Assert.Equal("{ this is not json", File.ReadAllText(brokenPath));
    }

    [Fact]
    public void The_bridge_command_names_a_non_default_data_folder()
    {
        Assert.Equal(Command, ClaudeCodeConnector.BridgeCommand(@"C:\Program Files\Strata\Strata.exe", null));
        Assert.EndsWith("--data-root \"D:\\sandbox\"", ClaudeCodeConnector.BridgeCommand(@"C:\Strata.exe", @"D:\sandbox"));
    }
}

[Trait("Category", "Unit")]
public class QuotaMonitorTests
{
    internal sealed class FakeSource : IQuotaSource
    {
        public int Calls;
        public Func<Task<QuotaSnapshot>> Next = () => throw new InvalidOperationException("no reply set");
        public Task<QuotaSnapshot> ReadAsync(QuotaSnapshot? last, CancellationToken ct) { Calls++; return Next(); }
    }

    private static (QuotaMonitor Monitor, FakeSource Source, Func<DateTimeOffset, DateTimeOffset> SetNow, QuotaSettings Settings) Make(
        QuotaSnapshot? claudeFile = null, bool claudeConnected = true, QuotaSnapshot? stored = null, IQuotaSource? claudeSource = null)
    {
        var now = QuotaFixtures.Now;
        var settings = new QuotaSettings();
        var source = new FakeSource();
        var monitor = new QuotaMonitor(settings, source, claudeSource, () => claudeFile, () => claudeConnected,
            stored is null ? null : new QuotaStore { Codex = stored }, null, () => now);
        return (monitor, source, t => now = t, settings);
    }

    [Fact]
    public async Task A_manual_refresh_joins_one_already_running()
    {
        var (m, src, _, _) = Make();
        var reply = new TaskCompletionSource<QuotaSnapshot>();
        src.Next = () => reply.Task;
        var first = m.RefreshAsync();
        var second = m.RefreshAsync();
        Assert.Same(first, second);
        Assert.True(m.Refreshing);
        reply.SetResult(QuotaFixtures.Codex("codex-weekly-reached.json"));
        await first;
        Assert.Equal(1, src.Calls);
        src.Next = () => Task.FromResult(QuotaFixtures.Codex("codex-weekly-reached.json"));
        await m.RefreshAsync();
        Assert.Equal(2, src.Calls);
    }

    [Fact]
    public async Task Before_any_reading_providers_wait_and_never_show_zero()
    {
        var (m, _, _, _) = Make(claudeConnected: true);
        Assert.Equal(QuotaStatus.WaitingForFirstSample, m.Codex.Status);
        Assert.Equal(QuotaStatus.WaitingForFirstSample, m.Claude.Status);
        Assert.Equal(QuotaStatus.WaitingForFirstSample, Make(claudeConnected: false).Monitor.Claude.Status);
        // A status line seen before the session's first response has no windows yet: still waiting.
        var empty = ClaudeStatusLine.Normalize(QuotaFixtures.Json("claude-statusline-no-ratelimits.json"), QuotaFixtures.Now);
        var (m2, _, _, _) = Make(claudeFile: empty);
        m2.Tick();
        Assert.Equal(QuotaStatus.WaitingForFirstSample, m2.Claude.Status);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task An_error_keeps_the_last_reading_marked_stale_with_the_reason()
    {
        var (m, src, setNow, _) = Make();
        src.Next = () => Task.FromResult(QuotaFixtures.Codex("codex-multi-bucket.json"));
        await m.RefreshAsync();
        Assert.Equal(QuotaStatus.Fresh, m.Codex.Status);

        setNow(QuotaFixtures.Now.AddMinutes(6));
        src.Next = () => throw new QuotaSourceException(QuotaErrorKind.RateLimited, CodexRpcSession.Friendly(QuotaErrorKind.RateLimited));
        await m.RefreshAsync();
        var st = m.Codex;
        Assert.Equal((QuotaStatus.Stale, QuotaErrorKind.RateLimited), (st.Status, st.Error));
        Assert.NotNull(st.Snapshot);                                        // the stale cache is still shown
        Assert.Equal(QuotaFixtures.Now.AddMinutes(6 + 15), m.NextCodexRead); // a 429 waits at least 15 minutes
    }

    [Fact]
    public async Task Failures_back_off_and_a_success_resets_it()
    {
        var (m, src, setNow, _) = Make();
        src.Next = () => throw new QuotaSourceException(QuotaErrorKind.Offline, "offline");
        await m.RefreshAsync();
        Assert.Equal((QuotaStatus.Error, QuotaErrorKind.Offline), (m.Codex.Status, m.Codex.Error));
        Assert.Equal(QuotaFixtures.Now.AddMinutes(5), m.NextCodexRead);
        await m.RefreshAsync();
        await m.RefreshAsync();
        Assert.Equal(QuotaFixtures.Now.AddMinutes(20), m.NextCodexRead);   // 5, 10, 20
        src.Next = () => Task.FromResult(QuotaFixtures.Codex("codex-weekly-reached.json"));
        await m.RefreshAsync();
        Assert.Equal(QuotaFixtures.Now.AddMinutes(5), m.NextCodexRead);
        setNow(QuotaFixtures.Now);
    }

    [Fact]
    public async Task Readings_go_stale_with_time()
    {
        var (m, src, setNow, settings) = Make(claudeFile: ClaudeStatusLine.Normalize(QuotaFixtures.Json("claude-statusline-full.json"), QuotaFixtures.Now));
        src.Next = () => Task.FromResult(QuotaFixtures.Codex("codex-multi-bucket.json"));
        await m.RefreshAsync();
        Assert.Equal((QuotaStatus.Fresh, QuotaStatus.Fresh), (m.Claude.Status, m.Codex.Status));
        setNow(QuotaFixtures.Now.AddMinutes(settings.RefreshMinutes * 2 + 1));
        Assert.Equal(QuotaStatus.Stale, m.Codex.Status);
        setNow(QuotaFixtures.Now.AddMinutes(settings.StaleMinutes + 1));
        Assert.Equal(QuotaStatus.Stale, m.Claude.Status);
    }

    [Fact]
    public async Task Tick_reads_when_due_and_not_again_until_the_next_period()
    {
        var (m, src, setNow, _) = Make();
        var reply = new TaskCompletionSource<QuotaSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        src.Next = () => reply.Task;
        m.Tick();
        m.Tick();
        Assert.Equal(1, m.CodexReads);
        var refresh = m.RefreshAsync(); // The pending reply guarantees this joins Tick's read.
        reply.SetResult(QuotaFixtures.Codex("codex-weekly-reached.json"));
        await refresh.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, src.Calls);
        m.Tick();
        Assert.Equal(1, m.CodexReads);
        reply = new TaskCompletionSource<QuotaSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        setNow(QuotaFixtures.Now.AddMinutes(5));
        m.Tick();
        Assert.Equal(2, m.CodexReads);
        refresh = m.RefreshAsync();
        reply.SetResult(QuotaFixtures.Codex("codex-weekly-reached.json"));
        await refresh.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, src.Calls);
    }

    [Fact]
    public async Task Disabled_providers_are_never_read()
    {
        var (m, src, _, settings) = Make();
        settings.CodexEnabled = false;
        settings.ClaudeEnabled = false;
        m.Tick();
        await m.RefreshAsync();
        Assert.Equal(0, src.Calls);
        Assert.Equal((QuotaStatus.Disabled, QuotaStatus.Disabled), (m.Claude.Status, m.Codex.Status));
    }

    [Fact]
    public void A_stored_reading_is_shown_at_start_but_as_stale()
    {
        var stored = QuotaFixtures.Codex("codex-multi-bucket.json") with { ObservedAt = QuotaFixtures.Now.AddHours(-3) };
        var (m, _, _, _) = Make(stored: stored);
        Assert.Equal(QuotaStatus.Stale, m.Codex.Status);
        Assert.Same(stored, m.Codex.Snapshot);
    }

    [Fact]
    public void Stored_snapshots_hold_no_secrets_raw_responses_or_paths()
    {
        Storage.Save(QuotaStore.FileName, new QuotaStore { Codex = QuotaFixtures.Codex("codex-multi-bucket.json") });
        string stored = File.ReadAllText(Storage.PathFor(QuotaStore.FileName));
        foreach (string secret in QuotaFixtures.Secrets) Assert.DoesNotContain(secret, stored, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"reportedUsedPercent\": 64.5", stored);
        Assert.DoesNotContain("\"usedPercent\"", stored);       // derived figures are not stored
        Assert.DoesNotContain("\"remainingPercent\"", stored);
        var back = Storage.Load<QuotaStore>(QuotaStore.FileName).Codex!;
        Assert.Equal(QuotaFixtures.Codex("codex-multi-bucket.json").Windows, back.Windows);
        File.Delete(Storage.PathFor(QuotaStore.FileName));
    }

    [Fact]
    public void Sample_data_is_labelled_as_sample()
    {
        var m = QuotaMonitor.Sample(new QuotaSettings(), QuotaFixtures.Now);
        Assert.True(m.Claude.Snapshot!.IsSample);
        Assert.True(m.Codex.Snapshot!.IsSample);
        Assert.Equal(("Sample data", "Sample data"), (m.Claude.Snapshot.Source, m.Codex.Snapshot.Source));
    }
}

[Trait("Category", "Unit")]
public class ClaudeUsageTests
{
    private static readonly DateTimeOffset Now = QuotaFixtures.Now;
    private static IEnumerable<string> Lines(string name) => QuotaFixtures.Text(name).Split('\n');

    [Fact]
    public void Session_weekly_and_model_windows_come_from_the_usage_report()
    {
        var s = ClaudeUsageReport.Parse(Lines("claude-usage-stream.jsonl"), "Pro", Now);
        Assert.Equal(["session", "weekly_all", "weekly_scoped/Opus"], s.Windows.Select(w => w.Id));
        Assert.Equal(["Session (5h)", "Weekly", "Opus · weekly"], s.Windows.Select(w => w.Label));
        Assert.Equal([QuotaWindowKind.Session, QuotaWindowKind.Weekly, QuotaWindowKind.Model], s.Windows.Select(w => w.Kind));
        Assert.Equal([32d, 44d, 100d], s.Windows.Select(w => w.ReportedUsedPercent!.Value));
        Assert.Equal(DateTimeOffset.Parse("2026-10-28T21:01:20Z"), s.Windows[0].ResetsAt);
        Assert.True(s.Windows[2].Reached);
        Assert.Equal(("Pro", ClaudeUsageReport.SourceName), (s.Plan, s.Source));
        // Extra usage: only whether it is on. Its amounts have no documented unit, so none is shown.
        Assert.Equal(new QuotaCredits(false, null, null, "extra usage", null, null), s.Credits);
    }

    [Fact]
    public void Nothing_but_the_windows_and_plan_is_kept()
    {
        var s = ClaudeUsageReport.Parse(Lines("claude-usage-stream.jsonl"), "Pro", Now);
        Storage.Save(QuotaStore.FileName, new QuotaStore { Claude = s });
        string stored = File.ReadAllText(Storage.PathFor(QuotaStore.FileName));
        foreach (string secret in QuotaFixtures.Secrets.Concat(["Fixture /usage text", "msg-fixture", "1.23", "2500", "claude-opus-fixture", "uuid-fixture"]))
            Assert.DoesNotContain(secret, stored, StringComparison.OrdinalIgnoreCase);
        File.Delete(Storage.PathFor(QuotaStore.FileName));
    }

    [Fact]
    public void No_rate_limits_means_no_windows_not_zeros()
    {
        var s = ClaudeUsageReport.Parse(Lines("claude-usage-nolimits.jsonl"), "Pro", Now);
        Assert.Empty(s.Windows);
        Assert.Null(QuotaSelector.Preferred(s, QuotaWindowKind.Session, Now));
    }

    [Fact]
    public void A_signed_out_run_is_a_sign_in_error_and_a_missing_report_a_bad_response()
    {
        var ex = Assert.Throws<QuotaSourceException>(() => ClaudeUsageReport.Parse(Lines("claude-usage-error.jsonl"), null, Now));
        Assert.Equal(QuotaErrorKind.SignInRequired, ex.Kind);
        Assert.Equal(ClaudeUsageReport.SignInText, ex.Message);
        var bad = Assert.Throws<QuotaSourceException>(() => ClaudeUsageReport.Parse(["{\"type\":\"result\",\"is_error\":false}"], null, Now));
        Assert.Equal(QuotaErrorKind.BadResponse, bad.Kind);
    }

    [Theory]
    [InlineData("{\"loggedIn\":true,\"authMethod\":\"claude.ai\",\"email\":\"someone@example.invalid\",\"orgId\":\"org-fixture\",\"subscriptionType\":\"pro\"}", 0, "Pro")]
    [InlineData("{\"loggedIn\":true,\"subscriptionType\":\"max\"}", 0, "Max")]
    [InlineData("{\"loggedIn\":true}", 0, null)]
    public void The_plan_comes_from_auth_status_and_nothing_else_is_kept(string json, int code, string? plan) =>
        Assert.Equal(plan, ClaudeUsageReport.PlanFromAuthStatus(json, code));

    [Theory]
    [InlineData("{\"loggedIn\":false}", 1)]
    [InlineData("{\"loggedIn\":false}", 0)]
    [InlineData("Not logged in", 1)]
    public void Signed_out_is_a_sign_in_error(string output, int code) =>
        Assert.Equal(QuotaErrorKind.SignInRequired, Assert.Throws<QuotaSourceException>(() => ClaudeUsageReport.PlanFromAuthStatus(output, code)).Kind);

    [Fact]
    public void The_background_run_is_isolated_unsaved_and_spends_no_tokens()
    {
        // /usage is answered locally by Claude Code; these flags keep the run from loading the user's hooks, plugins,
        // MCP servers or CLAUDE.md, and from saving a session.
        Assert.Equal(["-p", "/usage"], ClaudeUsageReport.UsageArguments.Take(2));
        Assert.Contains("--no-session-persistence", ClaudeUsageReport.UsageArguments);
        Assert.Contains("--safe-mode", ClaudeUsageReport.UsageArguments);
        Assert.Contains("--strict-mcp-config", ClaudeUsageReport.UsageArguments);
    }

    [Fact]
    public void The_newest_extension_copy_wins_by_version_not_by_name()
    {
        Assert.True(ClaudeUsageSource.ExtensionVersion("anthropic.claude-code-2.1.308-win32-x64") > ClaudeUsageSource.ExtensionVersion("anthropic.claude-code-2.1.99-win32-x64"));
        Assert.Equal(new Version(0, 0), ClaudeUsageSource.ExtensionVersion("something-else"));
    }

    private static (QuotaMonitor M, QuotaMonitorTests.FakeSource Codex, QuotaMonitorTests.FakeSource Claude, Action<DateTimeOffset> SetNow) Make(
        Func<QuotaSnapshot?>? statusLine = null)
    {
        var now = Now;
        var codex = new QuotaMonitorTests.FakeSource { Next = () => Task.FromResult(QuotaFixtures.Codex("codex-weekly-reached.json")) };
        var claude = new QuotaMonitorTests.FakeSource { Next = () => Task.FromResult(ClaudeUsageReport.Parse(Lines("claude-usage-stream.jsonl"), "Pro", now)) };
        var m = new QuotaMonitor(new QuotaSettings(), codex, claude, statusLine ?? (() => null), () => false, null, null, () => now);
        return (m, codex, claude, t => now = t);
    }

    [Fact]
    public async Task One_refresh_reads_both_providers_once()
    {
        var (m, codex, claude, _) = Make();
        var reply = new TaskCompletionSource<QuotaSnapshot>();
        claude.Next = () => reply.Task;                       // keep the first refresh in flight
        var first = m.RefreshAsync();
        var second = m.RefreshAsync();
        Assert.Same(first, second);
        reply.SetResult(ClaudeUsageReport.Parse(Lines("claude-usage-stream.jsonl"), "Pro", Now));
        await first;
        Assert.Equal((1, 1), (codex.Calls, claude.Calls));
        Assert.Equal((QuotaStatus.Fresh, ClaudeUsageReport.SourceName), (m.Claude.Status, m.Claude.Snapshot!.Source));
    }

    [Fact]
    public async Task Each_provider_backs_off_on_its_own()
    {
        var (m, codex, _, _) = Make();
        codex.Next = () => throw new QuotaSourceException(QuotaErrorKind.RateLimited, "slow down");
        await m.RefreshAsync();
        Assert.Equal(Now.AddMinutes(15), m.NextCodexRead);
        Assert.Equal(Now.AddMinutes(5), m.NextClaudeRead);
    }

    [Fact]
    public async Task A_newer_status_line_reading_wins_over_the_background_one()
    {
        var line = ClaudeStatusLine.Normalize(QuotaFixtures.Json("claude-statusline-full.json"), Now.AddMinutes(2));
        var (m, _, _, setNow) = Make(() => line);
        await m.RefreshAsync();
        setNow(Now.AddMinutes(3));
        m.Tick();
        Assert.Equal(ClaudeStatusLine.SourceName, m.Claude.Snapshot!.Source);
    }

    [Fact]
    public async Task Signed_out_shows_a_sign_in_and_keeps_any_earlier_reading_as_stale()
    {
        var (m, _, claude, _) = Make();
        await m.RefreshAsync();
        claude.Next = () => throw new QuotaSourceException(QuotaErrorKind.SignInRequired, ClaudeUsageReport.SignInText);
        await m.RefreshAsync();
        Assert.True(m.ClaudeNeedsSignIn);
        Assert.Equal((QuotaStatus.Stale, QuotaErrorKind.SignInRequired), (m.Claude.Status, m.Claude.Error));
        Assert.NotNull(m.Claude.Snapshot);

        var (fresh, _, signedOut, _) = Make();
        signedOut.Next = () => throw new QuotaSourceException(QuotaErrorKind.SignInRequired, ClaudeUsageReport.SignInText);
        await fresh.RefreshAsync();
        Assert.Equal((QuotaStatus.Error, QuotaErrorKind.SignInRequired), (fresh.Claude.Status, fresh.Claude.Error));
        Assert.True(fresh.ClaudeNeedsSignIn);
    }
}
