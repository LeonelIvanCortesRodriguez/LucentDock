using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Interop;
using Strata.Core;
using Strata.Native;

namespace Strata.Views;

/// <summary>
/// A popup card above (or below) a dock item. It is a real, activatable window so text boxes
/// get keyboard focus; it closes when you click anywhere else.
/// </summary>
public sealed class FlyoutWindow : Window
{
    private const double ShadowMargin = 0;
    private readonly FrameworkElement _anchor;
    private readonly bool _below;
    private readonly Border _card;
    private bool _closing;

    public FrameworkElement Anchor => _anchor;
    public FlyoutCloseReason CloseReason { get; private set; } = FlyoutCloseReason.Programmatic;
    /// <summary>Raised once, when closing begins, with why it is closing.</summary>
    public event Action<FlyoutCloseReason>? CloseStarted;

    public FlyoutWindow(FrameworkElement anchor, FrameworkElement content, double width, bool below)
    {
        _anchor = anchor;
        _below = below;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = false;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = true;
        SizeToContent = SizeToContent.Height;
        Width = width + ShadowMargin * 2;
        Opacity = 0;
        FontFamily = Theme.UiFont;

        _card = CreateCard(content);
        _card.RenderTransform = new TranslateTransform(0, below ? -8 : 8);
        Content = _card;

        SourceInitialized += (_, _) => ApplyComposition();
        Deactivated += (_, _) => CloseAnimated(FlyoutCloseReason.LostFocus);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { CloseAnimated(FlyoutCloseReason.Escape); e.Handled = true; } };
        Loaded += (_, _) => { PlaceNearAnchor(); Animate(1, 0); };
        SizeChanged += (_, _) => { if (IsLoaded) PlaceNearAnchor(); };
    }

    /// <summary>The rounded, shadowed card every flyout and menu sits in.</summary>
    public static Border CreateCard(FrameworkElement content)
    {
        var card = new Border
        {
            CornerRadius = new CornerRadius(WindowComposition.SystemCornerRadiusDip),
            Padding = new Thickness(16, 14, 16, 16),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(ShadowMargin),
            Child = content,
        };
        card.SetResourceReference(Border.BackgroundProperty, "FlyoutBg");
        card.SetResourceReference(Border.BorderBrushProperty, "FlyoutBorder");
        return card;
    }

    private void ApplyComposition()
    {
        // Same path as the dock: transparent render surface, extended frame, DWM corners. The card is opaque,
        // so the backdrop only shows through its antialiased edge pixels.
        WindowComposition.Apply(new WindowInteropHelper(this).Handle, rounded: true, darkMode: !Theme.Current.IsLight,
            systemBackdrop: MaterialPolicy.AllowsSystemBackdrop(MaterialEnvironment.ReadCurrent()));
    }

    /// <summary>
    /// Re-place the card against its anchor because the dock moved or resized under it. The pill dock re-centres
    /// whenever its content changes width, and without this the flyout stays where it was, detached from its tile.
    /// </summary>
    public void FollowAnchor() { if (IsLoaded) PlaceNearAnchor(); }

    private void PlaceNearAnchor()
    {
        var source = PresentationSource.FromVisual(_anchor);
        if (source?.CompositionTarget is null) return;
        var fromDevice = source.CompositionTarget.TransformFromDevice;

        var topLeft = fromDevice.Transform(_anchor.PointToScreen(new Point(0, 0)));
        var bottomRight = fromDevice.Transform(_anchor.PointToScreen(new Point(_anchor.ActualWidth, _anchor.ActualHeight)));
        var at = _anchor.PointToScreen(new Point(_anchor.ActualWidth / 2, _anchor.ActualHeight / 2));
        var mon = Win32.MonitorAt((int)at.X, (int)at.Y).rcMonitor;
        var monTl = fromDevice.Transform(new Point(mon.Left, mon.Top));
        var monBr = fromDevice.Transform(new Point(mon.Right, mon.Bottom));

        // The dock's own rounded shell is a few pixels taller than the item; leave a clear gap.
        const double gap = 10;
        double left = (topLeft.X + bottomRight.X) / 2 - ActualWidth / 2;
        left = Math.Clamp(left, monTl.X + 4 - ShadowMargin, monBr.X - ActualWidth - 4 + ShadowMargin);
        double top = _below
            ? bottomRight.Y + gap - ShadowMargin + 6
            : topLeft.Y - gap - ActualHeight + ShadowMargin - 6;
        top = Math.Clamp(top, monTl.Y - ShadowMargin, Math.Max(monTl.Y, monBr.Y - ActualHeight + ShadowMargin));
        Left = left;
        Top = top;
    }

    private void Animate(double to, double offset, Action? done = null)
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = to;
            var transform = (TranslateTransform)_card.RenderTransform;
            transform.BeginAnimation(TranslateTransform.YProperty, null);
            transform.Y = to > 0 ? 0 : (_below ? -6 : 6);
            done?.Invoke();
            return;
        }
        var duration = TimeSpan.FromMilliseconds(to > 0 ? 160 : 110);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fade = new DoubleAnimation(to, duration) { EasingFunction = ease };
        if (done is not null) fade.Completed += (_, _) => done();
        BeginAnimation(OpacityProperty, fade);
        double target = to > 0 ? 0 : (_below ? -6 : 6);
        ((TranslateTransform)_card.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(target, duration) { EasingFunction = ease });
    }

    public void CloseAnimated(FlyoutCloseReason reason = FlyoutCloseReason.Programmatic)
    {
        if (_closing) return;
        _closing = true;
        CloseReason = reason;
        CloseStarted?.Invoke(reason);
        Animate(0, 0, () => { try { Close(); } catch (InvalidOperationException) { } });
    }

    public bool IsClosing => _closing;
}
