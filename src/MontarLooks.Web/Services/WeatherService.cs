using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;

namespace MontarLooks.Web.Services;

public record CityLocation(string Name, string? Region, string? Country, double Latitude, double Longitude)
{
    public string Label => string.Join(", ", new[] { Name, Region, Country }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

public record WeatherNow(
    double TemperatureC,
    double FeelsLikeC,
    double MinC,
    double MaxC,
    double PrecipitationMm,
    int RainChancePercent,
    int WeatherCode)
{
    public string Description => WeatherCodes.Describe(WeatherCode);
    public string Icon => WeatherCodes.Icon(WeatherCode);

    public bool IsRainy => PrecipitationMm > 0.1 || RainChancePercent >= 60 || WeatherCodes.IsRain(WeatherCode);

    /// <summary>
    /// Temperature used to dress: feels-like now, pulled a bit toward the day's minimum
    /// so a look still works when it cools down later.
    /// </summary>
    public double DressingTemperature => Math.Round(FeelsLikeC * 0.7 + MinC * 0.3, 1);
}

/// <summary>Forecast for one whole day (planner, travel).</summary>
public record DailyForecast(
    DateOnly Date,
    double MinC,
    double MaxC,
    double FeelsMinC,
    double FeelsMaxC,
    double PrecipitationMm,
    int RainChancePercent,
    int WeatherCode,
    bool IsEstimate = false)
{
    public string Description => WeatherCodes.Describe(WeatherCode);
    public string Icon => WeatherCodes.Icon(WeatherCode);

    public bool IsRainy => PrecipitationMm >= 1 || RainChancePercent >= 60 || WeatherCodes.IsRain(WeatherCode) && RainChancePercent >= 40;

    /// <summary>
    /// Same idea as <see cref="WeatherNow.DressingTemperature"/>: a typical daytime "feels like"
    /// (closer to the max, when we are out) pulled toward the day's minimum.
    /// </summary>
    public double DressingTemperature => Compute(FeelsMinC, FeelsMaxC, MinC);

    public static double Compute(double feelsMin, double feelsMax, double min) =>
        Math.Round((feelsMax * 0.75 + feelsMin * 0.25) * 0.7 + min * 0.3, 1);
}

/// <summary>Weather and city lookup using Open-Meteo (free, no API key).</summary>
public class WeatherService(HttpClient http, IMemoryCache cache, ILogger<WeatherService> logger)
{
    public async Task<WeatherNow?> GetCurrentAsync(double lat, double lon, CancellationToken ct = default)
    {
        var key = $"weather:{lat:F2}:{lon:F2}";
        if (cache.TryGetValue(key, out WeatherNow? cached)) return cached;

        var url = string.Format(CultureInfo.InvariantCulture,
            "https://api.open-meteo.com/v1/forecast?latitude={0}&longitude={1}" +
            "&current=temperature_2m,apparent_temperature,precipitation,weather_code" +
            "&daily=temperature_2m_max,temperature_2m_min,precipitation_probability_max" +
            "&timezone=auto&forecast_days=1", lat, lon);
        try
        {
            var r = await http.GetFromJsonAsync<ForecastResponse>(url, ct);
            if (r?.Current is null || r.Daily is null) return null;
            var w = new WeatherNow(
                r.Current.Temperature,
                r.Current.ApparentTemperature,
                r.Daily.Min.FirstOrDefault() ?? r.Current.Temperature,
                r.Daily.Max.FirstOrDefault() ?? r.Current.Temperature,
                r.Current.Precipitation,
                r.Daily.RainChance.FirstOrDefault() ?? 0,
                r.Current.WeatherCode);
            cache.Set(key, w, TimeSpan.FromMinutes(20));
            return w;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            logger.LogWarning("Could not fetch weather from Open-Meteo: {Message}", ex.Message);
            // Remember the failure briefly so pages don't wait for the timeout on every request.
            cache.Set<WeatherNow?>(key, null, TimeSpan.FromMinutes(2));
            return null;
        }
    }

    /// <summary>Daily forecast from today, up to 16 days (Open-Meteo limit). Empty when unreachable.</summary>
    public async Task<IReadOnlyList<DailyForecast>> GetDailyForecastAsync(double lat, double lon, int days = 7, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 16);
        var key = $"weather:daily:{lat:F2}:{lon:F2}:{days}";
        if (cache.TryGetValue(key, out IReadOnlyList<DailyForecast>? cached) && cached != null) return cached;

        var url = string.Format(CultureInfo.InvariantCulture,
            "https://api.open-meteo.com/v1/forecast?latitude={0}&longitude={1}" +
            "&daily=weather_code,temperature_2m_max,temperature_2m_min,apparent_temperature_max,apparent_temperature_min," +
            "precipitation_sum,precipitation_probability_max&timezone=auto&forecast_days={2}", lat, lon, days);
        try
        {
            var r = await http.GetFromJsonAsync<ForecastResponse>(url, ct);
            var d = r?.Daily;
            if (d?.Time is null) return [];
            var list = new List<DailyForecast>();
            for (int i = 0; i < d.Time.Count; i++)
            {
                if (!DateOnly.TryParse(d.Time[i], CultureInfo.InvariantCulture, out var date)) continue;
                double? At(List<double?>? l) => l != null && i < l.Count ? l[i] : null;
                var max = At(d.Max) ?? 24;
                var min = At(d.Min) ?? max - 8;
                list.Add(new DailyForecast(date, min, max,
                    At(d.FeelsMin) ?? min, At(d.FeelsMax) ?? max,
                    At(d.PrecipitationSum) ?? 0,
                    i < d.RainChance.Count ? d.RainChance[i] ?? 0 : 0,
                    d.WeatherCode != null && i < d.WeatherCode.Count ? d.WeatherCode[i] ?? 0 : 0));
            }
            cache.Set<IReadOnlyList<DailyForecast>>(key, list, TimeSpan.FromMinutes(60));
            return list;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            logger.LogWarning("Could not fetch daily forecast from Open-Meteo: {Message}", ex.Message);
            cache.Set<IReadOnlyList<DailyForecast>>(key, [], TimeSpan.FromMinutes(2));
            return [];
        }
    }

    public async Task<IReadOnlyList<CityLocation>> SearchCitiesAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return [];
        var url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(name.Trim())}&count=8&language=pt&format=json";
        try
        {
            var r = await http.GetFromJsonAsync<GeocodingResponse>(url, ct);
            return r?.Results?.Select(x => new CityLocation(x.Name, x.Admin1, x.Country, x.Latitude, x.Longitude)).ToList()
                   ?? (IReadOnlyList<CityLocation>)[];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            logger.LogWarning("Could not search cities on Open-Meteo: {Message}", ex.Message);
            return [];
        }
    }

    private sealed class ForecastResponse
    {
        [JsonPropertyName("current")] public CurrentBlock? Current { get; set; }
        [JsonPropertyName("daily")] public DailyBlock? Daily { get; set; }
    }

    private sealed class CurrentBlock
    {
        [JsonPropertyName("temperature_2m")] public double Temperature { get; set; }
        [JsonPropertyName("apparent_temperature")] public double ApparentTemperature { get; set; }
        [JsonPropertyName("precipitation")] public double Precipitation { get; set; }
        [JsonPropertyName("weather_code")] public int WeatherCode { get; set; }
    }

    private sealed class DailyBlock
    {
        [JsonPropertyName("temperature_2m_max")] public List<double?> Max { get; set; } = [];
        [JsonPropertyName("temperature_2m_min")] public List<double?> Min { get; set; } = [];
        [JsonPropertyName("precipitation_probability_max")] public List<int?> RainChance { get; set; } = [];
        [JsonPropertyName("time")] public List<string>? Time { get; set; }
        [JsonPropertyName("weather_code")] public List<int?>? WeatherCode { get; set; }
        [JsonPropertyName("apparent_temperature_max")] public List<double?>? FeelsMax { get; set; }
        [JsonPropertyName("apparent_temperature_min")] public List<double?>? FeelsMin { get; set; }
        [JsonPropertyName("precipitation_sum")] public List<double?>? PrecipitationSum { get; set; }
    }

    private sealed class GeocodingResponse
    {
        [JsonPropertyName("results")] public List<GeoResult>? Results { get; set; }
    }

    private sealed class GeoResult
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("admin1")] public string? Admin1 { get; set; }
        [JsonPropertyName("country")] public string? Country { get; set; }
        [JsonPropertyName("latitude")] public double Latitude { get; set; }
        [JsonPropertyName("longitude")] public double Longitude { get; set; }
    }
}

/// <summary>WMO weather codes used by Open-Meteo.</summary>
public static class WeatherCodes
{
    public static bool IsRain(int c) => c is >= 51 and <= 67 or >= 80 and <= 82 or >= 95;

    public static string Describe(int c) => c switch
    {
        0 => "Céu limpo",
        1 => "Predominantemente limpo",
        2 => "Parcialmente nublado",
        3 => "Nublado",
        45 or 48 => "Neblina",
        >= 51 and <= 57 => "Garoa",
        >= 61 and <= 67 => "Chuva",
        >= 71 and <= 77 => "Neve",
        >= 80 and <= 82 => "Pancadas de chuva",
        >= 95 => "Tempestade",
        _ => "Tempo variável"
    };

    public static string Icon(int c) => c switch
    {
        0 => "☀️",
        1 or 2 => "⛅",
        3 => "☁️",
        45 or 48 => "🌫️",
        >= 51 and <= 67 => "🌧️",
        >= 71 and <= 77 => "❄️",
        >= 80 and <= 82 => "🌦️",
        >= 95 => "⛈️",
        _ => "🌡️"
    };
}
