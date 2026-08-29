using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using SmartcityBackend.src.AI.Interfaces;
using SmartcityBackend.src.AI.Models;
using System.Text;
using System.Text.Json;

namespace SmartcityBackend.src.AI.Services
{
    public class RouteTrafficAIService : IRouteTrafficAIService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<RouteTrafficAIService> _logger;
        private readonly string _apiKey;

        private const string ApiUrl = "https://api.anthropic.com/v1/messages";
        private const string Model = "claude-sonnet-4-6";
        private const string ApiVersion = "2023-06-01";
        private const int MaxTokens = 1200;
        private const int RetryCount = 2;

        public RouteTrafficAIService(
            IHttpClientFactory httpClientFactory,
            ILogger<RouteTrafficAIService> logger)
        {
            _httpClient = httpClientFactory.CreateClient("Anthropic");
            _logger = logger;
            _apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
                          ?? throw new InvalidOperationException("ANTHROPIC_API_KEY not set.");
        }

        public async Task<RouteTrafficAIOutput> AnalyseRouteAsync(
            RouteTrafficAIInput input, CancellationToken ct = default)
        {
            var weatherLabel = input.Weather switch
            {
                0 => "Clear",
                1 => "Cloudy",
                2 => "Rainy",
                3 => "Stormy",
                _ => "Unknown"
            };
            var dayLabel = input.DayOfWeek switch
            {
                0 => "Sunday",
                1 => "Monday",
                2 => "Tuesday",
                3 => "Wednesday",
                4 => "Thursday",
                5 => "Friday",
                6 => "Saturday",
                _ => "Weekday"
            };
            var isRushHour = (input.Hour is >= 7 and <= 9) || (input.Hour is >= 17 and <= 19);
            var distEst = HaversineKm(input.OriginLat, input.OriginLng,
                                          input.DestinationLat, input.DestinationLng);

            var prompt =
                "You are a Sri Lanka smart city route traffic AI.\n" +
                "Analyse the route and suggest 2-3 alternative road options.\n" +
                "Use your knowledge of Sri Lanka roads (A1, A6, E01 Southern Expressway, Kandy Road, etc.).\n\n" +
                "=== Journey ===\n" +
                $"From : {input.OriginName} ({input.OriginLat:F4}, {input.OriginLng:F4})\n" +
                $"To   : {input.DestinationName} ({input.DestinationLat:F4}, {input.DestinationLng:F4})\n" +
                $"Straight-line distance: ~{distEst:F1} km\n\n" +
                "=== Conditions ===\n" +
                $"Time: {input.Hour:00}:00 | Day: {dayLabel} | Rush hour: {(isRushHour ? "Yes" : "No")} | Weather: {weatherLabel}\n\n" +
                "=== Instructions ===\n" +
                "1. Suggest 2-3 REAL alternative routes using actual Sri Lanka road names.\n" +
                "2. For each route provide approximate waypoints (lat/lng) along the road path — minimum 4 points, maximum 8 points.\n" +
                "3. Mark the best route as isRecommended: true.\n" +
                "4. Estimate real road distances and times (not straight-line).\n" +
                "5. Consider current conditions for congestion levels.\n\n" +
                "Reply ONLY with this JSON (no markdown, no extra text):\n" +
                "{\n" +
                "  \"summary\": \"<one sentence about current traffic conditions>\",\n" +
                "  \"routes\": [\n" +
                "    {\n" +
                "      \"routeName\": \"<short name eg: Via E01 Expressway>\",\n" +
                "      \"roadNames\": \"<road names eg: E01 → A1 → Peradeniya Road>\",\n" +
                "      \"distanceKm\": <float>,\n" +
                "      \"estimatedMinutes\": <int>,\n" +
                "      \"congestionLevel\": <float 0-100>,\n" +
                "      \"congestionStatus\": \"<Low Congestion|Moderate Congestion|High Congestion>\",\n" +
                "      \"riskScore\": <float 0-100>,\n" +
                "      \"reason\": \"<why choose or avoid this route>\",\n" +
                "      \"isRecommended\": <true|false>,\n" +
                "      \"waypoints\": [\n" +
                "        {\"lat\": <float>, \"lng\": <float>},\n" +
                "        ...\n" +
                "      ]\n" +
                "    }\n" +
                "  ]\n" +
                "}";

            for (var attempt = 0; attempt <= RetryCount; attempt++)
            {
                try
                {
                    var text = await CallClaudeAsync(prompt, MaxTokens, ct);
                    var raw = JsonConvert.DeserializeObject<dynamic>(CleanJson(text));
                    if (raw == null) throw new Exception("Null response");

                    var routes = new List<RouteOption>();

                    foreach (var r in raw.routes)
                    {
                        var waypoints = new List<RouteWaypoint>();
                        if (r.waypoints != null)
                        {
                            foreach (var w in r.waypoints)
                                waypoints.Add(new RouteWaypoint
                                {
                                    Lat = (double)w.lat,
                                    Lng = (double)w.lng
                                });
                        }

                        routes.Add(new RouteOption
                        {
                            RouteName = r.routeName != null ? (string)r.routeName : "",
                            RoadNames = r.roadNames != null ? (string)r.roadNames : "",
                            DistanceKm = r.distanceKm != null ? (float)r.distanceKm : 0f,
                            EstimatedMinutes = r.estimatedMinutes != null ? (int)r.estimatedMinutes : 0,
                            CongestionLevel = r.congestionLevel != null ? (float)r.congestionLevel : 0f,
                            CongestionStatus = r.congestionStatus != null ? (string)r.congestionStatus : "",
                            RiskScore = r.riskScore != null ? (float)r.riskScore : 0f,
                            Reason = r.reason != null ? (string)r.reason : "",
                            IsRecommended = r.isRecommended != null ? (bool)r.isRecommended : false,
                            Waypoints = waypoints,
                        });
                    }

                    var best = routes.FirstOrDefault(r => r.IsRecommended)
                               ?? routes.OrderBy(r => r.CongestionLevel).FirstOrDefault()
                               ?? new RouteOption();

                    _logger.LogInformation(
                        "[RouteAI] {Origin}→{Dest}: {Count} routes, best={Best}",
                        input.OriginName, input.DestinationName, routes.Count, best.RouteName);

                    return new RouteTrafficAIOutput
                    {
                        Routes = routes,
                        Summary = (string)raw.summary ?? "",
                        DistanceKm = best.DistanceKm,
                        EstimatedMinutes = best.EstimatedMinutes,
                        CongestionLevel = best.CongestionLevel,
                        CongestionStatus = best.CongestionStatus,
                        Suggestion = best.Reason,
                        RiskScore = best.RiskScore,
                    };
                }
                catch (Exception ex) when (attempt < RetryCount)
                {
                    _logger.LogWarning(ex, "[RouteAI] Attempt {A} failed, retrying…", attempt + 1);
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[RouteAI] All attempts failed — fallback.");
                }
            }

            return FallbackRoute(input, distEst);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
        {
            const double R = 6371;
            var dLat = (lat2 - lat1) * Math.PI / 180;
            var dLon = (lon2 - lon1) * Math.PI / 180;
            var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                     + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180)
                     * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        }

        private static RouteTrafficAIOutput FallbackRoute(RouteTrafficAIInput input, double distKm)
        {
            var roadDist = distKm * 1.35;
            var isRushHour = (input.Hour is >= 7 and <= 9) || (input.Hour is >= 17 and <= 19);
            var congestion = (float)Math.Clamp((isRushHour ? 35 : 15) + (input.Weather * 8), 0, 100);
            var speedKmh = congestion >= 70 ? 25 : congestion >= 40 ? 45 : 65;
            var minutes = (int)(roadDist / speedKmh * 60);
            var status = congestion >= 75 ? "High Congestion"
                           : congestion >= 40 ? "Moderate Congestion" : "Low Congestion";

            var midLat = (input.OriginLat + input.DestinationLat) / 2;
            var midLng = (input.OriginLng + input.DestinationLng) / 2;

            var route = new RouteOption
            {
                RouteName = "Main Route",
                RoadNames = "Main Road",
                DistanceKm = (float)roadDist,
                EstimatedMinutes = minutes,
                CongestionLevel = congestion,
                CongestionStatus = status,
                RiskScore = Math.Clamp(congestion * 0.7f + input.Weather * 8, 0, 100),
                Reason = "Standard route — check live traffic before departing.",
                IsRecommended = true,
                Waypoints =
                [
                    new() { Lat = input.OriginLat,      Lng = input.OriginLng },
                    new() { Lat = midLat,               Lng = midLng },
                    new() { Lat = input.DestinationLat, Lng = input.DestinationLng },
                ],
            };

            return new RouteTrafficAIOutput
            {
                Routes = [route],
                Summary = "Route analysis based on estimated conditions.",
                DistanceKm = route.DistanceKm,
                EstimatedMinutes = route.EstimatedMinutes,
                CongestionLevel = route.CongestionLevel,
                CongestionStatus = route.CongestionStatus,
                Suggestion = route.Reason,
                RiskScore = route.RiskScore,
            };
        }

        private async Task<string> CallClaudeAsync(string prompt, int maxTokens, CancellationToken ct)
        {
            var body = System.Text.Json.JsonSerializer.Serialize(new
            {
                model = Model,
                max_tokens = maxTokens,
                messages = new[] { new { role = "user", content = prompt } }
            });

            using var req = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
            req.Headers.Add("x-api-key", _apiKey);
            req.Headers.Add("anthropic-version", ApiVersion);
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");

            var res = await _httpClient.SendAsync(req, ct);
            res.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            return doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString() ?? "{}";
        }

        private static string CleanJson(string text)
        {
            text = text.Trim();
            if (text.StartsWith("```")) { var l = text.Split('\n', 2); text = l.Length > 1 ? l[1] : text; }
            if (text.EndsWith("```")) text = text[..text.LastIndexOf("```")];
            return text.Trim();
        }
    }
}