using System.Runtime.InteropServices;
using Strata.Native;

namespace Strata.Core;

/// <summary>The shell AppBar calls, behind an interface so registration failures can be simulated.</summary>
public interface IAppBarNative
{
    bool New(IntPtr hwnd, uint callbackMessage);
    bool Remove(IntPtr hwnd);
    bool QueryPos(IntPtr hwnd, uint edge, ref Win32.RECT rect);
    bool SetPos(IntPtr hwnd, uint edge, ref Win32.RECT rect);
    int LastError { get; }
}

public sealed class Win32AppBarNative : IAppBarNative
{
    public int LastError { get; private set; }

    private Win32.APPBARDATA Data(IntPtr hwnd) => new() { cbSize = Marshal.SizeOf<Win32.APPBARDATA>(), hWnd = hwnd };

    private bool Call(uint msg, ref Win32.APPBARDATA abd)
    {
        IntPtr r = Win32.SHAppBarMessage(msg, ref abd);
        LastError = Marshal.GetLastPInvokeError();
        return r != IntPtr.Zero;
    }

    public bool New(IntPtr hwnd, uint callbackMessage)
    {
        var abd = Data(hwnd);
        abd.uCallbackMessage = callbackMessage;
        return Call(Win32.ABM_NEW, ref abd);
    }

    public bool Remove(IntPtr hwnd)
    {
        var abd = Data(hwnd);
        return Call(Win32.ABM_REMOVE, ref abd);
    }

    public bool QueryPos(IntPtr hwnd, uint edge, ref Win32.RECT rect)
    {
        var abd = Data(hwnd);
        abd.uEdge = edge;
        abd.rc = rect;
        bool ok = Call(Win32.ABM_QUERYPOS, ref abd);
        rect = abd.rc;
        return ok;
    }

    public bool SetPos(IntPtr hwnd, uint edge, ref Win32.RECT rect)
    {
        var abd = Data(hwnd);
        abd.uEdge = edge;
        abd.rc = rect;
        bool ok = Call(Win32.ABM_SETPOS, ref abd);
        rect = abd.rc;
        return ok;
    }
}

public enum AppBarReadinessTransition
{
    DockBecameReady,
    DpiOrDisplayChanged,
    ExplorerRestarted,
}

/// <summary>
/// Bounds registration retries independently of the 250 ms dock-readiness sampler. A cycle performs at most three
/// native calls, with a real delay between them, then remains suspended until a meaningful shell/window transition.
/// </summary>
public sealed class AppBarRegistrationRetry(TimeSpan backoff, int maxAttempts = AppBarController.MaxAttempts)
{
    private DateTime _nextAttemptAt = DateTime.MinValue;

    public int Attempts { get; private set; }
    public bool Suspended { get; private set; }

    public bool CanAttempt(DateTime now) => !Suspended && Attempts < maxAttempts && now >= _nextAttemptAt;

    public void RecordFailure(DateTime now)
    {
        Attempts++;
        if (Attempts >= maxAttempts) Suspended = true;
        else _nextAttemptAt = now + backoff;
    }

    public void RecordSuccess() => Reset();

    public void Rearm(AppBarReadinessTransition transition)
    {
        _ = transition; // The enum makes callers name the transition that authorizes a new bounded cycle.
        Reset();
    }

    private void Reset()
    {
        Attempts = 0;
        Suspended = false;
        _nextAttemptAt = DateTime.MinValue;
    }
}

/// <summary>
/// Reserves screen space for the dock like the real taskbar. Every shell call's result is checked:
/// a failed registration is retried, then reported, and the dock simply floats without reserving space.
/// </summary>
public sealed class AppBarController(IAppBarNative native, Func<DateTime>? utcNow = null)
{
    public const int MaxAttempts = 3;
    public static readonly TimeSpan RetryBackoff = TimeSpan.FromMilliseconds(750);

    private readonly Func<DateTime> _utcNow = utcNow ?? (() => DateTime.UtcNow);
    private readonly AppBarRegistrationRetry _retry = new(RetryBackoff);

    public bool Registered { get; private set; }
    public string? LastFailure { get; private set; }
    public Win32.RECT? ReservedRect { get; private set; }
    public bool LastRegisterAttempted { get; private set; }
    public bool RegistrationSuspended => _retry.Suspended;
    public int RegistrationAttempts => _retry.Attempts;

    public AppBarController() : this(new Win32AppBarNative()) { }

    public bool Register(IntPtr hwnd, uint callbackMessage)
    {
        LastRegisterAttempted = false;
        if (Registered) return true;
        DateTime now = _utcNow();
        if (!_retry.CanAttempt(now)) return false;

        LastRegisterAttempted = true;
        if (native.New(hwnd, callbackMessage))
        {
            Registered = true;
            LastFailure = null;
            _retry.RecordSuccess();
            return true;
        }

        _retry.RecordFailure(now);
        LastFailure = $"ABM_NEW failed (attempt {_retry.Attempts}/{MaxAttempts}, error {native.LastError}).";
        Log.Write(LastFailure);
        if (_retry.Suspended)
            Log.Write("AppBar registration retry cycle suspended; waiting for a dock, DPI/display, or Explorer transition.");
        return false;
    }

    /// <summary>Reserve a strip of <paramref name="heightPx"/> on the given edge. Returns the granted rect, or null.</summary>
    public Win32.RECT? Reserve(IntPtr hwnd, bool bottom, Win32.RECT monitor, int heightPx)
    {
        if (!Registered) return null;
        uint edge = bottom ? Win32.ABE_BOTTOM : Win32.ABE_TOP;
        var rc = monitor;
        if (bottom) rc.Top = rc.Bottom - heightPx; else rc.Bottom = rc.Top + heightPx;
        if (!native.QueryPos(hwnd, edge, ref rc))
            return Fail($"ABM_QUERYPOS failed (error {native.LastError}).");

        // The shell may move the proposed rect (another appbar on that edge); keep our height from its edge.
        if (bottom) rc.Top = rc.Bottom - heightPx; else rc.Bottom = rc.Top + heightPx;
        if (rc.Height <= 0 || rc.Width <= 0)
            return Fail($"ABM_QUERYPOS returned an empty rect ({rc.Left},{rc.Top},{rc.Right},{rc.Bottom}).");

        if (!native.SetPos(hwnd, edge, ref rc))
            return Fail($"ABM_SETPOS failed (error {native.LastError}).");
        if (rc.Height <= 0 || rc.Width <= 0)
            return Fail($"ABM_SETPOS granted an empty rect ({rc.Left},{rc.Top},{rc.Right},{rc.Bottom}).");

        ReservedRect = rc;
        LastFailure = null;
        return rc;
    }

    public bool Unregister(IntPtr hwnd)
    {
        if (!Registered) return true;
        bool ok = native.Remove(hwnd);
        if (!ok) Log.Write($"ABM_REMOVE failed (error {native.LastError}); the shell drops the reservation when the window closes.");
        Registered = false;
        ReservedRect = null;
        _retry.Rearm(AppBarReadinessTransition.DockBecameReady);
        return ok;
    }

    /// <summary>Explorer restarted: its appbar list is gone, so register again from scratch.</summary>
    public void ForgetRegistration()
    {
        Registered = false;
        ReservedRect = null;
        _retry.Rearm(AppBarReadinessTransition.ExplorerRestarted);
    }

    /// <summary>Allow one new bounded cycle after an observed readiness, DPI/display, or Explorer transition.</summary>
    public void RearmRegistration(AppBarReadinessTransition transition)
    {
        if (!Registered) _retry.Rearm(transition);
    }

    private Win32.RECT? Fail(string message)
    {
        LastFailure = message;
        ReservedRect = null;
        Log.Write(message);
        return null;
    }
}
