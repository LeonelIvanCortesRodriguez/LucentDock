using Strata.Core;

namespace Strata.Tests;

[Trait("Category", "Unit")]
public sealed class WindowSnapshotCoordinatorTests
{
    private sealed class Source : IWindowSource
    {
        public int Refreshes;
        public bool Changed;
        public bool Refresh() { Refreshes++; return Changed; }
        public IReadOnlyList<TrackedWindow> Windows => [];
        public IntPtr Foreground => IntPtr.Zero;
        public void Activate(IntPtr window) { }
        public void Minimize(IntPtr window) { }
        public void Close(IntPtr window) { }
        public bool IsMinimized(IntPtr window) => false;
    }

    [Fact]
    public void OneEnumerationFansOutToEveryDockAndSubscriptionsAreRecoverable()
    {
        var source = new Source { Changed = true };
        var coordinator = new WindowSnapshotCoordinator(source);
        int main = 0, secondary = 0;
        using var first = coordinator.Subscribe(() => main++);
        var second = coordinator.Subscribe(() => secondary++);

        coordinator.Refresh();
        Assert.Equal(1, source.Refreshes);
        Assert.Equal(1, main);
        Assert.Equal(1, secondary);
        Assert.Equal(2, coordinator.SubscriberCount);

        second.Dispose();
        source.Changed = false;
        coordinator.Refresh(monitorSensitive: true);
        Assert.Equal(2, source.Refreshes);
        Assert.Equal(2, main);
        Assert.Equal(1, secondary);
        Assert.Equal(1, coordinator.SubscriberCount);
    }
}
