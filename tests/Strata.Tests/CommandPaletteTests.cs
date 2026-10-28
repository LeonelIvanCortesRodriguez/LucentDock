using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Strata.Core;
using Strata.Native;
using Strata.Smart;
using Strata.Views;

namespace Strata.Tests;

[Trait("Category", "Unit")]
public sealed class CommandPaletteTests
{
    [Fact]
    public void Fuzzy_search_ranks_exact_prefix_and_subsequence_without_executing()
    {
        int executions = 0;
        var commands = new[]
        {
            Command("window", "Visual Studio Code", "Editor window", PaletteCommandKind.Window, () => executions++),
            Command("settings", "Code settings", "Strata settings", PaletteCommandKind.Setting, () => executions++),
            Command("app", "Calculator", "Installed application", PaletteCommandKind.Application, () => executions++),
        };

        Assert.Equal("Visual Studio Code", PaletteMatcher.Search(commands, "vsc")[0].Command.Title);
        Assert.Equal("Code settings", PaletteMatcher.Search(commands, "code settings")[0].Command.Title);
        Assert.Equal("Calculator", PaletteMatcher.Search(commands, "calc")[0].Command.Title);
        Assert.Equal(0, executions); // user text only ranks typed actions; it is never executed or parsed as a shell command
    }

    [Fact]
    public void Multiple_terms_must_all_match_and_result_limit_is_enforced()
    {
        var commands = Enumerable.Range(0, 30)
            .Select(i => Command(i.ToString(), $"Editor {i}", i % 2 == 0 ? "running window" : "installed application",
                PaletteCommandKind.Application, () => { })).ToList();
        var result = PaletteMatcher.Search(commands, "editor running", 7);
        Assert.Equal(7, result.Count);
        Assert.All(result, r => Assert.Contains("running", r.Command.Subtitle));
    }

    [Fact]
    public void Realistic_catalog_search_stays_responsive()
    {
        var commands = Enumerable.Range(0, 5000)
            .Select(i => Command($"app:{i}", $"Application {i:0000}", $"Window {i % 71}", PaletteCommandKind.Application, () => { }))
            .ToList();
        PaletteMatcher.Search(commands, "application 0420"); // JIT warm-up
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 100; i++) PaletteMatcher.Search(commands, $"app {i:0000}", 12);
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"100 searches took {sw.Elapsed.TotalMilliseconds:0} ms");
    }

    [Fact]
    public void Start_menu_source_accepts_only_shortcuts_inside_its_roots()
    {
        string root = Path.Combine(Path.GetTempPath(), "strata-palette-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Tools"));
        try
        {
            File.WriteAllText(Path.Combine(root, "One.lnk"), "fixture");
            File.WriteAllText(Path.Combine(root, "Tools", "Two.lnk"), "fixture");
            File.WriteAllText(Path.Combine(root, "Ignore.exe"), "fixture");
            var source = new StartMenuApplicationSource([root], path => new PinnedApp
            {
                Name = Path.GetFileNameWithoutExtension(path), Path = path,
            }, shellApps: () => []);

            var apps = source.Enumerate();
            Assert.Equal(["One", "Two"], apps.Select(a => a.Name).Order().ToArray());
            Assert.All(apps, a => Assert.EndsWith(".lnk", a.Path, StringComparison.OrdinalIgnoreCase));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Shell_catalog_includes_packaged_apps_by_typed_AUMID()
    {
        var source = new StartMenuApplicationSource([], _ => null, () =>
        [
            new PinnedApp { Name = "Fixture packaged app", Aumid = "Fixture.Package!App", Path = "" },
        ]);
        var app = Assert.Single(source.Enumerate());
        Assert.Equal("Fixture.Package!App", app.Aumid);
        Assert.Empty(app.Path);
    }

    [Fact]
    public void Shell_catalog_access_denial_is_contained_as_an_unavailable_source()
    {
        var source = new StartMenuApplicationSource([], _ => null,
            () => throw new UnauthorizedAccessException("fixture"));

        Assert.Empty(source.Enumerate());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failed_palette_hotkey_is_an_urgent_warning_even_during_quiet_hours(bool quietNow)
    {
        var notifier = new FakeNotifier();
        var clock = new ManualClock(new DateTime(2026, 10, 25, quietNow ? 23 : 12, 0, 0));
        var center = new NotificationCenter(notifier, clock, new QuietHours { Enabled = true });
        var hotkey = new CommandPaletteHotkey((mods, key) =>
        {
            Assert.Equal(Win32.MOD_WIN | Win32.MOD_ALT | Win32.MOD_NOREPEAT, mods);
            Assert.Equal(0x20u, key);
            return false;
        }, center);

        Assert.False(hotkey.Register());
        Assert.False(hotkey.Registered);
        var shown = Assert.Single(notifier.Shown);
        Assert.Equal(CommandPaletteHotkey.WarningTitle, shown.Title);
        Assert.Contains(CommandPaletteHotkey.Gesture, shown.Body);
        Assert.Equal(NotifyOutcome.Sent, Assert.Single(center.Recent).Outcome);
    }

    [Fact]
    public void Palette_geometry_stays_fixed_through_one_hundred_result_transitions() => OnSta(() =>
    {
        var commands = Enumerable.Range(0, 200)
            .Select(i => Command(i.ToString(), $"Command {i}", $"Status {i % 11}", PaletteCommandKind.Action, () => { }))
            .ToList();
        var palette = new CommandPaletteWindow(commands);
        Assert.Equal("Strata command palette", AutomationProperties.GetName(palette));

        for (int i = 0; i < 100; i++)
        {
            palette.SetQuery(i % 3 == 0 ? "" : i % 3 == 1 ? $"command {i}" : "no result at all");
            palette.Measure(new Size(CommandPaletteWindow.PaletteWidth, CommandPaletteWindow.PaletteHeight));
            Assert.Equal(CommandPaletteWindow.PaletteWidth, palette.Width);
            Assert.Equal(CommandPaletteWindow.PaletteHeight, palette.Height);
            Assert.InRange(palette.ResultCount, 0, CommandPaletteWindow.VisibleResultLimit);
        }
        palette.Close();
    });

    [Fact]
    public void Keyboard_selection_has_an_explicit_high_contrast_visible_state() => OnSta(() =>
    {
        Style style = CommandPaletteWindow.CreateResultItemStyle();
        Trigger selected = Assert.Single(style.Triggers.OfType<Trigger>());

        Assert.Equal(ListBoxItem.IsSelectedProperty, selected.Property);
        Assert.Equal(true, selected.Value);
        Assert.Contains(selected.Setters.OfType<Setter>(), setter =>
            setter.Property == Control.BackgroundProperty && Equals(setter.Value, SystemColors.HighlightBrush));
        Assert.Contains(selected.Setters.OfType<Setter>(), setter =>
            setter.Property == Control.ForegroundProperty && Equals(setter.Value, SystemColors.HighlightTextBrush));
        Assert.Contains(selected.Setters.OfType<Setter>(), setter => setter.Property == Control.BorderThicknessProperty);
    });

    private static PaletteCommand Command(string id, string title, string subtitle, PaletteCommandKind kind, Action action) =>
        new(id, title, subtitle, title + " " + subtitle, kind, action);

    private static void OnSta(Action action)
    {
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "STA command-palette test timed out.");
        error?.Throw();
    }
}
