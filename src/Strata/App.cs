using System.Net.Http;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Threading;
using Strata.Core;
using Strata.Native;
using Strata.Smart;
using Strata.Views;
using Strata.Widgets;

namespace Strata;

public sealed class App : Application
{
    public AppSettings Settings { get; private set; } = new();
    public ScreenTimeTracker ScreenTime { get; private set; } = null!;
    public SignalStore Signals { get; private set; } = new(new SystemClock());
    public RuleEngine? Automations { get; private set; }
    public NotificationCenter? Notifications { get; private set; }
    public DiagnosticErrorJournal DiagnosticErrors { get; }
    public FailureTracker Failures { get; }
    public PerformanceSampler Performance { get; } = new();
    public TaskbarController Taskbar { get; } = new();
    public bool TaskbarShown { get; internal set; }

    /// <summary>Set for `--render-previews`: render to PNGs and exit, without a dock or taskbar changes.</summary>
    public string? PreviewDirectory { get; init; }
    /// <summary>One-use token supplied only by the transactional updater; acknowledged after safe dock readiness.</summary>
    public string? UpdateHealthToken { get; set; }

    private DockWindow? _dock;
    /// <summary>Extra docks, one per other screen, while <see cref="AppSettings.DockOnEveryScreen"/> is on.</summary>
    private readonly List<DockWindow> _screenDocks = [];
    /// <summary>Whether the dock keyboard shortcut registered; null before the dock initializes.</summary>
    public bool? KeyboardShortcutRegistered => _dock?.KeyboardShortcutRegistered;
    public bool? CommandPaletteShortcutRegistered => _dock?.CommandPaletteShortcutRegistered;
    public int AppShortcutsRegistered => _dock?.AppShortcutsRegistered ?? 0;

    /// <summary>This build's version, e.g. "1.2.0+commit".</summary>
    public static string Version => typeof(App).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "0.0.0";

    public UpdateCheck? LastUpdateCheck { get; private set; }
    private DateTime _lastUpdateCheckAt = DateTime.MinValue;
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(1) };
    private readonly DispatcherTimer _windowTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer _startupReadinessTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DockStartupReadiness _startupReadiness = new(TimeSpan.FromSeconds(10));

    private static readonly Lazy<HttpClient> UpdateClient = new(() => Updater.CreateClient(Version));
    public static HttpClient UpdateHttp => UpdateClient.Value;

    /// <summary>Check the release source; notify once per new version when <paramref name="notify"/>.</summary>
    public async Task<UpdateCheck> CheckForUpdatesAsync(bool notify)
    {
        var check = await Updater.CheckAsync(Settings.UpdateFolder, Version, UpdateHttp);
        LastUpdateCheck = check;
        _lastUpdateCheckAt = DateTime.Now;
        if (notify && check.State == UpdateState.Available && check.Manifest is { } m)
            Notifications?.Send($"update|{m.Version}", $"Strata {Updater.Short(m.Version)} is ready to install",
                "Open Strata's settings, About, to install it.", "updates", TimeSpan.FromDays(3650));
        return check;
    }
    private Tray? _tray;
    private SettingsWindow? _settingsWindow;
    private MediaService? _media;
    private readonly WindowIdentityCache _foregroundIdentities = new(new Win32ProcessInfo(), WindowTracker.Identify, capacity: 64);
    private bool _cleanedUp;
    private readonly List<RegisteredWaitHandle> _signals = [];
    private readonly List<EventWaitHandle> _signalHandles = [];
    private readonly IMonitorWindowGeometry _monitorGeometry;
    private bool? _explorerEnumerationAvailable;
    internal WindowSnapshotCoordinator WindowSnapshots { get; } = new(new WindowTracker());

    public App(IMonitorWindowGeometry? monitorGeometry = null)
    {
        _monitorGeometry = monitorGeometry ?? new WindowsMonitorWindowGeometry();
        DiagnosticErrors = new DiagnosticErrorJournal();
        Failures = new FailureTracker(diagnostics: DiagnosticErrors);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        if (PreviewDirectory is not null)
        {
            PreparePreviewFixtures();
            ScreenTime = new ScreenTimeTracker { Enabled = false };
            int failures = PreviewRenderer.RenderAll(this, PreviewDirectory);
            var privacyHits = PreviewPrivacy.Scan(PreviewDirectory);
            File.WriteAllLines(Path.Combine(PreviewDirectory, "privacy-scan.txt"), privacyHits.Count == 0
                ? ["PASS: no user-profile paths, usernames, or supplied live-data markers found."]
                : ["FAIL: preview privacy markers found.", .. privacyHits]);
            failures += privacyHits.Count;
            Shutdown(failures == 0 ? 0 : 2);
            return;
        }

        ListenFor(Program.ExitEventName, Quit);
        ListenFor(Program.SettingsEventName, () => OpenSettings());

        DispatcherUnhandledException += OnUiException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception) DiagnosticErrors.Record("fatal", exception, DateTime.Now);
            Log.Write($"Fatal: {args.ExceptionObject}");
            Cleanup();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup();
        SessionEnding += (_, _) => Cleanup();

        Settings = AppSettings.Load();
        AppClock.Current = new SystemClock();
        Signals = new SignalStore(AppClock.Current);
        Theme.Apply(Settings.Style, Settings.GlassOpacity);
        ScreenTime = new ScreenTimeTracker { Enabled = Settings.TrackScreenTime };

        // Fail closed: a previous crash or an early Explorer/AppBar race must leave the Windows taskbar available
        // until the new native dock is visibly stable and its requested reservation is verified.
        Taskbar.RestoreForSafety(TaskbarSafetyBlockers.StartupNotReady);
        TaskbarShown = true;
        _startupReadiness.Begin(AppClock.Now, Settings.ReserveSpace);
        _startupReadinessTimer.Tick += (_, _) => ObserveDockReadiness();
        _startupReadinessTimer.Start();
        _windowTimer.Tick += (_, _) => RefreshWindowSnapshot();

        if (!Settings.PinnedImported)
        {
            Settings.Pinned = Launcher.ImportWindowsTaskbarPins();
            Settings.PinnedImported = true;
            Settings.Save();
        }

        _tray = new Tray(() => OpenSettings(), ToggleTaskbar, Quit);
        _dock = new DockWindow(this, monitorGeometry: _monitorGeometry);
        Notifications = NotificationCenter.Current = new NotificationCenter(new AppNotifier((title, body) => _tray?.Notify(title, body)),
            AppClock.Current, new QuietHours { Enabled = Settings.QuietHoursEnabled, Start = Settings.QuietHoursStart, End = Settings.QuietHoursEnd }, Signals);
        var automationPersistence = new AutomationPersistence();
        _dock.RestoreThemeOverride(automationPersistence.Load().ThemeOverrideOwner);
        Automations = new RuleEngine(Signals, AppClock.Current, _dock, Notifications, automationPersistence, DiagnosticErrors);
        foreach (var rule in Smart.Automations.Create()) Automations.Register(rule);
        _media = new MediaService(Signals);
        MediaService.Current = _media;
        // The media waves follow what is playing (its level only). Previews and tests keep the stand-in beat.
        Strata.Controls.WaveSlider.DefaultLevel = () => VolumeSource.Current.Peak;
        _dock.Show();
        _windowTimer.Start();
        SeedSignals();
        Automations.Start();
        _ = _media.StartAsync();
        Dispatcher.BeginInvoke(ObserveDockReadiness, DispatcherPriority.ContextIdle);
        SyncScreenDocks();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        // Updates: once a day from the chosen release folder (nothing when none is set).
        _updateTimer.Tick += (_, _) => { if (Settings.UpdateFolder.Length > 0 && DateTime.Now - _lastUpdateCheckAt > TimeSpan.FromHours(24)) _ = CheckForUpdatesAsync(notify: true); };
        _updateTimer.Start();
        if (Settings.UpdateFolder.Length > 0) Dispatcher.BeginInvoke(() => _ = CheckForUpdatesAsync(notify: true), DispatcherPriority.ApplicationIdle);
        Log.Write("Strata started.");
    }

    /// <summary>Named events let a second `Strata.exe` (or --exit) reach this instance.</summary>
    private void ListenFor(string name, Action action)
    {
        var handle = new EventWaitHandle(false, EventResetMode.AutoReset, name);
        _signalHandles.Add(handle);
        _signals.Add(ThreadPool.RegisterWaitForSingleObject(handle,
            (_, _) => Dispatcher.BeginInvoke(action), null, Timeout.Infinite, executeOnlyOnce: false));
    }

    private void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Contain isolated faults, but never swallow an unlimited crash loop.
        bool stop = Failures.Record("ui-thread", e.Exception, AppClock.Now);
        e.Handled = true;
        if (stop) Dispatcher.BeginInvoke(() => { Cleanup(); Shutdown(3); });
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(SyncScreenDocks);

    internal void RefreshWindowSnapshot(bool force = false)
    {
        WindowSnapshots.Refresh(force, monitorSensitive: Settings.DockOnEveryScreen);
        bool available = WindowSnapshots.Source.ExplorerEnumerationAvailable;
        if (!available && _explorerEnumerationAvailable != false)
            Notify("explorer-enumeration-unavailable", "File Explorer window tracking is unavailable",
                "Strata will not open another Explorer window from a normal click until existing folder windows can be identified safely.", urgent: true);
        _explorerEnumerationAvailable = available;
    }

    internal void ObserveDockReadiness()
    {
        if (_dock is null) return;
        DockStartupAction action = _startupReadiness.Observe(AppClock.Now, _dock.ObserveReadiness());
        if (action == DockStartupAction.RestoreTaskbarForSafety)
        {
            Taskbar.RestoreForSafety(TaskbarSafetyBlockers.StartupNotReady);
            TaskbarShown = true;
            _startupReadinessTimer.Stop();
            Log.Write("Dock readiness timed out; the Windows taskbar remains visible.");
            return;
        }
        if (action != DockStartupAction.ApplyConfiguredTaskbar) return;

        Taskbar.ClearSafetyBlocker(TaskbarSafetyBlockers.StartupNotReady | TaskbarSafetyBlockers.ExplorerReinitializing);
        TaskbarApplyResult result = Taskbar.ApplyConfigured(Settings.TaskbarMode);
        bool success = result is TaskbarApplyResult.Applied or TaskbarApplyResult.Left;
        _startupReadiness.CompleteApply(success);
        if (!success)
        {
            Taskbar.RestoreForSafety(TaskbarSafetyBlockers.StartupNotReady);
            _startupReadinessTimer.Stop();
            Log.Write($"Dock readiness could not apply the taskbar mode ({result}); the taskbar remains visible.");
        }
        else
        {
            _startupReadinessTimer.Stop();
            _dock.ResetTaskbarSafety();
            if (UpdateHealthToken is { } token && UpdateHealth.Acknowledge(token)) UpdateHealthToken = null;
        }
        TaskbarShown = !Taskbar.IsHidingOrAutoHiding;
    }

    internal void BeginExplorerReapplication()
    {
        Taskbar.RestoreForSafety(TaskbarSafetyBlockers.ExplorerReinitializing);
        TaskbarShown = true;
        _startupReadiness.ExplorerRestarted(AppClock.Now, Settings.ReserveSpace);
        if (!_startupReadinessTimer.IsEnabled) _startupReadinessTimer.Start();
    }

    /// <summary>A DPI/display transition may make a previously failed AppBar viable; run one fresh safe cycle.</summary>
    internal void BeginAppBarReapplication()
    {
        Taskbar.RestoreForSafety(TaskbarSafetyBlockers.StartupNotReady);
        TaskbarShown = true;
        _startupReadiness.Begin(AppClock.Now, Settings.ReserveSpace);
        if (!_startupReadinessTimer.IsEnabled) _startupReadinessTimer.Start();
    }

    /// <summary>The screens (HMONITORs) that need an extra dock: every screen but the main one, when the setting is on.</summary>
    public static IReadOnlyList<IntPtr> ScreensNeedingDocks(bool enabled, IReadOnlyList<IntPtr> screens, IntPtr primary) =>
        enabled ? screens.Where(m => m != IntPtr.Zero && m != primary).Distinct().ToList() : [];

    /// <summary>Open a dock on each other screen, and close the ones whose screen is gone or no longer wanted.</summary>
    private void SyncScreenDocks()
    {
        if (_dock is null || PrivacyGuard.PreviewActive) return;
        var screens = _monitorGeometry.EnumerateMonitors();
        var primary = _monitorGeometry.PrimaryMonitor();
        var wanted = ScreensNeedingDocks(Settings.DockOnEveryScreen, screens, primary);
        foreach (var d in _screenDocks.Where(d => !wanted.Contains(d.Monitor)).ToList())
        {
            try { d.Shutdown(); d.Close(); } catch (Exception ex) { Log.Write($"Closing a screen dock: {ex.Message}"); }
            _screenDocks.Remove(d);
        }
        foreach (var m in wanted.Where(m => _screenDocks.All(d => d.Monitor != m)))
        {
            var d = new DockWindow(this, m, monitorGeometry: _monitorGeometry);
            _screenDocks.Add(d);
            d.Show();
        }
        if (wanted.Count > 0 || _screenDocks.Count > 0) Log.Write($"Screen docks: {_screenDocks.Count} extra for {screens.Count} screen(s).");
    }

    public void ApplySettings()
    {
        _dock?.UserSettingsChanged();
        foreach (var d in _screenDocks) d.UserSettingsChanged();
        SyncScreenDocks();
        ScreenTime.Enabled = Settings.TrackScreenTime;
        if (Notifications is { } notifications)
        {
            notifications.Quiet.Enabled = Settings.QuietHoursEnabled;
            notifications.Quiet.Start = Settings.QuietHoursStart;
            notifications.Quiet.End = Settings.QuietHoursEnd;
            notifications.PublishState();
        }
        // Always record the latest preference, including while a readiness/AppBar blocker is deliberately keeping
        // the taskbar visible. ApplyConfigured preserves every blocker, so recovery later applies this value rather
        // than a stale pre-suspension mode.
        Taskbar.ApplyConfigured(Settings.TaskbarMode);
        TaskbarShown = !Taskbar.IsHidingOrAutoHiding;
        _dock?.ResetTaskbarSafety();
    }

    public void ResetScreenTime() => ScreenTime = new ScreenTimeTracker { Enabled = Settings.TrackScreenTime };

    public void OpenSettings(string? page = null)
    {
        _dock?.CloseFlyout();
        if (_settingsWindow is { IsLoaded: true })
        {
            if (page is not null) _settingsWindow.ShowPage(page);
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(this, page);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    /// <summary>Temporarily bring the Windows taskbar back (e.g. for tray icons), or hide it again.</summary>
    public void ToggleTaskbar()
    {
        if (TaskbarShown)
        {
            // Explicit user command: deliberately ends a safety suspension (documented in ARCHITECTURE.md).
            Taskbar.ApplyUserRequested(Settings.TaskbarMode == TaskbarMode.Leave ? TaskbarMode.Hide : Settings.TaskbarMode);
            TaskbarShown = !Taskbar.IsHidingOrAutoHiding;
            _dock?.ResetTaskbarSafety();
        }
        else
        {
            Taskbar.ApplyUserRequested(TaskbarMode.Leave);
            TaskbarShown = !Taskbar.IsHidingOrAutoHiding;
            _dock?.ResetTaskbarSafety();
        }
    }

    /// <summary>An explicit, idempotent recovery command exposed by the command palette.</summary>
    public void RestoreTaskbarForUser()
    {
        Taskbar.ApplyUserRequested(TaskbarMode.Leave);
        TaskbarShown = !Taskbar.IsHidingOrAutoHiding;
        _dock?.ResetTaskbarSafety();
    }

    public string CreateDiagnosticBundle()
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("The running executable is unavailable.");
        Performance.Sample();
        return DiagnosticBundleBuilder.Create(Version, executable, DiagnosticErrors, Performance.Snapshot(),
            DiagnosticBundleBuilder.CurrentMonitors(), DateTime.Now);
    }

    public void Notify(string key, string title, string text, bool urgent = false) =>
        Notifications?.Send(key, title, text, "widget", TimeSpan.FromMinutes(1), urgent);

    internal void PersistThemeOverride(string? owner) => Automations?.SetThemeOverrideOwner(owner);

    /// <summary>A settings backup (Settings > Privacy &amp; data > Back up): settings plus each automation's choices.</summary>
    public string ExportSettings() => SettingsBackup.Serialize(Settings,
        Automations is { } engine ? Smart.Automations.Create().Select(r => (r.Id, engine.Status(r.Id).State)) : [],
        Version, AppClock.Now);

    /// <summary>Restore a parsed backup: replace the settings, then each automation's on/off and parameters.</summary>
    public void RestoreSettings(SettingsBackupFile file)
    {
        if (file.Settings is null) return;
        bool startup = Settings.StartWithWindows;
        Settings = SettingsBackup.Restore(file, Automations);
        if (Settings.StartWithWindows != startup) StartupRegistration.Set(Settings.StartWithWindows);
        Theme.Apply(Settings.Style, Settings.GlassOpacity);
        ApplySettings();
        Log.Write("Settings restored from a backup.");
    }

    internal void PublishSignals()
    {
        try { Performance.Sample(); }
        catch (Exception ex) { DiagnosticErrors.Record("performance", ex, AppClock.Now); }
        var now = AppClock.Now;
        Signals.Publish(Smart.Signals.Minute, new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0), "clock");
        Signals.Publish(Smart.Signals.Cpu, SystemStats.Cpu, "system");
        Signals.Publish(Smart.Signals.Memory, SystemStats.Memory, "system");
        var level = NetworkInterface.GetIsNetworkAvailable() ? NetworkLevel.Internet : NetworkLevel.None;
        Signals.Publish(Smart.Signals.Network, new NetworkState(level, SystemStats.DownBytesPerSec, SystemStats.UpBytesPerSec), "network");
        if (Win32.GetSystemPowerStatus(out var power))
            Signals.Publish(Smart.Signals.Battery, new BatteryState(power.BatteryFlag != 128 && power.BatteryLifePercent != 255,
                power.BatteryLifePercent == 255 ? 0 : power.BatteryLifePercent, power.ACLineStatus == 1), "power");
        Signals.Publish(Smart.Signals.Calendar, EventStore.BuildState(now), "calendar");
        Signals.Publish(Smart.Signals.Pomodoro, PomodoroTimer.State(Settings), "pomodoro");
        Signals.Publish(Smart.Signals.ScreenTime, new ScreenTimeState(ScreenTime.Today.TotalSeconds, ScreenTime.TopApp), "screentime");
        var idle = Win32.GetIdleTime();
        Signals.Publish(Smart.Signals.Activity, new ActivityState(idle >= TimeSpan.FromMinutes(2), idle), "input");
        IntPtr foreground = Win32.GetForegroundWindow();
        if (foreground != IntPtr.Zero && _foregroundIdentities.Resolve(foreground) is { } foregroundApp)
            Signals.Publish(Smart.Signals.Foreground, new ForegroundApp(foregroundApp.Key, foregroundApp.DisplayName), "windows");
        var weather = WeatherService.For(Settings);
        if (weather is not null)
            Signals.Publish(Smart.Signals.Weather, new WeatherState(Settings.WeatherCity, weather.Code, weather.Temperature, weather.IsDay), "weather");
        PublishSun(now);
        Signals.CheckExpiry();
        Automations?.Heartbeat();
        _media?.Poll(now);
    }

    private void SeedSignals()
    {
        SystemStats.Update();
        PublishSignals();
        EventStore.Changed += PublishCalendar;
    }

    private void PublishCalendar() => Signals.Publish(Smart.Signals.Calendar, EventStore.BuildState(AppClock.Now), "calendar");

    private void PublishSun(DateTime now)
    {
        DateTime? rise = null, set = null;
        bool evening;
        string basis;
        if (Settings.WeatherLatitude is double lat && Settings.WeatherLongitude is double lon)
        {
            (rise, set) = SunCalculator.Compute(now.Date, lat, lon, TimeZoneInfo.Local);
            evening = set is { } sunset ? now >= sunset || rise is { } sunrise && now < sunrise : now.TimeOfDay >= TimeSpan.FromHours(19);
            basis = "location";
        }
        else
        {
            // The sunset automation's own "Evening starts" / "Morning starts" parameters (previously ignored).
            var parameters = Automations?.Status(Smart.Automations.SunsetTheme).State.Parameters;
            var eveningStart = SunCalculator.ParseTimeOfDay(parameters?.GetValueOrDefault("eveningStart"), TimeSpan.FromHours(19));
            var morningStart = SunCalculator.ParseTimeOfDay(parameters?.GetValueOrDefault("morningStart"), TimeSpan.FromHours(7));
            evening = SunCalculator.IsEveningBySetTime(now.TimeOfDay, eveningStart, morningStart);
            basis = "set time";
        }
        Signals.Publish(Smart.Signals.Sun, new SunState(rise, set, evening, basis), "sun");
    }

    private void PreparePreviewFixtures()
    {
        PrivacyGuard.EnterPreview();
        AppClock.Current = new ManualClock(new DateTime(2026, 3, 10, 14, 30, 0));
        Storage.Configure(Path.Combine(Path.GetTempPath(), $"strata-preview-{Environment.ProcessId}"));
        Settings = new AppSettings
        {
            PinnedImported = true,
            WeatherCity = "Sample City",
            WeatherLatitude = 40.0,
            WeatherLongitude = -3.0,
            Pinned = [new PinnedApp { Name = "Sample Editor" }, new PinnedApp { Name = "Sample Browser" }],
        };
        Signals = new SignalStore(AppClock.Current);
        EventStore.UseFixture([
            new CalendarEvent { Id = "sample-review", Title = "Design review", Start = AppClock.Now.AddMinutes(25) },
            new CalendarEvent { Id = "sample-focus", Title = "Focus block", Start = AppClock.Now.AddHours(2) },
        ]);
        TodoWidget.UseFixture([
            new TodoItem { Text = "Review the sample plan" },
            new TodoItem { Text = "Send a fixture-only update", Done = true },
        ]);
        File.WriteAllText(Storage.PathFor("notes.txt"), "Fixture notes\n\nNo personal data is used in preview mode.");
        var usage = new DayUsage { Date = AppClock.Today.ToString("yyyy-MM-dd") };
        usage.Hours[9] = 1800; usage.Hours[10] = 2400; usage.Hours[13] = 1200;
        usage.Apps["fixture:editor"] = new AppUsage { Name = "Sample Editor", Seconds = 3200 };
        usage.Apps["fixture:browser"] = new AppUsage { Name = "Sample Browser", Seconds = 2200 };
        Storage.Save(Path.Combine("screentime", $"{AppClock.Today:yyyy-MM-dd}.json"), usage);
        VolumeSource.Current = new FixtureVolume(62);
        RadioSource.Current = new FixtureRadios(wifi: true, bluetooth: false);
        _media = new MediaService(Signals);
        MediaService.Current = _media;
        _media.UseFixture("A Sample Track", "Demo Artist", true, MediaService.SampleArtwork(), TimeSpan.FromSeconds(114), TimeSpan.FromSeconds(232));
    }

    public void Quit()
    {
        Cleanup();
        Shutdown();
    }

    private void Cleanup()
    {
        if (_cleanedUp || PreviewDirectory is not null) return;
        _cleanedUp = true;
        foreach (var s in _signals) s.Unregister(null);
        foreach (var h in _signalHandles) h.Dispose();
        EventStore.Changed -= PublishCalendar;
        try { Automations?.Stop(); } catch (Exception ex) { Log.Write($"Automation shutdown: {ex.Message}"); }
        try { _dock?.Shutdown(); } catch (Exception ex) { Log.Write($"Dock shutdown: {ex.Message}"); }
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _updateTimer.Stop();
        _windowTimer.Stop();
        _startupReadinessTimer.Stop();
        foreach (var d in _screenDocks) { try { d.Shutdown(); } catch (Exception ex) { Log.Write($"Screen dock shutdown: {ex.Message}"); } }
        _screenDocks.Clear();
        try { Taskbar.Restore(); } catch (Exception ex) { Log.Write($"Taskbar restore: {ex.Message}"); }
        try { ScreenTime?.Save(); } catch { }
        try { _tray?.Dispose(); } catch { }
        try { _media?.Dispose(); } catch (Exception ex) { Log.Write($"Media shutdown: {ex.Message}"); }
        if (ReferenceEquals(MediaService.Current, _media)) MediaService.Current = null;
        Log.Write("Strata stopped.");
    }
}
