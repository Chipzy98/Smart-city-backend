using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text.Json;

namespace SmartcityBackend.src.Services
{
    /// <summary>
    /// Open-Meteo weather + air-quality client (free, no API key).
    /// Docs: https://open-meteo.com/en/docs and https://open-meteo.com/en/docs/air-quality-api
    /// Multiple coordinates are sent in ONE request (comma separated) → response is a JSON array.
    /// All times are requested in Asia/Colombo.
    /// </summary>
    public class OpenMeteoService
    {
        private const string ForecastUrl = "https://api.open-meteo.com/v1/forecast";
        private const string AirQualityUrl = "https://air-quality-api.open-meteo.com/v1/air-quality";
        private const string TimeZone = "Asia/Colombo";

        private readonly HttpClient _http;
        private readonly ILogger<OpenMeteoService> _logger;

        public OpenMeteoService(IHttpClientFactory httpClientFactory, ILogger<OpenMeteoService> logger)
        {
            _http = httpClientFactory.CreateClient("OpenMeteo");
            _logger = logger;
        }

        // ── Weather ────────────────────────────────────────────────────────

        /// <summary>Current + hourly (next N hours) + daily (N days) weather for many points.</summary>
        public async Task<List<WeatherForecast>> GetForecastsAsync(
            IReadOnlyList<GeoPoint> points, int forecastHours, int forecastDays, CancellationToken ct = default)
        {
            if (points.Count == 0) return new();

            var url =
                $"{ForecastUrl}?latitude={Join(points, p => p.Lat)}&longitude={Join(points, p => p.Lng)}" +
                "&current=temperature_2m,relative_humidity_2m,apparent_temperature,precipitation,weather_code,wind_speed_10m,wind_gusts_10m" +
                "&hourly=temperature_2m,precipitation,precipitation_probability,weather_code" +
                "&daily=weather_code,temperature_2m_max,temperature_2m_min,apparent_temperature_max,precipitation_sum,precipitation_probability_max,uv_index_max,wind_gusts_10m_max" +
                $"&timezone={Uri.EscapeDataString(TimeZone)}" +
                $"&forecast_hours={Math.Clamp(forecastHours, 1, 168)}" +
                $"&forecast_days={Math.Clamp(forecastDays, 1, 16)}";

            using var doc = await GetJsonAsync(url, ct);
            var list = new List<WeatherForecast>();
            foreach (var loc in EnumerateLocations(doc.RootElement))
                list.Add(ParseForecast(loc));
            return list;
        }

        public async Task<WeatherForecast?> GetForecastAsync(
            double lat, double lng, int forecastHours, int forecastDays, CancellationToken ct = default)
        {
            var list = await GetForecastsAsync(new[] { new GeoPoint(lat, lng) }, forecastHours, forecastDays, ct);
            return list.FirstOrDefault();
        }

        // ── Air quality ────────────────────────────────────────────────────

        public async Task<List<AirQualityResult>> GetAirQualityAsync(
            IReadOnlyList<GeoPoint> points, int forecastDays, CancellationToken ct = default)
        {
            if (points.Count == 0) return new();

            var url =
                $"{AirQualityUrl}?latitude={Join(points, p => p.Lat)}&longitude={Join(points, p => p.Lng)}" +
                "&current=us_aqi,european_aqi,pm2_5,pm10" +
                "&hourly=us_aqi,pm2_5,pm10" +
                $"&timezone={Uri.EscapeDataString(TimeZone)}" +
                $"&forecast_days={Math.Clamp(forecastDays, 1, 7)}";

            using var doc = await GetJsonAsync(url, ct);
            var list = new List<AirQualityResult>();
            foreach (var loc in EnumerateLocations(doc.RootElement))
            {
                var result = new AirQualityResult();
                if (loc.TryGetProperty("current", out var cur))
                {
                    result.UsAqi = Num(cur, "us_aqi");
                    result.EuropeanAqi = Num(cur, "european_aqi");
                    result.Pm25 = Num(cur, "pm2_5");
                    result.Pm10 = Num(cur, "pm10");
                }
                if (loc.TryGetProperty("hourly", out var hourly))
                {
                    var times = Strings(hourly, "time");
                    var aqi = Nums(hourly, "us_aqi");
                    var pm25 = Nums(hourly, "pm2_5");
                    for (var i = 0; i < times.Count; i++)
                    {
                        result.Hourly.Add(new AirQualityHour
                        {
                            Time = ParseLocal(times[i]),
                            UsAqi = At(aqi, i),
                            Pm25 = At(pm25, i),
                        });
                    }
                }
                list.Add(result);
            }
            return list;
        }

        // ── Weather code helpers (WMO codes) ───────────────────────────────

        /// <summary>Maps WMO weather code (+ rain intensity) to the app's 0–3 weather scale.</summary>
        public static int ToWeatherCategory(int? code, double? precipitationMm = null)
        {
            if (code is >= 95) return 3;                                // thunderstorm
            if (precipitationMm is >= 10) return 3;                      // very heavy rain
            if (code is >= 51 and <= 67 or >= 71 and <= 86) return 2;    // drizzle / rain / showers
            if (code is 2 or 3 or 45 or 48) return 1;                     // cloudy / fog
            return 0;                                                     // clear
        }

        public static string Describe(int? code) => code switch
        {
            0 => "Clear sky",
            1 => "Mainly clear",
            2 => "Partly cloudy",
            3 => "Overcast",
            45 or 48 => "Fog",
            51 or 53 or 55 => "Drizzle",
            56 or 57 => "Freezing drizzle",
            61 => "Slight rain",
            63 => "Moderate rain",
            65 => "Heavy rain",
            66 or 67 => "Freezing rain",
            71 or 73 or 75 or 77 => "Snow",
            80 => "Rain showers",
            81 => "Heavy showers",
            82 => "Violent rain showers",
            85 or 86 => "Snow showers",
            95 => "Thunderstorm",
            96 or 99 => "Thunderstorm with hail",
            97 => "Heavy thunderstorm",
            _ => "Unknown"
        };

        // ── Parsing ────────────────────────────────────────────────────────

        private static WeatherForecast ParseForecast(JsonElement loc)
        {
            var f = new WeatherForecast();

            if (loc.TryGetProperty("current", out var cur))
            {
                f.Current = new CurrentWeather
                {
                    Time = cur.TryGetProperty("time", out var t) && t.ValueKind == JsonValueKind.String
                        ? ParseLocal(t.GetString()!) : null,
                    Temperature = Num(cur, "temperature_2m"),
                    Humidity = Num(cur, "relative_humidity_2m"),
                    ApparentTemperature = Num(cur, "apparent_temperature"),
                    PrecipitationMm = Num(cur, "precipitation"),
                    WeatherCode = (int?)Num(cur, "weather_code"),
                    WindSpeedKmh = Num(cur, "wind_speed_10m"),
                    WindGustsKmh = Num(cur, "wind_gusts_10m"),
                };
            }

            if (loc.TryGetProperty("hourly", out var hourly))
            {
                var times = Strings(hourly, "time");
                var temp = Nums(hourly, "temperature_2m");
                var rain = Nums(hourly, "precipitation");
                var prob = Nums(hourly, "precipitation_probability");
                var code = Nums(hourly, "weather_code");
                for (var i = 0; i < times.Count; i++)
                {
                    f.Hourly.Add(new HourlyWeather
                    {
                        Time = ParseLocal(times[i]),
                        Temperature = At(temp, i),
                        PrecipitationMm = At(rain, i),
                        PrecipitationProbability = At(prob, i),
                        WeatherCode = (int?)At(code, i),
                    });
                }
            }

            if (loc.TryGetProperty("daily", out var daily))
            {
                var times = Strings(daily, "time");
                var code = Nums(daily, "weather_code");
                var tMax = Nums(daily, "temperature_2m_max");
                var tMin = Nums(daily, "temperature_2m_min");
                var feels = Nums(daily, "apparent_temperature_max");
                var rain = Nums(daily, "precipitation_sum");
                var prob = Nums(daily, "precipitation_probability_max");
                var uv = Nums(daily, "uv_index_max");
                var gust = Nums(daily, "wind_gusts_10m_max");
                for (var i = 0; i < times.Count; i++)
                {
                    f.Daily.Add(new DailyWeather
                    {
                        Date = DateTime.ParseExact(times[i], "yyyy-MM-dd", CultureInfo.InvariantCulture),
                        WeatherCode = (int?)At(code, i),
                        TemperatureMax = At(tMax, i),
                        TemperatureMin = At(tMin, i),
                        ApparentTemperatureMax = At(feels, i),
                        PrecipitationSumMm = At(rain, i),
                        PrecipitationProbabilityMax = At(prob, i),
                        UvIndexMax = At(uv, i),
                        WindGustsMaxKmh = At(gust, i),
                    });
                }
            }

            return f;
        }

        private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
        {
            var res = await _http.GetAsync(url, ct);
            if (!res.IsSuccessStatusCode)
            {
                var body = await res.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("[OpenMeteo] {Status} {Body}", (int)res.StatusCode, body);
                res.EnsureSuccessStatusCode();
            }
            return JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        }

        private static IEnumerable<JsonElement> EnumerateLocations(JsonElement root) =>
            root.ValueKind == JsonValueKind.Array ? root.EnumerateArray() : new[] { root };

        private static string Join(IReadOnlyList<GeoPoint> points, Func<GeoPoint, double> selector) =>
            string.Join(",", points.Select(p => selector(p).ToString("0.####", CultureInfo.InvariantCulture)));

        private static DateTime ParseLocal(string s) =>
            DateTime.ParseExact(s, "yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);

        private static double? Num(JsonElement obj, string name) =>
            obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

        private static List<double?> Nums(JsonElement obj, string name)
        {
            var list = new List<double?>();
            if (obj.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var v in arr.EnumerateArray())
                    list.Add(v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null);
            return list;
        }

        private static List<string> Strings(JsonElement obj, string name)
        {
            var list = new List<string>();
            if (obj.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var v in arr.EnumerateArray())
                    list.Add(v.GetString() ?? string.Empty);
            return list;
        }

        private static double? At(List<double?> list, int i) => i < list.Count ? list[i] : null;
    }

    // ── Models ─────────────────────────────────────────────────────────────

    public record GeoPoint(double Lat, double Lng);

    public class WeatherForecast
    {
        public CurrentWeather? Current { get; set; }
        public List<HourlyWeather> Hourly { get; set; } = new();
        public List<DailyWeather> Daily { get; set; } = new();
    }

    public class CurrentWeather
    {
        public DateTime? Time { get; set; }
        public double? Temperature { get; set; }
        public double? Humidity { get; set; }
        public double? ApparentTemperature { get; set; }
        public double? PrecipitationMm { get; set; }
        public int? WeatherCode { get; set; }
        public double? WindSpeedKmh { get; set; }
        public double? WindGustsKmh { get; set; }
    }

    public class HourlyWeather
    {
        /// <summary>Sri Lanka local time.</summary>
        public DateTime Time { get; set; }
        public double? Temperature { get; set; }
        public double? PrecipitationMm { get; set; }
        public double? PrecipitationProbability { get; set; }
        public int? WeatherCode { get; set; }
    }

    public class DailyWeather
    {
        public DateTime Date { get; set; }
        public int? WeatherCode { get; set; }
        public double? TemperatureMax { get; set; }
        public double? TemperatureMin { get; set; }
        public double? ApparentTemperatureMax { get; set; }
        public double? PrecipitationSumMm { get; set; }
        public double? PrecipitationProbabilityMax { get; set; }
        public double? UvIndexMax { get; set; }
        public double? WindGustsMaxKmh { get; set; }
    }

    public class AirQualityResult
    {
        /// <summary>US AQI shown to users — the station reading when one is close, else the model value.</summary>
        public double? UsAqi { get; set; }
        /// <summary>Open-Meteo (CAMS) model value, kept for comparison when a station overrides it.</summary>
        public double? ModelUsAqi { get; set; }
        /// <summary>"station" (WAQI ground station) or "model" (Open-Meteo / CAMS).</summary>
        public string Source { get; set; } = "model";
        public StationAirQuality? Station { get; set; }
        public double? EuropeanAqi { get; set; }
        public double? Pm25 { get; set; }
        public double? Pm10 { get; set; }
        public List<AirQualityHour> Hourly { get; set; } = new();
    }

    public class AirQualityHour
    {
        public DateTime Time { get; set; }
        public double? UsAqi { get; set; }
        public double? Pm25 { get; set; }
    }
}
