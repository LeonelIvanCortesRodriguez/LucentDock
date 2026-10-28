using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Strata.Core;

namespace Strata.Quota;

/// <summary>
/// Claude usage comes from Claude Code's documented status-line data: Claude Code runs a status-line command
/// and pipes it JSON whose <c>rate_limits</c> object has <c>five_hour</c>, <c>seven_day</c> and (behind a gateway)
/// <c>spend_limit</c> windows. With Strata connected, that command is <c>Strata.exe --claude-statusline</c>: it keeps
/// only those windows, writes them to <see cref="FileName"/>, and prints a short status line back.
/// Everything else in the input (workspace and transcript paths, session ids, cost, model) is ignored.
/// </summary>
public static class ClaudeStatusLine
{
    public const string FileName = "quota-claude.json";
    public const string SourceName = "Claude Code status line";
    public const string BridgeFlag = "--claude-statusline";

    /// <summary>The normalized snapshot for one status-line input. No rate_limits yet means no windows (not zero).</summary>
    public static QuotaSnapshot Normalize(JsonElement input, DateTimeOffset observedAt)
    {
        if (input.ValueKind != JsonValueKind.Object)
            throw new QuotaSourceException(QuotaErrorKind.BadResponse, "Claude Code sent status-line data in an unknown shape.");
        var windows = new List<QuotaWindow>();
        if (input.TryGetProperty("rate_limits", out var limits) && limits.ValueKind == JsonValueKind.Object)
        {
            Add(limits, "five_hour", "Session (5h)", QuotaWindowKind.Session, 300);
            Add(limits, "seven_day", "Weekly", QuotaWindowKind.Weekly, 10080);
            Add(limits, "spend_limit", "Spend limit", QuotaWindowKind.Spend, null);
        }
        return new QuotaSnapshot { Provider = QuotaProvider.Claude, Source = SourceName, Windows = windows, ObservedAt = observedAt };

        void Add(JsonElement root, string id, string label, QuotaWindowKind kind, int? minutes)
        {
            if (!root.TryGetProperty(id, out var w) || w.ValueKind != JsonValueKind.Object) return;
            if (!w.TryGetProperty("used_percentage", out var used) || used.ValueKind != JsonValueKind.Number) return;
            string? period = Str(w, "period");
            string? detail = Num(w, "used_usd") is double spent && Num(w, "limit_usd") is double limit
                ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"${spent:0.00} of ${limit:0.##}{(period is null ? "" : $" ({period})")} (estimated)") : null;
            double u = used.GetDouble();
            windows.Add(new QuotaWindow
            {
                Id = id,
                Label = kind == QuotaWindowKind.Spend && period is not null ? $"Spend limit ({period})" : label,
                Kind = kind,
                ReportedUsedPercent = u,
                ResetsAt = w.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.Number && r.TryGetInt64(out long s)
                    ? DateTimeOffset.FromUnixTimeSeconds(s) : null,
                WindowMinutes = minutes,
                Reached = u >= 100,
                Detail = detail,
            });
        }
    }

    /// <summary>What the bridge prints in Claude Code: each window with an explicit "used".</summary>
    public static string Render(QuotaSnapshot s, DateTimeOffset now)
    {
        if (s.Windows.Count == 0) return "Strata: waiting for Claude usage";
        return string.Join("  ·  ", s.Windows.Select(w =>
        {
            string name = w.Kind switch { QuotaWindowKind.Session => "5h", QuotaWindowKind.Weekly => "7d", _ => "spend" };
            string? left = QuotaFormat.Countdown(w.ResetsAt, now);
            return $"{name} {QuotaFormat.Value(w, now, remaining: false)}{(left is null ? "" : $", resets in {left}")}";
        }));
    }

    /// <summary>
    /// The status-line command. Always prints something and returns 0: a failure here must never disturb Claude Code.
    /// It writes only the normalized snapshot.
    /// </summary>
    public static int RunBridge(TextReader stdin, TextWriter stdout, Func<DateTimeOffset> now)
    {
        try
        {
            using var doc = JsonDocument.Parse(stdin.ReadToEnd());
            var snapshot = Normalize(doc.RootElement, now());
            Storage.Save(FileName, snapshot);
            stdout.Write(Render(snapshot, now()));
        }
        catch (Exception ex)
        {
            Log.Write($"Claude status line: {ex.GetType().Name}");
            stdout.Write("Strata: Claude usage unavailable");
        }
        stdout.Flush();
        return 0;
    }

    /// <summary>The last snapshot the bridge wrote, with the time it was written; null if it never ran.</summary>
    public static QuotaSnapshot? ReadLatest()
    {
        PrivacyGuard.ThrowIfPreview("Claude Code usage");
        string path = Storage.PathFor(FileName);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<QuotaSnapshot>(File.ReadAllText(path), Storage.Json); }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            throw new QuotaSourceException(QuotaErrorKind.BadResponse, "The saved Claude usage could not be read.", ex);
        }
    }

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}

public enum ClaudeConnection { NotConnected, Connected, OtherStatusLine, SettingsUnreadable }

/// <summary>
/// Connects Strata to Claude Code by setting <c>statusLine</c> in Claude Code's user settings, only when the user
/// asks (Settings > AI usage). It never replaces a status line the user already has, keeps every other setting and
/// backs the file up first. Disconnect restores that exact file when nothing else changed, else removes only its key.
/// Claude Code runs status lines in its terminal interface; the desktop app's Code tab does not (seen with 2.1.308).
/// </summary>
public sealed class ClaudeCodeConnector(string settingsPath)
{
    public string SettingsPath { get; } = settingsPath;

    /// <summary>~/.claude/settings.json, or CLAUDE_CONFIG_DIR's settings.json when that is set.</summary>
    public static ClaudeCodeConnector ForCurrentUser()
    {
        string? dir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (string.IsNullOrWhiteSpace(dir)) dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        return new ClaudeCodeConnector(Path.Combine(dir, "settings.json"));
    }

    /// <summary>The command Claude Code should run: this executable with the bridge flag (and a non-default data folder).</summary>
    public static string BridgeCommand(string exePath, string? dataRoot)
    {
        string cmd = $"\"{exePath}\" {ClaudeStatusLine.BridgeFlag}";
        return dataRoot is null ? cmd : $"{cmd} --data-root \"{dataRoot}\"";
    }

    public ClaudeConnection Status()
    {
        PrivacyGuard.ThrowIfPreview("Claude Code settings");
        if (!File.Exists(SettingsPath)) return ClaudeConnection.NotConnected;
        JsonObject? root;
        try { root = JsonNode.Parse(File.ReadAllText(SettingsPath)) as JsonObject; }
        catch (Exception ex) when (ex is JsonException or IOException) { return ClaudeConnection.SettingsUnreadable; }
        if (root is null) return ClaudeConnection.SettingsUnreadable;
        if (root["statusLine"] is not JsonObject line) return ClaudeConnection.NotConnected;
        return IsOurs(line) ? ClaudeConnection.Connected : ClaudeConnection.OtherStatusLine;
    }

    /// <summary>Adds Strata's status line. Returns false (and changes nothing) if another status line is set or the file is unreadable.</summary>
    public bool Connect(string command)
    {
        PrivacyGuard.ThrowIfPreview("Claude Code settings");
        JsonObject root;
        if (File.Exists(SettingsPath))
        {
            try { root = JsonNode.Parse(File.ReadAllText(SettingsPath)) as JsonObject ?? throw new JsonException("not an object"); }
            catch (Exception ex) when (ex is JsonException or IOException) { Log.Write($"Claude Code settings unreadable: {ex.GetType().Name}"); return false; }
            if (root["statusLine"] is JsonObject existing)
            {
                if (!IsOurs(existing)) return false;
            }
            else File.Copy(SettingsPath, BackupPath, overwrite: true); // the file exactly as it was before Strata touched it
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            root = new JsonObject();
        }
        // Refresh every 60 s so countdowns and resets stay current while a session is idle.
        root["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = command, ["refreshInterval"] = 60 };
        Write(root);
        return true;
    }

    /// <summary>Removes Strata's status line; anything else in the file is left as it is.</summary>
    public bool Disconnect()
    {
        PrivacyGuard.ThrowIfPreview("Claude Code settings");
        if (!File.Exists(SettingsPath)) return true;
        JsonObject root;
        try { root = JsonNode.Parse(File.ReadAllText(SettingsPath)) as JsonObject ?? throw new JsonException("not an object"); }
        catch (Exception ex) when (ex is JsonException or IOException) { return false; }
        if (root["statusLine"] is not JsonObject line || !IsOurs(line)) return true;
        root.Remove("statusLine");
        // Nothing else changed since Connect: put back the exact original file (formatting and all), or no file if
        // there was none. Otherwise keep the user's later changes, drop only Strata's key, and leave the backup.
        JsonNode? before = null;
        bool hadBackup = File.Exists(BackupPath);
        if (hadBackup) try { before = JsonNode.Parse(File.ReadAllText(BackupPath)); } catch (JsonException) { }
        if (hadBackup && before is not null && JsonNode.DeepEquals(before, root))
        {
            File.Copy(BackupPath, SettingsPath, overwrite: true);
            File.Delete(BackupPath);
        }
        else if (!hadBackup && root.Count == 0) File.Delete(SettingsPath);
        else Write(root);
        return true;
    }

    private string BackupPath => SettingsPath + ".strata-backup";

    private static bool IsOurs(JsonObject line) =>
        line["command"]?.GetValue<string>() is { } c && c.Contains(ClaudeStatusLine.BridgeFlag, StringComparison.Ordinal);

    private void Write(JsonObject root)
    {
        string tmp = SettingsPath + ".strata-tmp";
        // Relaxed escaping: the user's own values ("<", "+", "'", accented text) stay as they wrote them, not <.
        var options = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        File.WriteAllText(tmp, root.ToJsonString(options), new UTF8Encoding(false));
        File.Move(tmp, SettingsPath, overwrite: true);
    }
}
