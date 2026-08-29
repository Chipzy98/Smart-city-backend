using Microsoft.Extensions.Logging;
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
        private const int MaxTokens = 400;
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

            // Haversine distance estimate (km) for context
            var distEst = HaversineKm(
                input.OriginLat, input.OriginLng,
                input.DestinationLat, input.DestinationLng);

            var isRushHour = (input.Hour is >= 7 and <= 9) || (input.Hour is >= 17 and <= 19);

            var prompt =
                "You are a smart city route traffic analysis AI for Sri Lanka.\n" +
                "Analyse the traffic conditions between the two locations and return ONLY JSON.\n\n" +
                "=== Route ===\n" +
                $"Origin      : {input.OriginName} ({input.OriginLat:F4}, {input.OriginLng:F4})\n" +
                $"Destination : {input.DestinationName} ({input.DestinationLat:F4}, {input.DestinationLng:F4})\n" +
                $"Est. Distance: ~{distEst:F1} km (straight line — adjust for actual roads)\n\n" +
                "=== Conditions ===\n" +
                $"Time        : {input.Hour:00}:00  |  Day: {dayLabel}  |  Rush hour: {(isRushHour ? "Yes" : "No")}\n" +
                $"Weather     : {weatherLabel}\n\n" +
                "=== Instructions ===\n" +
                "1. distanceKm      — actual road distance in km (use Sri Lanka road knowledge)\n" +
                "2. estimatedMinutes— realistic travel time including traffic\n" +
                "3. congestionLevel — 0 (clear) to 100 (gridlock) for this route right now\n" +
                "4. congestionStatus— exactly one of: Low Congestion / Moderate Congestion / High Congestion\n" +
                "5. suggestion      — one specific practical tip for this exact route (mention road names if known)\n" +
                "6. riskScore       — 0-100 overall journey risk (weather + congestion + time of day)\n\n" +
                "Reply ONLY with this JSON (no markdown):\n" +
                "{\n" +
                "  \"distanceKm\"      : <float>,\n" +
                "  \"estimatedMinutes\": <int>,\n" +
                "  \"congestionLevel\" : <float 0-100>,\n" +
                "  \"congestionStatus\": \"<status>\",\n" +
                "  \"suggestion\"      : \"<route tip>\",\n" +
                "  \"riskScore\"       : <float 0-100>\n" +
                "}";

            for (var attempt = 0; attempt <= RetryCount; attempt++)
            {
                try
                {
                    var text = await CallClaudeAsync(prompt, MaxTokens, ct);
                    using var doc = JsonDocument.Parse(CleanJson(text));
                    var root = doc.RootElement;

                    var result = new RouteTrafficAIOutput
                    {
                        DistanceKm = Math.Max(0, root.GetProperty("distanceKm").GetSingle()),
                        EstimatedMinutes = Math.Max(1, root.GetProperty("estimatedMinutes").GetInt32()),
                        CongestionLevel = Math.Clamp(root.GetProperty("congestionLevel").GetSingle(), 0, 100),
                        CongestionStatus = root.GetProperty("congestionStatus").GetString() ?? "Moderate Congestion",
                        Suggestion = root.GetProperty("suggestion").GetString() ?? "Drive safely.",
                        RiskScore = Math.Clamp(root.GetProperty("riskScore").GetSingle(), 0, 100),
                    };

                    _logger.LogInformation(
                        "[RouteAI] {Origin}→{Dest}: {Dist}km, {Min}min, congestion={Level}",
                        input.OriginName, input.DestinationName,
                        result.DistanceKm, result.EstimatedMinutes, result.CongestionLevel);

                    return result;
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

        // ── Haversine ─────────────────────────────────────────────────────────
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

        // ── Fallback ──────────────────────────────────────────────────────────
        private static RouteTrafficAIOutput FallbackRoute(RouteTrafficAIInput input, double distKm)
        {
            var roadDist = distKm * 1.35; // road factor
            var isRushHour = (input.Hour is >= 7 and <= 9) || (input.Hour is >= 17 and <= 19);
            var congestion = (float)Math.Clamp(
                (isRushHour ? 35 : 15) + (input.Weather * 8), 0, 100);
            var speedKmh = congestion >= 70 ? 25 : congestion >= 40 ? 45 : 65;
            var minutes = (int)(roadDist / speedKmh * 60);

            return new RouteTrafficAIOutput
            {
                DistanceKm = (float)roadDist,
                EstimatedMinutes = minutes,
                CongestionLevel = congestion,
                CongestionStatus = congestion >= 75 ? "High Congestion"
                                 : congestion >= 40 ? "Moderate Congestion"
                                 : "Low Congestion",
                Suggestion = "Check live traffic before departing.",
                RiskScore = Math.Clamp(congestion * 0.7f + input.Weather * 8, 0, 100),
            };
        }

        // ── Claude API ────────────────────────────────────────────────────────
        private async Task<string> CallClaudeAsync(string prompt, int maxTokens, CancellationToken ct)
        {
            var body = JsonSerializer.Serialize(new
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