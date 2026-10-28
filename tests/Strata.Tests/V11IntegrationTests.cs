using Strata.Core;
using Strata.Smart;
using Strata.Views;
using Strata.Widgets;
using System.Windows.Controls;

namespace Strata.Tests;

[Trait("Category", "Unit")]
public class V11IntegrationTests
{
    [Fact]
    public void LiveDockImplementsTheAutomationActionSurface() =>
        Assert.True(typeof(IDockActions).IsAssignableFrom(typeof(DockWindow)));

    [Fact]
    public void CatalogHasExactlyNineNamedAutomations()   // 1.1's eight, plus the 1.2 night layout
    {
        var rules = Automations.Create();
        Assert.Equal(9, rules.Count);
        Assert.Equal(new[]
        {
            Automations.Connectivity, Automations.EventReminder, Automations.FocusMode, Automations.LowBattery,
            Automations.MeetingPrep, Automations.NightLayout, Automations.ResourcePressure, Automations.ScreenTimeCheck, Automations.SunsetTheme,
        }, rules.Select(r => r.Id).OrderBy(x => x));
    }

    [Fact]
    public void AutomationPersistenceUsesConfiguredStorageRoot()
    {
        var persistence = new AutomationPersistence();
        var state = new AutomationStateFile();
        const string marker = "Private meeting with someone@example.com";
        state.Rules[Automations.LowBattery] = new RuleState
        {
            Enabled = false, LastExplanation = marker, LastOutcome = marker, LastError = marker,
        };
        state.History.Add(new HistoryEntry(DateTime.Now, Automations.LowBattery, "Fired", marker, marker, PrivacySafe: true));
        persistence.Save(state);
        var loaded = persistence.Load();
        Assert.False(loaded.Rules[Automations.LowBattery].Enabled);
        Assert.DoesNotContain(marker, File.ReadAllText(Storage.PathFor(AutomationPersistence.FileName)));
        Assert.DoesNotContain(marker, loaded.Rules[Automations.LowBattery].LastExplanation);
        Assert.DoesNotContain(marker, loaded.Rules[Automations.LowBattery].LastOutcome);
        Assert.DoesNotContain(marker, loaded.Rules[Automations.LowBattery].LastError);
        Assert.DoesNotContain(loaded.History, item => item.Explanation.Contains(marker) || item.Detail.Contains(marker));
        Assert.StartsWith(TestSetup.DataRoot, Storage.PathFor(AutomationPersistence.FileName), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DpiAwareIconKeysSeparatePhysicalSizes()
    {
        Assert.Equal(60, IconCache.SourcePixels(30, 1.0));
        Assert.Equal(150, IconCache.SourcePixels(30, 2.5));
        Assert.NotEqual(IconCache.Key("app:x", 60, 1), IconCache.Key("app:x", 150, 2.5));
    }

    [Fact]
    public void AlphaRepairKeepsTransparentCornersTransparent()
    {
        byte[] pixels =
        [
            0, 0, 0, 0,          // transparent corner
            255, 128, 64, 0,     // colored legacy pixel
        ];
        IconCache.NormalizePremultipliedAlpha(pixels, hasAlpha: false);
        Assert.Equal(0, pixels[3]);
        Assert.Equal(255, pixels[7]);
        Assert.True(pixels[4] <= pixels[7] && pixels[5] <= pixels[7] && pixels[6] <= pixels[7]);
    }

    [Fact]
    public void PrivacyScanPassesFixturesAndFindsProfilePaths()
    {
        string dir = Path.Combine(TestSetup.DataRoot, "privacy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "safe.txt"), "Sample Editor and Sample City");
            Assert.Empty(PreviewPrivacy.Scan(dir));
            File.WriteAllText(Path.Combine(dir, "bad.txt"), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            Assert.NotEmpty(PreviewPrivacy.Scan(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AccountNamesMatchAsWholeCaseSensitiveWordsButPathsMatchAnywhere()
    {
        // An account name that is an ordinary word ("Guest") must not flag ordinary UI text, but must flag the name itself and any profile path.
        var markers = PreviewPrivacy.Markers("Guest", @"C:\Users\Guest");
        bool Flags(string text) => markers.Any(m => m.Found(text));
        Assert.False(Flags("Everything stays in the guest folder on this PC."));
        Assert.False(Flags("Guests and guestbooks are fine"));
        Assert.True(Flags("Signed in as Guest"));
        Assert.True(Flags(@"c:\users\someone-else\AppData"));      // profile-root prefix, any case
        Assert.True(Flags(@"C:\USERS\GUEST\Documents"));
        var named = PreviewPrivacy.Markers("fixtureperson", @"C:\Users\fixtureperson");
        Assert.Contains(named, m => m.Found("file of fixtureperson."));
        Assert.DoesNotContain(named, m => m.Found("fixturepersonx"));
    }

    [Fact]
    public void PrivacyScanChecksTextBeforePngRasterization()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var visual = new Border { Child = new TextBlock { Text = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) } };
                Assert.NotEmpty(PreviewPrivacy.ScanVisual(visual));
                ((TextBlock)visual.Child).Text = "Fixture storage (isolated)";
                Assert.Empty(PreviewPrivacy.ScanVisual(visual));
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "STA integration test timed out.");
        if (failure is not null) throw failure;
    }
}

[Trait("Category", "Unit")]
public class RefreshCoordinatorTests
{
    [Fact]
    public async Task BurstsNeverOverlapAndCoalesceToOneFollowUp()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0, active = 0, maximum = 0;
        using var coordinator = new RefreshCoordinator(async () =>
        {
            int current = Interlocked.Increment(ref active);
            maximum = Math.Max(maximum, current);
            int call = Interlocked.Increment(ref calls);
            if (call == 1)
            {
                started.SetResult();
                await release.Task;
            }
            Interlocked.Decrement(ref active);
        }, TimeSpan.FromSeconds(15));

        coordinator.Request();
        await started.Task;
        coordinator.Request();
        coordinator.Request();
        coordinator.Request();
        release.SetResult();
        await coordinator.WhenIdle;

        Assert.Equal(2, calls);
        Assert.Equal(1, maximum);
    }

    [Fact]
    public async Task PollingIsBoundedAndErrorsDoNotStopLaterRefreshes()
    {
        int calls = 0, errors = 0;
        using var coordinator = new RefreshCoordinator(() =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("fixture");
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(15), _ => Interlocked.Increment(ref errors));
        var start = new DateTime(2026, 1, 1, 12, 0, 0);

        coordinator.Poll(start);
        coordinator.Poll(start.AddSeconds(14));
        Assert.Equal(0, calls);
        coordinator.Poll(start.AddSeconds(15));
        await coordinator.WhenIdle;
        Assert.Equal(1, calls);
        Assert.Equal(1, errors);

        coordinator.Request();
        await coordinator.WhenIdle;
        Assert.Equal(2, calls);
    }
}
