using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Strata.Controls;
using Strata.Core;

namespace Strata.Views;

/// <summary>One app on the dock: a pin, a running app, or both.</summary>
public sealed class AppButton : DockButton
{
    public PinnedApp? Pin { get; }
    public List<TrackedWindow> Windows { get; } = [];
    public AppIdentity? Identity { get; private set; }
    /// <summary>Unread messages shown in its windows' titles ("(3) WhatsApp"), when unread badges are on.</summary>
    public int Unread { get; private set; }
    private static readonly SolidColorBrush UnreadBrush = Frozen(Color.FromRgb(0xE8, 0x3B, 0x3B));
    private static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private readonly Image _icon;
    private readonly Border _indicator;
    private readonly Border _badge;
    private readonly TextBlock _badgeText;
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly double _iconSize;

    public string DisplayName => Pin?.Name is { Length: > 0 } n ? n : Identity?.DisplayName ?? "App";

    /// <summary>Scrolled over the icon (wheel delta): step through this app's windows.</summary>
    public event Action<int>? Scrolled;

    private string? _shortcut;
    /// <summary>Its keyboard shortcut ("Win+Ctrl+Alt+3"), or null: shown in its tooltip and accessible status.</summary>
    public string? Shortcut { get => _shortcut; set { if (_shortcut == value) return; _shortcut = value; UpdateName(); } }

    /// <summary>Files (or folders) dropped on the icon, to open with this app.</summary>
    public event Action<string[]>? FilesDropped;
    /// <summary>A drag has rested on the icon: bring the app forward so the files can be dropped into its window.</summary>
    public event Action? DragRested;
    private readonly System.Windows.Threading.DispatcherTimer _dragRest = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly string _key;
    public string Key => _key;

    /// <summary>Pins are identified by the pin itself; unpinned buttons by the app they show.</summary>
    public static string KeyFor(PinnedApp? pin, AppIdentity? identity) =>
        pin is not null ? "pin:" + (pin.Aumid ?? pin.Path).ToLowerInvariant() + "|" + pin.GetHashCode()
                        : "app:" + identity?.Key;

    public AppButton(PinnedApp? pin, AppIdentity? identity, double iconSize, bool topEdge)
    {
        Pin = pin;
        Identity = identity;
        _key = KeyFor(pin, identity);
        _iconSize = iconSize;
        CornerRadius = new CornerRadius(10);
        Padding = new Thickness(6, 0, 6, 0);
        Margin = new Thickness(1, 0, 1, 0);
        Background = Brushes.Transparent;
        Cursor = Cursors.Hand;

        _icon = new Image
        {
            Width = iconSize,
            Height = iconSize,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _scale,
        };
        RenderOptions.SetBitmapScalingMode(_icon, BitmapScalingMode.HighQuality);

        _indicator = new Border
        {
            Height = 3,
            Width = 6,
            CornerRadius = new CornerRadius(1.5),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = topEdge ? VerticalAlignment.Top : VerticalAlignment.Bottom,
            Margin = new Thickness(0, topEdge ? 3 : 0, 0, topEdge ? 0 : 3),
            Visibility = Visibility.Collapsed,
        };

        // How many windows are open, when more than one (like "3" on the taskbar's grouped icons).
        _badgeText = new TextBlock { FontSize = 9, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _badge = new Border
        {
            MinWidth = 13, Height = 13, CornerRadius = new CornerRadius(6.5), Padding = new Thickness(3, 0, 3, 0),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, topEdge ? 6 : 2, -4, 0), Child = _badgeText, Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        _badge.SetResourceReference(Border.BackgroundProperty, "Accent");

        var grid = new Grid();
        grid.Children.Add(_icon);
        grid.Children.Add(_indicator);
        grid.Children.Add(_badge);
        Content = grid;

        MouseEnter += (_, _) => { SetResourceReference(BackgroundProperty, "Hover"); Bounce(1.1); };
        MouseLeave += (_, _) => { Background = Brushes.Transparent; Bounce(1.0); };
        PreviewMouseLeftButtonDown += (_, _) => Bounce(0.92);
        PreviewMouseLeftButtonUp += (_, _) => Bounce(IsMouseOver ? 1.1 : 1.0);

        Loaded += (_, _) => LoadIcon();

        MouseWheel += (_, e) => { if (Windows.Count > 0) { e.Handled = true; Scrolled?.Invoke(e.Delta); } };

        // Drag and drop, as on the taskbar: drop files to open them with this app; rest on it to bring it forward.
        AllowDrop = true;
        _dragRest.Tick += (_, _) => { _dragRest.Stop(); DragRested?.Invoke(); };
        DragEnter += (_, e) => { if (HasFiles(e)) { SetResourceReference(BackgroundProperty, "Hover"); Bounce(1.1); _dragRest.Start(); } AcceptFiles(e); };
        DragOver += (_, e) => AcceptFiles(e);
        DragLeave += (_, _) => EndDrag();
        Drop += (_, e) =>
        {
            EndDrag();
            if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) { e.Handled = true; FilesDropped?.Invoke(files); }
        };
    }

    private static bool HasFiles(DragEventArgs e) => e.Data.GetDataPresent(DataFormats.FileDrop);

    private static void AcceptFiles(DragEventArgs e)
    {
        e.Effects = HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void EndDrag()
    {
        _dragRest.Stop();
        Background = Brushes.Transparent;
        Bounce(1.0);
    }

    private void Bounce(double to)
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _scale.ScaleX = _scale.ScaleY = to;
            return;
        }
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(140)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    private void LoadIcon()
    {
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int pixels = IconCache.SourcePixels(_iconSize, dpi, animated: true);
        if (Pin?.Group is { Count: > 0 } members)
        {
            // A group tile: up to four member icons composed into one two-by-two image. The transparent backing
            // rect keeps the bounds square, so a group with one loadable icon still reads as a group and never
            // scales a lone icon up to look like an ordinary app (audit F9).
            var drawing = new DrawingGroup();
            drawing.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, pixels, pixels))));
            double cell = pixels / 2.0;
            for (int i = 0; i < members.Count && i < 4; i++)
            {
                var src = members[i] is { } member ? IconCache.For(member, Math.Max(8, (int)cell), dpi) : null;
                if (src is null) continue;
                drawing.Children.Add(new ImageDrawing(src, new Rect(i % 2 * cell + 1, i / 2 * cell + 1, cell - 2, cell - 2)));
            }
            drawing.Freeze();
            var composed = new DrawingImage(drawing);
            composed.Freeze();
            _icon.Source = composed;
            return;
        }
        ImageSource? img = null;
        if (Pin is not null) img = IconCache.For(Pin, pixels, dpi);
        if (img is null && Identity is not null) img = IconCache.For(Identity, Windows.FirstOrDefault()?.Handle ?? IntPtr.Zero, pixels, dpi);
        _icon.Source = img;
    }

    /// <summary>Attach the current windows of this app and refresh indicator and tooltip.</summary>
    public void SetWindows(IEnumerable<TrackedWindow> windows, IntPtr foreground, bool showUnread = false)
    {
        Windows.Clear();
        Windows.AddRange(windows);
        if (Identity is null && Windows.Count > 0) Identity = Windows[0].App;
        if (_icon.Source is null) LoadIcon();
        UpdateActive(foreground);

        // An unread count (red) takes the badge's place; otherwise the window count (accent), as before.
        Unread = UseUnreadBadge(Identity?.IdentityKind ?? AppIdentityKind.Standard, showUnread)
            ? UnreadCount.ForWindows(Windows.Select(w => w.Title)) : 0;
        string? badge = UnreadCount.Badge(Unread) ?? CountBadge(Windows.Count);
        _badgeText.Text = badge ?? "";
        _badge.Visibility = badge is null ? Visibility.Collapsed : Visibility.Visible;
        if (Unread > 0) _badge.Background = UnreadBrush;
        else _badge.SetResourceReference(Border.BackgroundProperty, "Accent");
        UpdateName();
    }

    public static bool UseUnreadBadge(AppIdentityKind identityKind, bool configured) =>
        configured && identityKind != AppIdentityKind.ExplorerFolder;

    private void UpdateName()
    {
        // Running apps show live window previews on hover instead of a text tooltip.
        ToolTip = Windows.Count == 0 ? DisplayName + (_shortcut is null ? "" : $"\n{_shortcut}") : null;
        string status = Pin?.Group is { Count: > 0 } group ? $"Group of {group.Count} apps"
            : Windows.Count == 0 ? "Pinned app" : $"{Windows.Count} open window{(Windows.Count == 1 ? "" : "s")}";
        if (Unread > 0) status += $", {Unread} unread";
        this.Named(DisplayName, _shortcut is null ? status : $"{status}, {_shortcut}");
    }

    /// <summary>Which window a scroll moves to: down (negative delta) the next, up the previous, wrapping round;
    /// from outside the app (no current window), its most recent one.</summary>
    public static int NextWindowIndex(int count, int current, int delta)
    {
        if (count <= 0) return -1;
        if (current < 0 || current >= count) return count - 1;
        return ((current + (delta < 0 ? 1 : -1)) % count + count) % count;
    }

    public void UpdateActive(IntPtr foreground)
    {
        bool running = Windows.Count > 0;
        bool active = running && Windows.Any(w => w.Handle == foreground);
        _indicator.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        _indicator.Width = active ? _iconSize * 0.55 : Windows.Count > 1 ? 10 : 5;
        _indicator.SetResourceReference(BackgroundProperty, active ? "Accent" : "FgSubtle");
        Opacity = 1;
    }

    /// <summary>The window-count badge: none for 0 or 1 window, "2".."9", then "9+".</summary>
    public static string? CountBadge(int windows) => windows < 2 ? null : windows > 9 ? "9+" : windows.ToString();
}
