using System.Runtime.ExceptionServices;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using Strata.Controls;
using Strata.Widgets;

namespace Strata.Tests;

/// <summary>The redesigned player: timeline estimate, album colour, waveform shape and the seekable wave slider.</summary>
[Trait("Category", "Unit")]
public class MediaPlayerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 24, 7, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(true, 1.0, 64)]    // playing: reported 60 s + 4 s since the report
    [InlineData(false, 1.0, 60)]   // paused: stays where it was reported
    [InlineData(true, 2.0, 68)]    // double speed
    public void Position_is_estimated_between_the_apps_reports(bool playing, double rate, double expected) =>
        Assert.Equal(TimeSpan.FromSeconds(expected),
            TrackTimeline.Estimate(TimeSpan.FromSeconds(60), T0, T0.AddSeconds(4), playing, rate, TimeSpan.FromSeconds(232)));

    [Fact]
    public void The_estimate_never_runs_past_the_track_or_below_zero()
    {
        Assert.Equal(TimeSpan.FromSeconds(232), TrackTimeline.Estimate(TimeSpan.FromSeconds(230), T0, T0.AddMinutes(5), true, 1, TimeSpan.FromSeconds(232)));
        Assert.Equal(TimeSpan.Zero, TrackTimeline.Estimate(TimeSpan.FromSeconds(-3), T0, T0, false, 1, TimeSpan.FromSeconds(232)));
    }

    [Theory]
    [InlineData(114, "1:54")]
    [InlineData(232, "3:52")]
    [InlineData(498, "8:18")]
    [InlineData(3725, "1:02:05")]
    [InlineData(0, "0:00")]
    public void Times_read_like_the_phone(int seconds, string text) => Assert.Equal(text, TrackTimeline.Format(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData("new\u0001track", "old\u0001track", false, false, true)]  // track changed: always load (even to clear stale art)
    [InlineData("new\u0001track", "old\u0001track", true, true, true)]    // track changed with art showing: load the new cover
    [InlineData("same\u0001track", "same\u0001track", false, true, true)] // the regression: thumbnail arrived an event late
    [InlineData("same\u0001track", "same\u0001track", false, false, false)] // artless track, nothing offered: no churn
    [InlineData("same\u0001track", "same\u0001track", true, true, false)]   // cover already loaded: no churn
    public void Artwork_reloads_when_a_late_thumbnail_appears_and_never_churns(
        string key, string current, bool loaded, bool offered, bool expected) =>
        Assert.Equal(expected, MediaService.ShouldLoadArtwork(key, current, loaded, offered));

    private static byte[] Pixels(params (Color Color, int Count)[] parts)
    {
        var list = new List<byte>();
        foreach (var (c, n) in parts) for (int i = 0; i < n; i++) list.AddRange([c.B, c.G, c.R, c.A]);
        return list.ToArray();
    }

    private static double Hue(Color c) { AlbumColor.ToHsv(c.R / 255.0, c.G / 255.0, c.B / 255.0, out double h, out _, out _); return h; }

    [Fact]
    public void A_violet_cover_gives_a_violet_player()
    {
        var c = AlbumColor.FromPixels(Pixels((Color.FromRgb(0x6A, 0x3F, 0xC4), 700), (Color.FromRgb(0x20, 0x20, 0x24), 300)), Colors.Gray);
        Assert.InRange(Hue(c), 255, 275);
        AlbumColor.ToHsv(c.R / 255.0, c.G / 255.0, c.B / 255.0, out _, out double s, out double v);
        Assert.True(s >= 0.55 && v > 0.9);   // vivid enough to read on the dark card
    }

    [Fact]
    public void The_strongest_colour_wins_over_a_bigger_dull_area()
    {
        var c = AlbumColor.FromPixels(Pixels((Color.FromRgb(0x2F, 0x6B, 0xF0), 300), (Color.FromRgb(0x90, 0x88, 0x80), 700)), Colors.Gray);
        Assert.InRange(Hue(c), 215, 230);
    }

    [Fact]
    public void A_small_vivid_colour_beats_a_large_mild_one()
    {
        // 600 pixels of mild teal against 300 of vivid red: the player takes the red, as the phone does.
        var c = AlbumColor.FromPixels(Pixels((Color.FromRgb(0x5A, 0x80, 0x80), 600), (Color.FromRgb(0xE8, 0x22, 0x30), 300)), Colors.Gray);
        Assert.True(Hue(c) < 10 || Hue(c) > 350, $"hue {Hue(c)}");
    }

    [Fact]
    public void A_black_and_white_cover_gets_a_soft_blue_grey_not_an_invented_hue()
    {
        Assert.Equal(AlbumColor.Muted, AlbumColor.FromPixels(Pixels((Colors.White, 500), (Color.FromRgb(0x30, 0x30, 0x30), 500)), Colors.Gray));
        Assert.Equal(Colors.Gray, AlbumColor.FromPixels([], Colors.Gray));
        Assert.Equal(Colors.Gray, AlbumColor.FromImage(null, Colors.Gray));
        // Transparent pixels don't count.
        Assert.Equal(AlbumColor.Muted, AlbumColor.FromPixels(Pixels((Color.FromArgb(0, 0xFF, 0, 0), 900), (Colors.White, 100)), Colors.Gray));
    }

    [Fact]
    public void The_card_wash_is_the_accents_hue_made_dark()
    {
        var tint = AlbumColor.CardTint(Color.FromRgb(0x8A, 0x5C, 0xFF));
        AlbumColor.ToHsv(tint.R / 255.0, tint.G / 255.0, tint.B / 255.0, out double h, out _, out double v);
        Assert.InRange(h, 250, 265);
        Assert.InRange(v, 0.2, 0.24);
    }

    [Fact]
    public void Waves_taper_to_nothing_at_the_start_and_at_the_position()
    {
        Assert.Equal(0, WaveShape.Envelope(0, 300));
        Assert.Equal(0, WaveShape.Envelope(300, 300));
        Assert.Equal(1, WaveShape.Envelope(150, 300), 6);
        Assert.Equal(1, WaveShape.Envelope(WaveShape.Taper, 300), 6);     // full height a short way in
        for (int layer = 0; layer < WaveShape.Layers.Length; layer++)
        {
            Assert.Equal(0, WaveShape.Height(0, 300, layer, 3.2, 1, 1, 1));
            Assert.Equal(0, WaveShape.Height(300, 300, layer, 3.2, 1, 1, 1));
            Assert.Equal(0, WaveShape.Height(120, 300, layer, 3.2, 0, 1, 1));   // paused: no waves, a flat line
            Assert.InRange(WaveShape.Height(120, 300, layer, 3.2, 1, 1, 1), 0, 1);
        }
    }

    [Fact]
    public void Crests_have_different_heights_along_the_bar_and_move()
    {
        var heights = Enumerable.Range(0, 300).Select(x => WaveShape.Height(x, 300, 0, 1.3, 1, 1, 1)).ToArray();
        var crests = heights.Where((h, i) => i > 0 && i < heights.Length - 1 && h > heights[i - 1] && h >= heights[i + 1] && h > 0.05).ToArray();
        Assert.True(crests.Length >= 2, $"{crests.Length} crests");
        Assert.True(crests.Max() - crests.Min() > 0.1, "crests should differ in height");
        Assert.NotEqual(WaveShape.Height(90, 300, 0, 1.0, 1, 1, 1), WaveShape.Height(90, 300, 0, 1.25, 1, 1, 1), 3);
    }

    [Fact]
    public void The_waves_are_bigger_when_the_music_is_louder()
    {
        for (int layer = 0; layer < WaveShape.Layers.Length; layer++)
        {
            double quiet = Enumerable.Range(0, 300).Max(x => WaveShape.Height(x, 300, layer, 2, 1, 0.1, 1));
            double loud = Enumerable.Range(0, 300).Max(x => WaveShape.Height(x, 300, layer, 2, 1, 1.0, 1));
            Assert.True(loud > quiet * 2.5, $"layer {layer}: {quiet} vs {loud}");
        }
    }

    [Fact]
    public void Each_part_of_the_song_has_its_own_scale()
    {
        var sections = Enumerable.Range(0, 30).Select(i => WaveShape.Section(i * 8, 0)).ToArray();
        Assert.All(sections, v => Assert.InRange(v, 0.55, 1));
        Assert.True(sections.Max() - sections.Min() > 0.2, "sections should differ");
        Assert.Equal(WaveShape.Section(61.5, 1), WaveShape.Section(61.5, 1));   // the same moment, the same scale
        Assert.True(Math.Abs(WaveShape.Section(61.5, 1) - WaveShape.Section(61.6, 1)) < 0.01, "and it changes smoothly");
    }

    [Fact]
    public void The_level_follows_the_music_at_any_volume_and_keeps_quiet_parts_quieter()
    {
        double hold = 0;
        Assert.Equal(0, WaveShape.Loudness(0, ref hold, 0.033));                       // silence: flat
        for (int i = 0; i < 30; i++) WaveShape.Loudness(0.12, ref hold, 0.033);         // music at a low volume
        Assert.True(WaveShape.Loudness(0.12, ref hold, 0.033) > 0.9, "a low volume still fills the waves");
        double loud = WaveShape.Loudness(0.6, ref hold, 0.033);                         // a loud passage
        double quiet = WaveShape.Loudness(0.2, ref hold, 0.033);                        // then a quiet one
        Assert.True(quiet < loud * 0.5, $"quiet {quiet} vs loud {loud}");
        for (int i = 0; i < 1800; i++) WaveShape.Loudness(0.2, ref hold, 0.033);        // a minute of the quiet part
        Assert.True(WaveShape.Loudness(0.2, ref hold, 0.033) > 0.9, "the gain adapts to a long quiet part");
    }

    [Fact]
    public void The_beat_rises_at_once_and_falls_back_more_slowly() => OnSta(() =>
    {
        double peak = 0.8;
        var wave = new WaveSlider { Level = () => peak, Playing = true };
        for (int i = 0; i < 3; i++) wave.Step(1 / 30.0);
        Assert.True(wave.Beat > 0.7, $"rise {wave.Beat}");
        peak = 0;
        for (int i = 0; i < 3; i++) wave.Step(1 / 30.0);
        Assert.True(wave.Beat > 0.3, $"fall should be slower: {wave.Beat}");
        for (int i = 0; i < 30; i++) wave.Step(1 / 30.0);
        Assert.True(wave.Beat < 0.05, $"settled {wave.Beat}");
    });

    [Fact]
    public void Upper_layers_are_lighter_tints_of_the_accent()
    {
        var accent = Color.FromRgb(0x60, 0x40, 0xE0);
        var bottom = WaveShape.LayerColor(accent, 0);
        var top = WaveShape.LayerColor(accent, 2);
        Assert.Equal((accent.R, accent.G, accent.B), (bottom.R, bottom.G, bottom.B));
        Assert.True(top.R > bottom.R && top.G > bottom.G && top.B >= bottom.B);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(10, 0)]
    [InlineData(110, 0.5)]
    [InlineData(300, 1)]
    public void A_pointer_seeks_to_its_position_on_the_track(double x, double expected) =>
        Assert.Equal(expected, WaveShape.FractionAt(x, 220, 10), 6);

    [Fact]
    public void Title_and_artist_read_as_one_line()
    {
        Assert.Equal("Song • Artist", MediaWidget.Line("Song", "Artist"));
        Assert.Equal("Song", MediaWidget.Line("Song", ""));
    }

    [Fact]
    public void The_wave_is_a_real_slider_for_screen_readers_and_seeks_from_the_keyboard() => OnSta(() =>
    {
        var wave = new WaveSlider { Maximum = 232, Value = 114 };
        AutomationProperties.SetName(wave, "Playback position");
        var peer = UIElementAutomationPeer.CreatePeerForElement(wave);
        Assert.Equal(AutomationControlType.Slider, peer.GetAutomationControlType());
        Assert.True(wave.Focusable);
        double? sought = null;
        wave.SeekRequested += s => sought = s;
        var key = new KeyEventArgs(Keyboard.PrimaryDevice, new FakeSource(), 0, Key.Right) { RoutedEvent = System.Windows.UIElement.KeyDownEvent };
        wave.RaiseEvent(key);
        Assert.Equal(119, sought);
        Assert.True(peer.IsControlElement());
        // The dock strip is decoration; the tile is the control. Found by the Phase B sweep on fffee6f.
        var strip = new WaveSlider { Compact = true };
        Assert.False(strip.Focusable);
        var stripPeer = UIElementAutomationPeer.CreatePeerForElement(strip);
        Assert.False(stripPeer.IsControlElement());
        Assert.False(stripPeer.IsContentElement());
    });

    /// <summary>
    /// Asked for after 1.3 was installed: with nothing playing the pill is one small icon, but its slot still kept
    /// the room a track, its artwork and the transport controls need, leaving the icon in a hole. The slot narrows
    /// while idle, takes its full width the moment a track arrives, and gives it back when the music stops.
    /// </summary>
    [Fact]
    public void An_idle_pill_takes_an_icons_room_and_a_playing_one_takes_its_own() => OnSta(() =>
    {
        var previous = MediaService.Current;
        try
        {
            const Strata.Core.DockSize size = Strata.Core.DockSize.Medium;
            double quiet = StableWidgetHost.IdleWidthFor("media", size);
            double playing = StableWidgetHost.WidthFor("media", size);
            var host = new Host();
            var media = new MediaService(host.Signals);
            media.UseFixture("", "", false, null);
            MediaService.Current = media;

            var widget = new MediaWidget(host);
            var slot = new StableWidgetHost("media", size, widget);

            Assert.True(widget.IsIdle);
            Assert.Equal(quiet, slot.Width);
            // The icon has to fit the narrowed slot, which clips: an idle pill must not be a cut-off glyph.
            widget.Measure(new System.Windows.Size(double.PositiveInfinity, 48));
            Assert.True(widget.DesiredSize.Width <= quiet, $"the idle pill wants {widget.DesiredSize.Width} px of a {quiet} px slot");
            // It is still a button with a flyout, so the icon remains worth clicking.
            Assert.NotNull(widget.PreviewFlyout());

            media.UseFixture("Song", "Artist", true, Cover(80, 80), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(200));
            widget.Tick();
            Assert.False(widget.IsIdle);
            Assert.Equal(playing, slot.Width);

            media.UseFixture("", "", false, null);
            widget.Tick();
            Assert.True(widget.IsIdle);
            Assert.Equal(quiet, slot.Width);
        }
        finally { MediaService.Current = previous; }
    });

    private sealed class Host : IDockHost
    {
        public Strata.Core.AppSettings Settings { get; } = new();
        public Strata.Core.ScreenTimeTracker ScreenTime { get; } = new();
        public Strata.Smart.SignalStore Signals { get; } = new(new Strata.Smart.ManualClock(new DateTime(2026, 3, 10, 9, 0, 0)));
        public void Notify(string key, string title, string text, bool urgent = false) { }
        public void ShowFlyout(System.Windows.FrameworkElement anchor, System.Windows.FrameworkElement content, double width = 320, bool fromKeyboard = false) { }
        public void CloseFlyout() { }
        public void SaveSettings() { }
        public void OpenSettings() { }
    }

    private static System.Windows.Media.Imaging.BitmapSource Cover(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 0xC4; pixels[i + 1] = 0x3F; pixels[i + 2] = 0x6A; pixels[i + 3] = 0xFF; }
        var bmp = System.Windows.Media.Imaging.BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>
    /// Found live with TIDAL on 82c576c: the card grew to the height of the screen. The blurred backdrop took part
    /// in layout, so the cover's own shape set the card's height. The card must fit its content whatever the cover.
    /// </summary>
    [Theory]
    [InlineData(300, 300)]
    [InlineData(160, 1280)]   // tall
    [InlineData(1280, 160)]   // wide
    [InlineData(1280, 1280)]  // large
    public void The_card_fits_its_content_whatever_the_covers_shape(int width, int height) => OnSta(() =>
    {
        var previous = MediaService.Current;
        try
        {
            var host = new Host();
            var media = new MediaService(host.Signals);
            media.UseFixture("Song", "Artist", true, Cover(width, height), TimeSpan.FromSeconds(52), TimeSpan.FromSeconds(126));
            MediaService.Current = media;
            var card = Strata.Views.FlyoutWindow.CreateCard(new MediaWidget(host).PreviewFlyout()!);
            card.Width = 330 + 34;
            card.Measure(new System.Windows.Size(card.Width, double.PositiveInfinity));
            Assert.InRange(card.DesiredSize.Height, 380, 560);
        }
        finally { MediaService.Current = previous; }
    });

    /// <summary>Asked by the user after 872bbab: paused, the card's played part is a flat line in the accent.</summary>
    [Fact]
    public void The_card_bar_is_a_flat_line_in_the_accent_when_paused() => OnSta(() =>
    {
        var wave = new WaveSlider { Maximum = 200, Value = 120, Accent = Color.FromRgb(0xFF, 0x20, 0x20), Level = () => 0.7 };
        wave.Measure(new System.Windows.Size(320, 38));
        wave.Arrange(new System.Windows.Rect(0, 0, 320, 38));
        int Rows()
        {
            wave.UpdateLayout();
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(320, 38, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(wave);
            var px = new byte[320 * 38 * 4];
            bmp.CopyPixels(px, 320 * 4, 0);
            int rows = 0;
            for (int y = 0; y < 38; y++)
            {
                bool red = false;
                for (int x = 12; x < 150 && !red; x++) { int i = (y * 320 + x) * 4; red = px[i + 2] > 0x30 && px[i + 2] > px[i + 1] * 2; }
                if (red) rows++;
            }
            return rows;
        }
        wave.Playing = true;
        for (int i = 0; i < 15; i++) wave.Step(1 / 30.0);
        Assert.True(Rows() >= 8, "playing: waves above the line");
        wave.Playing = false;
        for (int i = 0; i < 60 && !wave.Step(1 / 30.0); i++) { }
        Assert.Equal(0, wave.Energy);
        Assert.InRange(Rows(), 1, 4);                                                   // the 3 px line only
    });

    /// <summary>
    /// Found live on 82c576c: the dock strip never visibly moved. It was 5 px tall, leaving the filled waves
    /// about 2 px. This renders the real dock widget and looks at the strip's own pixels: layered waves rising
    /// from the bottom edge (as on the phone's lock screen), moving while playing, flat when paused.
    /// </summary>
    [Fact]
    public void The_dock_strip_visibly_waves_while_playing_and_lies_flat_when_paused() => OnSta(() =>
    {
        var previous = MediaService.Current;
        try
        {
            var host = new Host();
            var media = new MediaService(host.Signals);
            media.UseFixture("Song", "Artist", true, Cover(300, 300), TimeSpan.FromSeconds(63), TimeSpan.FromSeconds(126));
            MediaService.Current = media;
            var widget = new MediaWidget(host);
            var strip = Descendants(widget).OfType<WaveSlider>().Single();
            strip.Accent = Color.FromRgb(0xFF, 0x20, 0x20);
            widget.Measure(new System.Windows.Size(460, 48));
            widget.Arrange(new System.Windows.Rect(0, 0, 460, 48));
            widget.UpdateLayout();

            int Rows(out byte[] pixels)
            {
                // Render the whole widget (a child renders at its offset) and crop to the strip.
                var at = strip.TransformToAncestor(widget).Transform(new System.Windows.Point(0, 0));
                int w = (int)Math.Floor(strip.ActualWidth), h = (int)Math.Floor(strip.ActualHeight);
                var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(460, 48, 96, 96, PixelFormats.Pbgra32);
                bmp.Render(widget);
                pixels = new byte[w * h * 4];
                bmp.CopyPixels(new System.Windows.Int32Rect((int)Math.Round(at.X), (int)Math.Round(at.Y), w, h), pixels, w * 4, 0);
                int rows = 0;
                for (int y = 0; y < h; y++)
                {
                    bool red = false;
                    for (int x = 0; x < w / 2 - 6 && !red; x++)   // the played half
                    {
                        int i = (y * w + x) * 4;
                        red = pixels[i + 2] > 0x30 && pixels[i + 2] > pixels[i + 1] * 2;   // the accent, also in translucent layers (premultiplied)
                    }
                    if (red) rows++;
                }
                return rows;
            }

            Assert.True(strip.ActualHeight >= 8, $"strip {strip.ActualHeight} px");
            strip.Playing = true;
            strip.Step(0.5); widget.UpdateLayout();
            Assert.True(Rows(out var a) >= 6, "the waves should rise well above the line");
            strip.Step(0.15); widget.UpdateLayout();
            Rows(out var b);
            Assert.NotEqual(a, b);                          // it moves
            strip.Playing = false;
            for (int i = 0; i < 40 && !strip.Step(0.1); i++) { }
            widget.UpdateLayout();
            Assert.Equal(0, strip.Energy);
            Assert.InRange(Rows(out _), 1, 3);              // a flat line
        }
        finally { MediaService.Current = previous; }
    });

    private static IEnumerable<System.Windows.DependencyObject> Descendants(System.Windows.DependencyObject root)
    {
        foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(root).OfType<System.Windows.DependencyObject>())
        {
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    private sealed class FakeSource : System.Windows.PresentationSource
    {
        protected override CompositionTarget? GetCompositionTargetCore() => null;
        public override System.Windows.Media.Visual? RootVisual { get; set; }
        public override bool IsDisposed => false;
    }

    private static void OnSta(Action action)
    {
        ExceptionDispatchInfo? error = null;
        var t = new Thread(() => { try { action(); } catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(10)), "STA media test timed out.");
        error?.Throw();
    }
}
