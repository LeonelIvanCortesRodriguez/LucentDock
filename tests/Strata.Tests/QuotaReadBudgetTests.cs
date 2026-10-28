using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Strata.Quota;
using Xunit.Abstractions;

namespace Strata.Tests;

[Trait("Category", "Unit")]
public sealed class QuotaReadBudgetTests(ITestOutputHelper output)
{
    private static Process Child(string command)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(command)) }) psi.ArgumentList.Add(arg);
        return Process.Start(psi)!;
    }

    private static void Cleanup(Process child)
    {
        try { if (!child.HasExited) child.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        child.Dispose();
    }

    [Fact]
    public async Task Claude_auth_and_usage_share_one_deadline_and_both_children_end()
    {
        var budget = TimeSpan.FromSeconds(6);
        var children = new ConcurrentBag<Process>();
        var usageStarted = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watch = new Stopwatch();
        var launches = 0;
        var source = new ClaudeUsageSource(() => "fixture", budget, info =>
        {
            var stage = Interlocked.Increment(ref launches);
            Assert.Equal(stage == 1 ? new[] { "auth", "status" } : ClaudeUsageReport.UsageArguments, info.ArgumentList.ToArray());
            var child = Child(stage == 1
                ? "Start-Sleep -Milliseconds 3000; [Console]::Out.WriteLine('{\"loggedIn\":true,\"subscriptionType\":\"pro\"}')"
                : "Start-Sleep -Seconds 300");
            children.Add(Process.GetProcessById(child.Id));
            if (stage == 2) usageStarted.SetResult(watch.Elapsed);
            return child;
        });
        try
        {
            watch.Start();
            var read = source.ReadAsync(null, CancellationToken.None);
            var error = await Assert.ThrowsAsync<QuotaSourceException>(() => read.WaitAsync(TimeSpan.FromSeconds(9)));
            var elapsed = watch.Elapsed;
            Assert.Equal(QuotaErrorKind.Timeout, error.Kind);
            Assert.True(usageStarted.Task.IsCompletedSuccessfully, "Auth did not complete successfully before the shared deadline.");
            var usageAt = await usageStarted.Task;
            output.WriteLine($"Auth/launch consumed {usageAt.TotalSeconds:F2}s; total read {elapsed.TotalSeconds:F2}s; budget {budget.TotalSeconds:F0}s.");
            Assert.True(usageAt >= TimeSpan.FromSeconds(3), "The auth fixture did not consume its intended part of the budget.");
            Assert.True(elapsed < budget + TimeSpan.FromSeconds(1.5), "Usage received a fresh deadline after auth.");
            Assert.True(elapsed - usageAt < budget - TimeSpan.FromSeconds(1.5), "Usage did not inherit the time auth had already spent.");
            Assert.Equal(2, launches);
            Assert.Equal(2, children.Count);
            foreach (var child in children)
            {
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(child.HasExited);
            }
        }
        finally { foreach (var child in children) Cleanup(child); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_already_cancelled_read_neither_locates_nor_launches_a_provider(bool claude)
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var locations = 0;
        var launches = 0;
        string? Locate() { Interlocked.Increment(ref locations); return "fixture"; }
        Process? Start(ProcessStartInfo _) { Interlocked.Increment(ref launches); return null; }
        IQuotaSource source = claude
            ? new ClaudeUsageSource(Locate, TimeSpan.FromSeconds(10), Start)
            : new CodexAppServerSource(Locate, "1.3.2", TimeSpan.FromSeconds(10), Start);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.ReadAsync(null, cancel.Token).WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(0, locations);
        Assert.Equal(0, launches);
    }

    [Fact]
    public async Task A_success_returned_from_the_deadline_callback_cannot_beat_timeout()
    {
        var runner = new QuotaReadRunner(TimeSpan.FromSeconds(1), _ => throw new InvalidOperationException("No child is needed."));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshot = QuotaFixtures.Codex("codex-weekly-reached.json");
        var watch = Stopwatch.StartNew();
        var read = runner.ReadAsync((_, token) =>
        {
            // Deliberately allow inline continuations: this callback can finish the worker before WaitAsync's
            // cancellation callback is invoked. A provider's late success must still be rejected.
            var reply = new TaskCompletionSource<QuotaSnapshot>();
            token.Register(() => { reply.TrySetResult(snapshot); replied.SetResult(); });
            entered.SetResult();
            return reply.Task;
        }, CancellationToken.None);

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var completed = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(4)));
        Assert.Same(read, completed);
        await Assert.ThrowsAsync<TimeoutException>(() => read);
        await replied.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(read.IsFaulted);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), "The deadline did not settle the public read.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_blocking_provider_cancellation_callback_cannot_hold_up_the_public_read_or_caller(bool cancelByCaller)
    {
        using var release = new ManualResetEventSlim();
        using var cancel = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = new TaskCompletionSource<QuotaSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new QuotaReadRunner(TimeSpan.FromSeconds(cancelByCaller ? 10 : 1),
            _ => throw new InvalidOperationException("No child is needed."));
        var read = runner.ReadAsync((_, token) =>
        {
            token.Register(() =>
            {
                callbackEntered.SetResult();
                release.Wait();
                callbackFinished.SetResult();
            });
            entered.SetResult();
            return reply.Task;
        }, cancel.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if (cancelByCaller)
            {
                // A separate thread bounds a broken implementation that runs provider callbacks in Cancel().
                var cancelled = Task.Run(cancel.Cancel);
                await cancelled.WaitAsync(TimeSpan.FromSeconds(2));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(2)));
            }
            else
            {
                var completed = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(3)));
                Assert.Same(read, completed);
                await Assert.ThrowsAsync<TimeoutException>(() => read);
            }
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(callbackFinished.Task.IsCompleted);
        }
        finally
        {
            release.Set();
            reply.TrySetResult(QuotaFixtures.Codex("codex-weekly-reached.json"));
            await callbackFinished.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }
}
