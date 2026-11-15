using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using SmartcityBackend.src.Data;
using SmartcityBackend.src.Entities;
using SmartcityBackend.src.Helpers;
using System.Collections.Concurrent;
using System.Globalization;

namespace SmartcityBackend.src.Services
{
    /// <summary>
    /// Live traffic for the monitored roads. Provider order:
    ///   1) Google Routes API (GOOGLE_MAPS_API_KEY) — covers Sri Lanka
    ///   2) TomTom Traffic Flow (TOMTOM_API_KEY) — no real-time coverage in Sri Lanka
    /// Results are enriched with Open-Meteo weather and stored in smart_TrafficData
    /// (= ML training dataset) with Source = "google" or "tomtom".
    /// </summary>
    public class TomTomTrafficService
    {
        private readonly HttpClient _http;
        private readonly SmartCityDbContext _db;
        private readonly OpenMeteoService _weather;
        private readonly GoogleTrafficService _google;
        private readonly ILogger<TomTomTrafficService> _logger;
        private readonly string? _apiKey;

        private const string BaseUrl = "https://api.tomtom.com/traffic/services/4/flowSegmentData/absolute/10/json";

        /// <summary>Re-use stored data younger than this instead of calling the provider again
        /// (longer with Google, whose free cap is smaller).</summary>
        public static TimeSpan CacheWindow =>
            string.IsNullOrWhiteSpace(AppConfig.GoogleMapsApiKey) ? TimeSpan.FromMinutes(10) : TimeSpan.FromMinutes(30);

        /// <summary>Sri Lanka road points that are polled by the collector.</summary>
        public static readonly IReadOnlyList<(string Name, double Lat, double Lng)> SriLankaPoints = new List<(string, double, double)>
        {
            ("Colombo Fort",         6.9344, 79.8428),
            ("Galle Road Colombo",   6.8942, 79.8560),
            ("Kandy Road Colombo",   6.9500, 79.9000),
            ("Nugegoda",             6.8728, 79.8993),
            ("Maharagama",           6.8480, 79.9265),
            ("Kaduwela",             6.9328, 79.9793),
            ("Kottawa",              6.8400, 79.9600),
            ("Negombo Road",         7.1500, 79.8800),
            ("Kandy Town",           7.2906, 80.6337),
            ("Galle Town",           6.0535, 80.2210),
        };

        public TomTomTrafficService(
            IHttpClientFactory httpClientFactory,
            SmartCityDbContext db,
            OpenMeteoService weather,
            GoogleTrafficService google,
            ILogger<TomTomTrafficService> logger)
        {
            _http = httpClientFactory.CreateClient("TomTom");
            _db = db;
            _weather = weather;
            _google = google;
            _logger = logger;
            _apiKey = AppConfig.TomTomApiKey;
        }

        public bool IsConfigured => _google.IsConfigured || !string.IsNullOrWhiteSpace(_apiKey);

        /// <summary>"google" or "tomtom" — the provider tried first.</summary>
        public string Provider => _google.IsConfigured ? "google" : "tomtom";

        // ── Fetch all Sri Lanka points ─────────────────────────────────────

        public async Task<List<TrafficFlowPoint>> FetchAllPointsAsync(CancellationToken ct = default)
        {
            // ConcurrentBag → safe to add from parallel tasks
            var results = new ConcurrentBag<TrafficFlowPoint>();

            // Run requests in parallel (max 5 at a time to respect rate limits)
            using var semaphore = new SemaphoreSlim(5);
            var tasks = SriLankaPoints.Select(async point =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    var flow = await FetchFlowAsync(point.Lat, point.Lng, ct, point.Name);
                    if (flow != null)
                    {
                        flow.LocationName = point.Name;
                        results.Add(flow);
                    }
                }
                finally { semaphore.Release(); }
            });

            await Task.WhenAll(tasks);
            return results.OrderBy(r => r.LocationName).ToList();
        }

        // ── Fetch single point ─────────────────────────────────────────────

        public async Task<TrafficFlowPoint?> FetchFlowAsync(
            double lat, double lng, CancellationToken ct = default, string? locationName = null)
        {
            // 1) Google Routes (Sri Lanka coverage)
            if (_google.IsConfigured)
            {
                var g = await _google.FetchFlowAsync(lat, lng, locationName, ct);
                if (g != null) return g;
            }

            // 2) TomTom
            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                if (!_google.IsConfigured) _logger.LogWarning("[Traffic] No GOOGLE_MAPS_API_KEY or TOMTOM_API_KEY set.");
                return null;
            }

            var point = string.Create(CultureInfo.InvariantCulture, $"{lat},{lng}");
            var url = $"{BaseUrl}?point={point}&unit=KMPH&key={_apiKey}";

            try
            {
                var response = await _http.GetAsync(url, ct);
                var json = await response.Content.ReadAsStringAsync(ct);

                if (!response.IsSuccessStatusCode)
                {
                    // 400 "Point too far from nearest existing segment" = no TomTom traffic coverage there
                    var detail = json.Length > 300 ? json[..300] : json;
                    _logger.LogWarning("[TomTom] {Status} for {Lat},{Lng}: {Detail}",
                        (int)response.StatusCode, lat, lng, detail);
                    return null;
                }

                var data = JsonConvert.DeserializeObject<TomTomFlowResponse>(json);

                if (data?.FlowSegmentData == null) return null;

                var seg = data.FlowSegmentData;

                // Congestion = 1 - (currentSpeed / freeFlowSpeed), scaled 0-100
                var congestion = seg.FreeFlowSpeed > 0
                    ? (float)Math.Clamp((1.0 - (seg.CurrentSpeed / seg.FreeFlowSpeed)) * 100.0, 0, 100)
                    : 0f;

                return new TrafficFlowPoint
                {
                    Lat = lat,
                    Lng = lng,
                    CurrentSpeedKmh = (float)seg.CurrentSpeed,
                    FreeFlowSpeedKmh = (float)seg.FreeFlowSpeed,
                    CongestionLevel = congestion,
                    Confidence = (float)seg.Confidence,
                    FetchedAt = DateTime.UtcNow,
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[TomTom] Failed to fetch flow for {Lat},{Lng}", lat, lng);
                return null;
            }
        }

        // ── Fetch + enrich with weather + save to DB ───────────────────────

        public async Task<List<TrafficFlowPoint>> FetchAndSaveAsync(
            Guid createdUserId, CancellationToken ct = default)
        {
            var points = await FetchAllPointsAsync(ct);
            if (points.Count == 0) return points;

            await EnrichWithWeatherAsync(points, ct);
            await SaveAsync(points, createdUserId, ct);
            return points;
        }

        /// <summary>
        /// Latest stored point per location (no TomTom call). If the newest data is older
        /// than <see cref="CacheWindow"/> and <paramref name="refreshIfStale"/> is true,
        /// a fresh fetch is made first.
        /// </summary>
        public async Task<(List<TrafficFlowPoint> Points, bool Fresh)> GetLatestAsync(
            bool refreshIfStale, Guid userId, CancellationToken ct = default)
        {
            var newest = await _db.SmartTrafficData
                .Where(x => x.IsActive && (x.Source == "google" || x.Source == "tomtom"))
                .OrderByDescending(x => x.RecordedAt)
                .Select(x => (DateTime?)x.RecordedAt)
                .FirstOrDefaultAsync(ct);

            var stale = newest == null || DateTime.UtcNow - newest.Value > CacheWindow;
            if (stale && refreshIfStale && IsConfigured)
            {
                var fresh = await FetchAndSaveAsync(userId, ct);
                if (fresh.Count > 0) return (fresh, true);
            }

            if (newest == null) return (new List<TrafficFlowPoint>(), false);

            // newest batch = everything recorded within 15 minutes of the newest row
            var from = newest.Value.AddMinutes(-15);
            var rows = await _db.SmartTrafficData
                .Where(x => x.IsActive && (x.Source == "google" || x.Source == "tomtom") && x.RecordedAt >= from)
                .OrderByDescending(x => x.RecordedAt)
                .ToListAsync(ct);

            var latest = rows
                .GroupBy(r => r.LocationName ?? $"{r.Latitude:F4},{r.Longitude:F4}")
                .Select(g => g.First())
                .Select(r => new TrafficFlowPoint
                {
                    LocationName = r.LocationName ?? "Unknown",
                    Lat = (double)r.Latitude,
                    Lng = (double)r.Longitude,
                    CurrentSpeedKmh = r.CurrentSpeedKmh ?? 0,
                    FreeFlowSpeedKmh = r.FreeFlowSpeedKmh ?? 0,
                    CongestionLevel = r.CongestionLevel,
                    Confidence = r.Confidence ?? 0,
                    Temperature = r.Temperature,
                    Weather = r.Weather,
                    FetchedAt = DateTime.SpecifyKind(r.RecordedAt, DateTimeKind.Utc),
                    Provider = r.Source ?? "tomtom",
                })
                .OrderBy(p => p.LocationName)
                .ToList();

            return (latest, false);
        }

        private async Task EnrichWithWeatherAsync(List<TrafficFlowPoint> points, CancellationToken ct)
        {
            try
            {
                var forecasts = await _weather.GetForecastsAsync(
                    points.Select(p => new GeoPoint(p.Lat, p.Lng)).ToList(), forecastHours: 1, forecastDays: 1, ct);

                for (var i = 0; i < points.Count && i < forecasts.Count; i++)
                {
                    var cur = forecasts[i].Current;
                    if (cur == null) continue;
                    points[i].Temperature = (float?)cur.Temperature;
                    points[i].PrecipitationMm = (float?)cur.PrecipitationMm;
                    points[i].Weather = OpenMeteoService.ToWeatherCategory(cur.WeatherCode, cur.PrecipitationMm);
                }
            }
            catch (Exception ex)
            {
                // Weather is optional — traffic data is still saved
                _logger.LogWarning(ex, "[TomTom] Weather enrichment failed.");
            }
        }

        private async Task SaveAsync(List<TrafficFlowPoint> points, Guid createdUserId, CancellationToken ct)
        {
            var entities = points.Select(p =>
            {
                var local = AppTime.ToLocal(p.FetchedAt);
                return new SmartTrafficData
                {
                    TrafficDataId = Guid.NewGuid(),
                    Latitude = (decimal)p.Lat,
                    Longitude = (decimal)p.Lng,
                    CongestionLevel = p.CongestionLevel,
                    RecordedAt = p.FetchedAt,
                    IsActive = true,
                    CreatedDateTime = DateTime.UtcNow,
                    CreatedUserId = createdUserId,
                    LocationName = string.IsNullOrWhiteSpace(p.LocationName) ? null : p.LocationName,
                    CurrentSpeedKmh = p.CurrentSpeedKmh,
                    FreeFlowSpeedKmh = p.FreeFlowSpeedKmh,
                    Confidence = p.Confidence,
                    Temperature = p.Temperature,
                    PrecipitationMm = p.PrecipitationMm,
                    Weather = p.Weather,
                    LocalHour = local.Hour,
                    LocalDayOfWeek = (int)local.DayOfWeek,
                    Source = p.Provider,
                };
            }).ToList();

            _db.SmartTrafficData.AddRange(entities);
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation("[Traffic] Saved {Count} real-time traffic points.", entities.Count);
        }
    }

    // ── Result models ──────────────────────────────────────────────────────

    public class TrafficFlowPoint
    {
        public string LocationName { get; set; } = string.Empty;
        public double Lat { get; set; }
        public double Lng { get; set; }
        public float CurrentSpeedKmh { get; set; }
        public float FreeFlowSpeedKmh { get; set; }
        public float CongestionLevel { get; set; }  // 0–100
        public float Confidence { get; set; }  // 0–1
        public float? Temperature { get; set; }
        public float? PrecipitationMm { get; set; }
        public int? Weather { get; set; }
        public DateTime FetchedAt { get; set; }
        /// <summary>"google" or "tomtom" (stored in smart_TrafficData.Source).</summary>
        public string Provider { get; set; } = "tomtom";

        public string CongestionStatus => TrafficLevels.Status(CongestionLevel);
    }

    public static class TrafficLevels
    {
        public static string Status(double level) =>
            level >= 75 ? "High Congestion" :
            level >= 40 ? "Moderate Congestion" :
            "Low Congestion";
    }

    // ── TomTom JSON DTOs ───────────────────────────────────────────────────

    public class TomTomFlowResponse
    {
        [JsonProperty("flowSegmentData")]
        public FlowSegmentData? FlowSegmentData { get; set; }
    }

    public class FlowSegmentData
    {
        [JsonProperty("currentSpeed")]
        public double CurrentSpeed { get; set; }

        [JsonProperty("freeFlowSpeed")]
        public double FreeFlowSpeed { get; set; }

        [JsonProperty("currentTravelTime")]
        public double CurrentTravelTime { get; set; }

        [JsonProperty("freeFlowTravelTime")]
        public double FreeFlowTravelTime { get; set; }

        [JsonProperty("confidence")]
        public double Confidence { get; set; }
    }
}
