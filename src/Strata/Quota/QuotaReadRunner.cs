using System.Diagnostics;
using Strata.Core;

namespace Strata.Quota;

/// <summary>
/// Runs a whole provider read away from the caller and bounds its public wait, including executable lookup and
/// the synchronous native launch. Cancellation cannot interrupt CreateProcess itself: a late child is killed as
/// soon as its handle becomes available, and no further worker is started while that launch is still stuck.
/// </summary>
internal sealed class QuotaReadRunner(TimeSpan timeout, Func<ProcessStartInfo, Process?> start)
{
    private readonly object _gate = new();
    private Task? _retirement;

    public Task<QuotaSnapshot> ReadAsync(Func<QuotaProcessSession, CancellationToken, Task<QuotaSnapshot>> read, CancellationToken ct)
    {
        lock (_gate)
        {
            if (ct.IsCancellationRequested) return Task.FromCanceled<QuotaSnapshot>(ct);
            if (_retirement is { IsCompleted: false }) return Task.FromException<QuotaSnapshot>(new TimeoutException());

            var elapsed = Stopwatch.StartNew();
            // The worker token is deliberately independent. Linking it to the caller would run provider
            // cancellation callbacks (and any inline cleanup continuations) inside caller.Cancel().
            var deadline = new CancellationTokenSource();
            var token = deadline.Token;
            // Own children before starting the worker so the public timeout can abort them independently of
            // provider cancellation callbacks. A blocked callback must not prevent the child from being killed.
            var processes = new QuotaProcessSession(start, token);
            var retired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var worker = Task.Run(async () =>
            {
                using (processes)
                {
                    token.ThrowIfCancellationRequested();
                    var snapshot = await read(processes, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    return snapshot;
                }
            });
            _retirement = retired.Task;
            return WaitAsync(worker, processes, deadline, ct, timeout, elapsed, retired);
        }
    }

    private static async Task<QuotaSnapshot> WaitAsync(Task<QuotaSnapshot> worker, QuotaProcessSession processes,
        CancellationTokenSource deadline, CancellationToken caller, TimeSpan timeout, Stopwatch elapsed, TaskCompletionSource retired)
    {
        Task cancellation = Task.CompletedTask;
        try
        {
            var remaining = timeout - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero) throw new TimeoutException();
            // This deadline must not share the worker token: one blocking provider cancellation callback
            // must never hold up the public timeout or cancellation result.
            var snapshot = await worker.WaitAsync(remaining, caller).ConfigureAwait(false);
            caller.ThrowIfCancellationRequested();
            if (elapsed.Elapsed >= timeout) throw new TimeoutException();
            return snapshot;
        }
        catch (Exception ex) when (ex is not PrivacyViolationException && caller.IsCancellationRequested)
        {
            cancellation = deadline.CancelAsync();
            processes.Abort();
            throw new OperationCanceledException(caller);
        }
        catch (Exception ex) when (ex is not PrivacyViolationException && (ex is TimeoutException || elapsed.Elapsed >= timeout))
        {
            cancellation = deadline.CancelAsync();
            processes.Abort();
            throw new TimeoutException();
        }
        finally
        {
            // Keep cancellation alive until both the worker and its asynchronous cancellation callbacks have
            // finished. Keep admission closed too: retrying while a callback is stuck would accumulate workers.
            // Observe their faults even when the public read has already timed out.
            Observe(worker);
            Observe(cancellation);
            _ = Task.WhenAll(worker, cancellation).ContinueWith(t =>
            {
                _ = t.Exception;
                deadline.Dispose();
                retired.TrySetResult();
            },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    internal static void Observe(Task task) => _ = task.ContinueWith(t => { _ = t.Exception; },
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}

/// <summary>Owns every child in one read, including children whose native launch returns after the deadline.</summary>
internal sealed class QuotaProcessSession : IDisposable
{
    private readonly Func<ProcessStartInfo, Process?> _start;
    private readonly CancellationToken _token;
    private readonly object _gate = new();
    private readonly List<QuotaProcess> _children = [];
    private bool _cancelled;

    public QuotaProcessSession(Func<ProcessStartInfo, Process?> start, CancellationToken token)
    {
        _start = start;
        _token = token;
    }

    public QuotaProcess? Start(ProcessStartInfo info)
    {
        _token.ThrowIfCancellationRequested();
        // Never hold _gate across the OS call: the timeout must remain able to record cancellation.
        var process = _start(info);
        if (process is null) return null;
        var child = new QuotaProcess(process);
        lock (_gate)
        {
            if (!_cancelled && !_token.IsCancellationRequested)
            {
                _children.Add(child);
                return child;
            }
        }
        child.Dispose();
        _token.ThrowIfCancellationRequested();
        throw new OperationCanceledException();
    }

    public void Abort()
    {
        QuotaProcess[] children;
        lock (_gate)
        {
            _cancelled = true;
            children = _children.ToArray();
        }
        // Kill/tree enumeration and stream disposal are native work too. Queue them independently of the
        // worker token's callbacks, without making the public deadline or caller wait for native cleanup.
        if (children.Length > 0) _ = Task.Run(() => { foreach (var child in children) child.Dispose(); });
    }

    public void Dispose()
    {
        QuotaProcess[] children;
        lock (_gate)
        {
            _cancelled = true;
            children = _children.ToArray();
        }
        foreach (var child in children) child.Dispose();
    }
}

/// <summary>Serializes cleanup so cancellation and normal completion cannot dispose a handle before it is killed.</summary>
internal sealed class QuotaProcess(Process process) : IDisposable
{
    private readonly object _gate = new();
    private bool _disposed;
    public Process Process => process;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (!process.HasExited)
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch { try { process.Kill(); } catch { } }
                }
            }
            catch { }
            finally { try { process.Dispose(); } catch { } }
        }
    }
}
