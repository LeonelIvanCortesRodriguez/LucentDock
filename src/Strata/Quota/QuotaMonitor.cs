using Strata.Core;

namespace Strata.Quota;

/// <summary>AI usage settings (Settings > AI usage).</summary>
public sealed class QuotaSettings
{
    public bool ClaudeEnabled { get; set; } = true;
    public bool CodexEnabled { get; set; } = true;
    public QuotaTileMode TileMode { get; set; } = QuotaTileMode.MostConstrained;
    public QuotaWindowKind ClaudeWindow { get; set; } = QuotaWindowKind.Session;
    public QuotaWindowKind CodexWindow { get; set; } = QuotaWindowKind.Session;
    /// <summary>The tile shows remaining (true) or used (false); it always says which.</summary>
    public bool TileShowsRemaining { get; set; } = true;
    public int WarnRemainingPercent { get; set; } = 20;
    public int CriticalRemainingPercent { get; set; } = 5;
    /// <summary>Notify when a limit reaches the warning level, when it is reached, and when it resets.</summary>
    public bool Alerts { get; set; } = true;
    public int RefreshMinutes { get; set; } = 5;
    /// <summary>The optional status line only reports while Claude Code runs; older than this it is shown as stale.</summary>
    public int StaleMinutes { get; set; } = 30;
    /// <summary>Path to codex.exe; empty means find it automatically.</summary>
    public string CodexPath { get; set; } = "";
    /// <summary>Path to Claude Code's claude.exe; empty means find it automatically.</summary>
    public string ClaudePath { get; set; } = "";
}

/// <summary>Normalized snapshots kept between runs so the tile can show the last reading (marked stale) at once.</summary>
public sealed class QuotaStore
{
    public const string FileName = "quota.json";
    public QuotaSnapshot? Codex { get; set; }
    public QuotaSnapshot? Claude { get; set; }
}

/// <summary>
/// Keeps one <see cref="ProviderState"/> per provider. Codex (app-server) and Claude (Claude Code's <c>/usage</c>) are
/// read in the background every <see cref="QuotaSettings.RefreshMinutes"/>, each backing off after its own errors
/// (at least 15 minutes after a 429). Claude can also arrive through the optional status-line bridge; the freshest
/// reading wins. A manual refresh joins one already running instead of starting another.
/// </summary>
public sealed class QuotaMonitor
{
    private sealed class Poller(IQuotaSource? source, QuotaSnapshot? stored)
    {
        public IQuotaSource? Source { get; } = source;
        public QuotaSnapshot? Last { get; set; } = stored;
        public QuotaErrorKind Error { get; set; }
        public string? ErrorText { get; set; }
        public DateTimeOffset? Attempt { get; set; }
        public int Failures { get; set; }
        public int Reads { get; set; }

        public DateTimeOffset Next(int refreshMinutes)
        {
            if (Attempt is not { } at) return DateTimeOffset.MinValue;
            double minutes = Math.Max(1, refreshMinutes);
            if (Error == QuotaErrorKind.RateLimited) minutes = Math.Max(minutes, 15);
            else if (Failures > 0) minutes = Math.Min(30, minutes * Math.Pow(2, Failures - 1));
            return at + TimeSpan.FromMinutes(minutes);
        }

        public async Task RunAsync(Func<DateTimeOffset> now, string name)
        {
            if (Source is null) return;
            Attempt = now();
            Reads++;
            try
            {
                // Even an async source can block before its first await (notably in CreateProcess).
                // Invoke it on a worker; keep state changes and notifications on the caller's context.
                var last = Last;
                Last = await Task.Run(() => Source.ReadAsync(last, CancellationToken.None)).ConfigureAwait(true);
                Error = QuotaErrorKind.None;
                ErrorText = null;
                Failures = 0;
            }
            catch (QuotaSourceException ex)
            {
                Error = ex.Kind;
                ErrorText = ex.Message;
                Failures++;
                Log.Write($"{name} usage read failed: {ex.Kind}");
            }
            catch (Exception ex) when (ex is not PrivacyViolationException)
            {
                Error = QuotaErrorKind.Failed;
                ErrorText = $"{name} couldn't read usage.";
                Failures++;
                Log.Write($"{name} usage read failed: {ex.GetType().Name}");
            }
        }
    }

    private readonly QuotaSettings _settings;
    private readonly Poller _codex;
    private readonly Poller _claude;
    private readonly Func<QuotaSnapshot?> _readStatusLine;
    private readonly Func<bool> _statusLineConnected;
    private readonly Action<QuotaStore>? _save;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _gate = new();
    private Task? _inflight;
    private QuotaSnapshot? _statusLineLast;
    private bool _statusLineUnreadable;
    private DateTimeOffset _statusLineChecked = DateTimeOffset.MinValue;

    public QuotaMonitor(QuotaSettings settings, IQuotaSource codex, IQuotaSource? claude, Func<QuotaSnapshot?> readStatusLine,
        Func<bool> statusLineConnected, QuotaStore? stored, Action<QuotaStore>? save, Func<DateTimeOffset> now)
    {
        _settings = settings;
        _codex = new Poller(codex, stored?.Codex);
        _claude = new Poller(claude, stored?.Claude);
        _readStatusLine = readStatusLine;
        _statusLineConnected = statusLineConnected;
        _save = save;
        _now = now;
    }

    public event Action? Changed;
    public int CodexReads => _codex.Reads;
    public int ClaudeReads => _claude.Reads;
    public bool Refreshing => _inflight is { IsCompleted: false };
    public DateTimeOffset NextCodexRead => _codex.Next(_settings.RefreshMinutes);
    public DateTimeOffset NextClaudeRead => _claude.Next(_settings.RefreshMinutes);
    /// <summary>True when the last background read said Claude Code needs a sign-in (the flyout offers one).</summary>
    public bool ClaudeNeedsSignIn => _settings.ClaudeEnabled && _claude.Error == QuotaErrorKind.SignInRequired;

    public ProviderState Codex
    {
        get
        {
            if (!_settings.CodexEnabled) return new(QuotaProvider.Codex, QuotaStatus.Disabled, null);
            return Background(QuotaProvider.Codex, _codex);
        }
    }

    public ProviderState Claude
    {
        get
        {
            if (!_settings.ClaudeEnabled) return new(QuotaProvider.Claude, QuotaStatus.Disabled, null);
            var now = _now();
            var background = _claude.Last;
            var statusLine = _statusLineLast;
            // Freshest reading wins; a reading with windows beats one without.
            var pick = new[] { background, statusLine }.Where(s => s is not null)
                .OrderByDescending(s => s!.Windows.Count > 0).ThenByDescending(s => s!.ObservedAt).FirstOrDefault();
            if (pick is null || (pick == statusLine && pick.Windows.Count == 0 && background is null))
            {
                if (_claude.Error != QuotaErrorKind.None && !(_claude.Error == QuotaErrorKind.NotInstalled && _statusLineConnected()))
                    return new(QuotaProvider.Claude, QuotaStatus.Error, null, _claude.Error, _claude.ErrorText, _claude.Attempt);
                if (_statusLineUnreadable) return new(QuotaProvider.Claude, QuotaStatus.Error, null, QuotaErrorKind.BadResponse, "The saved Claude usage could not be read.");
                // A status line seen before the session's first response has no windows yet: still waiting.
                return new(QuotaProvider.Claude, QuotaStatus.WaitingForFirstSample, pick);
            }
            if (pick == background) return Background(QuotaProvider.Claude, _claude);
            bool stale = now - pick.ObservedAt > TimeSpan.FromMinutes(_settings.StaleMinutes);
            return new(QuotaProvider.Claude, stale ? QuotaStatus.Stale : QuotaStatus.Fresh, pick);
        }
    }

    private ProviderState Background(QuotaProvider provider, Poller p)
    {
        if (p.Last is null)
            return p.Error != QuotaErrorKind.None
                ? new(provider, QuotaStatus.Error, null, p.Error, p.ErrorText, p.Attempt)
                : ProviderState.Waiting(provider);
        // Fresh until two refresh periods pass without a successful read; an error keeps the reading, marked stale.
        bool stale = _now() - p.Last.ObservedAt > TimeSpan.FromMinutes(Math.Max(1, _settings.RefreshMinutes) * 2);
        return new(provider, stale || p.Error != QuotaErrorKind.None ? QuotaStatus.Stale : QuotaStatus.Fresh, p.Last, p.Error, p.ErrorText, p.Attempt);
    }

    /// <summary>Called once a second while the widget is on the dock.</summary>
    public void Tick()
    {
        var now = _now();
        if (now - _statusLineChecked >= TimeSpan.FromSeconds(5)) PollStatusLine(now);
        if (Refreshing) return;
        bool codexDue = _settings.CodexEnabled && now >= _codex.Next(_settings.RefreshMinutes);
        bool claudeDue = _settings.ClaudeEnabled && _claude.Source is not null && now >= _claude.Next(_settings.RefreshMinutes);
        if (codexDue || claudeDue) _ = Run(codexDue, claudeDue);
    }

    /// <summary>Reads both providers now. A call while a refresh is running returns that same refresh.</summary>
    public Task RefreshAsync() => Run(_settings.CodexEnabled, _settings.ClaudeEnabled);

    private Task Run(bool codex, bool claude)
    {
        lock (_gate)
        {
            if (_inflight is { IsCompleted: false } running) return running;
            _inflight = RunAsync(codex, claude);
            return _inflight;
        }
    }

    private async Task RunAsync(bool codex, bool claude)
    {
        PollStatusLine(_now());
        var work = new List<Task>();
        if (codex) work.Add(_codex.RunAsync(_now, "Codex"));
        if (claude) work.Add(_claude.RunAsync(_now, "Claude"));
        await Task.WhenAll(work).ConfigureAwait(true);
        _save?.Invoke(new QuotaStore { Codex = _codex.Last, Claude = _claude.Last });
        Changed?.Invoke();
    }

    private void PollStatusLine(DateTimeOffset now)
    {
        _statusLineChecked = now;
        if (!_settings.ClaudeEnabled) return;
        try
        {
            var latest = _readStatusLine();
            _statusLineUnreadable = false;
            if (latest is not null && latest.ObservedAt != _statusLineLast?.ObservedAt) { _statusLineLast = latest; Changed?.Invoke(); }
        }
        catch (QuotaSourceException) { _statusLineUnreadable = true; }
    }

    /// <summary>Clearly labelled sample readings for previews. Neither account is read.</summary>
    public static QuotaMonitor Sample(QuotaSettings settings, DateTimeOffset now)
    {
        var claude = new QuotaSnapshot
        {
            Provider = QuotaProvider.Claude, Source = "Sample data", IsSample = true, ObservedAt = now.AddMinutes(-2), Plan = "Pro",
            Windows =
            [
                new QuotaWindow { Id = "session", Label = "Session (5h)", Kind = QuotaWindowKind.Session, ReportedUsedPercent = 64, ResetsAt = now.AddMinutes(68), WindowMinutes = 300 },
                new QuotaWindow { Id = "weekly_all", Label = "Weekly", Kind = QuotaWindowKind.Weekly, ReportedUsedPercent = 41, ResetsAt = now.AddDays(3).AddHours(5), WindowMinutes = 10080 },
            ],
        };
        var codex = new QuotaSnapshot
        {
            Provider = QuotaProvider.Codex, Source = "Sample data", IsSample = true, ObservedAt = now.AddMinutes(-1), Plan = "Plus",
            Windows =
            [
                new QuotaWindow { Id = "codex/primary", Label = "Session (5h)", Kind = QuotaWindowKind.Session, ReportedUsedPercent = 93, ResetsAt = now.AddMinutes(68), WindowMinutes = 300 },
                new QuotaWindow { Id = "codex/secondary", Label = "Weekly", Kind = QuotaWindowKind.Weekly, ReportedUsedPercent = 58, ResetsAt = now.AddDays(4).AddHours(2), WindowMinutes = 10080 },
            ],
            Credits = new QuotaCredits(true, false, "12.50", "credits", null, null),
            ResetCredits = new ResetCreditsInfo(1, now.AddDays(9)),
            Blocked = false,
        };
        return new QuotaMonitor(settings, new SampleSource(codex), new SampleSource(claude), () => null, () => false,
            new QuotaStore { Codex = codex, Claude = claude }, null, () => now);
    }

    private sealed class SampleSource(QuotaSnapshot snapshot) : IQuotaSource
    {
        public Task<QuotaSnapshot> ReadAsync(QuotaSnapshot? last, CancellationToken ct) => Task.FromResult(snapshot);
    }
}

/// <summary>The app's one monitor, created when the AI usage widget first appears on the dock.</summary>
public static class QuotaService
{
    private static QuotaMonitor? _monitor;
    private static AppSettings? _builtFor;

    /// <summary>The monitor for these settings. A different settings object (a restored backup) gets a new one, built
    /// from its own switches and executable paths: the old monitor's sources read the settings they were made with.</summary>
    public static QuotaMonitor For(AppSettings settings)
    {
        if (_monitor is not null && ReferenceEquals(_builtFor, settings)) return _monitor;
        _builtFor = settings;
        if (PrivacyGuard.PreviewActive) return _monitor = QuotaMonitor.Sample(settings.Quota, new DateTimeOffset(Smart.AppClock.Now));
        var store = Storage.Load<QuotaStore>(QuotaStore.FileName);
        string version = typeof(QuotaService).Assembly.GetName().Version?.ToString(3) ?? "1.2.0";
        var connector = ClaudeCodeConnector.ForCurrentUser();
        bool? connected = null;
        return _monitor = new QuotaMonitor(settings.Quota,
            new CodexAppServerSource(() => CodexAppServerSource.Locate(settings.Quota.CodexPath), version),
            new ClaudeUsageSource(() => ClaudeUsageSource.Locate(settings.Quota.ClaudePath)),
            ClaudeStatusLine.ReadLatest,
            () => connected ??= connector.Status() == ClaudeConnection.Connected,
            store,
            s => Storage.Save(QuotaStore.FileName, s),
            () => DateTimeOffset.Now);
    }

    /// <summary>Forget the monitor (after settings change the sources); the next widget build makes a new one.</summary>
    public static void Reset() { _monitor = null; _builtFor = null; }

    /// <summary>Where the current monitor runs codex.exe and claude.exe from: the paths in its settings, else the usual
    /// places. Null when none was built, or none is found.</summary>
    public static (string? Codex, string? Claude) Executables() => _builtFor is not { } s ? (null, null)
        : (CodexAppServerSource.Locate(s.Quota.CodexPath), ClaudeUsageSource.Locate(s.Quota.ClaudePath));
}
