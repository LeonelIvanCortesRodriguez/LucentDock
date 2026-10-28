using System.Runtime.ExceptionServices;
using Strata.Native;
using Strata.Views;

namespace Strata.Tests;

/// <summary>
/// Guards the corner fix found in Phase B: painted WPF corners must use the same radius as DWM's rounded clip.
/// A larger WPF radius left a black wedge between the two curves on every dock and flyout corner.
/// </summary>
[Trait("Category", "Unit")]
public class CompositionTests
{
    private static void OnSta(Action action)
    {
        ExceptionDispatchInfo? error = null;
        var t = new Thread(() => { try { action(); } catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(10)), "STA composition test timed out.");
        error?.Throw();
    }

    [Fact]
    public void FlyoutCardsUseTheDwmCornerRadius() => OnSta(() =>
    {
        var card = FlyoutWindow.CreateCard(new System.Windows.Controls.TextBlock());
        Assert.Equal(WindowComposition.SystemCornerRadiusDip, card.CornerRadius.TopLeft);
        Assert.Equal(WindowComposition.SystemCornerRadiusDip, card.CornerRadius.BottomRight);
        Assert.Equal(0, card.Margin.Left); // no transparent margin that would render as backing
    });

    [Fact]
    public void DockShellRadiusComesFromTheSharedConstant()
    {
        // The dock window needs a live HWND to build, so check the source of truth it uses instead.
        string dock = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Strata", "Views", "DockWindow.cs"));
        // The shared constant stays the base radius and the floor; Liquid glass alone may rise to the full pill
        // (half the dock height), which the layered window's per-pixel clipping makes exact.
        Assert.Contains("WindowComposition.SystemCornerRadiusDip", dock);
        Assert.Contains("h / 2 - 2", dock);
        Assert.DoesNotContain("h * 0.36", dock);
        Assert.Contains("WindowComposition.Apply(", dock);
        string flyout = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Strata", "Views", "FlyoutWindow.cs"));
        Assert.Contains("WindowComposition.Apply(", flyout);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, ".gitattributes"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repository root not found");
    }
}
