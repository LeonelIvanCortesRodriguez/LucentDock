using Strata.Core;
using Strata.Views;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Strata.Tests;

[Trait("Category", "Unit")]
public class DockOverflowTests
{
    [Fact]
    public void LowerPriorityAndWiderItemsMoveFirstUntilTheDockFits()
    {
        DockOverflowCandidate[] items =
        [
            new("clock", 94, 50),
            new("media", 326, 10),
            new("weather", 84, 10),
            new("cpu", 112, 20),
            new("notes", 48, 0),
        ];
        Assert.Equal(["notes", "media"], DockOverflowPlanner.Select(350, items));
    }

    [Fact]
    public void NothingMovesWhenContentFits() =>
        Assert.Empty(DockOverflowPlanner.Select(0, [new("clock", 94, 50)]));

    [Fact]
    public void SelectionIsDeterministicForEqualCandidates()
    {
        DockOverflowCandidate[] items = [new("b", 50, 1), new("a", 50, 1), new("c", 50, 1)];
        Assert.Equal(["a", "b"], DockOverflowPlanner.Select(75, items));
    }

    [Fact]
    public void ManyOverflowItemsStayInsideSmallMonitorAndRemainInKeyboardTabOrder() => OnSta(() =>
    {
        var items = new StackPanel();
        for (int i = 0; i < 100; i++) items.Children.Add(new Button { Content = $"Fixture {i}" });

        ScrollViewer scroll = DockWindow.CreateOverflowScroller(items, monitorHeightDip: 300);
        scroll.Measure(new Size(300, double.PositiveInfinity));

        Assert.Equal(204, scroll.MaxHeight);
        Assert.True(scroll.DesiredSize.Height <= scroll.MaxHeight);
        Assert.Equal(ScrollBarVisibility.Auto, scroll.VerticalScrollBarVisibility);
        Assert.False(scroll.Focusable);
        Assert.False(scroll.IsTabStop);
        Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(scroll)));
        Assert.All(items.Children.OfType<Button>(), button => Assert.True(button.IsTabStop));
    });

    private static void OnSta(Action action)
    {
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "STA overflow test timed out.");
        error?.Throw();
    }
}
