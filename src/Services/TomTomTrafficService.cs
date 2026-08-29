using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using SmartcityBackend.src.Data;
using SmartcityBackend.src.Entities;

namespace SmartcityBackend.src.Services
{
    /// <summary>
    /// Fetches real-time traffic flow data from TomTom Traffic API
    /// and stores enriched records in smart_TrafficData.
    ///
    /// TomTom Flow API docs:
    /// https://developer.tomtom.com/traffic-api/documentation/traffic-flow/traffic-flow-service
    /// </summary>
    public class TomTomTrafficService
    {
        private readonly HttpClient _http;
        private readonly SmartCityDbContext _db;
        private readonly ILogger<TomTomTrafficService> _logger;
        private readonly string _apiKey;

        private const string BaseUrl = "https://api.tomtom.com/traffic/services/4/flowSegmentData/absolute/10/json";

        // Key Sri Lanka road points to poll
        private static readonly List<(string Name, double Lat, double Lng)> SriLankaPoints = new()
        {
            ("Colombo Fort",         6.9344, 79.8428),
            ("Galle Road Colombo",   6.8942, 79.8560),
            ("Kandy Road Colombo",   6.9500, 79.9000),
            ("Nugegoda",             6.8728, 79.8993),
            ("Maharagama",           6.8480, 79.9265),
            ("Kaduwela",             6.9328, 79.9793),
            ("Malabe",               6.9100, 79.9750),
            ("Kelaniya",             7.0000, 79.9200),
            ("Negombo Road",         7.1500, 79.8800),
            ("Kottawa",              6.8400, 79.9600),
        };

        public TomTomTrafficService(
            IHttpClientFactory httpClientFactory,
            SmartCityDbContext db,
            ILogger<TomTomTrafficService> logger)
        {
            _http = httpClientFactory.CreateClient("TomTom");
            _db = db;
            _logger = logger;
            _apiKey = Environment.GetEnvironmentVariable("TOMTOM_API_KEY")
                      ?? throw new InvalidOperationException("TOMTOM_API_KEY not set.");
        }

        // ── Fetch all Sri Lanka points ─────────────────────────────────────

        public async Task<List<TrafficFlowPoint>> FetchAllPointsAsync(CancellationToken ct = default)
        {
            var results = new List<TrafficFlowPoint>();

            // Run all requests in parallel (max 5 at a time to respect rate limits)
            var semaphore = new SemaphoreSlim(5);
            var tasks = SriLankaPoints.Select(async point =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    var flow = await FetchFlowAsync(point.Lat, point.Lng, ct);
                    if (flow != null)
                    {
                        flow.LocationName = point.Name;
                        results.Add(flow);
                    }
                }
                finally { semaphore.Release(); }
            });

            await Task.WhenAll(tasks);
            return results;
        }

        // ── Fetch single point ─────────────────────────────────────────────

        public async Task<TrafficFlowPoint?> FetchFlowAsync(
            double lat, double lng, CancellationToken ct = default)
        {
            var url = $"{BaseUrl}?point={lat},{lng}&unit=KMPH&key={_apiKey}";

            try
            {
                var response = await _http.GetAsync(url, ct);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(ct);
                var data = JsonConvert.DeserializeObject<TomTomFlowResponse>(json);

                if (data?.FlowSegmentData == null) return null;

                var seg = data.FlowSegmentData;

                // Congestion = 1 - (currentSpeed / freeFlowSpeed), scaled 0-100
                var congestion = seg.FreeFlowSpeed > 0
                    ? (float)Math.Clamp(
                        (1.0 - (seg.CurrentSpeed / seg.FreeFlowSpeed)) * 100.0,
                        0, 100)
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

        // ── Fetch + Save to DB ─────────────────────────────────────────────

        public async Task<List<TrafficFlowPoint>> FetchAndSaveAsync(
            Guid createdUserId, CancellationToken ct = default)
        {
            var points = await FetchAllPointsAsync(ct);

            if (points.Count > 0)
            {
                var entities = points.Select(p => new SmartTrafficData
                {
                    TrafficDataId = Guid.NewGuid(),
                    Latitude = (decimal)p.Lat,
                    Longitude = (decimal)p.Lng,
                    CongestionLevel = p.CongestionLevel,
                    RecordedAt = p.FetchedAt,
                    IsActive = true,
                    CreatedDateTime = DateTime.UtcNow,
                    CreatedUserId = createdUserId,
                }).ToList();

                _db.SmartTrafficData.AddRange(entities);
                await _db.SaveChangesAsync(ct);

                _logger.LogInformation(
                    "[TomTom] Saved {Count} real-time traffic points.", entities.Count);
            }

            return points;
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
        public DateTime FetchedAt { get; set; }

        public string CongestionStatus =>
            CongestionLevel >= 75 ? "High Congestion" :
            CongestionLevel >= 40 ? "Moderate Congestion" :
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