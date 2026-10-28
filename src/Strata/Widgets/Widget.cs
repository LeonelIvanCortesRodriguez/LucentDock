using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Strata.Controls;
using Strata.Core;
using Strata.Smart;

namespace Strata.Widgets;

/// <summary>What a widget can ask of the dock. Widgets read shared state from <see cref="Signals"/>
/// and never reference other widgets.</summary>
public interface IDockHost
{
    AppSettings Settings { get; }
    ScreenTimeTracker ScreenTime { get; }
    SignalStore Signals { get; }
    void Notify(string key, string title, string text, bool urgent = false);
    void ShowFlyout(FrameworkElement anchor, FrameworkElement content, double width = 320, bool fromKeyboard = false);
    void CloseFlyout();
    void SaveSettings();
    void OpenSettings();
}

/// <summary>
/// One shared one-second tick for everything that updates while visible (open flyouts), so there is
/// no timer per widget or per flyout. Raised by the app's heartbeat.
/// </summary>
public static class Heartbeat
{
    public static event Action? Tick;
    public static void Raise() => Tick?.Invoke();
}

/// <summary>
/// A tile on the dock. The compact view lives on the dock; clicking (or Enter/Space) opens an optional
/// flyout. <see cref="Tick"/> runs once per second from the shared heartbeat.
/// </summary>
public abstract class Widget : DockButton
{
    protected IDockHost Host { get; }
    protected AppSettings Settings => Host.Settings;
    protected SignalStore Signals => Host.Signals;

    /// <summary>Manifest id (set by the catalog when the widget is created).</summary>
    public string WidgetId { get; internal set; } = "";

    protected Widget(IDockHost host)
    {
        Host = host;
        Padding = new Thickness(8, 0, 8, 0);
        Margin = new Thickness(1, 0, 1, 0);
        // Liquid glass sets each widget on its own glass chip; the other styles have no chip (transparent, no rim).
        if (Theme.Current.Liquid)
        {
            SetResourceReference(BackgroundProperty, "Chip");
            SetResourceReference(BorderBrushProperty, "ChipBorder");
            BorderThickness = new Thickness(1);
            CornerRadius = new CornerRadius(12);
            Margin = new Thickness(2, 3, 2, 3);
        }
        Click += (_, _) => OpenFlyout(fromKeyboard: InputManager.Current.MostRecentInputDevice is KeyboardDevice);
        MouseWheel += (_, e) => OnScroll(e.Delta);
        PreviewKeyDown += (_, e) =>
        {
            // Up from a bottom dock (or Down from a top dock) also opens the flyout, like a menu bar.
            if (e.Key == (Settings.Edge == DockEdge.Bottom ? Key.Up : Key.Down)) { OpenFlyout(fromKeyboard: true); e.Handled = true; }
        };
    }

    /// <summary>Old widget code assigns <c>Child</c>; a button holds <c>Content</c>.</summary>
    protected UIElement? Child
    {
        get => Content as UIElement;
        set => Content = value;
    }

    public virtual void Tick() { }

    private bool _isIdle;

    /// <summary>
    /// Raised when <see cref="IsIdle"/> changes, so the slot can take the other declared width.
    /// </summary>
    public event Action? IdleChanged;

    /// <summary>
    /// A widget with nothing to show at all right now - the media pill with no track - sets this, and its slot
    /// narrows to the compact width declared for it instead of leaving its full width empty. This is the one thing
    /// a widget may do to its own geometry, and only between two widths fixed in advance: a widget that never sets
    /// it keeps a single width for its whole life, and no ordinary refresh moves anything either way.
    /// </summary>
    public bool IsIdle
    {
        get => _isIdle;
        protected set
        {
            if (_isIdle == value) return;
            _isIdle = value;
            IdleChanged?.Invoke();
        }
    }

    /// <summary>Update what screen readers announce for this widget (e.g. "CPU, 12 percent").</summary>
    protected void Announce(string name, string? status = null)
    {
        AutomationProperties.SetName(this, name);
        if (status is not null) AutomationProperties.SetItemStatus(this, status);
    }

    /// <summary>Content for the click flyout; null means the widget has no flyout.</summary>
    protected virtual FrameworkElement? BuildFlyout() => null;
    protected virtual double FlyoutWidth => 320;

    /// <summary>Open this widget's flyout (click, Enter/Space, or arrow toward the screen).</summary>
    public virtual void OpenFlyout(bool fromKeyboard)
    {
        var content = BuildFlyout();
        if (content is not null) Host.ShowFlyout(this, content, FlyoutWidth, fromKeyboard);
    }

    protected virtual void OnScroll(int delta) { }

    /// <summary>Flyout content for off-screen preview rendering.</summary>
    public FrameworkElement? PreviewFlyout() => BuildFlyout();
    public double PreviewFlyoutWidth => FlyoutWidth;

    public virtual void Dispose() { }
}

/// <summary>Small helpers so widget code stays readable without a XAML file per widget.</summary>
public static class UI
{
    public static TextBlock Text(string text, double size = 12.5, bool subtle = false, bool flyout = false,
        FontWeight? weight = null)
    {
        var t = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontFamily = Theme.UiFont,
            FontWeight = weight ?? FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        t.SetResourceReference(TextBlock.ForegroundProperty, (flyout, subtle) switch
        {
            (true, true) => "FlyoutSubtle",
            (true, false) => "FlyoutFg",
            (false, true) => "FgSubtle",
            _ => "Fg",
        });
        return t;
    }

    /// <summary>An icon-font glyph. Decorative: hidden from screen readers (the control around it carries the name).</summary>
    public static TextBlock Icon(string glyph, double size = 16, bool subtle = false, bool flyout = false)
    {
        var t = Text(glyph, size, subtle, flyout);
        t.FontFamily = Theme.IconFont;
        t.TextTrimming = TextTrimming.None;
        t.Focusable = false;
        t.IsHitTestVisible = false;
        return t;
    }

    public static StackPanel Row(double spacing, params UIElement[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        for (int i = 0; i < children.Length; i++)
        {
            if (i > 0 && children[i] is FrameworkElement fe)
                fe.Margin = new Thickness(spacing, fe.Margin.Top, fe.Margin.Right, fe.Margin.Bottom);
            p.Children.Add(children[i]);
        }
        return p;
    }

    public static StackPanel Column(double spacing, params UIElement[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Vertical };
        for (int i = 0; i < children.Length; i++)
        {
            if (i > 0 && children[i] is FrameworkElement fe)
                fe.Margin = new Thickness(fe.Margin.Left, spacing, fe.Margin.Right, fe.Margin.Bottom);
            p.Children.Add(children[i]);
        }
        return p;
    }

    /// <summary>Flat button for flyouts. <paramref name="name"/> is what screen readers announce.</summary>
    public static FlatButton Button(UIElement content, Action onClick, bool accent = false, Thickness? padding = null, string? name = null)
    {
        var b = new FlatButton(accent) { Content = content };
        if (padding is { } p) b.Padding = p;
        b.Click += (_, e) => { e.Handled = true; onClick(); };
        string? label = name ?? (content as TextBlock)?.Text;
        if (label is not null) AutomationProperties.SetName(b, label);
        return b;
    }

    public static FlatButton TextButton(string text, Action onClick, bool accent = false)
    {
        var t = new TextBlock
        {
            Text = text, FontFamily = Theme.UiFont, FontSize = 12.5, FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        return Button(t, onClick, accent, name: text); // text color comes from the button (white on accent)
    }

    public static FlatButton IconButton(string glyph, Action onClick, double size, string name)
    {
        var b = Button(Icon(glyph, size, flyout: true), onClick, padding: new Thickness(8), name: name);
        b.ToolTip = name;
        return b;
    }

    public static TextBox Input(string text = "", string? placeholder = null, bool multiline = false)
    {
        var box = new TextBox
        {
            Text = text,
            FontFamily = Theme.UiFont,
            FontSize = 13,
            Padding = new Thickness(8, 6, 8, 6),
            BorderThickness = new Thickness(0),
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
            FocusVisualStyle = FocusVisuals.Small,
        };
        if (placeholder is not null) AutomationProperties.SetName(box, placeholder);
        box.SetResourceReference(Control.BackgroundProperty, "InputBg");
        box.SetResourceReference(Control.ForegroundProperty, "FlyoutFg");
        box.SetResourceReference(System.Windows.Controls.Primitives.TextBoxBase.CaretBrushProperty, "FlyoutFg");
        var template = new ControlTemplate(typeof(TextBox));
        var border = new FrameworkElementFactory(typeof(Border), "Bd");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background")
            { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding")
            { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var grid = new FrameworkElementFactory(typeof(Grid));
        if (!multiline) grid.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        var host = new FrameworkElementFactory(typeof(ScrollViewer));
        host.Name = "PART_ContentHost";
        grid.AppendChild(host);
        var hint = new FrameworkElementFactory(typeof(TextBlock));
        hint.Name = "Placeholder";
        hint.SetValue(TextBlock.TextProperty, placeholder ?? "");
        hint.SetValue(UIElement.IsHitTestVisibleProperty, false);
        hint.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 0, 0, 0));
        hint.SetResourceReference(TextBlock.ForegroundProperty, "FlyoutSubtle");
        grid.AppendChild(hint);
        border.AppendChild(grid);
        template.VisualTree = border;
        // A visible focus cue for keyboard users (the caret alone is easy to miss).
        template.Triggers.Add(new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true,
            Setters = { new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("Accent"), "Bd") } });
        box.Template = template;
        void UpdateHint()
        {
            if (box.Template.FindName("Placeholder", box) is TextBlock tb)
                tb.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        box.Loaded += (_, _) => UpdateHint();
        box.TextChanged += (_, _) => UpdateHint();
        return box;
    }

    public static Border Card(UIElement child, Thickness? padding = null)
    {
        var b = new Border
        {
            Child = child,
            Padding = padding ?? new Thickness(10),
            CornerRadius = new CornerRadius(10),
        };
        b.SetResourceReference(Border.BackgroundProperty, "InputBg");
        return b;
    }

    public static TextBlock Header(string text)
    {
        var t = Text(text, 15, flyout: true, weight: FontWeights.SemiBold);
        AutomationProperties.SetHeadingLevel(t, AutomationHeadingLevel.Level2);
        return t;
    }

    public static Border Separator()
    {
        var b = new Border { Height = 1, Margin = new Thickness(0, 4, 0, 4), Opacity = 0.5 };
        b.SetResourceReference(Border.BackgroundProperty, "FlyoutBorder");
        return b;
    }

    /// <summary>Progress bar used by system and screen-time widgets.</summary>
    public static Grid Bar(double fraction, double height = 6)
    {
        var g = new Grid { Height = height, VerticalAlignment = VerticalAlignment.Center };
        var track = new Border { CornerRadius = new CornerRadius(height / 2) };
        track.SetResourceReference(Border.BackgroundProperty, "InputBg");
        var fill = new Border { CornerRadius = new CornerRadius(height / 2), HorizontalAlignment = HorizontalAlignment.Left };
        fill.SetResourceReference(Border.BackgroundProperty, "Accent");
        g.Children.Add(track);
        g.Children.Add(fill);
        g.SizeChanged += (_, _) => fill.Width = Math.Max(height, g.ActualWidth * Math.Clamp(fraction, 0, 1));
        return g;
    }
}
