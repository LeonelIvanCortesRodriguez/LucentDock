namespace Strata.Smart;

/// <summary>
/// Sunrise and sunset from latitude/longitude (NOAA general solar position equations, about ±2 minutes
/// at mid latitudes). Pure math: no network. Returns null for polar day/night.
/// </summary>
public static class SunCalculator
{
    /// <summary>
    /// Evening by set local times: from <paramref name="eveningStart"/> until <paramref name="morningStart"/>.
    /// Normally the window crosses midnight (19:00 → 07:00); a same-day window (17:45 → 17:55) also works.
    /// </summary>
    public static bool IsEveningBySetTime(TimeSpan now, TimeSpan eveningStart, TimeSpan morningStart) =>
        eveningStart == morningStart ? false
        : eveningStart > morningStart ? now >= eveningStart || now < morningStart
        : now >= eveningStart && now < morningStart;

    /// <summary>Parse "HH:mm" (24-hour); falls back to <paramref name="fallback"/> for anything else.</summary>
    public static TimeSpan ParseTimeOfDay(string? text, TimeSpan fallback) =>
        TimeSpan.TryParseExact(text?.Trim(), @"h\:mm", System.Globalization.CultureInfo.InvariantCulture, out var t) && t < TimeSpan.FromDays(1)
            ? t : fallback;

    public static (DateTime? Sunrise, DateTime? Sunset) Compute(DateTime localDate, double latitude, double longitude, TimeZoneInfo zone)
    {
        var date = localDate.Date;
        int n = date.DayOfYear;
        double gamma = 2 * Math.PI / (DateTime.IsLeapYear(date.Year) ? 366 : 365) * (n - 1);
        double eqTime = 229.18 * (0.000075 + 0.001868 * Math.Cos(gamma) - 0.032077 * Math.Sin(gamma)
                                  - 0.014615 * Math.Cos(2 * gamma) - 0.040849 * Math.Sin(2 * gamma));
        double decl = 0.006918 - 0.399912 * Math.Cos(gamma) + 0.070257 * Math.Sin(gamma)
                      - 0.006758 * Math.Cos(2 * gamma) + 0.000907 * Math.Sin(2 * gamma)
                      - 0.002697 * Math.Cos(3 * gamma) + 0.00148 * Math.Sin(3 * gamma);
        double lat = latitude * Math.PI / 180;
        double cosHa = Math.Cos(90.833 * Math.PI / 180) / (Math.Cos(lat) * Math.Cos(decl)) - Math.Tan(lat) * Math.Tan(decl);
        if (cosHa < -1 || cosHa > 1) return (null, null);
        double ha = Math.Acos(cosHa) * 180 / Math.PI;

        double sunriseUtcMin = 720 - 4 * (longitude + ha) - eqTime;
        double sunsetUtcMin = 720 - 4 * (longitude - ha) - eqTime;
        var utcMidnight = DateTime.SpecifyKind(date, DateTimeKind.Utc);
        DateTime ToLocal(double minutes) => TimeZoneInfo.ConvertTimeFromUtc(utcMidnight.AddMinutes(minutes), zone);
        return (ToLocal(sunriseUtcMin), ToLocal(sunsetUtcMin));
    }
}
