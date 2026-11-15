using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using SmartcityBackend.src.Helpers;

namespace SmartcityBackend.src.Services
{
    /// <summary>
    /// Climate features: live weather for Sri Lankan cities, 7-day forecast, air quality (US AQI)
    /// and rule-based climate alerts (heat, heavy rain, storms, wind, air quality, UV).
    /// Data: Open-Meteo (cached 10 minutes to stay well inside the free usage limits).
    /// Air quality: WAQI ground-station readings when a station is close (WAQI_API_TOKEN),
    /// otherwise the Open-Meteo / CAMS model value.
    /// </summary>
    public class ClimateService
    {
        private static readonly TimeSpan CacheTime = TimeSpan.FromMinutes(10);

        public static readonly IReadOnlyList<(string Name, double Lat, double Lng)> Cities = new List<(string, double, double)>
        {
            ("Colombo",       6.9271, 79.8612),
            ("Kandy",         7.2906, 80.6337),
            ("Galle",         6.0535, 80.2210),
            ("Jaffna",        9.6615, 80.0255),
            ("Trincomalee",   8.5874, 81.2152),
            ("Anuradhapura",  8.3114, 80.4037),
            ("Nuwara Eliya",  6.9497, 80.7891),
            ("Ratnapura",     6.6828, 80.3992),
        };

        private readonly OpenMeteoService _meteo;
        private readonly WaqiService _waqi;
        private readonly InsightService _insights;
        private readonly IMemoryCache _cache;
        private readonly ILogger<ClimateService> _logger;

        public ClimateService(
            OpenMeteoService meteo, WaqiService waqi, InsightService insights, IMemoryCache cache, ILogger<ClimateService> logger)
        {
            _meteo = meteo;
            _waqi = waqi;
            _insights = insights;
            _cache = cache;
            _logger = logger;
        }

        // ── Overview: all cities ───────────────────────────────────────────

        public async Task<object> GetOverviewAsync(CancellationToken ct = default)
        {
            if (_cache.TryGetValue("climate:overview", out object? cached) && cached != null)
                return cached;

            var points = Cities.Select(c => new GeoPoint(c.Lat, c.Lng)).ToList();
            var forecasts = await _meteo.GetForecastsAsync(points, forecastHours: 24, forecastDays: 3, ct);

            List<AirQualityResult> air;
            try { air = await _meteo.GetAirQualityAsync(points, 1, ct); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Climate] Air quality unavailable.");
                air = new List<AirQualityResult>();
            }

            // Real station readings (WAQI) replace the model AQI where a station is close
            while (air.Count < Cities.Count) air.Add(null!);
            for (var i = 0; i < Cities.Count; i++)
                air[i] = (await ApplyStationAsync(air[i], Cities[i].Lat, Cities[i].Lng, ct))!;

            var allAlerts = new List<ClimateAlert>();
            var cities = new List<object>();

            for (var i = 0; i < Cities.Count && i < forecasts.Count; i++)
            {
                var city = Cities[i];
                var f = forecasts[i];
                var aq = i < air.Count ? air[i] : null;
                var alerts = BuildAlerts(city.Name, f, aq);
                allAlerts.AddRange(alerts);

                var today = f.Daily.ElementAtOrDefault(0);
                var tomorrow = f.Daily.ElementAtOrDefault(1);

                cities.Add(new
                {
                    name = city.Name,
                    latitude = city.Lat,
                    longitude = city.Lng,
                    current = MapCurrent(f.Current),
                    today = MapDaily(today),
                    tomorrow = MapDaily(tomorrow),
                    air = MapAir(aq),
                    alertCount = alerts.Count,
                    maxSeverity = alerts.Count > 0 ? alerts.Max(a => a.SeverityRank) : 0,
                });
            }

            var ordered = allAlerts
                .OrderByDescending(a => a.SeverityRank)
                .ThenBy(a => a.City)
                .ToList();

            // Store the most important alerts as dashboard insights
            foreach (var a in ordered.Where(a => a.SeverityRank >= 3).Take(3))
                await _insights.AddAsync("climate", $"{a.Title} — {a.City}: {a.Message}", TimeSpan.FromHours(12), ct);

            var result = new
            {
                updatedAt = AppTime.ToIsoLocal(AppTime.NowLocal()),
                source = _waqi.IsConfigured
                    ? "Open-Meteo (weather, air-quality model) + WAQI ground stations (aqicn.org)"
                    : "Open-Meteo (weather & air quality)",
                cities,
                alerts = ordered,
                counts = new
                {
                    critical = ordered.Count(a => a.Severity == "critical"),
                    high = ordered.Count(a => a.Severity == "high"),
                    moderate = ordered.Count(a => a.Severity == "moderate"),
                    low = ordered.Count(a => a.Severity == "low"),
                },
            };

            _cache.Set("climate:overview", (object)result, CacheTime);
            return result;
        }

        // ── 7-day forecast for one place ───────────────────────────────────

        public async Task<object> GetForecastAsync(double lat, double lng, string? name, CancellationToken ct = default)
        {
            var key = $"climate:forecast:{lat:F2}:{lng:F2}";
            if (_cache.TryGetValue(key, out object? cached) && cached != null)
                return cached;

            var f = await _meteo.GetForecastAsync(lat, lng, forecastHours: 24, forecastDays: 7, ct)
                    ?? new WeatherForecast();

            AirQualityResult? aq = null;
            try { aq = (await _meteo.GetAirQualityAsync(new[] { new GeoPoint(lat, lng) }, 2, ct)).FirstOrDefault(); }
            catch (Exception ex) { _logger.LogWarning(ex, "[Climate] Air quality unavailable."); }
            aq = await ApplyStationAsync(aq, lat, lng, ct);

            var nowLocal = AppTime.NowLocal();
            var hourStart = new DateTime(nowLocal.Year, nowLocal.Month, nowLocal.Day, nowLocal.Hour, 0, 0);

            var result = new
            {
                name = string.IsNullOrWhiteSpace(name) ? $"{lat:F3}, {lng:F3}" : name,
                latitude = lat,
                longitude = lng,
                current = MapCurrent(f.Current),
                air = MapAir(aq),
                daily = f.Daily.Select(d => new
                {
                    date = d.Date.ToString("yyyy-MM-dd"),
                    label = d.Date.ToString("ddd"),
                    weatherCode = d.WeatherCode,
                    description = OpenMeteoService.Describe(d.WeatherCode),
                    temperatureMax = d.TemperatureMax,
                    temperatureMin = d.TemperatureMin,
                    apparentTemperatureMax = d.ApparentTemperatureMax,
                    precipitationSumMm = d.PrecipitationSumMm,
                    precipitationProbabilityMax = d.PrecipitationProbabilityMax,
                    uvIndexMax = d.UvIndexMax,
                    windGustsMaxKmh = d.WindGustsMaxKmh,
                }),
                hourly = f.Hourly.Select(h => new
                {
                    time = AppTime.ToIsoLocal(h.Time),
                    hour = h.Time.Hour,
                    temperature = h.Temperature,
                    precipitationMm = h.PrecipitationMm,
                    precipitationProbability = h.PrecipitationProbability,
                    weatherCode = h.WeatherCode,
                    usAqi = aq?.Hourly.FirstOrDefault(a => a.Time == h.Time)?.UsAqi,
                }),
                alerts = BuildAlerts(string.IsNullOrWhiteSpace(name) ? "Selected location" : name!, f, aq),
                updatedAt = AppTime.ToIsoLocal(nowLocal),
            };

            _cache.Set(key, (object)result, CacheTime);
            return result;
        }

        /// <summary>Current weather only (used to auto-fill the Energy temperature).</summary>
        public async Task<object?> GetCurrentAsync(double lat, double lng, CancellationToken ct = default)
        {
            var key = $"climate:current:{lat:F2}:{lng:F2}";
            if (_cache.TryGetValue(key, out object? cached) && cached != null)
                return cached;

            var f = await _meteo.GetForecastAsync(lat, lng, 1, 1, ct);
            var result = MapCurrent(f?.Current);
            if (result != null) _cache.Set(key, result, CacheTime);
            return result;
        }

        /// <summary>Overrides the model AQI with a nearby WAQI station reading (if any).</summary>
        private async Task<AirQualityResult?> ApplyStationAsync(AirQualityResult? aq, double lat, double lng, CancellationToken ct)
        {
            if (!_waqi.IsConfigured) return aq;

            var station = await _waqi.GetNearestAsync(lat, lng, ct);
            if (station == null) return aq;

            aq ??= new AirQualityResult();
            aq.ModelUsAqi = aq.UsAqi;
            aq.UsAqi = station.UsAqi;
            aq.Source = "station";
            aq.Station = station;
            return aq;
        }

        // ── Alert rules ────────────────────────────────────────────────────

        /// <summary>
        /// Heat  : NOAA heat-index bands applied to "feels like" temperature
        ///         (32–41 °C extreme caution, 41–54 °C danger, ≥ 54 °C extreme danger).
        /// Rain  : daily rainfall ≥ 50 mm moderate, ≥ 100 mm high, ≥ 150 mm critical.
        /// Storm : WMO thunderstorm codes 95–99.   Wind: gusts ≥ 60 / ≥ 90 km/h.
        /// Air   : US AQI > 100 / > 150 / > 200.   UV: index ≥ 8 / ≥ 11.
        /// </summary>
        public static List<ClimateAlert> BuildAlerts(string city, WeatherForecast f, AirQualityResult? aq)
        {
            var alerts = new List<ClimateAlert>();
            var today = f.Daily.ElementAtOrDefault(0);
            var tomorrow = f.Daily.ElementAtOrDefault(1);

            // Heat
            var feels = Max(f.Current?.ApparentTemperature, today?.ApparentTemperatureMax);
            if (feels is >= 54)
                alerts.Add(Alert(city, "heat", "critical", "Extreme heat danger",
                    $"Feels like {feels:F0} °C.", "Stay indoors in cool places, drink water often and check on elderly people.", feels));
            else if (feels is >= 41)
                alerts.Add(Alert(city, "heat", "high", "Heat danger",
                    $"Feels like {feels:F0} °C.", "Avoid outdoor work 11am–3pm, drink plenty of water, watch for heat exhaustion.", feels));
            else if (feels is >= 32)
                alerts.Add(Alert(city, "heat", "moderate", "Heat caution",
                    $"Feels like {feels:F0} °C.", "Take breaks in the shade and stay hydrated.", feels));

            // Heavy rain / flood risk (today or tomorrow)
            var rainToday = today?.PrecipitationSumMm ?? 0;
            var rainTomorrow = tomorrow?.PrecipitationSumMm ?? 0;
            var rain = Math.Max(rainToday, rainTomorrow);
            var when = rainToday >= rainTomorrow ? "today" : "tomorrow";
            if (rain >= 150)
                alerts.Add(Alert(city, "rain", "critical", "Very heavy rain – flood & landslide risk",
                    $"About {rain:F0} mm of rain expected {when}.", "Avoid low-lying roads and slopes, follow DMC / Met Department warnings.", rain));
            else if (rain >= 100)
                alerts.Add(Alert(city, "rain", "high", "Heavy rain – flood risk",
                    $"About {rain:F0} mm of rain expected {when}.", "Expect flooding on roads; plan extra travel time.", rain));
            else if (rain >= 50)
                alerts.Add(Alert(city, "rain", "moderate", "Heavy showers",
                    $"About {rain:F0} mm of rain expected {when}.", "Carry rain gear and drive carefully.", rain));

            // Thunderstorms
            var stormNow = f.Current?.WeatherCode is >= 95;
            var stormSoon = today?.WeatherCode is >= 95 || tomorrow?.WeatherCode is >= 95;
            if (stormNow || stormSoon)
                alerts.Add(Alert(city, "storm", "moderate", "Thunderstorms",
                    stormNow ? "Thunderstorm activity right now." : "Thunderstorms in the forecast.",
                    "Stay away from open areas and trees during lightning; unplug sensitive electronics.", null));

            // Wind
            var gust = Max(f.Current?.WindGustsKmh, today?.WindGustsMaxKmh);
            if (gust is >= 90)
                alerts.Add(Alert(city, "wind", "high", "Damaging wind gusts",
                    $"Gusts up to {gust:F0} km/h.", "Secure loose objects; avoid travel near the coast and large trees.", gust));
            else if (gust is >= 60)
                alerts.Add(Alert(city, "wind", "moderate", "Strong wind gusts",
                    $"Gusts up to {gust:F0} km/h.", "Take care on two-wheelers and near trees.", gust));

            // Air quality
            var aqi = aq?.UsAqi;
            var atStation = aq?.Source == "station" && aq.Station != null ? $" at station {aq.Station.StationName}" : "";
            var pmText = aq?.Pm25 is double pm25 ? $" (PM2.5 {pm25:F0} µg/m³)" : "";
            if (aqi is > 200)
                alerts.Add(Alert(city, "air", "critical", "Very unhealthy air",
                    $"US AQI {aqi:F0}{atStation}{pmText}.", "Everyone should avoid outdoor activity; wear an N95 mask outside.", aqi));
            else if (aqi is > 150)
                alerts.Add(Alert(city, "air", "high", "Unhealthy air",
                    $"US AQI {aqi:F0}{atStation}{pmText}.", "Reduce prolonged outdoor exertion; sensitive groups stay indoors.", aqi));
            else if (aqi is > 100)
                alerts.Add(Alert(city, "air", "moderate", "Unhealthy for sensitive groups",
                    $"US AQI {aqi:F0}{atStation}.", "Children, elderly and people with asthma should limit outdoor activity.", aqi));

            // UV
            var uv = today?.UvIndexMax;
            if (uv is >= 11)
                alerts.Add(Alert(city, "uv", "moderate", "Extreme UV",
                    $"UV index up to {uv:F0}.", "Avoid midday sun, use SPF 30+ sunscreen, hat and sunglasses.", uv));
            else if (uv is >= 8)
                alerts.Add(Alert(city, "uv", "low", "Very high UV",
                    $"UV index up to {uv:F0}.", "Use sunscreen and seek shade around midday.", uv));

            return alerts;
        }

        public static (string Category, string Advice) AqiCategory(double? aqi) => aqi switch
        {
            null => ("Unknown", "No air quality data."),
            <= 50 => ("Good", "Air quality is good — enjoy outdoor activities."),
            <= 100 => ("Moderate", "Acceptable; unusually sensitive people should limit long outdoor exertion."),
            <= 150 => ("Unhealthy for Sensitive Groups", "Children, elderly and people with asthma should reduce outdoor activity."),
            <= 200 => ("Unhealthy", "Everyone may feel effects; reduce prolonged outdoor exertion."),
            <= 300 => ("Very Unhealthy", "Health alert — avoid outdoor activity, wear an N95 mask outside."),
            _ => ("Hazardous", "Emergency conditions — stay indoors with windows closed."),
        };

        // ── Mapping helpers ────────────────────────────────────────────────

        private static object? MapCurrent(CurrentWeather? c) => c == null ? null : new
        {
            temperature = c.Temperature,
            apparentTemperature = c.ApparentTemperature,
            humidity = c.Humidity,
            precipitationMm = c.PrecipitationMm,
            windSpeedKmh = c.WindSpeedKmh,
            windGustsKmh = c.WindGustsKmh,
            weatherCode = c.WeatherCode,
            description = OpenMeteoService.Describe(c.WeatherCode),
            weather = OpenMeteoService.ToWeatherCategory(c.WeatherCode, c.PrecipitationMm),
        };

        private static object? MapDaily(DailyWeather? d) => d == null ? null : new
        {
            date = d.Date.ToString("yyyy-MM-dd"),
            weatherCode = d.WeatherCode,
            description = OpenMeteoService.Describe(d.WeatherCode),
            temperatureMax = d.TemperatureMax,
            temperatureMin = d.TemperatureMin,
            precipitationSumMm = d.PrecipitationSumMm,
            precipitationProbabilityMax = d.PrecipitationProbabilityMax,
            uvIndexMax = d.UvIndexMax,
        };

        private static object? MapAir(AirQualityResult? a)
        {
            if (a == null) return null;
            var (category, advice) = AqiCategory(a.UsAqi);
            return new
            {
                usAqi = a.UsAqi,
                europeanAqi = a.EuropeanAqi,
                pm25 = a.Pm25,
                pm10 = a.Pm10,
                category,
                advice,
                source = a.Source,              // "station" | "model"
                modelUsAqi = a.ModelUsAqi,
                station = a.Station == null ? null : new
                {
                    name = a.Station.StationName,
                    latitude = a.Station.StationLatitude,
                    longitude = a.Station.StationLongitude,
                    distanceKm = a.Station.DistanceKm,
                    measuredAt = a.Station.MeasuredAtUtc.HasValue
                        ? AppTime.ToIsoLocal(AppTime.ToLocal(a.Station.MeasuredAtUtc.Value))
                        : null,
                    dominantPollutant = a.Station.DominantPollutant,
                    attribution = a.Station.Attribution,
                },
            };
        }

        private static ClimateAlert Alert(
            string city, string type, string severity, string title, string message, string advice, double? value) =>
            new()
            {
                Id = $"{city}-{type}".ToLowerInvariant().Replace(' ', '-'),
                City = city,
                Type = type,
                Severity = severity,
                Title = title,
                Message = message,
                Advice = advice,
                Value = value.HasValue ? Math.Round(value.Value, 1) : null,
            };

        private static double? Max(double? a, double? b) =>
            a.HasValue && b.HasValue ? Math.Max(a.Value, b.Value) : a ?? b;
    }

    public class ClimateAlert
    {
        public string Id { get; set; } = string.Empty;
        public string City { get; set; } = string.Empty;
        /// <summary>heat | rain | storm | wind | air | uv</summary>
        public string Type { get; set; } = string.Empty;
        /// <summary>low | moderate | high | critical</summary>
        public string Severity { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string Advice { get; set; } = string.Empty;
        public double? Value { get; set; }

        public int SeverityRank => Severity switch
        {
            "critical" => 4,
            "high" => 3,
            "moderate" => 2,
            "low" => 1,
            _ => 0,
        };
    }
}
