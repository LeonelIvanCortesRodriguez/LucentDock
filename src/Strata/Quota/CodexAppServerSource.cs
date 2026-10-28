using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Strata.Core;

namespace Strata.Quota;

/// <summary>Something that can produce a fresh snapshot for one provider.</summary>
public interface IQuotaSource
{
    Task<QuotaSnapshot> ReadAsync(QuotaSnapshot? last, CancellationToken ct);
}

/// <summary>
/// The JSON-RPC conversation with <c>codex app-server</c> over its stdio: <c>initialize</c>, <c>initialized</c>,
/// then the read-only <c>account/rateLimits/read</c>. <c>account/rateLimits/updated</c> notifications that
/// arrive after the read are merged on top of it. Codex itself holds the sign-in; Strata never sees a token.
/// </summary>
public sealed class CodexRpcSession(TextReader fromServer, TextWriter toServer)
{
    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    public async Task<QuotaSnapshot> ReadRateLimitsAsync(Func<DateTimeOffset> now, string clientVersion, TimeSpan linger, CancellationToken ct)
    {
        await SendAsync(new JsonObject
        {
            ["id"] = 1,
            ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["clientInfo"] = new JsonObject { ["name"] = "strata", ["title"] = "Strata", ["version"] = clientVersion },
                ["capabilities"] = new JsonObject { ["experimentalApi"] = false, ["requestAttestation"] = false },
            },
        });
        await WaitForResultAsync(1, ct);
        await SendAsync(new JsonObject { ["method"] = "initialized" });
        // Full read, including reset-credit details: their expiry is part of what the flyout shows.
        await SendAsync(new JsonObject { ["id"] = 2, ["method"] = "account/rateLimits/read", ["params"] = new JsonObject { ["excludeResetCreditDetails"] = false } });
        using var result = await WaitForResultAsync(2, ct);
        var snapshot = CodexRateLimits.Parse(result.RootElement.GetProperty("result"), now());

        // Briefly drain anything the server pushes right after the read.
        using var drain = CancellationTokenSource.CreateLinkedTokenSource(ct);
        drain.CancelAfter(linger);
        try
        {
            while (await NextAsync(drain.Token) is { } msg)
                using (msg)
                    if (Method(msg) == "account/rateLimits/updated" && msg.RootElement.TryGetProperty("params", out var p))
                        snapshot = CodexRateLimits.Merge(snapshot, p, now());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        return snapshot;
    }

    private async Task SendAsync(JsonObject message)
    {
        await toServer.WriteLineAsync(message.ToJsonString(Compact));
        await toServer.FlushAsync();
    }

    /// <summary>Reads until the response with <paramref name="id"/>; notifications in between are skipped.</summary>
    private async Task<JsonDocument> WaitForResultAsync(int id, CancellationToken ct)
    {
        while (true)
        {
            var msg = await NextAsync(ct) ?? throw new QuotaSourceException(QuotaErrorKind.Failed, "Codex closed the connection before answering.");
            var root = msg.RootElement;
            if (root.TryGetProperty("id", out var mid) && mid.ValueKind == JsonValueKind.Number && mid.GetInt32() == id)
            {
                if (root.TryGetProperty("error", out var err))
                {
                    string? text = err.TryGetProperty("message", out var m) ? m.GetString() : null;
                    string code = err.TryGetProperty("code", out var c) ? c.ToString() : "?";
                    msg.Dispose();
                    var kind = CodexRateLimits.Classify(text);
                    throw new QuotaSourceException(kind, kind == QuotaErrorKind.Failed ? $"Codex could not read usage (error {code})." : Friendly(kind));
                }
                if (!root.TryGetProperty("result", out _)) { msg.Dispose(); throw new QuotaSourceException(QuotaErrorKind.BadResponse, Friendly(QuotaErrorKind.BadResponse)); }
                return msg;
            }
            msg.Dispose();
        }
    }

    private async Task<JsonDocument?> NextAsync(CancellationToken ct)
    {
        while (true)
        {
            string? line = await fromServer.ReadLineAsync(ct);
            if (line is null) return null;
            if (line.Length == 0) continue;
            try { return JsonDocument.Parse(line); }
            catch (JsonException) { throw new QuotaSourceException(QuotaErrorKind.BadResponse, Friendly(QuotaErrorKind.BadResponse)); }
        }
    }

    private static string? Method(JsonDocument d) =>
        d.RootElement.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;

    /// <summary>What the UI says for each kind of failure. Raw provider text is never shown or stored: it can contain paths.</summary>
    public static string Friendly(QuotaErrorKind kind) => kind switch
    {
        QuotaErrorKind.NotInstalled => "Codex isn't installed, or Strata can't find it. You can set its path in Settings > AI usage.",
        QuotaErrorKind.SignInRequired => "Sign in to Codex again, then refresh.",
        QuotaErrorKind.RateLimited => "Codex asked Strata to slow down. Strata will try again later.",
        QuotaErrorKind.Offline => "Can't reach Codex's service. Check your connection.",
        QuotaErrorKind.BadResponse => "Codex answered in a format Strata doesn't recognise (Codex may have been updated).",
        QuotaErrorKind.Timeout => "Codex didn't answer in time.",
        _ => "Codex couldn't read usage.",
    };
}

/// <summary>
/// Runs <c>codex app-server</c> for one read and closes it (about 0.7 s and 55 MB while it runs; it starts no other
/// servers). Polled by <see cref="QuotaMonitor"/> while the AI usage widget is on the dock, and on manual refresh.
/// </summary>
public sealed class CodexAppServerSource : IQuotaSource
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly Func<string?> _locate;
    private readonly string _clientVersion;
    private readonly QuotaReadRunner _runner;

    public CodexAppServerSource(Func<string?> locate, string clientVersion) : this(locate, clientVersion, Timeout, Process.Start) { }

    internal CodexAppServerSource(Func<string?> locate, string clientVersion, TimeSpan timeout, Func<ProcessStartInfo, Process?> start)
    {
        _locate = locate;
        _clientVersion = clientVersion;
        _runner = new QuotaReadRunner(timeout, start);
    }

    public async Task<QuotaSnapshot> ReadAsync(QuotaSnapshot? last, CancellationToken ct)
    {
        PrivacyGuard.ThrowIfPreview("Codex (app-server)");
        try { return await _runner.ReadAsync(ReadCoreAsync, ct).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            throw new QuotaSourceException(QuotaErrorKind.Timeout, CodexRpcSession.Friendly(QuotaErrorKind.Timeout));
        }
    }

    private async Task<QuotaSnapshot> ReadCoreAsync(QuotaProcessSession processes, CancellationToken ct)
    {
        string exe = _locate() ?? throw new QuotaSourceException(QuotaErrorKind.NotInstalled, CodexRpcSession.Friendly(QuotaErrorKind.NotInstalled));
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        psi.ArgumentList.Add("app-server");
        QuotaProcess child;
        try { child = processes.Start(psi) ?? throw new QuotaSourceException(QuotaErrorKind.NotInstalled, CodexRpcSession.Friendly(QuotaErrorKind.NotInstalled)); }
        catch (System.ComponentModel.Win32Exception ex) { throw new QuotaSourceException(QuotaErrorKind.NotInstalled, CodexRpcSession.Friendly(QuotaErrorKind.NotInstalled), ex); }
        using (child)
        {
            var process = child.Process;
            var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
            QuotaReadRunner.Observe(stderr);
            try
            {
                var session = new CodexRpcSession(process.StandardOutput, process.StandardInput);
                return await session.ReadRateLimitsAsync(() => DateTimeOffset.Now, _clientVersion, TimeSpan.FromMilliseconds(300), ct).ConfigureAwait(false);
            }
            catch (QuotaSourceException ex) when (ex.Kind == QuotaErrorKind.Failed && process.HasExited)
            {
                // It quit early: its stderr says why (not signed in, offline). Used only to classify, never kept.
                string why = await stderr.WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                var kind = CodexRateLimits.Classify(why);
                throw new QuotaSourceException(kind, CodexRpcSession.Friendly(kind), ex);
            }
            finally
            {
                try { process.StandardInput.Close(); } catch { }
            }
        }
    }

    /// <summary>
    /// Finds codex.exe: the path set in Settings, then PATH, then the Codex app (Microsoft Store package),
    /// then the newest VS Code extension copy. Null when none exists.
    /// </summary>
    public static string? Locate(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath)) return File.Exists(overridePath) ? overridePath : null;
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { string p = Path.Combine(dir.Trim(), "codex.exe"); if (File.Exists(p)) return p; } catch (ArgumentException) { }
        }
        try
        {
            var packages = new Windows.Management.Deployment.PackageManager().FindPackagesForUser("", "OpenAI.Codex_2p2nqsd0c76g0");
            foreach (var package in packages.OrderByDescending(p => p.Id.Version.Major).ThenByDescending(p => p.Id.Version.Minor).ThenByDescending(p => p.Id.Version.Build))
            {
                string p = Path.Combine(package.InstalledLocation.Path, "app", "resources", "codex.exe");
                if (File.Exists(p)) return p;
            }
        }
        catch (Exception ex) { Log.Write($"Codex package lookup failed: {ex.Message}"); }
        string extensions = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vscode", "extensions");
        if (Directory.Exists(extensions))
            return Directory.GetDirectories(extensions, "openai.chatgpt-*")
                .OrderByDescending(d => Directory.GetLastWriteTimeUtc(d))
                .Select(d => Path.Combine(d, "bin", "windows-x86_64", "codex.exe"))
                .FirstOrDefault(File.Exists);
        return null;
    }
}
