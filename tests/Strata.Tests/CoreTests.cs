using System.Globalization;
using Strata.Core;
using Strata.Smart;
using Strata.Views;
using Strata.Widgets;

namespace Strata.Tests;

public class FormattingTests
{
    [Theory]
    [InlineData(0, "0s")]
    [InlineData(59, "59s")]
    [InlineData(60, "1m")]
    [InlineData(3599, "59m")]
    [InlineData(3600, "1h 0m")]
    [InlineData(3725, "1h 2m")]
    [InlineData(90000, "25h 0m")]
    public void ScreenTimeFormat(int seconds, string expected) =>
        Assert.Equal(expected, ScreenTimeTracker.Format(seconds));

    [Theory]
    [InlineData(500, "500 B/s")]
    [InlineData(2048, "2 KB/s")]
    [InlineData(1.5 * 1024 * 1024, "1.5 MB/s")]
    public void NetworkRate(double bytes, string expected) =>
        Assert.Equal(expected, SystemStats.FormatRate(bytes).Replace(',', '.'));
}

public class AppIdentityTests
{
    [Fact]
    public void KeyPrefersAumid()
    {
        Assert.Equal("aumid:microsoft.windowscalculator_8wekyb3d8bbwe!app",
            new AppIdentity("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", @"C:\x\calc.exe", "Calc").Key);
        Assert.Equal(@"exe:c:\apps\tool.exe", new AppIdentity(null, @"C:\Apps\Tool.exe", "Tool").Key);
    }

    [Fact]
    public void PinMatchesByAumidOrExeIgnoringCase()
    {
        var running = new AppIdentity("Foo.App", @"C:\Program Files\Foo\foo.exe", "Foo");
        Assert.True(running.Matches(new PinnedApp { Aumid = "foo.app" }));
        Assert.True(running.Matches(new PinnedApp { ExePath = @"c:\program files\foo\FOO.EXE" }));
        Assert.False(running.Matches(new PinnedApp { ExePath = @"C:\Other\bar.exe" }));
        Assert.False(running.Matches(new PinnedApp { Path = @"C:\Users\x\foo.lnk" })); // a shortcut with no resolved target
    }

    [Fact]
    public void ButtonKeysSeparatePinsFromRunningApps()
    {
        var app = new AppIdentity(null, @"C:\a.exe", "A");
        var pinA = new PinnedApp { Path = @"C:\a.exe" };
        var pinB = new PinnedApp { Path = @"C:\a.exe" };
        Assert.NotEqual(AppButton.KeyFor(pinA, app), AppButton.KeyFor(null, app));
        // Two pins to the same program must not collide (this used to crash the refresh).
        Assert.NotEqual(AppButton.KeyFor(pinA, null), AppButton.KeyFor(pinB, null));
        Assert.Equal(AppButton.KeyFor(pinA, null), AppButton.KeyFor(pinA, app));
    }
}

public class TimeParsingTests
{
    [Theory]
    [InlineData("21:30", 21, 30)]
    [InlineData("09:05", 9, 5)]
    [InlineData("9", 9, 0)]
    [InlineData(" 7:15 ", 7, 15)]
    [InlineData("9:30 PM", 21, 30)]
    public void AcceptsCommonFormats(string input, int h, int m)
    {
        Assert.True(CalendarWidget.TryParseTime(input, out var t));
        Assert.Equal(new TimeSpan(h, m, 0), t);
    }

    [Theory]
    [InlineData("")]
    [InlineData("lunch")]
    [InlineData("25")]
    [InlineData("2026-10-23")]
    public void RejectsNonTimes(string input) => Assert.False(CalendarWidget.TryParseTime(input, out _));

    [Fact]
    public void AcceptsSpanishMeridiem()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("es-MX");
            string text = DateTime.Today.AddHours(21.5).ToString("h:mm tt"); // e.g. "9:30 p. m."
            Assert.True(CalendarWidget.TryParseTime(text, out var t), $"could not parse '{text}'");
            Assert.Equal(new TimeSpan(21, 30, 0), t);
        }
        finally { CultureInfo.CurrentCulture = saved; }
    }
}

public class WeatherTests
{
    [Theory]
    [InlineData(0, "Clear")]
    [InlineData(2, "Partly cloudy")]
    [InlineData(45, "Fog")]
    [InlineData(63, "Rain")]
    [InlineData(75, "Snow")]
    [InlineData(81, "Showers")]
    [InlineData(96, "Thunderstorm")]
    public void DescribesWmoCodes(int code, string expected) => Assert.Equal(expected, WeatherService.Describe(code));

    [Fact]
    public void NightSymbolDiffersForClearSky() =>
        Assert.NotEqual(WeatherService.Symbol(0, isDay: true), WeatherService.Symbol(0, isDay: false));
}

[Collection("Focus timer")] // the focus timer is static; the quick-settings Focus tile test uses it too
public class PomodoroTests
{
    [Fact]
    public void StartPauseSkipReset()
    {
        var s = new AppSettings { PomodoroWorkMinutes = 25, PomodoroBreakMinutes = 5 };
        PomodoroTimer.Reset();
        if (PomodoroTimer.Current != PomodoroTimer.Phase.Work) PomodoroTimer.Skip(s);
        PomodoroTimer.Reset();

        Assert.False(PomodoroTimer.Running);
        Assert.Equal(TimeSpan.FromMinutes(25), PomodoroTimer.Remaining(s));

        PomodoroTimer.Toggle(s);
        Assert.True(PomodoroTimer.Running);
        Assert.InRange(PomodoroTimer.Remaining(s).TotalSeconds, 24 * 60 + 58, 25 * 60);

        PomodoroTimer.Toggle(s);
        Assert.False(PomodoroTimer.Running);
        var paused = PomodoroTimer.Remaining(s);
        Thread.Sleep(1100);
        Assert.Equal(paused, PomodoroTimer.Remaining(s)); // paused time does not tick

        PomodoroTimer.Skip(s);
        Assert.Equal(PomodoroTimer.Phase.Break, PomodoroTimer.Current);
        Assert.Equal(TimeSpan.FromMinutes(5), PomodoroTimer.Remaining(s));

        PomodoroTimer.Skip(s);
        PomodoroTimer.Reset();
        Assert.Equal(PomodoroTimer.Phase.Work, PomodoroTimer.Current);
    }

    [Fact]
    public void FormatsMinutesAndSeconds() => Assert.Equal("04:05", PomodoroTimer.Format(new TimeSpan(0, 4, 5)));
}

public class SettingsAndStorageTests
{
    private sealed class RecordingDurableFileIO : IDurableFileIO
    {
        public List<string> Operations { get; } = [];
        public MemoryStream Stream { get; private set; } = new();
        public Stream CreateWriteThrough(string path) { Operations.Add("create-write-through"); return Stream = new(); }
        public void FlushToDisk(Stream stream) { Assert.Same(Stream, stream); Operations.Add("flush-to-disk"); }
        public void ReplaceWriteThrough(string source, string destination) { Operations.Add("replace-write-through"); }
        public bool FileExists(string path) => false;
        public void DeleteFile(string path) => Operations.Add("delete");
    }

    [Fact]
    public void Durable_write_flushes_contents_before_write_through_replacement()
    {
        var io = new RecordingDurableFileIO();

        DurableFile.WriteAllTextAtomic(@"X:\fixture\state.json", "fixture", io);

        Assert.Equal(["create-write-through", "flush-to-disk", "replace-write-through"], io.Operations);
        Assert.Equal("fixture", System.Text.Encoding.UTF8.GetString(io.Stream.ToArray()));
    }

    [Fact]
    public void SettingsRoundTripWithEnumsAsText()
    {
        string name = $"test-{Guid.NewGuid():N}.json";
        try
        {
            var s = new AppSettings { Edge = DockEdge.Top, Style = DockStyle.Dark, Size = DockSize.Large, WeatherCity = "Lima" };
            s.Pinned.Add(new PinnedApp { Name = "Notepad", ExePath = @"C:\Windows\notepad.exe" });
            Storage.Save(name, s);
            string json = File.ReadAllText(Storage.PathFor(name));
            Assert.Contains("\"Dark\"", json);

            var back = Storage.Load<AppSettings>(name);
            Assert.Equal(DockEdge.Top, back.Edge);
            Assert.Equal(DockStyle.Dark, back.Style);
            Assert.Equal(DockSize.Large, back.Size);
            Assert.Equal("Lima", back.WeatherCity);
            Assert.Single(back.Pinned);
        }
        finally { File.Delete(Storage.PathFor(name)); }
    }

    [Fact]
    public void CorruptFileFallsBackToDefaults()
    {
        string name = $"test-{Guid.NewGuid():N}.json";
        try
        {
            File.WriteAllText(Storage.PathFor(name), "{ not json");
            var s = Storage.Load<AppSettings>(name);
            Assert.Equal(DockEdge.Bottom, s.Edge);
        }
        finally { File.Delete(Storage.PathFor(name)); }
    }

    [Theory]
    [InlineData(DockSize.Small, 40)]
    [InlineData(DockSize.Medium, 48)]
    [InlineData(DockSize.Large, 58)]
    public void DockHeightFollowsSize(DockSize size, double height) =>
        Assert.Equal(height, new AppSettings { Size = size }.DockHeight);

    [Fact]
    public void DefaultLayoutOnlyUsesKnownWidgets()
    {
        var s = new AppSettings();
        foreach (var id in s.LeftWidgets.Concat(s.RightWidgets))
            Assert.NotNull(WidgetCatalog.Find(id));
        Assert.Equal(WidgetCatalog.All.Count, WidgetCatalog.All.Select(w => w.Id).Distinct().Count());
    }
}

public class LauncherTests
{
    [Fact]
    public void PinFromExecutable()
    {
        string notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe");
        var pin = Launcher.FromFile(notepad);
        Assert.Equal(notepad, pin.ExePath);
        Assert.False(string.IsNullOrWhiteSpace(pin.Name));
    }

    [Fact]
    public void PinFromFolderHasNoExe()
    {
        var pin = Launcher.FromFile(Path.GetTempPath());
        Assert.Null(pin.ExePath);
        Assert.Equal(Path.GetTempPath(), pin.Path);
    }
}

[Collection("Settings file")] // tests here write the sandboxed settings.json
public class ThemeTests
{
    private static string ResourceFingerprint(DockStyle style)
    {
        var p = Theme.For(style);
        return string.Join('|', p.AcrylicTint, p.DockFill, p.DockBorder, p.Foreground,
            p.Subtle, p.Hover, p.Pressed, p.FlyoutBackground, p.FlyoutBorder,
            p.FlyoutForeground, p.FlyoutSubtle, p.InputBackground, p.Accent, p.IsLight, p.Chip, p.ChipBorder, p.Liquid);
    }

    [Fact]
    public void EveryStyleHasOpaqueFlyouts()
    {
        foreach (var style in Enum.GetValues<DockStyle>())
            Assert.Equal(255, Theme.For(style).FlyoutBackground.A); // regression: flyouts used to be see-through
    }

    [Fact]
    public void Strata_offers_exactly_glass_liquid_glass_and_dark()
    {
        Assert.Equal([DockStyle.Glass, DockStyle.Clear, DockStyle.Dark], DockStyles.Selectable);
        Assert.Equal(["Glass", "Liquid glass", "Dark"], DockStyles.Selectable.Select(DockStyles.DisplayName));
        Assert.True(Theme.For(DockStyle.Clear).Liquid);
        Assert.False(Theme.For(DockStyle.Glass).Liquid);
        Assert.False(Theme.For(DockStyle.Dark).Liquid);
        Assert.True(Theme.For(DockStyle.Dark).DockFill.A > 200);                                      // dark mode: near opaque
        Assert.False(Theme.For(DockStyle.Dark).IsLight);
    }

    [Fact]
    public void Liquid_glass_runs_from_fully_transparent_to_a_solid_fill()
    {
        var clear = Theme.LiquidGlass(0);
        Assert.Equal(0u, clear.AcrylicTint);         // no blur at all
        Assert.True(clear.DockFill.A <= 1,           // visually untouched wallpaper; alpha 1 keeps the layered
            "the transparent endpoint may keep one alpha step for hit-testing, nothing more"); // window clickable

        var mid = Theme.LiquidGlass(Theme.DefaultGlassOpacity);
        Assert.NotEqual(0u, mid.AcrylicTint);        // blurred, translucent glass
        Assert.InRange(mid.DockFill.A, 0x20, 0x60);  // one thin milky wash, not a smoked veil
        Assert.True(mid.DockFill.R >= 0xE0 && mid.DockFill.G >= 0xE0 && mid.DockFill.B >= 0xE0,
            "liquid glass brightens what is behind it: the scrim is near-white (the owner's reference, 2026-10-27)");
        Assert.Equal(0, mid.Chip.A); // one glass, one pass: content floats directly on it, no plates at all

        var solid = Theme.LiquidGlass(100);
        Assert.Equal(255, solid.DockFill.A);         // a full fill
        Assert.Equal(0xFFu, solid.AcrylicTint >> 24);

        int last = -1;
        for (int o = 0; o <= 100; o++)
        {
            var p = Theme.LiquidGlass(o);
            Assert.True(p.DockFill.A >= last, $"fill must not drop at {o}%");
            last = p.DockFill.A;
            Assert.Equal(255, p.FlyoutBackground.A);  // flyouts stay opaque at every opacity
            Assert.Equal(0xFF, p.Foreground.A);
        }
    }

    [Theory]
    [InlineData(-20, 0)]
    [InlineData(150, 100)]
    [InlineData(45, 45)]
    public void Liquid_glass_opacity_is_clamped(int asked, int used) =>
        Assert.Equal(Theme.LiquidGlass(used), Theme.LiquidGlass(asked));

    [Fact]
    public void The_liquid_glass_style_uses_the_opacity_it_is_given()
    {
        Assert.Equal(Theme.LiquidGlass(20), Theme.For(DockStyle.Clear, 20));
        Assert.Equal(Theme.LiquidGlass(Theme.DefaultGlassOpacity), Theme.For(DockStyle.Clear));
        Assert.Equal(Theme.For(DockStyle.Glass), Theme.For(DockStyle.Glass, 5));   // the other styles ignore it
        Assert.Equal(Theme.For(DockStyle.Dark), Theme.For(DockStyle.Dark, 95));
    }

    [Fact]
    public void Glass_opacity_is_saved_with_the_settings()
    {
        Assert.Equal(Theme.DefaultGlassOpacity, new AppSettings().GlassOpacity);
        File.WriteAllText(Storage.PathFor("settings.json"), "{ \"style\": \"Clear\", \"glassOpacity\": 12 }");
        try
        {
            var s = AppSettings.Load();
            Assert.Equal(DockStyle.Clear, s.Style);
            Assert.Equal(12, s.GlassOpacity);
        }
        finally { File.Delete(Storage.PathFor("settings.json")); }
    }

    [Theory]
    [InlineData("Frost", DockStyle.Clear)]
    [InlineData("White", DockStyle.Clear)]
    [InlineData("Midnight", DockStyle.Dark)]
    [InlineData("Black", DockStyle.Dark)]
    [InlineData("Glass", DockStyle.Glass)]
    [InlineData("Clear", DockStyle.Clear)]
    [InlineData("Dark", DockStyle.Dark)]
    public void Styles_saved_by_1_1_load_as_the_nearest_1_2_style(string saved, DockStyle expected)
    {
        File.WriteAllText(Storage.PathFor("settings.json"), "{ \"style\": \"" + saved + "\", \"edge\": \"Top\" }");
        try
        {
            var s = AppSettings.Load();
            Assert.Equal(expected, s.Style);
            Assert.Equal(DockEdge.Top, s.Edge);          // the rest of the file still loads
            Assert.Equal(Theme.For(expected), Theme.For(Enum.Parse<DockStyle>(saved)));
        }
        finally { File.Delete(Storage.PathFor("settings.json")); }
    }

    [Theory]
    [InlineData(2, DockStyle.Dark)]    // the 1.1 default evening style, Midnight, saved as a number
    [InlineData(4, DockStyle.Clear)]   // White
    [InlineData(6, DockStyle.Dark)]
    [InlineData(99, DockStyle.Glass)]  // not a style at all
    public void Stored_style_numbers_keep_their_meaning(int stored, DockStyle expected) =>
        Assert.Equal(expected, DockStyles.Normalize((DockStyle)stored));

    [Fact]
    public void Liquid_glass_uses_light_text_on_glass_chips()
    {
        var liquid = Theme.For(DockStyle.Clear);
        Assert.False(liquid.IsLight);
        Assert.True(liquid.Foreground.R > 220);
        Assert.NotEqual(default, liquid.Chip);
        Assert.NotEqual(default, liquid.ChipBorder);
        Assert.Equal(default, Theme.For(DockStyle.Glass).Chip);   // only liquid glass draws chips
    }

    [Fact]
    public void SynchronizingSettingsChangesTheActiveTheme()
    {
        var controller = new ThemeController(DockStyle.Glass);
        foreach (var style in DockStyles.Selectable)
        {
            controller.SyncUserStyle(style);
            Assert.Equal(style, controller.UserStyle);
            Assert.Equal(style, controller.Effective);
        }
    }

    [Fact]
    public void EveryThemeHasDistinctKeyResourceValues()
    {
        var fingerprints = DockStyles.Selectable.Select(ResourceFingerprint).ToArray();
        Assert.Equal(fingerprints.Length, fingerprints.Distinct().Count());
    }

    [Fact]
    public void PreviewPlanCoversEveryDistinctTheme()
    {
        Assert.Equal(DockStyles.Selectable, PreviewRenderer.DockStyles);
        Assert.Equal(PreviewRenderer.DockStyles.Count,
            PreviewRenderer.DockStyles.Select(ResourceFingerprint).Distinct().Count());
    }

    [Fact]
    public void GeneratedDockPreviewSetDoesNotCollapseToOneTheme()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "README.md"))) root = root.Parent;
        Assert.NotNull(root);

        var hashes = PreviewRenderer.DockStyles.Select(style =>
        {
            string path = Path.Combine(root!.FullName, "previews", $"dock-{style.ToString().ToLowerInvariant()}.png");
            Assert.True(File.Exists(path), $"Missing generated preview: {path}");
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
        }).ToArray();
        Assert.Equal(hashes.Length, hashes.Distinct().Count());
    }
}

public class MenuTests
{
    [Fact]
    public void MenuKeepsItemOrder()
    {
        var m = new DockMenu();
        m.Header("App");
        m.Add("Open", () => { });
        m.Separator();
        m.Add("Close", () => { }, danger: true);
        Assert.Equal(new[] { "Open", "Close" }, m.Labels);
    }
}

/// <summary>
/// Found live after installing 1.2 over 1.0: the AI usage widget existed but was on no one's dock, because the
/// 1.0 settings (and the 1.2 defaults) never listed it.
/// </summary>
[Trait("Category", "Unit")]
[Collection("Settings file")]
public class WidgetUpgradeTests
{
    // The shape of a 1.0 settings file (default layout, no introducedWidgets).
    private const string V10Settings = """
        {"edge":"Bottom","style":"Glass","leftWidgets":["weather","media"],
         "rightWidgets":["screentime","cpu","ram","volume","battery","clock"],"pinned":[],"pinnedImported":true}
        """;

    // Through the real AppSettings.Load, from the (sandboxed) settings file.
    private static AppSettings Load(string json)
    {
        File.WriteAllText(Storage.PathFor("settings.json"), json);
        try { return AppSettings.Load(); }
        finally { File.Delete(Storage.PathFor("settings.json")); }
    }

    [Fact]
    public void Settings_from_1_0_get_ai_usage_on_the_dock_once()
    {
        var s = Load(V10Settings);
        Assert.Equal(["aiusage", "screentime", "cpu", "ram", "volume", "battery", "clock"], s.RightWidgets);
        Assert.Equal(["weather", "media"], s.LeftWidgets);
        Assert.Equal(["aiusage"], s.IntroducedWidgets);
        s.IntroduceNewWidgets();
        Assert.Single(s.RightWidgets, "aiusage");
    }

    [Fact]
    public void A_widget_the_user_removed_stays_removed()
    {
        var s = Load(V10Settings);
        s.RightWidgets.Remove("aiusage");
        var again = Load(System.Text.Json.JsonSerializer.Serialize(s, Storage.Json));
        Assert.DoesNotContain("aiusage", again.RightWidgets);
        Assert.DoesNotContain("aiusage", again.LeftWidgets);
    }

    [Fact]
    public void A_widget_already_placed_is_not_added_twice()
    {
        var s = Load(V10Settings.Replace("\"media\"]", "\"media\",\"aiusage\"]"));
        Assert.Equal(["weather", "media", "aiusage"], s.LeftWidgets);
        Assert.DoesNotContain("aiusage", s.RightWidgets);
    }

    [Fact]
    public void New_installs_have_ai_usage_on_the_dock()
    {
        var s = new AppSettings();
        s.IntroduceNewWidgets();
        Assert.Single(s.RightWidgets, "aiusage");
        Assert.All(AppSettings.AddedInLaterVersions, id => Assert.NotNull(WidgetCatalog.Find(id)));
    }
}

/// <summary>
/// Found live by the user on 872bbab: clicking an open widget's tile again reopened its flyout instead of closing
/// it. The dock never takes focus, so the click did not close the flyout first.
/// </summary>
[Trait("Category", "Unit")]
public class FlyoutToggleTests
{
    [Fact]
    public void A_second_click_on_the_same_item_closes_its_flyout()
    {
        object media = new(), calendar = new();
        Assert.True(DockWindow.IsSecondClick(flyoutOpen: true, openAnchor: media, clicked: media));
        Assert.False(DockWindow.IsSecondClick(flyoutOpen: true, openAnchor: media, clicked: calendar));   // another item: switch
        Assert.False(DockWindow.IsSecondClick(flyoutOpen: false, openAnchor: media, clicked: media));    // closed: open it
        Assert.False(DockWindow.IsSecondClick(flyoutOpen: false, openAnchor: null, clicked: media));
    }
}
