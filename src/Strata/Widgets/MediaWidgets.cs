using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Strata.Controls;
using Strata.Core;
using Strata.Native;
using Strata.Smart;
using Windows.Media.Control;

namespace Strata.Widgets;

/// <summary>Master volume, abstracted so previews never open the real audio device.</summary>
public interface IVolumeControl
{
    /// <summary>0..100, or -1 when there is no playback device.</summary>
    int Level { get; set; }
    bool Muted { get; set; }
    /// <summary>The playback peak level now (0..1), or null when it can't be read.</summary>
    double? Peak { get; }
}

public sealed class SystemVolume : IVolumeControl
{
    private readonly AudioEndpoint _endpoint = new();
    public int Level { get => _endpoint.Level; set => _endpoint.Level = value; }
    public bool Muted { get => _endpoint.Muted; set => _endpoint.Muted = value; }
    public double? Peak => _endpoint.Peak;
}

public sealed class FixtureVolume(int level) : IVolumeControl
{
    public int Level { get; set; } = level;
    public bool Muted { get; set; }
    public double? Peak => null;
}

public static class VolumeSource
{
    private static IVolumeControl? _current;
    public static IVolumeControl Current
    {
        get
        {
            if (_current is null)
            {
                PrivacyGuard.ThrowIfPreview("the audio device");
                _current = new SystemVolume();
            }
            return _current;
        }
        set => _current = value;
    }
}

public sealed class VolumeWidget : Widget
{
    private readonly TextBlock _icon, _value;
    private IVolumeControl Audio => VolumeSource.Current;

    public VolumeWidget(IDockHost host) : base(host)
    {
        _icon = UI.Icon("", Settings.FontSize + 3);
        _value = UI.Text("", Settings.FontSize, weight: FontWeights.SemiBold);
        _value.MinWidth = 28;
        Child = UI.Row(5, _icon, _value);
        ToolTip = "Volume - scroll to change, click for more";
        Announce("Volume");
    }

    private static string GlyphFor(int level, bool muted) =>
        muted || level == 0 ? "" : level < 33 ? "" : level < 66 ? "" : "";

    public override void Tick()
    {
        int level = Audio.Level;
        if (level < 0) { _icon.Text = ""; _value.Text = "-"; Announce("Volume", "no playback device"); return; }
        bool muted = Audio.Muted;
        _icon.Text = GlyphFor(level, muted);
        _value.Text = muted ? "Mute" : $"{level}%";
        Announce("Volume", muted ? "muted" : $"{level} percent");
    }

    protected override void OnScroll(int delta)
    {
        int level = Audio.Level;
        if (level < 0) return;
        Audio.Level = level + (delta > 0 ? 2 : -2);
        if (Audio.Muted && delta > 0) Audio.Muted = false;
        Tick();
    }

    protected override double FlyoutWidth => 300;

    protected override FrameworkElement BuildFlyout()
    {
        var slider = new FlatSlider { Value = Math.Max(0, Audio.Level) }.Named("Volume level");
        var pct = UI.Text("", 13, flyout: true, weight: FontWeights.SemiBold);
        pct.MinWidth = 40;
        pct.TextAlignment = TextAlignment.Right;
        var muteIcon = UI.Icon("", 16, flyout: true);
        var mute = UI.Button(muteIcon, () => { Audio.Muted = !Audio.Muted; Tick(); }, padding: new Thickness(8), name: "Mute");
        slider.UserChanged += v => { Audio.Level = (int)v; if (Audio.Muted) Audio.Muted = false; Tick(); };

        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(mute, Dock.Left);
        DockPanel.SetDock(pct, Dock.Right);
        row.Children.Add(mute);
        row.Children.Add(pct);
        slider.Margin = new Thickness(6, 0, 6, 0);
        row.Children.Add(slider);

        var sound = UI.TextButton("Sound settings", () => Launcher.Start("ms-settings:sound", ""));
        var mixer = UI.TextButton("Volume mixer", () => Launcher.Start("ms-settings:apps-volume", ""));
        var quick = QuickSettings.Build(Host, out var refreshQuick);
        var panel = UI.Column(10, UI.Header("Volume"), row, UI.Row(8, sound, mixer), UI.Separator(), quick);
        return panel.Updating(() =>
        {
            refreshQuick();
            int level = Audio.Level;
            bool muted = Audio.Muted;
            if (!slider.IsMouseCaptured) slider.Value = Math.Max(0, level);
            pct.Text = level < 0 ? "No device" : $"{level}%";
            muteIcon.Text = GlyphFor(level, muted);
            mute.Named(muted ? "Unmute" : "Mute");
        });
    }
}

/// <summary>
/// Now playing, via Windows' system media controls. Owned by the app (not the widget) so automations get
/// <see cref="Signals.Media"/> even when the media widget isn't on the dock. Publishes on change.
/// </summary>
public enum MediaRepeat { Off, List, Track }

public sealed class MediaService : IDisposable
{
    private readonly SignalStore _signals;
    private readonly SynchronizationContext? _context;
    private readonly RefreshCoordinator _refresh;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private string _trackKey = "";
    private bool _disposed;

    public static MediaService? Current { get; set; }

    public string Title { get; private set; } = "";
    public string Artist { get; private set; } = "";
    public string SourceApp { get; private set; } = "";
    public bool IsPlaying { get; private set; }
    public ImageSource? Artwork { get; private set; }
    /// <summary>The player's colour, taken from the album cover (<see cref="AlbumColor"/>).</summary>
    public Color Accent { get; private set; } = DefaultAccent;
    public static readonly Color DefaultAccent = Color.FromRgb(0x6E, 0x7B, 0xFF);
    public TimeSpan Position { get; private set; }
    public TimeSpan Duration { get; private set; }
    public DateTimeOffset PositionReportedAt { get; private set; }
    public double PlaybackRate { get; private set; } = 1;
    public bool CanSeek { get; private set; }
    public bool CanShuffle { get; private set; }
    public bool? Shuffle { get; private set; }
    public bool CanRepeat { get; private set; }
    public MediaRepeat Repeat { get; private set; }
    public event Action? Changed;

    /// <summary>The position now, estimated from the app's last report (apps report it only now and then).</summary>
    public TimeSpan EstimatedPosition(DateTimeOffset now) =>
        TrackTimeline.Estimate(Position, PositionReportedAt, now, IsPlaying, PlaybackRate, Duration);

    public MediaService(SignalStore signals)
    {
        _signals = signals;
        _context = SynchronizationContext.Current;
        _refresh = new RefreshCoordinator(RefreshAsync, TimeSpan.FromSeconds(15), OnRefreshError);
    }

    public async Task StartAsync()
    {
        PrivacyGuard.ThrowIfPreview("media sessions");
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += OnCurrentSessionChanged;
            _manager.SessionsChanged += OnSessionsChanged;
            _refresh.Request();
            await _refresh.WhenIdle;
        }
        catch (Exception ex)
        {
            Log.Write($"Media controls unavailable: {ex.Message}");
            _signals.PublishError(Signals.Media, "media", ex.Message);
        }
    }

    /// <summary>Fixture state for previews; no Windows media session is opened.</summary>
    public void UseFixture(string title, string artist, bool playing, ImageSource? artwork, TimeSpan position = default, TimeSpan duration = default)
    {
        Title = title; Artist = artist; IsPlaying = playing; Artwork = artwork; SourceApp = "Sample Player";
        Accent = AlbumColor.FromImage(artwork, DefaultAccent);
        Position = position; Duration = duration; PositionReportedAt = new DateTimeOffset(AppClock.Now); PlaybackRate = 1;
        CanSeek = CanShuffle = CanRepeat = true; Shuffle = false; Repeat = MediaRepeat.Off;
        Publish();
    }

    /// <summary>Invented artwork for previews: a violet gradient with soft light, so the album colour can be shown.</summary>
    public static ImageSource SampleArtwork()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x3B, 0x2A, 0x86), Color.FromRgb(0x8E, 0x4F, 0xC9), 45), null, new Rect(0, 0, 160, 160));
            dc.DrawEllipse(new RadialGradientBrush(Color.FromArgb(0x90, 0xF0, 0xC8, 0xFF), Color.FromArgb(0, 0xF0, 0xC8, 0xFF)), null, new Point(105, 60), 60, 60);
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x60, 0x1A, 0x10, 0x40)), null, new Point(50, 120), 38, 38);
        }
        var bmp = new RenderTargetBitmap(160, 160, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Seek, when the app allows it (<see cref="CanSeek"/>).</summary>
    public void Seek(TimeSpan to)
    {
        if (!CanSeek) return;
        Position = to; PositionReportedAt = DateTimeOffset.Now; Changed?.Invoke();
        Control(x => x.TryChangePlaybackPositionAsync((_startTime + to).Ticks));
    }

    public void ToggleShuffle()
    {
        if (CanShuffle) Control(x => x.TryChangeShuffleActiveAsync(Shuffle != true));
    }

    /// <summary>Off, then the whole list, then one track, as phones do.</summary>
    public void CycleRepeat()
    {
        if (!CanRepeat) return;
        var next = Repeat switch { MediaRepeat.Off => global::Windows.Media.MediaPlaybackAutoRepeatMode.List, MediaRepeat.List => global::Windows.Media.MediaPlaybackAutoRepeatMode.Track, _ => global::Windows.Media.MediaPlaybackAutoRepeatMode.None };
        Control(x => x.TryChangeAutoRepeatModeAsync(next));
    }

    private TimeSpan _startTime;

    public async void Control(Func<GlobalSystemMediaTransportControlsSession, global::Windows.Foundation.IAsyncOperation<bool>> action)
    {
        try
        {
            var session = _manager?.GetCurrentSession();
            if (session is null) return;
            await action(session);
            await Task.Delay(250);
            _refresh.Request();
        }
        catch (Exception ex) { Log.Write($"Media control failed: {ex.Message}"); }
    }

    /// <summary>Bounded fallback called by the dock heartbeat; events remain the primary refresh path.</summary>
    public void Poll(DateTime now) => _refresh.Poll(now);

    private async Task RefreshAsync()
    {
        if (_disposed || _manager is null) return;
        var session = _manager.GetCurrentSession();
        BindSession(session);
        if (session is null)
        {
            Title = Artist = SourceApp = "";
            IsPlaying = false;
            Artwork = null;
            Accent = DefaultAccent;
            Position = Duration = TimeSpan.Zero;
            CanSeek = CanShuffle = CanRepeat = false;
            _trackKey = "";
        }
        else
        {
            var props = await session.TryGetMediaPropertiesAsync();
            var playback = session.GetPlaybackInfo();
            IsPlaying = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var timeline = session.GetTimelineProperties();
            _startTime = timeline.StartTime;
            Duration = timeline.EndTime > timeline.StartTime ? timeline.EndTime - timeline.StartTime : TimeSpan.Zero;
            Position = timeline.Position - timeline.StartTime;
            PositionReportedAt = timeline.LastUpdatedTime == default ? DateTimeOffset.Now : timeline.LastUpdatedTime;
            PlaybackRate = playback.PlaybackRate ?? 1;
            CanSeek = playback.Controls.IsPlaybackPositionEnabled && Duration > TimeSpan.Zero;
            CanShuffle = playback.Controls.IsShuffleEnabled;
            Shuffle = playback.IsShuffleActive;
            CanRepeat = playback.Controls.IsRepeatEnabled;
            Repeat = playback.AutoRepeatMode switch
            {
                global::Windows.Media.MediaPlaybackAutoRepeatMode.List => MediaRepeat.List,
                global::Windows.Media.MediaPlaybackAutoRepeatMode.Track => MediaRepeat.Track,
                _ => MediaRepeat.Off,
            };
            Title = props?.Title ?? "";
            Artist = props?.Artist ?? "";
            SourceApp = session.SourceAppUserModelId ?? "";
            string key = Title + "\u0001" + Artist;
            if (ShouldLoadArtwork(key, _trackKey, Artwork is not null, props?.Thumbnail is not null))
            {
                _trackKey = key;
                Artwork = props?.Thumbnail is { } thumb ? await LoadThumbnail(thumb) : null;
                Accent = AlbumColor.FromImage(Artwork, DefaultAccent);
            }
        }
        Publish();
    }

    /// <summary>
    /// Whether this refresh should (re)load the artwork: always on a track change, and again while the current
    /// track has no artwork but the session now offers a thumbnail. Apps often publish the thumbnail stream one
    /// event after the track properties; loading only on a track change latched that early absence for the whole
    /// track, leaving the flyout without its cover.
    /// </summary>
    public static bool ShouldLoadArtwork(string key, string currentTrackKey, bool artworkLoaded, bool thumbnailOffered)
        => key != currentTrackKey || (!artworkLoaded && thumbnailOffered);

    private void BindSession(GlobalSystemMediaTransportControlsSession? session)
    {
        if (ReferenceEquals(_session, session)) return;
        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
        }
        _session = session;
        _trackKey = "";
        Artwork = null;
        if (_session is not null)
        {
            _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
            _session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
        }
    }

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender,
        CurrentSessionChangedEventArgs args) => _refresh.Request();

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender,
        SessionsChangedEventArgs args) => _refresh.Request();

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender,
        MediaPropertiesChangedEventArgs args) => _refresh.Request();

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender,
        PlaybackInfoChangedEventArgs args) => _refresh.Request();

    private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender,
        TimelinePropertiesChangedEventArgs args) => _refresh.Request();

    private void OnRefreshError(Exception ex)
    {
        Log.Write($"Media refresh failed: {ex.Message}");
        void Send() => _signals.PublishError(Signals.Media, "media", ex.Message);
        if (_context is not null && SynchronizationContext.Current != _context) _context.Post(_ => Send(), null);
        else Send();
    }

    private void Publish()
    {
        void Send()
        {
            _signals.Publish(Signals.Media, new Strata.Smart.MediaState(Title.Length > 0, IsPlaying, Title, Artist, SourceApp), "media");
            Changed?.Invoke();
        }
        if (_context is not null && SynchronizationContext.Current != _context) _context.Post(_ => Send(), null);
        else Send();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_manager is not null)
        {
            _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
            _manager.SessionsChanged -= OnSessionsChanged;
        }
        BindSession(null);
        _refresh.Dispose();
        _manager = null;
        Changed = null;
    }

    private static async Task<ImageSource?> LoadThumbnail(global::Windows.Storage.Streams.IRandomAccessStreamReference reference)
    {
        try
        {
            using var stream = await reference.OpenReadAsync();
            using var net = stream.AsStreamForRead();
            var ms = new MemoryStream();
            await net.CopyToAsync(ms);
            ms.Position = 0;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 320; // sharp in the flyout card
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }
}

/// <summary>
/// Now playing. On the dock: a compact pill (round cover, "Title • Artist", previous/play/next) with the progress
/// waves along its bottom edge, moving with the music. With nothing playing it is a single small icon, and its
/// slot narrows to match, so an idle player takes an icon's room rather than a track's. Its flyout is the full card: the cover
/// blurred as the background, the cover itself, "Title • Artist", the waveform progress bar (drag or arrow keys to seek), elapsed and total time, and
/// round controls with repeat and shuffle where the app supports them. Everything takes the cover's colour.
/// </summary>
public sealed class MediaWidget : Widget
{
    private const string PlayGlyph = "", PauseGlyph = "", PreviousGlyph = "", NextGlyph = "",
        IdleGlyph = "", ShuffleGlyph = "", RepeatAllGlyph = "", RepeatOneGlyph = "";

    private readonly Image _art = new() { Stretch = Stretch.UniformToFill };
    private readonly Border _artFrame;
    private readonly TextBlock _line, _playGlyph, _idle;
    private readonly StackPanel _controls;
    private readonly FlatButton _playButton;
    private readonly WaveSlider _strip;

    private static MediaService? Media => MediaService.Current;
    private static DateTimeOffset Now => new(AppClock.Now);

    public MediaWidget(IDockHost host) : base(host)
    {
        double s = Settings.Size == DockSize.Small ? 22 : Settings.Size == DockSize.Large ? 32 : 26;
        _art.Width = _art.Height = s;
        _artFrame = new Border
        {
            Width = s, Height = s, CornerRadius = new CornerRadius(s / 2), ClipToBounds = true, Child = _art,
            Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            Clip = new EllipseGeometry(new Point(s / 2, s / 2), s / 2, s / 2),
        };

        _line = UI.Text("", Settings.FontSize, weight: FontWeights.SemiBold);
        _line.MaxWidth = 200;
        _line.Margin = new Thickness(8, 0, 4, 0);

        _playGlyph = UI.Icon(PlayGlyph, 13);
        _playButton = MiniButton(_playGlyph, "Play or pause", () => Media?.Control(x => x.TryTogglePlayPauseAsync()));
        _controls = UI.Row(0,
            MiniButton(UI.Icon(PreviousGlyph, 13), "Previous track", () => Media?.Control(x => x.TrySkipPreviousAsync())),
            _playButton,
            MiniButton(UI.Icon(NextGlyph, 13), "Next track", () => Media?.Control(x => x.TrySkipNextAsync())));
        _idle = UI.Icon(IdleGlyph, Settings.FontSize + 3, subtle: true);

        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(_idle);
        row.Children.Add(_artFrame);
        row.Children.Add(_line);
        row.Children.Add(_controls);
        // The waves run along the bottom edge, under the title and controls, like the phone's pill.
        _strip = new WaveSlider { Compact = true, Height = 12, Margin = new Thickness(s + 8, 0, 4, 0) };
        var grid = new Grid { VerticalAlignment = VerticalAlignment.Center };
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_strip, 1);
        grid.Children.Add(row);
        grid.Children.Add(_strip);
        Child = grid;
        // Middle-click plays or pauses, without opening the card.
        PreviewMouseUp += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Middle || Media is not { Title.Length: > 0 } m) return;
            e.Handled = true;
            m.Control(x => x.TryTogglePlayPauseAsync());
        };
        Announce("Now playing", "nothing playing");
        Render();
    }

    /// <summary>What a scroll on the pill does: the volume ±2% per notch, or with Shift, ±10 s in the track
    /// (only where the app allows seeking). Null parts mean "no change".</summary>
    public static (int? Volume, TimeSpan? SeekTo) Scroll(int delta, bool shift, int volume, bool canSeek, TimeSpan position, TimeSpan duration)
    {
        int notch = delta > 0 ? 1 : delta < 0 ? -1 : 0;
        if (notch == 0) return (null, null);
        if (shift)
        {
            if (!canSeek || duration <= TimeSpan.Zero) return (null, null);
            var to = position + TimeSpan.FromSeconds(10 * notch);
            return (null, to < TimeSpan.Zero ? TimeSpan.Zero : to > duration ? duration : to);
        }
        return volume < 0 ? (null, null) : (Math.Clamp(volume + 2 * notch, 0, 100), null);
    }

    private string? _flash;
    private DateTime _flashUntil;

    /// <summary>Show a short message in place of the title (the new volume or position), then the title again.</summary>
    private void Flash(string text)
    {
        _flash = text;
        _flashUntil = DateTime.Now.AddMilliseconds(1300);
        Render();
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1350) };
        t.Tick += (_, _) => { t.Stop(); Render(); };
        t.Start();
    }

    protected override void OnScroll(int delta)
    {
        if (Media is not { Title.Length: > 0 } m) return;
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var audio = VolumeSource.Current;
        var (volume, seekTo) = Scroll(delta, shift, audio.Level, m.CanSeek, m.EstimatedPosition(Now), m.Duration);
        if (volume is { } v)
        {
            audio.Level = v;
            if (audio.Muted && delta > 0) audio.Muted = false;
            Flash($"Volume {v}%");
        }
        else if (seekTo is { } to)
        {
            m.Seek(to);
            Flash($"{TrackTimeline.Format(to)} / {TrackTimeline.Format(m.Duration)}");
        }
        else if (shift) Flash("This app doesn't allow seeking");
    }

    private static FlatButton MiniButton(UIElement glyph, string name, Action click)
    {
        var b = UI.Button(glyph, click, padding: new Thickness(6, 3, 6, 3), name: name);
        b.Background = Brushes.Transparent;
        b.ToolTip = name;
        return b;
    }

    /// <summary>"Title • Artist", or just the title.</summary>
    public static string Line(string title, string artist) => artist.Length == 0 ? title : $"{title} • {artist}";

    public override void Tick() => Render();

    private void Render()
    {
        var m = Media;
        bool idle = m is null || m.Title.Length == 0;
        // Nothing playing leaves only the idle glyph, so the pill asks for its compact slot: a small icon to click,
        // not a track-sized gap in the dock.
        IsIdle = idle;
        _idle.Visibility = idle ? Visibility.Visible : Visibility.Collapsed;
        _artFrame.Visibility = _line.Visibility = _controls.Visibility = idle ? Visibility.Collapsed : Visibility.Visible;
        _strip.Visibility = !idle && m!.Duration > TimeSpan.Zero ? Visibility.Visible : Visibility.Collapsed;
        _strip.Playing = !idle && m!.IsPlaying;
        if (idle)
        {
            ToolTip = "Nothing playing";
            Announce("Now playing", "nothing playing");
            return;
        }
        string line = Line(m!.Title, m.Artist);
        _line.Text = _flash is not null && DateTime.Now < _flashUntil ? _flash : line;
        _art.Source = m.Artwork;
        _artFrame.Visibility = m.Artwork is null ? Visibility.Collapsed : Visibility.Visible;
        _playGlyph.Text = m.IsPlaying ? PauseGlyph : PlayGlyph;
        _playButton.Named(m.IsPlaying ? "Pause" : "Play");
        _strip.Accent = m.Accent;
        _strip.Maximum = Math.Max(1, m.Duration.TotalSeconds);
        _strip.Value = m.EstimatedPosition(Now).TotalSeconds;
        string track = string.IsNullOrEmpty(m.Artist) ? m.Title : $"{m.Title} - {m.Artist}";
        ToolTip = track + "\nScroll: volume · Shift+scroll: skip 10 s · Middle-click: " + (m.IsPlaying ? "pause" : "play");
        Announce("Now playing", (m.IsPlaying ? "playing " : "paused ") + track);
    }

    protected override double FlyoutWidth => 330;

    protected override FrameworkElement? BuildFlyout()
    {
        var m = Media;
        if (m is null || m.Title.Length == 0)
            return UI.Column(6, UI.Header("Now playing"),
                UI.Text("Nothing is playing. Start music or a video in any app and it shows up here.", 12.5, subtle: true, flyout: true)
                    .Also(t => t.TextWrapping = TextWrapping.Wrap));

        static TextBlock Light(string text, double size, double opacity = 1, FontWeight? weight = null)
        {
            var t = new TextBlock { Text = text, FontSize = size, FontFamily = Theme.UiFont, FontWeight = weight ?? FontWeights.Normal,
                Foreground = new SolidColorBrush(Color.FromArgb((byte)(255 * opacity), 255, 255, 255)), TextTrimming = TextTrimming.CharacterEllipsis };
            return t;
        }

        // Background: the cover, blurred, under a wash of its own colour (dark enough for white text on any cover).
        // A brush, not an Image: a brush never takes part in layout, so a cover of any shape fills the card
        // without changing its size (an Image made the card as tall as the screen for a tall cover).
        static Brush Cover(ImageSource? source) => source is null ? Brushes.Transparent : new ImageBrush(source) { Stretch = Stretch.UniformToFill };
        var backdrop = new Rectangle { Fill = Cover(m.Artwork), Margin = new Thickness(-50),
            Effect = new BlurEffect { Radius = 60, RenderingBias = RenderingBias.Performance } };
        var wash = new Rectangle();
        void Wash(Color accent)
        {
            var tint = AlbumColor.CardTint(accent);
            wash.Fill = new LinearGradientBrush(Color.FromArgb(0xB8, tint.R, tint.G, tint.B), Color.FromArgb(0xEE, 0x0E, 0x0E, 0x12), 90);
        }
        Wash(m.Accent);

        var output = Light("This PC", 12, 0.75);
        var art = new Image { Source = m.Artwork, Stretch = Stretch.UniformToFill, Width = 206, Height = 206 };
        var artFrame = new Border { Width = 206, Height = 206, CornerRadius = new CornerRadius(14), ClipToBounds = true, Child = art,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 14),
            Clip = new RectangleGeometry(new Rect(0, 0, 206, 206), 14, 14),
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Opacity = 0.45, Color = Colors.Black } };
        var line = Light(Line(m.Title, m.Artist), 15, 1, FontWeights.SemiBold);
        line.HorizontalAlignment = HorizontalAlignment.Center;
        line.TextAlignment = TextAlignment.Center;

        var wave = new WaveSlider { Height = 38, Margin = new Thickness(-10, 6, -10, 0) }.Named("Playback position", "Arrow keys move 5 seconds");
        wave.SeekRequested += secs => m.Seek(TimeSpan.FromSeconds(secs));
        var elapsed = Light("", 12, 0.7);
        var total = Light("", 12, 0.7);
        var times = new DockPanel();
        DockPanel.SetDock(total, Dock.Right);
        times.Children.Add(total);
        times.Children.Add(elapsed);

        var circle = new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
        RoundButton Round(string glyph, double size, string name, Action click)
        {
            var b = new RoundButton(size, circle) { Content = UI.Icon(glyph, size * 0.36).Also(t => t.Foreground = Brushes.White) }.Named(name);
            b.Click += (_, e) => { e.Handled = true; click(); };
            b.ToolTip = name;
            return b;
        }
        const double ControlSize = 44; // previous, play and next are one size, as on the phone
        var playIcon = UI.Icon(PlayGlyph, 17).Also(t => t.Foreground = Brushes.White);
        var play = new RoundButton(ControlSize, circle) { Content = playIcon };
        play.Click += (_, e) => { e.Handled = true; m.Control(x => x.TryTogglePlayPauseAsync()); };
        var repeatIcon = UI.Icon(RepeatAllGlyph, 14).Also(t => t.Foreground = Brushes.White);
        var repeat = new RoundToggle(36) { Content = repeatIcon }.Named("Repeat");
        repeat.Click += (_, e) => { e.Handled = true; m.CycleRepeat(); };
        var shuffleIcon = UI.Icon(ShuffleGlyph, 14).Also(t => t.Foreground = Brushes.White);
        var shuffle = new RoundToggle(36) { Content = shuffleIcon }.Named("Shuffle");
        shuffle.Click += (_, e) => { e.Handled = true; m.ToggleShuffle(); };
        var controls = new UniformGrid { Columns = 5, Margin = new Thickness(0, 6, 0, 0) };
        foreach (var c in new FrameworkElement[] { repeat, Round(PreviousGlyph, ControlSize, "Previous track", () => m.Control(x => x.TrySkipPreviousAsync())),
                     play, Round(NextGlyph, ControlSize, "Next track", () => m.Control(x => x.TrySkipNextAsync())), shuffle })
        {
            c.HorizontalAlignment = HorizontalAlignment.Center;
            c.VerticalAlignment = VerticalAlignment.Center;
            controls.Children.Add(c);
        }

        var content = new StackPanel { Margin = new Thickness(18, 14, 18, 16) };
        foreach (var c in new UIElement[] { output, artFrame, line, wave, times, controls }) content.Children.Add(c);
        var grid = new Grid();
        grid.Children.Add(backdrop);
        grid.Children.Add(wash);
        grid.Children.Add(content);
        // Fill the flyout to its rounded edge, like the phone's card.
        var frame = new Border { Child = grid, ClipToBounds = true, CornerRadius = new CornerRadius(8), Margin = new Thickness(-16, -14, -16, -16) }; // the flyout card's own padding
        frame.SizeChanged += (_, _) => frame.Clip = new RectangleGeometry(new Rect(0, 0, frame.ActualWidth, frame.ActualHeight), 8, 8);

        string? shownTrack = null;
        return frame.Updating(() =>
        {
            line.Text = Line(m.Title, m.Artist);
            if (shownTrack != line.Text)
            {
                shownTrack = line.Text;
                art.Source = m.Artwork;
                backdrop.Fill = Cover(m.Artwork);
                artFrame.Visibility = m.Artwork is null ? Visibility.Collapsed : Visibility.Visible;
                Wash(m.Accent);
            }
            var position = m.EstimatedPosition(Now);
            wave.Accent = m.Accent;
            wave.Playing = m.IsPlaying;
            wave.Maximum = Math.Max(1, m.Duration.TotalSeconds);
            if (!wave.IsDragging) wave.Value = position.TotalSeconds;
            // Always focusable, so a screen reader can read the position; it seeks only where the app allows it.
            wave.IsHitTestVisible = m.CanSeek;
            System.Windows.Automation.AutomationProperties.SetHelpText(wave, m.CanSeek ? "" : "This app doesn't allow seeking");
            wave.Visibility = m.Duration > TimeSpan.Zero ? Visibility.Visible : Visibility.Hidden;
            elapsed.Text = m.Duration > TimeSpan.Zero ? TrackTimeline.Format(position) : "";
            total.Text = m.Duration > TimeSpan.Zero ? TrackTimeline.Format(m.Duration) : "";
            System.Windows.Automation.AutomationProperties.SetItemStatus(wave, m.Duration > TimeSpan.Zero ? $"{elapsed.Text} of {total.Text}" : "");
            playIcon.Text = m.IsPlaying ? PauseGlyph : PlayGlyph;
            play.Named(m.IsPlaying ? "Pause" : "Play");
            play.ToolTip = m.IsPlaying ? "Pause" : "Play";
            // Toggles show their state in the cover's colour. The app decides whether it has shuffle and repeat;
            // where it doesn't, they are hidden (their places kept, so the row stays centred).
            shuffle.Visibility = m.CanShuffle ? Visibility.Visible : Visibility.Hidden;
            shuffle.IsChecked = m.Shuffle == true;
            shuffleIcon.Foreground = m.Shuffle == true ? new SolidColorBrush(m.Accent) : Brushes.White;
            repeat.Visibility = m.CanRepeat ? Visibility.Visible : Visibility.Hidden;
            repeat.IsChecked = m.Repeat != MediaRepeat.Off;
            repeatIcon.Text = m.Repeat == MediaRepeat.Track ? RepeatOneGlyph : RepeatAllGlyph;
            repeatIcon.Foreground = m.Repeat != MediaRepeat.Off ? new SolidColorBrush(m.Accent) : Brushes.White;
            repeat.Named(m.Repeat switch { MediaRepeat.List => "Repeat: all", MediaRepeat.Track => "Repeat: one", _ => "Repeat: off" });
        });
    }
}

internal static class Fluent
{
    public static T Also<T>(this T value, Action<T> configure) { configure(value); return value; }
}
