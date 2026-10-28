using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Strata.Core;
using Strata.Smart;

namespace Strata.Tests;

[Trait("Category", "Unit")]
public sealed class DiagnosticsActivityTests
{
    [Fact]
    public void Error_journal_is_bounded_and_never_keeps_messages_or_component_details()
    {
        var journal = new DiagnosticErrorJournal(3);
        for (int i = 0; i < 5; i++)
            journal.Record("widget:PrivateWidgetName", new IOException($@"C:\Users\Alice\Secret-{i}.txt token=abc"),
                new DateTime(2026, 10, 25, 12, i, 0));

        Assert.Equal(3, journal.Items.Count);
        Assert.All(journal.Items, e =>
        {
            Assert.Equal("widget", e.Component);
            Assert.Equal(nameof(IOException), e.ErrorType);
        });
        string serialized = JsonSerializer.Serialize(journal.Items, Storage.Json);
        Assert.DoesNotContain("Alice", serialized);
        Assert.DoesNotContain("Secret", serialized);
        Assert.DoesNotContain("token", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PrivateWidgetName", serialized);
    }

    [Fact]
    public void Diagnostic_bundle_contains_required_evidence_but_no_planted_personal_values()
    {
        const string marker = "PLANTED_PERSONAL_MARKER_9F3";
        string dir = Path.Combine(Path.GetTempPath(), "strata-diagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string executable = Path.Combine(dir, marker + ".exe");
        byte[] bytes = Encoding.UTF8.GetBytes("fixture executable bytes");
        File.WriteAllBytes(executable, bytes);
        try
        {
            var errors = new DiagnosticErrorJournal();
            errors.Record("window:" + marker, new IOException(marker + " token account file.txt"), new DateTime(2026, 10, 25, 12, 0, 0));
            var performance = new PerformanceSnapshot(60, 1.25, 8.5, 100, 120, 140, 10, 12, 4, 5);
            var monitors = new[] { new MonitorGeometry(0, 0, 1920, 1080, 0, 0, 1920, 1040, true) };
            string json = DiagnosticBundleBuilder.Create("1.3.0+fixture", executable, errors, performance, monitors,
                new DateTime(2026, 10, 25, 12, 1, 0));

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            Assert.Equal(DiagnosticBundleBuilder.Format, root.GetProperty("format").GetString());
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), root.GetProperty("executableSha256").GetString());
            Assert.Equal(bytes.Length, root.GetProperty("executableSizeBytes").GetInt64());
            Assert.True(root.GetProperty("configurationSchema").TryGetProperty("taskbarMode", out _));
            Assert.Equal(64, root.GetProperty("configurationSchemaSha256").GetString()!.Length);
            Assert.Equal(60, root.GetProperty("performance").GetProperty("samples").GetInt64());
            Assert.Equal(1920, root.GetProperty("monitors")[0].GetProperty("width").GetInt32());
            Assert.DoesNotContain(marker, json);
            Assert.DoesNotContain(executable, json);
            Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("account file", json, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Performance_sampler_tracks_mean_max_growth_and_current_values()
    {
        var sampler = new PerformanceSampler();
        sampler.Record(2, 100, 10, 4);
        sampler.Record(8, 150, 13, 6);
        sampler.Record(5, 120, 11, 5);
        var snapshot = sampler.Snapshot();
        Assert.Equal(3, snapshot.Samples);
        Assert.Equal(5, snapshot.CpuMeanPercent);
        Assert.Equal(8, snapshot.CpuMaxPercent);
        Assert.True(snapshot.PrivateMemoryMaxBytes >= 150);
        Assert.Equal(120, snapshot.PrivateMemoryCurrentBytes);
        Assert.True(snapshot.HandlesMax >= 13);
        Assert.Equal(11, snapshot.HandlesCurrent);
        Assert.True(snapshot.ThreadsMax >= 6);
        Assert.Equal(5, snapshot.ThreadsCurrent);
    }

    [Fact]
    public void Activity_projection_explains_actions_and_undo_without_copying_private_history_text()
    {
        const string marker = "PRIVATE_MEETING_TITLE_77";
        var at = new DateTime(2026, 10, 25, 12, 0, 0);
        var history = new[]
        {
            new HistoryEntry(at, Automations.MeetingPrep, "Fired", $"{marker} starts soon", $"opened {marker}"),
            new HistoryEntry(at.AddMinutes(20), Automations.MeetingPrep, "Ended", $"{marker} ended", $"closed {marker}"),
        };
        var projected = AutomationActivity.Project(history, Automations.Create());
        Assert.Equal(2, projected.Count);
        Assert.True(projected[0].Undone);
        Assert.Contains("Restored", projected[0].Changed);
        Assert.Contains("Condition matched", projected[1].Why);
        string json = JsonSerializer.Serialize(projected, Storage.Json);
        Assert.DoesNotContain(marker, json);
        Assert.Contains("Meeting preparation", json);
        Assert.Contains("calendar", json);
    }

    [Fact]
    public void Activity_view_is_hard_bounded_to_fifty_entries()
    {
        var history = Enumerable.Range(0, 90)
            .Select(i => new HistoryEntry(new DateTime(2026, 10, 25).AddMinutes(i), Automations.LowBattery, "Fired", i.ToString(), i.ToString()))
            .ToList();
        var activity = AutomationActivity.Project(history, Automations.Create(), 90);
        Assert.Equal(AutomationActivity.ViewLimit, activity.Count);
        Assert.Equal(history[^1].At, activity[0].At);
    }
}
