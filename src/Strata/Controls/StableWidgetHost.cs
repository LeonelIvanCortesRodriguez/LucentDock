using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Strata.Core;
using Strata.Widgets;

namespace Strata.Controls;

/// <summary>
/// A fixed geometry contract for live dock widgets. Text, status, percentages and animations may change inside
/// the slot, but ordinary refreshes cannot remeasure the dock or move a neighbour.
///
/// One thing may: a widget that can have nothing at all to show (<see cref="Widget.IsIdle"/>) takes a second width,
/// also fixed here. Only the media pill does, only when a track starts or stops, and the dock glides between the
/// two widths rather than jumping. Everything else keeps a single width for its whole life.
/// </summary>
public sealed class StableWidgetHost : Border
{
    private readonly Widget _widget;
    private bool _overflowed;
    private static readonly IReadOnlyDictionary<string, double> MediumWidths = new Dictionary<string, double>(StringComparer.Ordinal)
    {
        ["clock"] = 94,
        ["cpu"] = 112,
        ["ram"] = 112,
        ["network"] = 100,
        ["battery"] = 80,
        ["volume"] = 82,
        ["media"] = 326,
        ["calendar"] = 210,
        ["todos"] = 62,
        ["notes"] = 48,
        ["pomodoro"] = 88,
        ["weather"] = 84,
        ["screentime"] = 98,
        ["aiusage"] = 258,
    };

    /// <summary>
    /// The second width a slot may take, for a widget that can have nothing at all to show (see
    /// <see cref="Widget.IsIdle"/>). Both widths are fixed here, so a slot is still only ever one of two known
    /// sizes and the dock's geometry stays predictable; a widget absent from this table has one width only.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, double> MediumIdleWidths = new Dictionary<string, double>(StringComparer.Ordinal)
    {
        // Nothing playing: the pill is only its small idle glyph, so the slot shrinks to fit the glyph rather than
        // leave the 326 px of a track, artwork and transport controls standing empty.
        ["media"] = 46,
    };

    public string WidgetId { get; }
    private readonly DockSize _size;

    public StableWidgetHost(string widgetId, DockSize size, Widget widget)
    {
        _widget = widget;
        WidgetId = widgetId;
        _size = size;
        ApplyWidth();
        widget.IdleChanged += ApplyWidth;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Stretch;
        ClipToBounds = true;
        Focusable = false;
        IsHitTestVisible = true;
        Child = widget;
        Typography.SetNumeralAlignment(widget, FontNumeralAlignment.Tabular);
        widget.IsVisibleChanged += (_, _) => UpdateVisibility();
        UpdateVisibility();
        SizeChanged += (_, _) => UpdateRoundedClip(widget);
    }

    public bool IsOverflowed => _overflowed;

    public void SetOverflowed(bool overflowed)
    {
        _overflowed = overflowed;
        UpdateVisibility();
    }

    private void UpdateVisibility() =>
        Visibility = !_overflowed && _widget.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;

    private void UpdateRoundedClip(Widget widget)
    {
        double radius = Math.Max(widget.CornerRadius.TopLeft, 0);
        Clip = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), radius, radius);
    }

    /// <summary>How long the slot takes to move between its two widths on a live dock.</summary>
    public const int IdleTransitionMilliseconds = 180;

    /// <summary>The width this slot is headed for: its idle width while the widget has nothing to show, else its
    /// full width. <see cref="FrameworkElement.Width"/> equals it except during the glide between the two.</summary>
    public double TargetWidth { get; private set; }

    /// <summary>
    /// Take the width the widget's state calls for. On a live dock the change is animated: a pill dock sizes to its
    /// content and is centred, so a 280 px step would jerk every tile sideways at once. Off the dock, or with the
    /// system's animations turned off, the width is set outright, so nothing can measure a slot mid-glide.
    /// </summary>
    private void ApplyWidth()
    {
        double target = _widget.IsIdle ? IdleWidthFor(WidgetId, _size) : WidthFor(WidgetId, _size);
        if (target == TargetWidth && Width == target) return;
        TargetWidth = target;
        BeginAnimation(WidthProperty, null);
        if (!IsLoaded || !SystemParameters.ClientAreaAnimation) { Width = target; return; }
        BeginAnimation(WidthProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(IdleTransitionMilliseconds))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    /// <summary>Stop following the widget. The dock drops both together when it rebuilds, so this is hygiene
    /// rather than a leak, but a host that outlived its widget would otherwise keep resizing for it.</summary>
    public void Detach() => _widget.IdleChanged -= ApplyWidth;

    public static double WidthFor(string widgetId, DockSize size) =>
        ForSize(MediumWidths.GetValueOrDefault(widgetId, 96), size);

    /// <summary>The compact width for a widget that can go idle; its full width for every other widget.</summary>
    public static double IdleWidthFor(string widgetId, DockSize size) =>
        MediumIdleWidths.TryGetValue(widgetId, out double medium) ? ForSize(medium, size) : WidthFor(widgetId, size);

    private static double ForSize(double medium, DockSize size) => size switch
    {
        DockSize.Small => Math.Round(medium * 0.9),
        DockSize.Large => Math.Round(medium * 1.12),
        _ => medium,
    };

    /// <summary>
    /// Physical-pixel slot bounds used by deterministic DPI geometry tests and evidence. Widgets named in
    /// <paramref name="idleWidgetIds"/> are measured at their idle width, so a dock recorded with nothing playing
    /// is described as it actually is rather than as a track-sized media slot and every offset after it wrong.
    /// </summary>
    public static IReadOnlyList<(int Left, int Width)> PhysicalBounds(IEnumerable<string> widgetIds, DockSize size, double dpiScale,
        IReadOnlyCollection<string>? idleWidgetIds = null)
    {
        int left = 0;
        var result = new List<(int Left, int Width)>();
        foreach (string id in widgetIds)
        {
            double slot = idleWidgetIds is not null && idleWidgetIds.Contains(id) ? IdleWidthFor(id, size) : WidthFor(id, size);
            int width = (int)Math.Round(slot * dpiScale, MidpointRounding.AwayFromZero);
            result.Add((left, width));
            left += width;
        }
        return result;
    }
}
