using Strata.Core;

namespace Strata.Tests;

[Trait("Category", "Unit")]
public class DockStartupReadinessTests
{
    private static readonly DateTime Start = new(2026, 10, 25, 12, 0, 0);
    private static DockReadinessSample Ready(int width = 1200, bool appBar = true) => new(true, width, 72, appBar);

    [Fact]
    public void AppliesOnlyAfterThreeStablePhysicalGeometrySamples()
    {
        var gate = new DockStartupReadiness(TimeSpan.FromSeconds(10));
        gate.Begin(Start, reserveSpace: true);
        Assert.Equal(DockStartupAction.None, gate.Observe(Start, Ready(1000)));
        Assert.Equal(DockStartupAction.None, gate.Observe(Start.AddMilliseconds(250), Ready(1001)));
        Assert.Equal(DockStartupAction.None, gate.Observe(Start.AddMilliseconds(500), Ready(1001)));
        Assert.Equal(DockStartupAction.ApplyConfiguredTaskbar, gate.Observe(Start.AddMilliseconds(750), Ready(1001)));
        Assert.Equal(DockStartupState.ApplyingTaskbar, gate.State);
        gate.CompleteApply(success: true);
        Assert.Equal(DockStartupState.Ready, gate.State);
    }

    [Fact]
    public void AppBarFailureNeverAppliesAndTimeoutRestoresOnce()
    {
        var gate = new DockStartupReadiness(TimeSpan.FromSeconds(10));
        gate.Begin(Start, reserveSpace: true);
        for (int i = 0; i < 40; i++)
            Assert.Equal(DockStartupAction.None, gate.Observe(Start.AddMilliseconds(i * 250), Ready(appBar: false)));
        Assert.Equal(DockStartupAction.RestoreTaskbarForSafety, gate.Observe(Start.AddSeconds(10), Ready(appBar: false)));
        Assert.Equal(DockStartupState.SafetySuspended, gate.State);
        Assert.False(gate.AutomaticObservationAllowed);
        Assert.Equal(DockStartupAction.None, gate.Observe(Start.AddSeconds(11), Ready(appBar: false)));
    }

    [Fact]
    public void TimeoutStaysSuspendedUntilAMeaningfulTransitionRearmsReadiness()
    {
        var gate = new DockStartupReadiness(TimeSpan.FromSeconds(1));
        gate.Begin(Start, reserveSpace: true);
        Assert.Equal(DockStartupAction.RestoreTaskbarForSafety, gate.Observe(Start.AddSeconds(1), default));
        Assert.Equal(DockStartupAction.None, gate.Observe(Start.AddSeconds(2), Ready()));
        Assert.Equal(DockStartupAction.None, gate.Observe(Start.AddSeconds(3), Ready()));
        Assert.Equal(DockStartupState.SafetySuspended, gate.State);

        gate.ExplorerRestarted(Start.AddSeconds(4), reserveSpace: true);
        Assert.True(gate.AutomaticObservationAllowed);
        Assert.Equal(DockStartupAction.None, gate.Observe(Start.AddSeconds(4), Ready()));
        Assert.Equal(DockStartupAction.None, gate.Observe(Start.AddSeconds(4.25), Ready()));
        Assert.Equal(DockStartupAction.ApplyConfiguredTaskbar, gate.Observe(Start.AddSeconds(4.5), Ready()));
    }

    [Fact]
    public void ExplorerRestartRequiresFreshStableSamples()
    {
        var gate = new DockStartupReadiness(TimeSpan.FromSeconds(10));
        gate.Begin(Start, reserveSpace: true);
        gate.Observe(Start, Ready());
        gate.Observe(Start.AddMilliseconds(250), Ready());
        Assert.Equal(DockStartupAction.ApplyConfiguredTaskbar, gate.Observe(Start.AddMilliseconds(500), Ready()));
        gate.CompleteApply(true);

        gate.ExplorerRestarted(Start.AddSeconds(2), reserveSpace: true);
        Assert.Equal(DockStartupState.WaitingForDock, gate.State);
        Assert.Equal(DockStartupAction.None, gate.Observe(Start.AddSeconds(2.25), Ready()));
        Assert.Equal(DockStartupAction.None, gate.Observe(Start.AddSeconds(2.5), Ready()));
        Assert.Equal(DockStartupAction.ApplyConfiguredTaskbar, gate.Observe(Start.AddSeconds(2.75), Ready()));
    }

    [Fact]
    public void FloatingDockDoesNotRequireAppBar()
    {
        var gate = new DockStartupReadiness(TimeSpan.FromSeconds(10));
        gate.Begin(Start, reserveSpace: false);
        gate.Observe(Start, Ready(appBar: false));
        gate.Observe(Start.AddMilliseconds(250), Ready(appBar: false));
        Assert.Equal(DockStartupAction.ApplyConfiguredTaskbar,
            gate.Observe(Start.AddMilliseconds(500), Ready(appBar: false)));
    }

    [Fact]
    public void RepeatedStartStressNeverAppliesBeforeEveryPrerequisite()
    {
        for (int run = 0; run < 500; run++)
        {
            var gate = new DockStartupReadiness(TimeSpan.FromSeconds(10));
            gate.Begin(Start, reserveSpace: true);
            bool applied = false;
            for (int sample = 0; sample < 20; sample++)
            {
                bool visible = sample >= run % 5;
                bool appBar = sample >= 5 + run % 4;
                int width = sample < 10 + run % 3 ? 1000 + sample % 2 : 1200;
                var value = new DockReadinessSample(visible, visible ? width : 0, visible ? 72 : 0, appBar);
                var action = gate.Observe(Start.AddMilliseconds(sample * 250), value);
                if (action != DockStartupAction.ApplyConfiguredTaskbar) continue;
                Assert.True(visible);
                Assert.True(appBar);
                Assert.True(gate.StableSamples >= 3);
                applied = true;
                gate.CompleteApply(true);
            }
            Assert.True(applied);
        }
    }
}
