using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Strata.Core;
using Strata.Quota;
using Strata.Smart;
using Strata.Widgets;
using Xunit.Abstractions;

namespace Strata.Tests;

[Trait("Category", "Unit")]
public sealed class QuotaStartupTests(ITestOutputHelper output)
{
    private sealed class Host : IDockHost
    {
        public AppSettings Settings { get; } = new() { Quota = new() { ClaudeEnabled = false, TileMode = QuotaTileMode.Codex } };
        public ScreenTimeTracker ScreenTime { get; } = new();
        public SignalStore Signals { get; } = new(new ManualClock(QuotaFixtures.Now.LocalDateTime));
        public void Notify(string key, string title, string text, bool urgent = false) { }
        public void ShowFlyout(FrameworkElement anchor, FrameworkElement content, double width = 320, bool fromKeyboard = false) { }
        public void CloseFlyout() { }
        public void SaveSettings() { }
        public void OpenSettings() { }
    }

    private sealed class Source(Func<Task<QuotaSnapshot>> read) : IQuotaSource
    {
        public Task<QuotaSnapshot> ReadAsync(QuotaSnapshot? last, CancellationToken ct) => read();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Constructing_the_tile_returns_while_a_source_is_stuck_and_keeps_the_unknown_state(bool synchronousBlock)
    {
        using var release = new ManualResetEventSlim();
        var reply = new TaskCompletionSource<QuotaSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                var host = new Host();
                QuotaMonitor Monitor(IQuotaSource source) => new(host.Settings.Quota, source, null,
                    () => null, () => false, null, null, () => QuotaFixtures.Now);
                // Warm WPF/theme setup so the bound measures widget startup, not first framework load.
                host.Settings.Quota.CodexEnabled = false;
                var warm = new QuotaWidget(host, Monitor(new Source(() => reply.Task)));
                warm.Dispose();
                host.Settings.Quota.CodexEnabled = true;
                var monitor = Monitor(new Source(() =>
                {
                    entered.TrySetResult(Environment.CurrentManagedThreadId);
                    if (synchronousBlock) release.Wait(); // no progress until test cleanup; ignores cancellation
                    return reply.Task;
                }));
                var watch = Stopwatch.StartNew();
                var tile = new QuotaWidget(host, monitor);
                watch.Stop();
                output.WriteLine($"Tile construction: {watch.Elapsed.TotalMilliseconds:F1} ms (blocking prefix: {synchronousBlock})");
                Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(250), "Widget construction waited for the source.");
                Assert.Equal(QuotaStatus.WaitingForFirstSample, monitor.Codex.Status);
                Assert.Null(monitor.Codex.Snapshot);
                Assert.Contains(QuotaFormat.WaitingForFirstSample, AutomationProperties.GetItemStatus(tile));
                Assert.Contains(((Grid)tile.Content).Children.OfType<TextBlock>(), text => text.Text == "Loading…");
                Assert.True(monitor.Refreshing);
                Assert.True(entered.Task.Wait(TimeSpan.FromSeconds(5)), "Source worker did not start.");
                Assert.NotEqual(Environment.CurrentManagedThreadId, entered.Task.Result);
                var refresh = monitor.RefreshAsync();
                var changedOn = 0;
                monitor.Changed += () => changedOn = Environment.CurrentManagedThreadId;
                tile.Dispose();
                release.Set();
                reply.TrySetResult(QuotaFixtures.Codex("codex-weekly-reached.json"));
                var until = Stopwatch.StartNew();
                while (!refresh.IsCompleted && until.Elapsed < TimeSpan.FromSeconds(5))
                {
                    Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ContextIdle, new Action(() => { }));
                    Thread.Sleep(1);
                }
                Assert.True(refresh.IsCompletedSuccessfully, "Completion did not return through the dispatcher.");
                Assert.Equal(Environment.CurrentManagedThreadId, changedOn);
                Assert.Equal(QuotaStatus.Fresh, monitor.Codex.Status);
            }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try { Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Widget startup blocked its STA thread."); }
        finally
        {
            release.Set();
            reply.TrySetResult(QuotaFixtures.Codex("codex-weekly-reached.json"));
            thread.Join(TimeSpan.FromSeconds(5));
        }
        failure?.Throw();
    }

    private static IQuotaSource Provider(bool claude, TimeSpan timeout, Func<ProcessStartInfo, Process?> start) => claude
        ? new ClaudeUsageSource(() => "fixture", timeout, start)
        : new CodexAppServerSource(() => "fixture", "1.3.2", timeout, start);

    private static Process SleepingChild()
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 300" }) psi.ArgumentList.Add(arg);
        return Process.Start(psi)!;
    }

    private static void Cleanup(Process? child)
    {
        try { if (child is { HasExited: false }) child.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        child?.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_read_deadline_reports_timeout_keeps_the_cache_and_kills_the_child(bool claude)
    {
        Process? observer = null;
        var source = Provider(claude, TimeSpan.FromSeconds(2), _ =>
        {
            var child = SleepingChild();
            observer = Process.GetProcessById(child.Id);
            return child;
        });
        var cached = QuotaFixtures.Codex("codex-weekly-reached.json");
        var settings = new QuotaSettings { ClaudeEnabled = claude, CodexEnabled = !claude };
        var monitor = new QuotaMonitor(settings, source, claude ? source : null, () => null, () => false,
            new QuotaStore { Codex = cached, Claude = cached with { Provider = QuotaProvider.Claude } }, null, () => QuotaFixtures.Now);
        try
        {
            var watch = Stopwatch.StartNew();
            await monitor.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(6));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), "Read exceeded its deadline plus scheduling allowance.");
            var state = claude ? monitor.Claude : monitor.Codex;
            Assert.Equal((QuotaStatus.Stale, QuotaErrorKind.Timeout), (state.Status, state.Error));
            Assert.NotNull(state.Snapshot);
            Assert.NotNull(observer);
            await observer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(observer.HasExited);
        }
        finally { Cleanup(observer); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Caller_cancellation_kills_an_already_started_child(bool claude)
    {
        Process? observer = null;
        using var cancel = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = Provider(claude, TimeSpan.FromSeconds(10), _ =>
        {
            var child = SleepingChild();
            observer = Process.GetProcessById(child.Id);
            started.SetResult();
            return child;
        });
        try
        {
            var read = source.ReadAsync(null, cancel.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(5)));
            await observer!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { Cleanup(observer); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_stuck_launch_times_out_without_retry_workers_and_kills_a_late_child(bool claude)
    {
        Process? observer = null;
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launches = 0;
        var source = Provider(claude, TimeSpan.FromSeconds(2), _ =>
        {
            Interlocked.Increment(ref launches);
            var child = SleepingChild();
            observer = Process.GetProcessById(child.Id);
            entered.SetResult();
            release.Wait(); // Process.Start has not returned a handle to the source yet.
            return child;
        });
        try
        {
            var watch = Stopwatch.StartNew();
            var read = source.ReadAsync(null, CancellationToken.None);
            Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(250), "ReadAsync ran the launch on its caller.");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var error = await Assert.ThrowsAsync<QuotaSourceException>(() => read.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(QuotaErrorKind.Timeout, error.Kind);
            Assert.False(observer!.HasExited); // The OS has not given the source a handle it can kill.
            for (int i = 0; i < 3; i++)
            {
                error = await Assert.ThrowsAsync<QuotaSourceException>(() => source.ReadAsync(null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)));
                Assert.Equal(QuotaErrorKind.Timeout, error.Kind);
            }
            Assert.Equal(1, launches);
            release.Set();
            await observer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { release.Set(); Cleanup(observer); }
    }
}
