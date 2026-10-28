using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Strata.Widgets;

namespace Strata.Controls;

/// <summary>
/// The player's progress bar: layered waves in the album's colour flow along the played part while music plays,
/// rising and falling with the music's level; paused, the played part is a flat line in that colour. The rest is
/// a plain thin track, and a small glowing dot marks the position.
/// A real <see cref="RangeBase"/> (seconds), so screen readers get a slider with RangeValue, and it seeks from the
/// mouse (drag) and keyboard (arrows ±5 s, Home/End). <see cref="Compact"/> is the strip along the dock tile's
/// bottom edge: the same waves, smaller, without the dot.
/// Animation runs only while playing, visible, and with Windows' animation effects on (30 frames a second), so an
/// idle or paused player costs nothing.
/// </summary>
public sealed class WaveSlider : RangeBase
{
    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(nameof(Accent), typeof(Color), typeof(WaveSlider),
        new FrameworkPropertyMetadata(Color.FromRgb(0x6E, 0x7B, 0xFF), FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Raised when the user picks a position (end of a drag, a click, a key), in seconds.</summary>
    public event Action<double>? SeekRequested;

    /// <summary>Where every new slider reads the playback level (0..1, null if unknown). Set by the app; null in
    /// previews and tests, which use the stand-in beat.</summary>
    public static Func<double?>? DefaultLevel { get; set; }

    /// <summary>This slider's level source; <see cref="DefaultLevel"/> unless set (read each frame, so a slider
    /// made before the app set the default still follows the music).</summary>
    public Func<double?>? Level { get => _level ?? DefaultLevel; set => _level = value; }
    private Func<double?>? _level;

    private bool _playing;
    private double _energy = double.NaN; // 0 paused (flat) .. 1 playing; eases between them; NaN until first drawn
    private double _beat = double.NaN;   // the music's level now, smoothed (fast rise, slower fall)
    private double _hold;                // automatic gain: the recent loudest peak (WaveShape.Loudness)
    private double _phase;               // seconds of wave motion so far
    private TimeSpan _lastFrame;
    private bool _subscribed;
    private bool _dragging;

    public WaveSlider()
    {
        Minimum = 0;
        Maximum = 1;
        SmallChange = 5;
        LargeChange = 15;
        Focusable = true;
        FocusVisualStyle = FocusVisuals.Rounded;
        Cursor = Cursors.Hand;
        IsVisibleChanged += (_, _) => UpdateAnimation();
        Unloaded += (_, _) => Unsubscribe();
        Loaded += (_, _) => UpdateAnimation();
    }

    public Color Accent { get => (Color)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }

    /// <summary>The strip along the dock tile's bottom edge (not focusable: the tile itself is the control).</summary>
    public bool Compact
    {
        get => _compact;
        init { _compact = value; Focusable = !value; IsHitTestVisible = !value; }
    }
    private readonly bool _compact;

    /// <summary>True while the track plays: the waves move. False: they settle to a flat line.</summary>
    public bool Playing
    {
        get => _playing;
        set { if (_playing == value) return; _playing = value; UpdateAnimation(); InvalidateVisual(); }
    }

    /// <summary>True while the user drags the thumb: position updates from the player are held off until release.</summary>
    public bool IsDragging => _dragging;

    /// <summary>Windows' "Animation effects" setting (off: the waves are drawn still).</summary>
    public static bool AnimationsEnabled => SystemParameters.ClientAreaAnimation;

    /// <summary>How much the waves show now: 0 paused (a flat line) .. 1 playing.</summary>
    public double Energy => double.IsNaN(_energy) ? (_playing ? 1 : 0) : _energy;

    /// <summary>The music's level the waves follow now (0..1).</summary>
    public double Beat => double.IsNaN(_beat) ? (_playing ? WaveShape.SyntheticLevel(_phase) : 0) : _beat;

    protected override AutomationPeer OnCreateAutomationPeer() => new WavePeer(this);

    protected override void OnValueChanged(double oldValue, double newValue)
    {
        base.OnValueChanged(oldValue, newValue);
        InvalidateVisual();
    }

    private void UpdateAnimation()
    {
        bool run = IsVisible && IsLoaded && AnimationsEnabled && (_playing || Energy > 0.01);
        if (run && !_subscribed) { CompositionTarget.Rendering += OnRendering; _subscribed = true; _lastFrame = TimeSpan.Zero; }
        else if (!run) Unsubscribe();
        if (!AnimationsEnabled) { _energy = _playing ? 1 : 0; _beat = _playing ? 0.6 : 0; }
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        CompositionTarget.Rendering -= OnRendering;
        _subscribed = false;
    }

    private const double FrameSeconds = 1.0 / 30;

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (_lastFrame != TimeSpan.Zero && (now - _lastFrame).TotalSeconds < FrameSeconds) return;
        double dt = _lastFrame == TimeSpan.Zero ? FrameSeconds : Math.Min(0.2, (now - _lastFrame).TotalSeconds);
        _lastFrame = now;
        if (Step(dt)) Unsubscribe();
    }

    /// <summary>One frame: <paramref name="seconds"/> of motion. Returns true once paused and flat.</summary>
    public bool Step(double seconds)
    {
        double target = _playing ? 1 : 0;
        double from = double.IsNaN(_energy) ? 0 : _energy;
        _energy = from + (target - from) * Math.Min(1, seconds * 6);
        if (_playing) _phase += seconds;

        // The music's level: rises at once on a beat, falls back more slowly, as a level meter does.
        double level = 0;
        if (_playing)
            level = Level?.Invoke() is { } peak ? WaveShape.Loudness(peak, ref _hold, seconds) : WaveShape.SyntheticLevel(_phase);
        double beat = double.IsNaN(_beat) ? 0 : _beat;
        _beat = beat + (level - beat) * Math.Min(1, seconds * (level > beat ? 22 : 7));

        InvalidateVisual();
        if (_playing || _energy >= 0.01) return false;
        _energy = 0;
        return true;
    }

    private const double Pad = 10;

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        if (width <= (Compact ? 4 : Pad * 2) || height <= 0) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height)); // the whole area seeks
        double fraction = Maximum > Minimum ? Math.Clamp((Value - Minimum) / (Maximum - Minimum), 0, 1) : 0;
        double pad = Compact ? 0 : Pad;
        double left = pad, right = width - pad, x = left + (right - left) * fraction;
        // The dock strip: waves rise from its bottom edge, with tighter crests for its size.
        double line = Compact ? 2 : 3, baseline = Compact ? height - line / 2 - 0.5 : height * 0.62;
        // The strip's crests are lifted more (clipped at its height) so they read at dock size, as on the lock screen.
        double amp = Compact ? height - line - 1 : height * 0.58, scale = Compact ? 0.6 : 1, lift = Compact ? 1.5 : 1;
        var accent = Accent;
        double energy = Energy, beat = Beat;

        // Unplayed track: a plain thin line.
        var rest = new SolidColorBrush(Color.FromArgb(Compact ? (byte)0x40 : (byte)0x55, 0xFF, 0xFF, 0xFF));
        dc.DrawRoundedRectangle(rest, null, new Rect(x, baseline - line / 2, Math.Max(0, right - x), line), line / 2, line / 2);

        // Played part: the waves (none when paused), then the solid line along their base.
        double played = x - left;
        if (played > 2 && amp > 0 && energy > 0.001)
        {
            for (int layer = 0; layer < WaveShape.Layers.Length; layer++)
            {
                double section = WaveShape.Section(Value, layer);
                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    g.BeginFigure(new Point(left, baseline), true, true);
                    for (double px = 0; px <= played; px += 2)
                        g.LineTo(new Point(left + px, baseline - amp * Math.Min(1, lift * WaveShape.Height(px / scale, played / scale, layer, _phase, energy, beat, section))), true, false);
                    g.LineTo(new Point(x, baseline), true, false);
                }
                geometry.Freeze();
                var fill = new SolidColorBrush(WaveShape.LayerColor(accent, layer));
                fill.Freeze();
                dc.DrawGeometry(fill, null, geometry);
            }
        }
        if (played > 0)
            dc.DrawRoundedRectangle(new SolidColorBrush(accent), null, new Rect(left, baseline - line / 2, played, line), line / 2, line / 2);

        if (!Compact)
        {
            // The position: a small accent dot with a light ring and a soft glow.
            var glow = new RadialGradientBrush(Color.FromArgb(0x70, accent.R, accent.G, accent.B), Color.FromArgb(0, accent.R, accent.G, accent.B));
            dc.DrawEllipse(glow, null, new Point(x, baseline), 9, 9);
            dc.DrawEllipse(new SolidColorBrush(accent), new Pen(Brushes.White, 1.75), new Point(x, baseline), 4.5, 4.5);
        }
    }

    private void SeekTo(double seconds)
    {
        Value = Math.Clamp(seconds, Minimum, Maximum);
        SeekRequested?.Invoke(Value);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (Compact) return; // the dock tile's click opens the flyout
        Focus();
        CaptureMouse();
        _dragging = true;
        Value = Minimum + WaveShape.FractionAt(e.GetPosition(this).X, ActualWidth, Pad) * (Maximum - Minimum);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging) Value = Minimum + WaveShape.FractionAt(e.GetPosition(this).X, ActualWidth, Pad) * (Maximum - Minimum);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        SeekTo(Value);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        double? next = e.Key switch
        {
            Key.Left or Key.Down => Value - SmallChange,
            Key.Right or Key.Up => Value + SmallChange,
            Key.PageDown => Value - LargeChange,
            Key.PageUp => Value + LargeChange,
            Key.Home => Minimum,
            Key.End => Maximum,
            _ => null,
        };
        if (next is { } v && !Compact) { SeekTo(v); e.Handled = true; }
        else base.OnKeyDown(e);
    }

    private sealed class WavePeer(WaveSlider owner) : RangeBaseAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;
        protected override string GetClassNameCore() => nameof(WaveSlider);
        // The dock strip is decoration (the tile is the control), so screen readers don't meet an unnamed slider there.
        protected override bool IsControlElementCore() => !owner.Compact;
        protected override bool IsContentElementCore() => !owner.Compact;
    }
}
