namespace Strata.Core;

public enum DockStartupState
{
    NotStarted,
    WaitingForDock,
    WaitingForAppBar,
    WaitingForStableGeometry,
    ApplyingTaskbar,
    Ready,
    SafetySuspended,
}

public enum DockStartupAction { None, ApplyConfiguredTaskbar, RestoreTaskbarForSafety }

public readonly record struct DockReadinessSample(
    bool WindowVisible,
    int WidthPixels,
    int HeightPixels,
    bool AppBarReady);

/// <summary>
/// Fail-closed startup/restart ordering. Taskbar policy is not applied until the native dock exists, its physical
/// geometry is stable across consecutive observations, and (when requested) the AppBar reservation is verified.
/// </summary>
public sealed class DockStartupReadiness(TimeSpan timeout, int stableSamplesRequired = 3)
{
    private DateTime _startedAt;
    private (int Width, int Height)? _lastGeometry;
    private int _stableSamples;
    private bool _applyIssued;
    private bool _safetyRestoreIssued;
    private bool _reserveSpace;

    public DockStartupState State { get; private set; } = DockStartupState.NotStarted;
    public int StableSamples => _stableSamples;
    public bool AutomaticObservationAllowed => State is not DockStartupState.Ready and not DockStartupState.SafetySuspended;

    public void Begin(DateTime now, bool reserveSpace)
    {
        _startedAt = now;
        _reserveSpace = reserveSpace;
        _lastGeometry = null;
        _stableSamples = 0;
        _applyIssued = false;
        _safetyRestoreIssued = false;
        State = DockStartupState.WaitingForDock;
    }

    /// <summary>Explorer replaced its taskbar/AppBar registry: return to the same safe readiness gate.</summary>
    public void ExplorerRestarted(DateTime now, bool reserveSpace) => Begin(now, reserveSpace);

    public DockStartupAction Observe(DateTime now, DockReadinessSample sample)
    {
        if (State is DockStartupState.NotStarted or DockStartupState.Ready or DockStartupState.SafetySuspended)
            return DockStartupAction.None;
        if (now - _startedAt >= timeout && !_safetyRestoreIssued)
        {
            _safetyRestoreIssued = true;
            State = DockStartupState.SafetySuspended;
            return DockStartupAction.RestoreTaskbarForSafety;
        }
        if (!sample.WindowVisible || sample.WidthPixels <= 0 || sample.HeightPixels <= 0)
        {
            _lastGeometry = null;
            _stableSamples = 0;
            State = DockStartupState.WaitingForDock;
            return DockStartupAction.None;
        }
        if (_reserveSpace && !sample.AppBarReady)
        {
            _lastGeometry = null;
            _stableSamples = 0;
            State = DockStartupState.WaitingForAppBar;
            return DockStartupAction.None;
        }

        var geometry = (sample.WidthPixels, sample.HeightPixels);
        _stableSamples = _lastGeometry == geometry ? _stableSamples + 1 : 1;
        _lastGeometry = geometry;
        if (_stableSamples < stableSamplesRequired)
        {
            State = DockStartupState.WaitingForStableGeometry;
            return DockStartupAction.None;
        }
        if (_applyIssued) return DockStartupAction.None;
        _applyIssued = true;
        State = DockStartupState.ApplyingTaskbar;
        return DockStartupAction.ApplyConfiguredTaskbar;
    }

    public void CompleteApply(bool success)
    {
        if (success) State = DockStartupState.Ready;
        else
        {
            _applyIssued = false;
            _lastGeometry = null;
            _stableSamples = 0;
            State = DockStartupState.SafetySuspended;
        }
    }
}
