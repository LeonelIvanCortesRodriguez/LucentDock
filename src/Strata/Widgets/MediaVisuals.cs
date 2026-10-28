using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Strata.Widgets;

/// <summary>Where a track is now. Apps report the position only now and then; between reports it is estimated.</summary>
public static class TrackTimeline
{
    /// <summary>The reported position plus the time since it was reported (when playing), kept within 0..duration.</summary>
    public static TimeSpan Estimate(TimeSpan reported, DateTimeOffset reportedAt, DateTimeOffset now, bool playing, double rate, TimeSpan duration)
    {
        var p = reported;
        if (playing && now > reportedAt) p += TimeSpan.FromTicks((long)((now - reportedAt).Ticks * (rate > 0 ? rate : 1)));
        if (p < TimeSpan.Zero) p = TimeSpan.Zero;
        return duration > TimeSpan.Zero && p > duration ? duration : p;
    }

    /// <summary>"3:42", or "1:02:05" for an hour or more.</summary>
    public static string Format(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }
}

/// <summary>
/// The colour a player takes from its album cover: the most vivid hue, made bright enough to read on a dark card.
/// A cover with no real colour (black and white) gives a soft blue-grey instead of an invented hue.
/// </summary>
public static class AlbumColor
{
    public static readonly Color Muted = Color.FromRgb(0xA9, 0xB4, 0xC8);

    /// <summary>From BGRA32 pixels. Hues are binned and weighted by saturation and brightness; the strongest bin wins.</summary>
    public static Color FromPixels(ReadOnlySpan<byte> bgra, Color fallback)
    {
        const int bins = 18;
        Span<double> weight = stackalloc double[bins];
        Span<double> r = stackalloc double[bins], g = stackalloc double[bins], b = stackalloc double[bins];
        double total = 0;
        int pixels = bgra.Length / 4;
        for (int i = 0; i < pixels; i++)
        {
            double bb = bgra[i * 4] / 255.0, gg = bgra[i * 4 + 1] / 255.0, rr = bgra[i * 4 + 2] / 255.0, a = bgra[i * 4 + 3] / 255.0;
            if (a < 0.5) continue;
            ToHsv(rr, gg, bb, out double h, out double s, out double v);
            double w = s * s * v; // colourful and not dark
            if (s < 0.18 || v < 0.15) continue;
            int bin = Math.Min(bins - 1, (int)(h / 360 * bins));
            weight[bin] += w; r[bin] += rr * w; g[bin] += gg * w; b[bin] += bb * w;
            total += w;
        }
        if (pixels == 0) return fallback;
        // Too little colour across the cover (e.g. a black-and-white photo): no hue to take from it.
        if (total / pixels < 0.02) return Muted;
        int best = 0;
        for (int i = 1; i < bins; i++) if (weight[i] > weight[best]) best = i;
        ToHsv(r[best] / weight[best], g[best] / weight[best], b[best] / weight[best], out double hue, out double sat, out _);
        // Readable on the dark card: vivid but not neon.
        return FromHsv(hue, Math.Clamp(sat, 0.55, 0.85), 0.95);
    }

    public static Color FromImage(ImageSource? image, Color fallback)
    {
        if (image is not BitmapSource source) return fallback;
        try
        {
            var small = new TransformedBitmap(source, new ScaleTransform(32.0 / Math.Max(1, source.PixelWidth), 32.0 / Math.Max(1, source.PixelHeight)));
            var bgra = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
            int stride = bgra.PixelWidth * 4;
            var pixels = new byte[stride * bgra.PixelHeight];
            bgra.CopyPixels(pixels, stride, 0);
            return FromPixels(pixels, fallback);
        }
        catch (Exception) { return fallback; }
    }

    /// <summary>The card's wash behind the blurred cover: the accent's hue, dark.</summary>
    public static Color CardTint(Color accent)
    {
        ToHsv(accent.R / 255.0, accent.G / 255.0, accent.B / 255.0, out double h, out double s, out _);
        return FromHsv(h, Math.Min(s, 0.6), 0.22);
    }

    public static void ToHsv(double r, double g, double b, out double h, out double s, out double v)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        v = max;
        s = max <= 0 ? 0 : d / max;
        if (d <= 0) { h = 0; return; }
        h = max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
    }

    public static Color FromHsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = (h % 360) switch
        {
            < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x),
            < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x),
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}

/// <summary>
/// The moving waves over the played part of the progress bar, as on the phone: three translucent layers of crests,
/// each a lighter tint of the accent, crests of different heights along the bar, tapered to nothing at the start
/// and at the position. Their height follows the music (its level, see <see cref="Loudness"/>) and changes from one
/// part of the song to the next (<see cref="Section"/>). Paused: no energy, a flat line. Pure maths, so it can be tested.
/// </summary>
public static class WaveShape
{
    /// <summary>Each layer: crest spacing (DIP), speed (crests a second; the sign is the direction), height,
    /// opacity, and how far its colour is lifted toward white (the upper layers are lighter).</summary>
    public static readonly (double Wavelength, double Speed, double Amplitude, byte Alpha, double Lighten)[] Layers =
    [
        (96, 0.55, 1.00, 0x78, 0.0),
        (62, -0.80, 0.78, 0x60, 0.35),
        (41, 1.15, 0.55, 0x48, 0.65),
    ];

    /// <summary>The waves rise over this many DIP from the start and fall to nothing at the position.</summary>
    public const double Taper = 22;

    /// <summary>A layer's colour: the accent lifted toward white by the layer's amount.</summary>
    public static Color LayerColor(Color accent, int layer)
    {
        var (_, _, _, alpha, lighten) = Layers[layer];
        byte Lift(byte c) => (byte)Math.Round(c + (255 - c) * lighten);
        return Color.FromArgb(alpha, Lift(accent.R), Lift(accent.G), Lift(accent.B));
    }

    /// <summary>0 at both ends of a played part <paramref name="length"/> DIP long, 1 from <see cref="Taper"/> in.</summary>
    public static double Envelope(double x, double length)
    {
        if (length <= 0 || x <= 0 || x >= length) return 0;
        double d = Math.Min(1, Math.Min(x, length - x) / Math.Min(Taper, length / 2));
        return d * d * (3 - 2 * d);
    }

    /// <summary>
    /// Height (0..1) of one layer at <paramref name="x"/> DIP along a played part <paramref name="length"/> DIP long,
    /// at time <paramref name="phase"/> (seconds). <paramref name="energy"/> is 0 when paused (flat) and 1 when
    /// playing; <paramref name="level"/> is the music's level now (0..1); <paramref name="section"/> is this part
    /// of the song's scale.
    /// </summary>
    public static double Height(double x, double length, int layer, double phase, double energy, double level, double section)
    {
        var (wavelength, speed, amplitude, _, _) = Layers[layer];
        double crest = 0.5 + 0.5 * Math.Sin(2 * Math.PI * (x / wavelength - speed * phase));
        // Neighbouring crests differ in height: a slower wave over the crests, drifting the other way.
        double group = 0.45 + 0.55 * (0.5 + 0.5 * Math.Sin(2 * Math.PI * (x / (wavelength * 2.7) + speed * phase * 0.35 + layer * 1.7)));
        double loud = 0.18 + 0.82 * Math.Clamp(level, 0, 1);
        return Math.Clamp(Envelope(x, length) * amplitude * crest * group * loud * section * Math.Clamp(energy, 0, 1), 0, 1);
    }

    /// <summary>How big a layer's waves are in this part of the song (0.55..1): smooth noise over the track position,
    /// changing every few seconds, the same each time the same moment plays.</summary>
    public static double Section(double trackSeconds, int layer)
    {
        double t = Math.Max(0, trackSeconds) / 9 + layer * 13.37;
        double i = Math.Floor(t), f = t - i;
        f = f * f * (3 - 2 * f);
        return 0.55 + 0.45 * (Hash(i) + (Hash(i + 1) - Hash(i)) * f);
    }

    private static double Hash(double n)
    {
        double x = Math.Sin(n * 127.1 + 311.7) * 43758.5453;
        return x - Math.Floor(x);
    }

    /// <summary>
    /// The music's level (0..1) from the playback peak, with an automatic gain (<paramref name="hold"/>, the recent
    /// loudest peak, fading over about 12 s): the waves fill their height at any volume, while a quiet passage stays
    /// smaller than the loud one before it. Silence is 0.
    /// </summary>
    public static double Loudness(double peak, ref double hold, double seconds)
    {
        hold = Math.Max(Math.Max(peak, 0.02), hold * Math.Exp(-Math.Max(0, seconds) / 12));
        if (peak < 0.002) return 0;
        return Math.Clamp(Math.Pow(peak / hold, 1.4), 0, 1);
    }

    /// <summary>A stand-in beat, about two a second, when the level can't be read (no playback device, previews).</summary>
    public static double SyntheticLevel(double phase)
    {
        double beat = 0.5 + 0.5 * Math.Cos(2 * Math.PI * phase * 2);
        return 0.35 + 0.65 * beat * beat * beat;
    }

    /// <summary>Where a pointer at <paramref name="x"/> seeks to, as a fraction of the track.</summary>
    public static double FractionAt(double x, double width, double pad) =>
        width <= pad * 2 ? 0 : Math.Clamp((x - pad) / (width - pad * 2), 0, 1);
}
