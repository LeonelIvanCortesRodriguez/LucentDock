using System.Diagnostics;
using Strata.Quota;

namespace Strata.Tests;

[Trait("Category", "Unit")]
public sealed class QuotaCancellationCleanupTests
{
    private static Process SleepingChild()
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 300" })
            info.ArgumentList.Add(argument);
        return Process.Start(info)!;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_blocking_cancellation_callback_cannot_keep_an_owned_child_alive_or_admit_retry_workers(bool cancelByCaller)
    {
        using var release = new ManualResetEventSlim();
        using var cancel = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = new TaskCompletionSource<QuotaSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshot = QuotaFixtures.Codex("codex-weekly-reached.json");
        Process? observer = null;
        var runner = new QuotaReadRunner(TimeSpan.FromSeconds(cancelByCaller ? 10 : 2), _ => SleepingChild());
        var read = runner.ReadAsync((processes, token) =>
        {
            var child = processes.Start(new ProcessStartInfo("fixture"))!;
            observer = Process.GetProcessById(child.Process.Id);
            // Registered after child ownership: cleanup must not depend on reaching an earlier token callback.
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
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (cancelByCaller) await Task.Run(cancel.Cancel).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Same(read, await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(5))));
            if (cancelByCaller) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
            else await Assert.ThrowsAsync<TimeoutException>(() => read);

            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(callbackFinished.Task.IsCompleted);
            await observer!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(observer.HasExited);
            Assert.False(callbackFinished.Task.IsCompleted); // Child cleanup did not wait for the callback.

            // Let the provider finish while its cancellation callback stays blocked. New reads must remain
            // excluded throughout that interval, rather than accumulating one blocked callback per refresh.
            reply.SetResult(snapshot);
            var attempts = Stopwatch.StartNew();
            var retryInvocations = 0;
            Task<QuotaSnapshot> Retry() => runner.ReadAsync((_, _) =>
            {
                Interlocked.Increment(ref retryInvocations);
                return Task.FromResult(snapshot);
            }, CancellationToken.None);
            do
            {
                await Assert.ThrowsAsync<TimeoutException>(() => Retry());
                await Task.Delay(10);
            } while (attempts.Elapsed < TimeSpan.FromMilliseconds(250));
            Assert.Equal(0, retryInvocations);

            release.Set();
            await callbackFinished.Task.WaitAsync(TimeSpan.FromSeconds(3));
            // Retirement includes the worker and callback, so admission may reopen just after the marker.
            var retryUntil = Stopwatch.StartNew();
            QuotaSnapshot? retried = null;
            while (retried is null && retryUntil.Elapsed < TimeSpan.FromSeconds(3))
            {
                try { retried = await Retry().WaitAsync(TimeSpan.FromSeconds(1)); }
                catch (TimeoutException) { await Task.Delay(10); }
            }
            Assert.Same(snapshot, retried);
            Assert.Equal(1, retryInvocations);
        }
        finally
        {
            release.Set();
            reply.TrySetResult(snapshot);
            if (callbackEntered.Task.IsCompleted) await callbackFinished.Task.WaitAsync(TimeSpan.FromSeconds(3));
            try { if (observer is { HasExited: false }) observer.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            observer?.Dispose();
        }
    }
}
