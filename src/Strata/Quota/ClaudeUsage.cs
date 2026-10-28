using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Strata.Core;

namespace Strata.Quota;

/// <summary>
/// Reads Claude's plan limits the way Codex's are read: in the background, with Claude Code's own built-in
/// <c>/usage</c> command in print mode (<c>claude -p /usage --output-format stream-json</c>). Claude Code answers it
/// locally and attaches a <c>usage_report</c> (Agent SDK SDKUsageReport, documented as experimental) to the reply:
/// no model request, no tokens, and with <c>--no-session-persistence</c> nothing is saved to the user's sessions.
/// <c>claude auth status</c> first says whether a sign-in is needed. Only the limit windows and the plan name are
/// kept: the account's email, organization, session cost and the command's text output are ignored.
/// </summary>
public static class ClaudeUsageReport
{
    public const string SourceName = "Claude Code /usage";

    /// <summary>The arguments for one background read: isolated (--safe-mode loads none of the user's hooks, plugins,
    /// MCP servers or CLAUDE.md), not saved, no MCP configuration.</summary>
    public static readonly string[] UsageArguments =
        ["-p", "/usage", "--output-format", "stream-json", "--verbose", "--no-session-persistence", "--strict-mcp-config", "--safe-mode"];

    /// <summary>The plan from <c>claude auth status</c> JSON; throws SignInRequired when it says the user is signed out.</summary>
    public static string? PlanFromAuthStatus(string json, int exitCode)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(json).RootElement; }
        catch (JsonException)
        {
            if (exitCode != 0) throw new QuotaSourceException(QuotaErrorKind.SignInRequired, SignInText);
            throw new QuotaSourceException(QuotaErrorKind.BadResponse, "Claude Code reported its sign-in status in an unknown shape.");
        }
        bool loggedIn = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("loggedIn", out var l) && l.ValueKind == JsonValueKind.True;
        if (exitCode != 0 || !loggedIn) throw new QuotaSourceException(QuotaErrorKind.SignInRequired, SignInText);
        string? plan = root.TryGetProperty("subscriptionType", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        return plan is null or "" ? null : char.ToUpperInvariant(plan[0]) + plan[1..];
    }

    public const string SignInText = "Sign in to Claude Code, then refresh.";

    /// <summary>Parses the stream-json lines of one <c>/usage</c> run into a snapshot.</summary>
    public static QuotaSnapshot Parse(IEnumerable<string> lines, string? plan, DateTimeOffset observedAt)
    {
        JsonElement? report = null;
        JsonElement? result = null;
        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonElement msg;
            try { msg = JsonDocument.Parse(line).RootElement.Clone(); }
            catch (JsonException) { continue; } // stray non-JSON output (warnings) is not the report
            if (msg.ValueKind != JsonValueKind.Object) continue;
            if (msg.TryGetProperty("usage_report", out var r)) report = r;
            if (Str(msg, "type") == "result") result = msg;
        }
        if (report is null)
        {
            if (result is { } res && res.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True)
            {
                var kind = CodexRateLimits.Classify(Str(res, "result") ?? Str(res, "subtype"));
                throw new QuotaSourceException(kind, kind switch
                {
                    QuotaErrorKind.SignInRequired => SignInText,
                    QuotaErrorKind.Failed => "Claude Code couldn't report usage.",
                    _ => CodexRpcSession.Friendly(kind).Replace("Codex", "Claude Code"),
                });
            }
            throw new QuotaSourceException(QuotaErrorKind.BadResponse, "Claude Code didn't report usage (it may need updating).");
        }

        var windows = new List<QuotaWindow>();
        QuotaCredits? credits = null;
        if (report.Value.ValueKind == JsonValueKind.Object && report.Value.TryGetProperty("rate_limits", out var limits) && limits.ValueKind == JsonValueKind.Object)
        {
            if (limits.TryGetProperty("limits", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var w in list.EnumerateArray())
                    if (Window(w) is { } win) windows.Add(win);
            if (limits.TryGetProperty("extra_usage", out var extra) && extra.ValueKind == JsonValueKind.Object)
                // Only whether extra usage is on: the report doesn't document the units of its amounts, and Strata
                // never shows a figure it can't label correctly.
                credits = extra.TryGetProperty("is_enabled", out var on) && on.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? new QuotaCredits(on.GetBoolean(), null, null, "extra usage", null, null) : null;
        }
        // rate_limits null: Claude Code couldn't get the plan's usage (e.g. a token without the profile scope) -> no windows, not zeros.
        return new QuotaSnapshot
        {
            Provider = QuotaProvider.Claude,
            Source = SourceName,
            Plan = plan,
            Windows = windows,
            Credits = credits,
            ObservedAt = observedAt,
        };
    }

    private static QuotaWindow? Window(JsonElement w)
    {
        if (w.ValueKind != JsonValueKind.Object) return null;
        string kind = Str(w, "kind") ?? "";
        if (!w.TryGetProperty("percent", out var p) || p.ValueKind != JsonValueKind.Number) return null;
        string? model = w.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.Object
            && scope.TryGetProperty("model", out var m) ? ModelName(m) : null;
        var (id, label, windowKind, minutes) = kind switch
        {
            "session" => ("session", "Session (5h)", QuotaWindowKind.Session, (int?)300),
            "weekly_all" => ("weekly_all", "Weekly", QuotaWindowKind.Weekly, (int?)10080),
            "weekly_scoped" => ($"weekly_scoped/{model ?? "scoped"}", $"{model ?? "Scoped"} · weekly", QuotaWindowKind.Model, (int?)10080),
            _ => (kind.Length > 0 ? kind : "limit", Title(kind), QuotaWindowKind.Other, (int?)null),
        };
        double used = p.GetDouble();
        return new QuotaWindow
        {
            Id = id,
            Label = label,
            Kind = windowKind,
            ReportedUsedPercent = used,
            ResetsAt = Str(w, "resets_at") is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null,
            WindowMinutes = minutes,
            Reached = used >= 100,
        };
    }

    private static string? ModelName(JsonElement m) => m.ValueKind switch
    {
        JsonValueKind.String => m.GetString(),
        JsonValueKind.Object => Str(m, "display_name") ?? Str(m, "name") ?? Str(m, "id"),
        _ => null,
    };

    private static string Title(string kind) => kind.Length == 0 ? "Limit"
        : string.Join(' ', kind.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));

    private static string? Str(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>
/// Runs Claude Code hidden for one read: <c>claude auth status</c> (about 0.4 s), then <c>claude -p /usage</c> (about
/// 1.5 s). No window opens and nothing keeps running. When a sign-in is needed, <see cref="StartSignIn"/> opens one
/// visible console with <c>claude auth login</c>: signing in needs the user and their browser.
/// </summary>
public sealed class ClaudeUsageSource : IQuotaSource
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly Func<string?> _locate;
    private readonly QuotaReadRunner _runner;

    public ClaudeUsageSource(Func<string?> locate) : this(locate, Timeout, Process.Start) { }

    internal ClaudeUsageSource(Func<string?> locate, TimeSpan timeout, Func<ProcessStartInfo, Process?> start)
    {
        _locate = locate;
        _runner = new QuotaReadRunner(timeout, start);
    }

    public async Task<QuotaSnapshot> ReadAsync(QuotaSnapshot? last, CancellationToken ct)
    {
        PrivacyGuard.ThrowIfPreview("Claude Code usage");
        try { return await _runner.ReadAsync(ReadCoreAsync, ct).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            throw new QuotaSourceException(QuotaErrorKind.Timeout, "Claude Code didn't answer in time.");
        }
    }

    private async Task<QuotaSnapshot> ReadCoreAsync(QuotaProcessSession processes, CancellationToken ct)
    {
        string exe = _locate() ?? throw new QuotaSourceException(QuotaErrorKind.NotInstalled, NotInstalledText);
        var (authCode, authOut) = await RunAsync(processes, exe, ["auth", "status"], ct).ConfigureAwait(false);
        string? plan = ClaudeUsageReport.PlanFromAuthStatus(authOut, authCode);
        var (_, usageOut) = await RunAsync(processes, exe, ClaudeUsageReport.UsageArguments, ct).ConfigureAwait(false);
        return ClaudeUsageReport.Parse(usageOut.Split('\n'), plan, DateTimeOffset.Now);
    }

    public const string NotInstalledText = "Claude Code isn't installed for the terminal, or Strata can't find it. Install Claude Code, or set quota.claudePath in Strata's settings.json.";

    private static async Task<(int Code, string Out)> RunAsync(QuotaProcessSession processes, string exe, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            // An empty folder of Strata's own, so no project settings, hooks or CLAUDE.md are anywhere near the run.
            WorkingDirectory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "Strata-claude-usage")).FullName,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        psi.Environment["DISABLE_AUTOUPDATER"] = "1";
        QuotaProcess child;
        try { child = processes.Start(psi) ?? throw new QuotaSourceException(QuotaErrorKind.NotInstalled, NotInstalledText); }
        catch (System.ComponentModel.Win32Exception ex) { throw new QuotaSourceException(QuotaErrorKind.NotInstalled, NotInstalledText, ex); }
        using (child)
        {
            var process = child.Process;
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            QuotaReadRunner.Observe(stdout);
            QuotaReadRunner.Observe(process.StandardError.ReadToEndAsync(CancellationToken.None)); // drained, never kept: it can contain paths
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return (process.ExitCode, await stdout.ConfigureAwait(false));
        }
    }

    /// <summary>Opens a visible console running <c>claude auth login</c> (the user signs in in their browser).</summary>
    public static bool StartSignIn(string? exe)
    {
        PrivacyGuard.ThrowIfPreview("Claude Code sign-in");
        if (exe is null) return false;
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = true };
            psi.ArgumentList.Add("auth");
            psi.ArgumentList.Add("login");
            Process.Start(psi)?.Dispose();
            return true;
        }
        catch (Exception ex) { Log.Write($"Claude sign-in could not start: {ex.GetType().Name}"); return false; }
    }

    /// <summary>
    /// Finds Claude Code's CLI: the path set in Settings, then PATH, then the native installer's ~/.local/bin, then the
    /// newest copy shipped with an IDE extension. (The Claude desktop app's own copy is inside its Store package and
    /// can't be run from outside it.) Null when none exists.
    /// </summary>
    public static string? Locate(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath)) return File.Exists(overridePath) ? overridePath : null;
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { string p = Path.Combine(dir.Trim(), "claude.exe"); if (File.Exists(p)) return p; } catch (ArgumentException) { }
        }
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string local = Path.Combine(home, ".local", "bin", "claude.exe");
        if (File.Exists(local)) return local;
        return new[] { ".antigravity-ide", ".vscode", ".vscode-insiders", ".cursor", ".windsurf" }
            .Select(d => Path.Combine(home, d, "extensions"))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.GetDirectories(d, "anthropic.claude-code-*"))
            .Select(d => (Dir: d, Version: ExtensionVersion(d)))
            .OrderByDescending(x => x.Version)
            .Select(x => Path.Combine(x.Dir, "resources", "native-binary", "claude.exe"))
            .FirstOrDefault(File.Exists);
    }

    public static Version ExtensionVersion(string dir) =>
        System.Text.RegularExpressions.Regex.Match(Path.GetFileName(dir), @"claude-code-(\d+\.\d+\.\d+)") is { Success: true } m
            ? Version.Parse(m.Groups[1].Value) : new Version(0, 0);
}
