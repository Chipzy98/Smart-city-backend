using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SmartcityBackend.src.Helpers;

namespace SmartcityBackend.src.Services
{
    /// <summary>
    /// Live traffic for Sri Lanka from the Google Routes API (computeRoutes, TRAFFIC_AWARE).
    ///
    /// For each monitored road a short segment (≈1–2 km) is routed twice by Google in one call:
    ///   duration       = travel time with current traffic
    ///   staticDuration = travel time without traffic
    /// Speeds are distance / time, and congestion uses the same formula as TomTom:
    ///   congestion = (1 − currentSpeed / freeFlowSpeed) × 100 = (1 − staticDuration / duration) × 100
    ///
    /// Billing: TRAFFIC_AWARE requests are the "Compute Routes Pro" SKU (5,000 free requests / month).
    /// A daily limit (GOOGLE_ROUTES_DAILY_LIMIT, default 160 ≈ 4,960 / month) keeps usage inside the free cap.
    /// </summary>
    public class GoogleTrafficService
    {
        private const string Endpoint = "https://routes.googleapis.com/directions/v2:computeRoutes";

        private readonly HttpClient _http;
        private readonly ILogger<GoogleTrafficService> _logger;
        private readonly string? _apiKey;

        // Daily request counter (per process — resets at local midnight or on restart)
        private static readonly object CounterLock = new();
        private static DateTime _counterDay = DateTime.MinValue;
        private static int _counter;

        /// <summary>End point of the measured segment for each monitored road (same names as the collector list).</summary>
        private static readonly Dictionary<string, (double Lat, double Lng)> SegmentEnds = new()
        {
            ["Colombo Fort"]       = (6.9380, 79.8530),
            ["Galle Road Colombo"] = (6.9060, 79.8530),
            ["Kandy Road Colombo"] = (6.9620, 79.9130),
            ["Nugegoda"]           = (6.8800, 79.8870),
            ["Maharagama"]         = (6.8580, 79.9130),
            ["Kaduwela"]           = (6.9250, 79.9650),
            ["Kottawa"]            = (6.8430, 79.9450),
            ["Negombo Road"]       = (7.1350, 79.8800),
            ["Kandy Town"]         = (7.2830, 80.6200),
            ["Galle Town"]         = (6.0640, 80.2050),
        };

        public GoogleTrafficService(IHttpClientFactory httpClientFactory, ILogger<GoogleTrafficService> logger)
        {
            _http = httpClientFactory.CreateClient("GoogleRoutes");
            _logger = logger;
            _apiKey = AppConfig.GoogleMapsApiKey;
        }

        public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

        public static int DailyLimit =>
            int.TryParse(Environment.GetEnvironmentVariable("GOOGLE_ROUTES_DAILY_LIMIT"), out var n) && n > 0 ? n : 160;

        /// <summary>Requests used today (local Sri Lanka date).</summary>
        public static int UsedToday
        {
            get { lock (CounterLock) { return _counterDay == AppTime.NowLocal().Date ? _counter : 0; } }
        }

        private static bool TryTakeQuota()
        {
            lock (CounterLock)
            {
                var today = AppTime.NowLocal().Date;
                if (_counterDay != today) { _counterDay = today; _counter = 0; }
                if (_counter >= DailyLimit) return false;
                _counter++;
                return true;
            }
        }

        /// <summary>
        /// Live flow at a point. <paramref name="locationName"/> picks the fixed segment of a
        /// monitored road; any other point is measured towards a spot ≈1 km to the north-east.
        /// Returns null when Google is not configured, the daily limit is reached or the call fails.
        /// </summary>
        public async Task<TrafficFlowPoint?> FetchFlowAsync(
            double lat, double lng, string? locationName = null, CancellationToken ct = default)
        {
            if (!IsConfigured) return null;

            if (!TryTakeQuota())
            {
                _logger.LogWarning("[Google] Daily limit of {Limit} Routes requests reached — skipping.", DailyLimit);
                return null;
            }

            var end = locationName != null && SegmentEnds.TryGetValue(locationName, out var e)
                ? e
                : (Lat: lat + 0.007, Lng: lng + 0.007);

            var body = new
            {
                origin = new { location = new { latLng = new { latitude = lat, longitude = lng } } },
                destination = new { location = new { latLng = new { latitude = end.Lat, longitude = end.Lng } } },
                travelMode = "DRIVE",
                routingPreference = "TRAFFIC_AWARE",
            };

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
                {
                    Content = JsonContent.Create(body),
                };
                request.Headers.Add("X-Goog-Api-Key", _apiKey);
                request.Headers.Add("X-Goog-FieldMask", "routes.duration,routes.staticDuration,routes.distanceMeters");

                using var response = await _http.SendAsync(request, ct);
                var json = await response.Content.ReadAsStringAsync(ct);

                if (!response.IsSuccessStatusCode)
                {
                    var detail = json.Length > 400 ? json[..400] : json;
                    _logger.LogWarning("[Google] {Status} for {Lat},{Lng}: {Detail}", (int)response.StatusCode, lat, lng, detail);
                    return null;
                }

                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("routes", out var routes) || routes.GetArrayLength() == 0)
                {
                    _logger.LogWarning("[Google] No route for {Lat},{Lng}.", lat, lng);
                    return null;
                }

                var route = routes[0];
                var distance = route.TryGetProperty("distanceMeters", out var d) ? d.GetDouble() : 0;
                var duration = ParseSeconds(route, "duration");
                var staticDuration = ParseSeconds(route, "staticDuration");

                // Ignore segments that Google routed oddly (too short / long detour)
                if (distance < 300 || distance > 6000 || duration <= 0 || staticDuration <= 0)
                {
                    _logger.LogWarning("[Google] Unusable segment for {Lat},{Lng}: {Distance} m, {Duration}s / {Static}s",
                        lat, lng, distance, duration, staticDuration);
                    return null;
                }

                var currentSpeed = distance / duration * 3.6;       // km/h with traffic
                var freeFlowSpeed = distance / staticDuration * 3.6; // km/h without traffic
                var congestion = (float)Math.Clamp((1.0 - staticDuration / duration) * 100.0, 0, 100);

                return new TrafficFlowPoint
                {
                    LocationName = locationName ?? string.Empty,
                    Lat = lat,
                    Lng = lng,
                    CurrentSpeedKmh = (float)currentSpeed,
                    FreeFlowSpeedKmh = (float)freeFlowSpeed,
                    CongestionLevel = congestion,
                    Confidence = 1f,
                    FetchedAt = DateTime.UtcNow,
                    Provider = "google",
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Google] Failed to fetch traffic for {Lat},{Lng}", lat, lng);
                return null;
            }
        }

        // ── A → B routes with live traffic ─────────────────────────────────

        /// <summary>
        /// Full A → B routes (optionally with alternatives) with traffic-aware travel time,
        /// the same data Google Maps shows. One request = one quota unit.
        /// Returns an empty list when not configured, over the daily limit or on error.
        /// </summary>
        public async Task<List<GoogleRoute>> ComputeRoutesAsync(
            double oLat, double oLng, double dLat, double dLng, bool alternatives, CancellationToken ct = default)
        {
            var result = new List<GoogleRoute>();
            if (!IsConfigured) return result;
            if (!TryTakeQuota())
            {
                _logger.LogWarning("[Google] Daily limit of {Limit} Routes requests reached — route skipped.", DailyLimit);
                return result;
            }

            var body = new
            {
                origin = new { location = new { latLng = new { latitude = oLat, longitude = oLng } } },
                destination = new { location = new { latLng = new { latitude = dLat, longitude = dLng } } },
                travelMode = "DRIVE",
                routingPreference = "TRAFFIC_AWARE",
                computeAlternativeRoutes = alternatives,
                polylineQuality = "HIGH_QUALITY",   // detailed path that follows every bend of the road
                languageCode = "en",
                units = "METRIC",
            };

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = JsonContent.Create(body) };
                request.Headers.Add("X-Goog-Api-Key", _apiKey);
                request.Headers.Add("X-Goog-FieldMask",
                    "routes.distanceMeters,routes.duration,routes.staticDuration,routes.polyline.encodedPolyline,routes.description,routes.warnings");

                using var response = await _http.SendAsync(request, ct);
                var json = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                {
                    var detail = json.Length > 400 ? json[..400] : json;
                    _logger.LogWarning("[Google] Route {Status}: {Detail}", (int)response.StatusCode, detail);
                    return result;
                }
                return ParseRoutes(json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Google] Route request failed.");
                return result;
            }
        }

        /// <summary>Parses a computeRoutes response (public for unit tests).</summary>
        public static List<GoogleRoute> ParseRoutes(string json)
        {
            var list = new List<GoogleRoute>();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("routes", out var routes)) return list;

            foreach (var r in routes.EnumerateArray())
            {
                var distance = r.TryGetProperty("distanceMeters", out var d) ? d.GetDouble() : 0;
                var duration = ParseSeconds(r, "duration");
                var staticDuration = ParseSeconds(r, "staticDuration");
                if (distance <= 0 || duration <= 0) continue;
                if (staticDuration <= 0) staticDuration = duration;

                var encoded = r.TryGetProperty("polyline", out var pl) && pl.TryGetProperty("encodedPolyline", out var ep)
                    ? ep.GetString() ?? "" : "";

                var warnings = new List<string>();
                if (r.TryGetProperty("warnings", out var w) && w.ValueKind == JsonValueKind.Array)
                    foreach (var x in w.EnumerateArray())
                        if (x.GetString() is { Length: > 0 } s) warnings.Add(s);

                list.Add(new GoogleRoute
                {
                    DistanceKm = distance / 1000.0,
                    DurationMinutes = duration / 60.0,
                    StaticDurationMinutes = staticDuration / 60.0,
                    CongestionLevel = (float)Math.Clamp((1.0 - staticDuration / duration) * 100.0, 0, 100),
                    Description = r.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : "",
                    Points = Thin(DecodePolyline(encoded), 2000),
                    Warnings = warnings,
                });
            }
            return list;
        }

        /// <summary>Google encoded polyline → list of (lat, lng).</summary>
        public static List<(double Lat, double Lng)> DecodePolyline(string encoded)
        {
            var points = new List<(double, double)>();
            int index = 0, lat = 0, lng = 0;
            while (index < encoded.Length)
            {
                lat += NextValue(encoded, ref index);
                if (index >= encoded.Length) break;
                lng += NextValue(encoded, ref index);
                points.Add((lat / 1e5, lng / 1e5));
            }
            return points;
        }

        private static int NextValue(string s, ref int index)
        {
            int result = 0, shift = 0, b;
            do
            {
                b = s[index++] - 63;
                result |= (b & 0x1f) << shift;
                shift += 5;
            } while (b >= 0x20 && index < s.Length);
            return (result & 1) != 0 ? ~(result >> 1) : result >> 1;
        }

        /// <summary>Keeps at most <paramref name="max"/> points (always keeps the last one).</summary>
        private static List<(double Lat, double Lng)> Thin(List<(double Lat, double Lng)> pts, int max)
        {
            if (pts.Count <= max) return pts;
            var step = (double)(pts.Count - 1) / (max - 1);
            var outPts = new List<(double, double)>(max);
            for (var i = 0; i < max - 1; i++) outPts.Add(pts[(int)Math.Round(i * step)]);
            outPts.Add(pts[^1]);
            return outPts;
        }

        /// <summary>Routes API returns durations as strings such as "165s".</summary>
        private static double ParseSeconds(JsonElement route, string name)
        {
            if (!route.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return 0;
            var s = v.GetString()?.TrimEnd('s') ?? "";
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var sec) ? sec : 0;
        }
    }

    /// <summary>One A → B route from the Google Routes API.</summary>
    public class GoogleRoute
    {
        public double DistanceKm { get; set; }
        /// <summary>Travel time with current traffic.</summary>
        public double DurationMinutes { get; set; }
        /// <summary>Travel time without traffic.</summary>
        public double StaticDurationMinutes { get; set; }
        /// <summary>(1 − static / duration) × 100.</summary>
        public float CongestionLevel { get; set; }
        /// <summary>Google's short route name, e.g. "A1".</summary>
        public string Description { get; set; } = string.Empty;
        public List<(double Lat, double Lng)> Points { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
    }
}
