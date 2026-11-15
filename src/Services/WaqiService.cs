using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using SmartcityBackend.src.Helpers;

namespace SmartcityBackend.src.Services
{
    /// <summary>
    /// Real air-quality readings from ground monitoring stations — World Air Quality Index
    /// project (aqicn.org / api.waqi.info). Free with a token (WAQI_API_TOKEN).
    ///
    /// The geo feed returns the station closest to a point, which can be far away in Sri Lanka
    /// (few stations). A reading is only used when the station is within
    /// WAQI_MAX_DISTANCE_KM (default 25 km) and not older than 6 hours; otherwise the caller
    /// keeps the Open-Meteo model value.
    ///
    /// Note: WAQI's "aqi" is on the US EPA scale. Its per-pollutant "iaqi" values are AQI
    /// sub-indexes, not µg/m³ concentrations, so concentrations still come from Open-Meteo.
    /// Attribution to the WAQI project and the originating agency is required.
    /// </summary>
    public class WaqiService
    {
        private static readonly TimeSpan CacheTime = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan MaxAge = TimeSpan.FromHours(6);

        private readonly HttpClient _http;
        private readonly IMemoryCache _cache;
        private readonly ILogger<WaqiService> _logger;
        private readonly string? _token;

        public WaqiService(IHttpClientFactory httpClientFactory, IMemoryCache cache, ILogger<WaqiService> logger)
        {
            _http = httpClientFactory.CreateClient("Waqi");
            _cache = cache;
            _logger = logger;
            _token = AppConfig.WaqiApiToken;
        }

        public bool IsConfigured => !string.IsNullOrWhiteSpace(_token);

        public static double MaxDistanceKm =>
            double.TryParse(Environment.GetEnvironmentVariable("WAQI_MAX_DISTANCE_KM"), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var km) && km > 0 ? km : 25;

        /// <summary>Nearest station reading, or null when not configured / too far / stale / failed.</summary>
        public async Task<StationAirQuality?> GetNearestAsync(double lat, double lng, CancellationToken ct = default)
        {
            if (!IsConfigured) return null;

            var key = $"waqi:{lat:F2}:{lng:F2}";
            if (_cache.TryGetValue(key, out StationAirQuality? cached))
                return cached;   // may be a cached null ("no usable station")

            var result = await FetchAsync(lat, lng, ct);
            _cache.Set(key, result, CacheTime);
            return result;
        }

        private async Task<StationAirQuality?> FetchAsync(double lat, double lng, CancellationToken ct)
        {
            var url = string.Create(CultureInfo.InvariantCulture,
                $"https://api.waqi.info/feed/geo:{lat};{lng}/?token={Uri.EscapeDataString(_token!)}");
            try
            {
                using var response = await _http.GetAsync(url, ct);
                var json = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("[WAQI] HTTP {Status} for {Lat},{Lng}", (int)response.StatusCode, lat, lng);
                    return null;
                }
                return Parse(json, lat, lng, DateTime.UtcNow, _logger);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[WAQI] Request failed for {Lat},{Lng}", lat, lng);
                return null;
            }
        }

        /// <summary>Parses a WAQI feed response (public for unit tests).</summary>
        public static StationAirQuality? Parse(string json, double lat, double lng, DateTime nowUtc, ILogger? logger = null)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("status", out var status) || status.GetString() != "ok")
            {
                var msg = root.TryGetProperty("data", out var d0) && d0.ValueKind == JsonValueKind.String ? d0.GetString() : "unknown";
                logger?.LogWarning("[WAQI] status not ok: {Message}", msg);   // e.g. "Invalid key"
                return null;
            }

            var data = root.GetProperty("data");

            // aqi can be "-" when the station has no current value
            if (!data.TryGetProperty("aqi", out var aqiEl)) return null;
            double aqi;
            if (aqiEl.ValueKind == JsonValueKind.Number) aqi = aqiEl.GetDouble();
            else if (!double.TryParse(aqiEl.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out aqi)) return null;

            var city = data.GetProperty("city");
            var name = city.TryGetProperty("name", out var n) ? n.GetString() ?? "Station" : "Station";
            double? sLat = null, sLng = null;
            if (city.TryGetProperty("geo", out var geo) && geo.ValueKind == JsonValueKind.Array && geo.GetArrayLength() >= 2)
            {
                sLat = geo[0].GetDouble();
                sLng = geo[1].GetDouble();
            }
            if (sLat == null || sLng == null) return null;

            var distance = DistanceKm(lat, lng, sLat.Value, sLng.Value);
            if (distance > MaxDistanceKm)
            {
                logger?.LogInformation("[WAQI] Nearest station {Name} is {Km:F0} km away — using model data.", name, distance);
                return null;
            }

            DateTime? measuredUtc = null;
            if (data.TryGetProperty("time", out var time) && time.TryGetProperty("iso", out var iso)
                && DateTimeOffset.TryParse(iso.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
                measuredUtc = dto.UtcDateTime;

            if (measuredUtc != null && nowUtc - measuredUtc.Value > MaxAge)
            {
                logger?.LogInformation("[WAQI] Station {Name} reading is older than {Hours} h — using model data.", name, MaxAge.TotalHours);
                return null;
            }

            string? dominant = data.TryGetProperty("dominentpol", out var dp) ? dp.GetString() : null;   // (sic) WAQI spelling
            var attribution = new List<string>();
            if (data.TryGetProperty("attributions", out var attrs) && attrs.ValueKind == JsonValueKind.Array)
                foreach (var a in attrs.EnumerateArray())
                    if (a.TryGetProperty("name", out var an) && an.GetString() is { Length: > 0 } s) attribution.Add(s);

            return new StationAirQuality
            {
                UsAqi = aqi,
                StationName = name,
                StationLatitude = sLat.Value,
                StationLongitude = sLng.Value,
                DistanceKm = Math.Round(distance, 1),
                MeasuredAtUtc = measuredUtc,
                DominantPollutant = dominant,
                Attribution = attribution,
            };
        }

        /// <summary>Great-circle distance (haversine).</summary>
        public static double DistanceKm(double lat1, double lng1, double lat2, double lng2)
        {
            const double R = 6371.0;
            double ToRad(double d) => d * Math.PI / 180.0;
            var dLat = ToRad(lat2 - lat1);
            var dLng = ToRad(lng2 - lng1);
            var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                    Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2)) * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
            return 2 * R * Math.Asin(Math.Min(1, Math.Sqrt(a)));
        }
    }

    public class StationAirQuality
    {
        public double UsAqi { get; set; }
        public string StationName { get; set; } = string.Empty;
        public double StationLatitude { get; set; }
        public double StationLongitude { get; set; }
        public double DistanceKm { get; set; }
        public DateTime? MeasuredAtUtc { get; set; }
        public string? DominantPollutant { get; set; }
        public List<string> Attribution { get; set; } = new();
    }
}
