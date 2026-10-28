namespace Strata.Core;

public sealed record WeatherDay(DateTime Date, int Code, double High, double Low);
public sealed record WeatherReport(double Temperature, int Code, bool IsDay, double Wind, int Humidity, List<WeatherDay> Days, DateTime Fetched);

/// <summary>What a forecast is for: location (rounded to ~100 m) and units. Reports are only shown for their own key.</summary>
public sealed record WeatherKey(double Latitude, double Longitude, bool Fahrenheit)
{
    public static WeatherKey? For(AppSettings s) =>
        s.WeatherLatitude is double lat && s.WeatherLongitude is double lon
            ? new WeatherKey(Math.Round(lat, 3), Math.Round(lon, 3), s.Fahrenheit)
            : null;
}

/// <summary>
/// Forecast cache with request tokens. Each refresh takes a token; a response is accepted only if it belongs to
/// the newest request, so a slow answer for the previous city can never replace (or be shown as) the new one.
/// </summary>
public sealed class WeatherCache
{
    private readonly Dictionary<WeatherKey, WeatherReport> _reports = new();
    private readonly Dictionary<WeatherKey, DateTime> _lastAttempt = new();
    private long _latestToken;
    private WeatherKey? _latestKey;

    public long Begin(WeatherKey key, DateTime now)
    {
        _latestKey = key;
        _lastAttempt[key] = now;
        return ++_latestToken;
    }

    /// <returns>True if the report was accepted (it answers the newest request).</returns>
    public bool Complete(long token, WeatherKey key, WeatherReport report)
    {
        if (token != _latestToken || key != _latestKey) return false;
        _reports[key] = report;
        return true;
    }

    /// <summary>The report for exactly this key, or null (= show "loading").</summary>
    public WeatherReport? Get(WeatherKey? key) => key is not null && _reports.TryGetValue(key, out var r) ? r : null;

    public bool IsStale(WeatherKey key, DateTime now, TimeSpan maxAge) =>
        !_reports.TryGetValue(key, out var r) || now - r.Fetched > maxAge;

    public bool RecentlyAttempted(WeatherKey key, DateTime now, TimeSpan minGap) =>
        _lastAttempt.TryGetValue(key, out var t) && now - t < minGap;

    public void Put(WeatherKey key, WeatherReport report) => _reports[key] = report;
}
