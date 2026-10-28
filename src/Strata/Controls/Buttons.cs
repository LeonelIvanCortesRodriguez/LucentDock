using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Strata.Controls;

/// <summary>Shared focus ring: a rounded accent outline shown only for keyboard focus.</summary>
public static class FocusVisuals
{
    public static readonly Style Rounded = Build(10);
    public static readonly Style Small = Build(8);

    private static Style Build(double radius)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(2));
        border.SetValue(FrameworkElement.MarginProperty, new Thickness(-2));
        border.SetResourceReference(Border.BorderBrushProperty, "Accent");
        var style = new Style(typeof(Control));
        style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(Control)) { VisualTree = border }));
        style.Seal();
        return style;
    }
}

/// <summary>
/// The dock's clickable tile (apps, widgets, Start/Search, suggestions). A real <see cref="Button"/>, so it
/// gets Enter/Space activation, keyboard focus and a UI Automation Invoke pattern; it can also be
/// emphasized (accent ring that pulses briefly) by automations.
/// </summary>
public class DockButton : Button
{
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(CornerRadius), typeof(DockButton), new PropertyMetadata(new CornerRadius(10)));

    public static readonly DependencyProperty IsEmphasizedProperty = DependencyProperty.Register(
        nameof(IsEmphasized), typeof(bool), typeof(DockButton), new PropertyMetadata(false, OnEmphasisChanged));

    private static readonly ControlTemplate SharedTemplate = BuildTemplate();

    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public bool IsEmphasized
    {
        get => (bool)GetValue(IsEmphasizedProperty);
        set => SetValue(IsEmphasizedProperty, value);
    }

    public DockButton()
    {
        Template = SharedTemplate;
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        Cursor = Cursors.Hand;
        Focusable = true;
        FocusVisualStyle = FocusVisuals.Rounded;
        HorizontalContentAlignment = HorizontalAlignment.Center;
        VerticalContentAlignment = VerticalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Stretch;
        MouseEnter += (_, _) => RefreshInteractionContrast();
        MouseLeave += (_, _) => RefreshInteractionContrast();
        GotKeyboardFocus += (_, _) => RefreshInteractionContrast();
        LostKeyboardFocus += (_, _) => RefreshInteractionContrast();
        PreviewMouseDown += (_, _) => RefreshInteractionContrast();
        PreviewMouseUp += (_, _) => RefreshInteractionContrast();
    }

    private void RefreshInteractionContrast()
    {
        bool active = IsMouseOver || IsKeyboardFocusWithin || IsPressed;
        Brush foreground = Application.Current?.TryFindResource("AccentFg") as Brush ?? SystemColors.HighlightTextBrush;
        HighContrastInteraction.Apply(this, SystemParameters.HighContrast, active, foreground);
    }

    private static ControlTemplate BuildTemplate()
    {
        var root = new FrameworkElementFactory(typeof(Grid));
        var bd = new FrameworkElementFactory(typeof(Border), "Bd");
        bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        bd.SetValue(Border.CornerRadiusProperty, new TemplateBindingExtension(CornerRadiusProperty));
        bd.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(BorderBrushProperty));
        bd.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(BorderThicknessProperty));
        root.AppendChild(bd);

        var ring = new FrameworkElementFactory(typeof(Border), "Ring");
        ring.SetValue(Border.CornerRadiusProperty, new TemplateBindingExtension(CornerRadiusProperty));
        ring.SetValue(Border.BorderThicknessProperty, new Thickness(1.5));
        ring.SetResourceReference(Border.BorderBrushProperty, "Accent");
        ring.SetValue(UIElement.OpacityProperty, 0.0);
        ring.SetValue(UIElement.IsHitTestVisibleProperty, false);
        root.AppendChild(ring);

        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(FrameworkElement.MarginProperty, new TemplateBindingExtension(PaddingProperty));
        cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, new TemplateBindingExtension(HorizontalContentAlignmentProperty));
        cp.SetValue(FrameworkElement.VerticalAlignmentProperty, new TemplateBindingExtension(VerticalContentAlignmentProperty));
        root.AppendChild(cp);

        var t = new ControlTemplate(typeof(DockButton)) { VisualTree = root };
        t.Triggers.Add(new Trigger { Property = IsMouseOverProperty, Value = true, Setters = { new Setter(Border.BackgroundProperty, new DynamicResourceExtension("Hover"), "Bd") } });
        t.Triggers.Add(new Trigger { Property = IsKeyboardFocusedProperty, Value = true, Setters = { new Setter(Border.BackgroundProperty, new DynamicResourceExtension("Hover"), "Bd") } });
        t.Triggers.Add(new Trigger { Property = IsPressedProperty, Value = true, Setters = { new Setter(Border.BackgroundProperty, new DynamicResourceExtension("Pressed"), "Bd") } });
        t.Triggers.Add(new Trigger { Property = IsEmphasizedProperty, Value = true, Setters = { new Setter(UIElement.OpacityProperty, 1.0, "Ring") } });
        t.Triggers.Add(new Trigger { Property = IsEnabledProperty, Value = false, Setters = { new Setter(UIElement.OpacityProperty, 0.45) } });
        t.Seal();
        return t;
    }

    private static void OnEmphasisChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DockButton b || e.NewValue is not true) return;
        b.ApplyTemplate();
        if (b.Template.FindName("Ring", b) is not Border ring) return;
        if (!SystemParameters.ClientAreaAnimation) return;
        // A short pulse draws the eye, then the ring stays (the trigger holds opacity 1).
        ring.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(500))
        {
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(3),
            FillBehavior = FillBehavior.Stop,
        });
    }
}

/// <summary>
/// A local resource override reaches widget TextBlocks that use the shared Fg/FgSubtle dynamic resources. This
/// keeps their text on the Windows Highlight/HighlightText pair while a high-contrast dock tile is interactive.
/// </summary>
public static class HighContrastInteraction
{
    public static void Apply(Control host, bool highContrast, bool active, Brush interactionForeground)
    {
        if (highContrast && active)
        {
            host.Resources["Fg"] = interactionForeground;
            host.Resources["FgSubtle"] = interactionForeground;
            host.Foreground = interactionForeground;
        }
        else
        {
            host.Resources.Remove("Fg");
            host.Resources.Remove("FgSubtle");
            host.ClearValue(Control.ForegroundProperty);
        }
    }
}

/// <summary>Flat button for flyouts and settings.</summary>
public sealed class FlatButton : Button
{
    private static readonly Style PlainStyle = BuildStyle(accent: false);
    private static readonly Style AccentStyle = BuildStyle(accent: true);

    public FlatButton(bool accent = false)
    {
        Style = accent ? AccentStyle : PlainStyle;
        FocusVisualStyle = FocusVisuals.Small;
    }

    private static Style BuildStyle(bool accent)
    {
        var bd = new FrameworkElementFactory(typeof(Border), "Bd");
        bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        bd.SetValue(Border.PaddingProperty, new TemplateBindingExtension(PaddingProperty));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, new TemplateBindingExtension(HorizontalContentAlignmentProperty));
        cp.SetValue(FrameworkElement.VerticalAlignmentProperty, new TemplateBindingExtension(VerticalContentAlignmentProperty));
        bd.AppendChild(cp);
        var template = new ControlTemplate(typeof(FlatButton)) { VisualTree = bd };
        template.Triggers.Add(new Trigger { Property = IsMouseOverProperty, Value = true, Setters = { new Setter(UIElement.OpacityProperty, 0.85, "Bd") } });
        template.Triggers.Add(new Trigger { Property = IsPressedProperty, Value = true, Setters = { new Setter(UIElement.OpacityProperty, 0.7, "Bd") } });
        template.Triggers.Add(new Trigger { Property = IsEnabledProperty, Value = false, Setters = { new Setter(UIElement.OpacityProperty, 0.4, "Bd") } });

        var style = new Style(typeof(FlatButton));
        style.Setters.Add(new Setter(TemplateProperty, template));
        style.Setters.Add(new Setter(BackgroundProperty, new DynamicResourceExtension(accent ? "Accent" : "InputBg")));
        style.Setters.Add(new Setter(ForegroundProperty, new DynamicResourceExtension(accent ? "AccentFg" : "FlyoutFg")));
        style.Setters.Add(new Setter(PaddingProperty, new Thickness(10, 6, 10, 6)));
        style.Setters.Add(new Setter(CursorProperty, Cursors.Hand));
        style.Setters.Add(new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
        style.Setters.Add(new Setter(VerticalContentAlignmentProperty, VerticalAlignment.Center));
        style.Seal();
        return style;
    }
}

/// <summary>On/off switch with the Toggle automation pattern.</summary>
public sealed class ToggleSwitch : ToggleButton
{
    private static readonly Style SwitchStyle = BuildStyle();

    public ToggleSwitch()
    {
        Style = SwitchStyle;
        FocusVisualStyle = FocusVisuals.Rounded;
    }

    private static Style BuildStyle()
    {
        var track = new FrameworkElementFactory(typeof(Border), "Track");
        track.SetValue(WidthProperty, 42.0);
        track.SetValue(HeightProperty, 22.0);
        track.SetValue(Border.CornerRadiusProperty, new CornerRadius(11));
        track.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        track.SetResourceReference(Border.BackgroundProperty, "InputBg");
        track.SetResourceReference(Border.BorderBrushProperty, "FlyoutSubtle");
        var knob = new FrameworkElementFactory(typeof(Ellipse), "Knob");
        knob.SetValue(WidthProperty, 14.0);
        knob.SetValue(HeightProperty, 14.0);
        knob.SetValue(MarginProperty, new Thickness(3, 0, 3, 0));
        knob.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Left);
        knob.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        knob.SetResourceReference(Shape.FillProperty, "FlyoutSubtle");
        track.AppendChild(knob);
        var template = new ControlTemplate(typeof(ToggleSwitch)) { VisualTree = track };
        template.Triggers.Add(new Trigger
        {
            Property = IsCheckedProperty, Value = true,
            Setters =
            {
                new Setter(Border.BackgroundProperty, new DynamicResourceExtension("Accent"), "Track"),
                new Setter(Border.BorderThicknessProperty, new Thickness(0), "Track"),
                new Setter(Shape.FillProperty, new DynamicResourceExtension("AccentFg"), "Knob"),
                new Setter(HorizontalAlignmentProperty, HorizontalAlignment.Right, "Knob"),
            },
        });
        var style = new Style(typeof(ToggleSwitch));
        style.Setters.Add(new Setter(TemplateProperty, template));
        style.Setters.Add(new Setter(CursorProperty, Cursors.Hand));
        style.Seal();
        return style;
    }
}

/// <summary>One option of a segmented choice (radio semantics: SelectionItem pattern).</summary>
public sealed class ChoiceButton : RadioButton
{
    private static readonly Style ChoiceStyle = BuildStyle();

    public ChoiceButton()
    {
        Style = ChoiceStyle;
        FocusVisualStyle = FocusVisuals.Small;
    }

    private static Style BuildStyle()
    {
        var bd = new FrameworkElementFactory(typeof(Border), "Bd");
        bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        bd.SetValue(Border.PaddingProperty, new TemplateBindingExtension(PaddingProperty));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        bd.AppendChild(cp);
        var template = new ControlTemplate(typeof(ChoiceButton)) { VisualTree = bd };
        template.Triggers.Add(new Trigger { Property = IsMouseOverProperty, Value = true, Setters = { new Setter(UIElement.OpacityProperty, 0.88, "Bd") } });

        var style = new Style(typeof(ChoiceButton));
        style.Setters.Add(new Setter(TemplateProperty, template));
        style.Setters.Add(new Setter(BackgroundProperty, new DynamicResourceExtension("InputBg")));
        style.Setters.Add(new Setter(ForegroundProperty, new DynamicResourceExtension("FlyoutFg")));
        style.Setters.Add(new Setter(PaddingProperty, new Thickness(12, 6, 12, 6)));
        style.Setters.Add(new Setter(CursorProperty, Cursors.Hand));
        style.Setters.Add(new Setter(FontSizeProperty, 12.5));
        style.Triggers.Add(new Trigger
        {
            Property = IsCheckedProperty, Value = true,
            Setters =
            {
                new Setter(BackgroundProperty, new DynamicResourceExtension("Accent")),
                new Setter(ForegroundProperty, new DynamicResourceExtension("AccentFg")),
                new Setter(FontWeightProperty, FontWeights.SemiBold),
            },
        });
        style.Seal();
        return style;
    }
}

public static class A11y
{
    /// <summary>Set the name (and optional description) screen readers announce.</summary>
    public static T Named<T>(this T element, string name, string? help = null) where T : DependencyObject
    {
        AutomationProperties.SetName(element, name);
        if (help is not null) AutomationProperties.SetHelpText(element, help);
        return element;
    }
}

/// <summary>
/// A round player button (previous, play/pause, next): a circle of the given size, light on the dark media card.
/// <see cref="RoundToggle"/> is the same shape for shuffle and repeat, with the Toggle automation pattern.
/// </summary>
public sealed class RoundButton : Button
{
    public RoundButton(double size, Brush background)
    {
        Width = Height = size;
        Template = RoundTemplate.Build(typeof(RoundButton), size);
        Background = background;
        Foreground = Brushes.White;
        Cursor = Cursors.Hand;
        FocusVisualStyle = FocusVisuals.Small;
    }
}

public sealed class RoundToggle : ToggleButton
{
    public RoundToggle(double size)
    {
        Width = Height = size;
        Template = RoundTemplate.Build(typeof(RoundToggle), size);
        Background = Brushes.Transparent;
        Foreground = Brushes.White;
        Cursor = Cursors.Hand;
        FocusVisualStyle = FocusVisuals.Small;
    }
}

internal static class RoundTemplate
{
    public static ControlTemplate Build(Type owner, double size)
    {
        var bd = new FrameworkElementFactory(typeof(Border), "Bd");
        bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(size / 2));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        bd.AppendChild(cp);
        var template = new ControlTemplate(owner) { VisualTree = bd };
        template.Triggers.Add(new Trigger { Property = UIElement.IsMouseOverProperty, Value = true, Setters = { new Setter(UIElement.OpacityProperty, 0.85, "Bd") } });
        template.Triggers.Add(new Trigger { Property = ButtonBase.IsPressedProperty, Value = true, Setters = { new Setter(UIElement.OpacityProperty, 0.7, "Bd") } });
        template.Triggers.Add(new Trigger { Property = UIElement.IsEnabledProperty, Value = false, Setters = { new Setter(UIElement.OpacityProperty, 0.35, "Bd") } });
        return template;
    }
}
