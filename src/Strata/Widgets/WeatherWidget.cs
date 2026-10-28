using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Strata.Core;

namespace Strata.Widgets;

public sealed record Place(string Name, string Detail, double Latitude, double Longitude);

/// <summary>
/// Forecasts from Open-Meteo (free, no account or key). Only the chosen city's coordinates are sent,
/// and only when the weather widget is on the dock. Results are cached per <see cref="WeatherKey"/>.
/// </summary>
public static class WeatherService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    public static WeatherCache Cache { get; } = new();

    /// <summary>Replaceable for tests; the default calls Open-Meteo.</summary>
    public static Func<WeatherKey, Task<WeatherReport>> Fetch { get; set; } = FetchFromOpenMeteo;

    static WeatherService() =>
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("Strata/" + (typeof(WeatherService).Assembly.GetName().Version?.ToString(3) ?? "1"));

    /// <summary>The forecast for the city currently selected in settings, or null while it loads.</summary>
    public static WeatherReport? For(AppSettings s) => Cache.Get(WeatherKey.For(s));

    /// <summary>Fixed sample forecast for preview rendering (no network call).</summary>
    public static void UseSampleData(AppSettings s, DateTime today)
    {
        if (WeatherKey.For(s) is not { } key) return;
        int[] codes = [1, 3, 61, 80, 0, 2];
        Cache.Put(key, new WeatherReport(21, 1, true, 12, 58,
            codes.Select((c, i) => new WeatherDay(today.AddDays(i), c, 24 - i % 3, 14 - i % 2)).ToList(), today.AddHours(9)));
    }

    public static async Task RefreshIfStale(AppSettings s, bool force = false)
    {
        if (WeatherKey.For(s) is not { } key) return;
        var now = DateTime.Now;
        if (!force && (!Cache.IsStale(key, now, TimeSpan.FromMinutes(30)) || Cache.RecentlyAttempted(key, now, TimeSpan.FromMinutes(2))))
            return;
        PrivacyGuard.ThrowIfPreview("the network (weather)");
        long token = Cache.Begin(key, now);
        try
        {
            var report = await Fetch(key);
            if (!Cache.Complete(token, key, report))
                Log.Write("Discarded a weather response for a previous city or unit selection.");
        }
        catch (Exception ex) { Log.Write($"Weather refresh failed: {ex.Message}"); }
    }

    private static async Task<WeatherReport> FetchFromOpenMeteo(WeatherKey key)
    {
        string unit = key.Fahrenheit ? "&temperature_unit=fahrenheit&wind_speed_unit=mph" : "";
        string url = "https://api.open-meteo.com/v1/forecast?latitude=" + key.Latitude.ToString(CultureInfo.InvariantCulture)
            + "&longitude=" + key.Longitude.ToString(CultureInfo.InvariantCulture)
            + "&current=temperature_2m,weather_code,is_day,wind_speed_10m,relative_humidity_2m"
            + "&daily=weather_code,temperature_2m_max,temperature_2m_min&forecast_days=6&timezone=auto" + unit;
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
        var cur = doc.RootElement.GetProperty("current");
        var daily = doc.RootElement.GetProperty("daily");
        var days = new List<WeatherDay>();
        var dates = daily.GetProperty("time").EnumerateArray().ToList();
        for (int i = 0; i < dates.Count; i++)
        {
            days.Add(new WeatherDay(
                DateTime.Parse(dates[i].GetString()!, CultureInfo.InvariantCulture),
                daily.GetProperty("weather_code")[i].GetInt32(),
                daily.GetProperty("temperature_2m_max")[i].GetDouble(),
                daily.GetProperty("temperature_2m_min")[i].GetDouble()));
        }
        return new WeatherReport(
            cur.GetProperty("temperature_2m").GetDouble(),
            cur.GetProperty("weather_code").GetInt32(),
            cur.GetProperty("is_day").GetInt32() == 1,
            cur.GetProperty("wind_speed_10m").GetDouble(),
            (int)cur.GetProperty("relative_humidity_2m").GetDouble(),
            days, DateTime.Now);
    }

    public static async Task<List<Place>> Search(string query)
    {
        var results = new List<Place>();
        PrivacyGuard.ThrowIfPreview("the network (city search)");
        try
        {
            string url = "https://geocoding-api.open-meteo.com/v1/search?count=5&language=en&name=" + Uri.EscapeDataString(query);
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            if (!doc.RootElement.TryGetProperty("results", out var arr)) return results;
            foreach (var r in arr.EnumerateArray())
            {
                string name = r.GetProperty("name").GetString() ?? "";
                string admin = r.TryGetProperty("admin1", out var a) ? a.GetString() ?? "" : "";
                string country = r.TryGetProperty("country", out var c) ? c.GetString() ?? "" : "";
                string detail = string.Join(", ", new[] { admin, country }.Where(x => x.Length > 0));
                results.Add(new Place(name, detail, r.GetProperty("latitude").GetDouble(), r.GetProperty("longitude").GetDouble()));
            }
        }
        catch (Exception ex) { Log.Write($"City search failed: {ex.Message}"); }
        return results;
    }

    public static string Symbol(int code, bool isDay = true) => code switch
    {
        0 => isDay ? "☀" : "☾",
        1 or 2 => isDay ? "⛅" : "☁",
        3 => "☁",
        45 or 48 => "≋",
        >= 51 and <= 67 => "☂",
        >= 71 and <= 77 => "❄",
        >= 80 and <= 82 => "☂",
        85 or 86 => "❄",
        >= 95 => "⚡",
        _ => "☁",
    };

    public static string Describe(int code) => code switch
    {
        0 => "Clear",
        1 => "Mostly clear",
        2 => "Partly cloudy",
        3 => "Overcast",
        45 or 48 => "Fog",
        >= 51 and <= 57 => "Drizzle",
        >= 61 and <= 67 => "Rain",
        >= 71 and <= 77 => "Snow",
        >= 80 and <= 82 => "Showers",
        85 or 86 => "Snow showers",
        >= 95 => "Thunderstorm",
        _ => "Cloudy",
    };
}

public sealed class WeatherWidget : Widget
{
    private readonly TextBlock _symbol, _temp;
    private int _tick;

    public WeatherWidget(IDockHost host) : base(host)
    {
        _symbol = UI.Text("☁", Settings.FontSize + 4);
        _symbol.FontFamily = new System.Windows.Media.FontFamily("Segoe UI Symbol");
        _temp = UI.Text("", Settings.FontSize, weight: FontWeights.SemiBold);
        Child = UI.Row(5, _symbol, _temp);
        _ = WeatherService.RefreshIfStale(Settings);
        Tick();
    }

    public override void Tick()
    {
        if (++_tick % 30 == 0) _ = WeatherService.RefreshIfStale(Settings);
        var w = WeatherService.For(Settings);
        // Announce in every branch: without it the tile was an unnamed button to screen readers.
        if (Settings.WeatherLatitude is null) { _temp.Text = "Set city"; ToolTip = "Click to choose your city"; Announce("Weather", "no city set"); return; }
        if (w is null) { _temp.Text = "--°"; Announce("Weather", "loading"); return; }
        _symbol.Text = WeatherService.Symbol(w.Code, w.IsDay);
        _temp.Text = $"{Math.Round(w.Temperature)}°";
        ToolTip = $"{Settings.WeatherCity}: {WeatherService.Describe(w.Code)}, {Math.Round(w.Temperature)}°";
        Announce("Weather", $"{WeatherService.Describe(w.Code)}, {Math.Round(w.Temperature)} degrees");
    }

    protected override double FlyoutWidth => 320;

    protected override FrameworkElement BuildFlyout()
    {
        var root = new StackPanel();
        var body = new StackPanel();
        var search = new StackPanel();
        root.Children.Add(body);
        root.Children.Add(search);

        void ShowForecast()
        {
            body.Children.Clear();
            var w = WeatherService.For(Settings);
            body.Children.Add(UI.Header(Settings.WeatherCity.Length > 0 ? Settings.WeatherCity : "Weather"));
            if (w is null)
            {
                body.Children.Add(UI.Text(Settings.WeatherLatitude is null ? "Choose a city below." : "Loading forecast...", 12.5, subtle: true, flyout: true));
                return;
            }
            var sym = UI.Text(WeatherService.Symbol(w.Code, w.IsDay), 40, flyout: true);
            sym.FontFamily = new System.Windows.Media.FontFamily("Segoe UI Symbol");
            var temp = UI.Text($"{Math.Round(w.Temperature)}°", 40, flyout: true, weight: FontWeights.SemiBold);
            temp.FontFamily = Theme.DisplayFont;
            var desc = UI.Column(2,
                UI.Text(WeatherService.Describe(w.Code), 13, flyout: true, weight: FontWeights.SemiBold),
                UI.Text($"Wind {Math.Round(w.Wind)} {(Settings.Fahrenheit ? "mph" : "km/h")} · Humidity {w.Humidity}%", 12, subtle: true, flyout: true));
            desc.VerticalAlignment = VerticalAlignment.Center;
            body.Children.Add(UI.Row(12, sym, temp, desc).Also(r => r.Margin = new Thickness(0, 6, 0, 10)));

            foreach (var d in w.Days.Take(6))
            {
                var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var name = UI.Text(d.Date.Date == Smart.AppClock.Today ? "Today" : d.Date.ToString("ddd"), 13, flyout: true);
                var s = UI.Text(WeatherService.Symbol(d.Code), 15, flyout: true);
                s.FontFamily = new System.Windows.Media.FontFamily("Segoe UI Symbol");
                var label = UI.Text(WeatherService.Describe(d.Code), 12.5, subtle: true, flyout: true);
                var range = UI.Text($"{Math.Round(d.High)}° / {Math.Round(d.Low)}°", 13, flyout: true, weight: FontWeights.SemiBold);
                Grid.SetColumn(s, 1); Grid.SetColumn(label, 2); Grid.SetColumn(range, 3);
                row.Children.Add(name); row.Children.Add(s); row.Children.Add(label); row.Children.Add(range);
                body.Children.Add(row);
            }
            var credit = UI.Text($"Open-Meteo · updated {w.Fetched:t}", 11, subtle: true, flyout: true);
            credit.Margin = new Thickness(0, 8, 0, 0);
            body.Children.Add(credit);
        }

        // City picker
        var input = UI.Input(placeholder: "Search city");
        var results = new StackPanel();
        var unitToggle = UI.TextButton(Settings.Fahrenheit ? "Use °C" : "Use °F", () =>
        {
            Settings.Fahrenheit = !Settings.Fahrenheit;
            Host.SaveSettings();
            Host.CloseFlyout();
        });
        async void DoSearch()
        {
            results.Children.Clear();
            if (input.Text.Trim().Length < 2) return;
            results.Children.Add(UI.Text("Searching...", 12, subtle: true, flyout: true));
            var places = await WeatherService.Search(input.Text.Trim());
            results.Children.Clear();
            if (places.Count == 0) results.Children.Add(UI.Text("No matches", 12, subtle: true, flyout: true));
            foreach (var p in places)
            {
                var item = UI.Button(UI.Column(0, UI.Text(p.Name, 13, flyout: true, weight: FontWeights.SemiBold),
                    UI.Text(p.Detail, 11.5, subtle: true, flyout: true)), async () =>
                {
                    Settings.WeatherCity = p.Name;
                    Settings.WeatherLatitude = p.Latitude;
                    Settings.WeatherLongitude = p.Longitude;
                    Host.SaveSettings();
                    results.Children.Clear();
                    input.Text = "";
                    await WeatherService.RefreshIfStale(Settings, force: true);
                    ShowForecast();
                    Tick();
                });
                item.Margin = new Thickness(0, 4, 0, 0);
                results.Children.Add(item);
            }
        }
        input.KeyDown += (_, e) => { if (e.Key == Key.Enter) DoSearch(); };
        var pickerRow = new DockPanel();
        DockPanel.SetDock(unitToggle, Dock.Right);
        unitToggle.Margin = new Thickness(6, 0, 0, 0);
        pickerRow.Children.Add(unitToggle);
        pickerRow.Children.Add(input);
        search.Children.Add(UI.Separator().Also(s => s.Margin = new Thickness(0, 10, 0, 10)));
        search.Children.Add(pickerRow);
        search.Children.Add(results);

        ShowForecast();
        if (WeatherService.For(Settings) is null && Settings.WeatherLatitude is not null) _ = WeatherService.RefreshIfStale(Settings, force: true).ContinueWith(
            _ => Dispatcher.Invoke(ShowForecast));
        return root;
    }
}
