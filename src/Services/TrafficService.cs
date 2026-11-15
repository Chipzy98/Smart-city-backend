using Microsoft.EntityFrameworkCore;
using SmartcityBackend.src.AI.Interfaces;
using SmartcityBackend.src.AI.Models;
using SmartcityBackend.src.AI.Services;
using SmartcityBackend.src.Data;
using SmartcityBackend.src.Entities;
using SmartcityBackend.src.Helpers;
using SmartcityBackend.src.Services.Interfaces;
using SmartcityBackend.src.ViewModels;

namespace SmartcityBackend.src.Services
{
    public class TrafficService : ITrafficService
    {
        private readonly SmartCityDbContext _db;
        private readonly ITrafficAIService _trafficAI;
        private readonly IRouteTrafficAIService _routeAI;
        private readonly TrafficMLService _ml;

        // Default location for "what-if" predictions (Colombo Fort)
        private const double DefaultLat = 6.9344;
        private const double DefaultLng = 79.8428;

        public TrafficService(
            SmartCityDbContext db,
            ITrafficAIService trafficAI,
            IRouteTrafficAIService routeAI,
            TrafficMLService ml,
            GoogleTrafficService google)
        {
            _db = db;
            _trafficAI = trafficAI;
            _routeAI = routeAI;
            _ml = ml;
            _google = google;
        }

        private readonly GoogleTrafficService _google;

        /// <summary>
        /// Route options from Google Routes (real roads, traffic-aware time — same as Google Maps).
        /// Null when Google is not configured or returned nothing → caller falls back to the AI route.
        /// </summary>
        private async Task<RouteTrafficAIOutput?> GoogleRoutesAsync(RouteTrafficRequestVM r, int weather)
        {
            var routes = await _google.ComputeRoutesAsync(r.OriginLat, r.OriginLng, r.DestinationLat, r.DestinationLng, alternatives: true);
            if (routes.Count == 0) return null;

            var ordered = routes.OrderBy(x => x.DurationMinutes).ToList();
            var options = ordered.Select((g, i) =>
            {
                var minutes = (int)Math.Round(g.DurationMinutes);
                var freeMinutes = (int)Math.Round(g.StaticDurationMinutes);
                var delay = Math.Max(0, minutes - freeMinutes);
                var status = TrafficLevels.Status(g.CongestionLevel);
                var name = string.IsNullOrWhiteSpace(g.Description) ? $"Route {i + 1}" : $"Via {g.Description}";
                var reason = i == 0
                    ? $"Fastest now: {FormatMinutes(minutes)} with traffic ({FormatMinutes(freeMinutes)} without, +{delay} min delay)."
                    : $"{FormatMinutes(minutes)} with traffic — {minutes - (int)Math.Round(ordered[0].DurationMinutes)} min slower than the fastest route.";
                if (g.Warnings.Count > 0) reason += " " + string.Join(" ", g.Warnings);

                return new RouteOption
                {
                    RouteName = name,
                    RoadNames = string.IsNullOrWhiteSpace(g.Description) ? "Google route" : g.Description,
                    DistanceKm = (float)g.DistanceKm,
                    EstimatedMinutes = minutes,
                    CongestionLevel = g.CongestionLevel,
                    CongestionStatus = status,
                    RiskScore = Math.Clamp(g.CongestionLevel * 0.7f + weather * 8, 0, 100),
                    Reason = reason,
                    IsRecommended = i == 0,
                    Waypoints = g.Points.Select(p => new RouteWaypoint { Lat = p.Lat, Lng = p.Lng }).ToList(),
                };
            }).ToList();

            var best = options[0];
            return new RouteTrafficAIOutput
            {
                Routes = options,
                Summary = $"Live Google traffic: {options.Count} route(s) found. Fastest is {best.RouteName.ToLowerInvariant()} — " +
                          $"{best.DistanceKm:F0} km, {FormatMinutes(best.EstimatedMinutes)} ({best.CongestionStatus.ToLowerInvariant()}).",
                DistanceKm = best.DistanceKm,
                EstimatedMinutes = best.EstimatedMinutes,
                CongestionLevel = best.CongestionLevel,
                CongestionStatus = best.CongestionStatus,
                Suggestion = best.Reason,
                RiskScore = best.RiskScore,
            };
        }

        private static string FormatMinutes(int m) => m >= 60 ? $"{m / 60} h {m % 60} min" : $"{m} min";

        /// <summary>Best single route for the map's route planner (no DB write).</summary>
        public async Task<object?> GetLiveRouteAsync(double oLat, double oLng, double dLat, double dLng)
        {
            var routes = await _google.ComputeRoutesAsync(oLat, oLng, dLat, dLng, alternatives: false);
            var g = routes.OrderBy(x => x.DurationMinutes).FirstOrDefault();
            if (g == null) return null;
            return new
            {
                source = "google",
                distanceKm = Math.Round(g.DistanceKm, 1),
                durationMinutes = (int)Math.Round(g.DurationMinutes),
                durationWithoutTrafficMinutes = (int)Math.Round(g.StaticDurationMinutes),
                congestionLevel = Math.Round(g.CongestionLevel, 1),
                congestionStatus = TrafficLevels.Status(g.CongestionLevel),
                description = g.Description,
                coordinates = g.Points.Select(p => new[] { p.Lng, p.Lat }),   // GeoJSON order
            };
        }

        // ── Live traffic summary ──────────────────────────────────────────────
        public async Task<object> GetTrafficSummary(
            Guid userId, int status, string q, int page, int pageSize)
        {
            // CSV training rows are not live observations — hide them from the live list
            var query = _db.SmartTrafficData.Where(x => x.IsActive && x.Source != "csv").AsQueryable();

            if (!string.IsNullOrWhiteSpace(q))
                query = query.Where(x =>
                    (x.LocationName != null && x.LocationName.Contains(q)) ||
                    x.Latitude.ToString().Contains(q) ||
                    x.Longitude.ToString().Contains(q));

            var totalCount = await query.CountAsync();
            var data = await query
                .OrderByDescending(x => x.RecordedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(x => new
                {
                    x.TrafficDataId,
                    x.LocationName,
                    x.Latitude,
                    x.Longitude,
                    x.CongestionLevel,
                    x.CurrentSpeedKmh,
                    x.Source,
                    x.RecordedAt
                })
                .ToListAsync();

            return new { totalCount, page, pageSize, data };
        }

        // ── "What-if" prediction (manual inputs) ──────────────────────────────
        // Uses the trained ML.NET model when available, otherwise the Claude prompt.
        public async Task<object> PredictTrafficAsync(TrafficPredictionRequestVM request)
        {
            var hour = Math.Clamp((int)request.Hour, 0, 23);
            var day = Math.Clamp((int)request.Day, 0, 6);
            var weather = Math.Clamp((int)request.Weather, 0, 3);

            if (_ml.IsTrained)
            {
                var input = TrafficModelInput.Create(
                    hour, day, weather,
                    request.Temperature ?? 28f,
                    weather >= 2 ? 5f : 0f,
                    request.Latitude ?? DefaultLat,
                    request.Longitude ?? DefaultLng,
                    50f);

                var level = _ml.Predict(input);
                if (level != null)
                {
                    var metrics = _ml.Metrics;
                    return new
                    {
                        predictedCongestionLevel = Math.Round(level.Value, 2),
                        status = TrafficLevels.Status(level.Value),
                        suggestion = level >= 75 ? "Heavy traffic expected — consider an alternate route or another time."
                                   : level >= 40 ? "Moderate traffic expected — allow extra travel time."
                                   : "Roads should be clear at this time.",
                        modelUsed = "ml",
                        modelMae = metrics?.MeanAbsoluteError,
                        modelR2 = metrics?.RSquared,
                    };
                }
            }

            var aiResult = await _trafficAI.PredictAsync(new TrafficAIInput
            {
                Hour = hour,
                Day = day,
                Weather = weather,
                VehicleCount = request.VehicleCount,
            });

            return new
            {
                predictedCongestionLevel = Math.Round(aiResult.PredictedCongestionLevel, 2),
                status = aiResult.Status,
                suggestion = aiResult.Suggestion,
                modelUsed = "claude",
                modelMae = (double?)null,
                modelR2 = (double?)null,
            };
        }

        // ── Route A → B analysis with multiple route suggestions ──────────────
        public async Task<object> AnalyseRouteAsync(Guid userId, RouteTrafficRequestVM request)
        {
            // Sri Lanka local time — rush hour logic must not use UTC
            var now = AppTime.NowLocal();

            // 1) Google Routes — real roads + live traffic (same times as Google Maps)
            // 2) otherwise the Claude / rule-based route estimate
            var googleResult = await GoogleRoutesAsync(request, request.Weather);
            var routeSource = googleResult != null ? "google" : "ai";
            var aiResult = googleResult ?? await _routeAI.AnalyseRouteAsync(new RouteTrafficAIInput
            {
                OriginName = request.OriginName,
                OriginLat = request.OriginLat,
                OriginLng = request.OriginLng,
                DestinationName = request.DestinationName,
                DestinationLat = request.DestinationLat,
                DestinationLng = request.DestinationLng,
                Hour = now.Hour,
                DayOfWeek = (int)now.DayOfWeek,
                Weather = request.Weather,
            });

            // Save best route to DB
            var record = new SmartRouteTraffic
            {
                RouteTrafficId = Guid.NewGuid(),
                UserId = userId,
                OriginName = request.OriginName,
                OriginLat = (decimal)request.OriginLat,
                OriginLng = (decimal)request.OriginLng,
                DestinationName = request.DestinationName,
                DestinationLat = (decimal)request.DestinationLat,
                DestinationLng = (decimal)request.DestinationLng,
                DistanceKm = aiResult.DistanceKm,
                EstimatedMinutes = aiResult.EstimatedMinutes,
                CongestionLevel = aiResult.CongestionLevel,
                CongestionStatus = aiResult.CongestionStatus,
                Suggestion = aiResult.Suggestion,
                RiskScore = aiResult.RiskScore,
                Hour = now.Hour,
                DayOfWeek = (int)now.DayOfWeek,
                Weather = request.Weather,
                IsActive = true,
                CreatedDateTime = DateTime.UtcNow,
            };

            _db.SmartRouteTraffic.Add(record);
            await _db.SaveChangesAsync();

            // ── Build response — includes routes[] with waypoints for map lines ──
            var routesPayload = aiResult.Routes.Select(r => new
            {
                routeName = r.RouteName,
                roadNames = r.RoadNames,
                distanceKm = Math.Round(r.DistanceKm, 1),
                estimatedMinutes = r.EstimatedMinutes,
                congestionLevel = Math.Round(r.CongestionLevel, 1),
                congestionStatus = r.CongestionStatus,
                riskScore = Math.Round(r.RiskScore, 1),
                reason = r.Reason,
                isRecommended = r.IsRecommended,
                waypoints = r.Waypoints.Select(w => new { lat = w.Lat, lng = w.Lng }),
            }).ToList();

            return new
            {
                routeTrafficId = record.RouteTrafficId,
                originName = record.OriginName,
                destinationName = record.DestinationName,
                summary = aiResult.Summary,
                // "google" = real roads + live traffic; "ai" = Claude / rule estimate
                source = routeSource,

                // Best route summary
                distanceKm = Math.Round(aiResult.DistanceKm, 1),
                estimatedMinutes = aiResult.EstimatedMinutes,
                congestionLevel = Math.Round(aiResult.CongestionLevel, 1),
                congestionStatus = aiResult.CongestionStatus,
                suggestion = aiResult.Suggestion,
                riskScore = Math.Round(aiResult.RiskScore, 1),

                // All route options with waypoints → frontend draws lines on map
                routes = routesPayload,
            };
        }

        // ── Route history ─────────────────────────────────────────────────────
        public async Task<object> GetMyRouteHistoryAsync(Guid userId, int page, int pageSize)
        {
            var query = _db.SmartRouteTraffic
                .Where(x => x.IsActive && x.UserId == userId)
                .OrderByDescending(x => x.CreatedDateTime);

            var totalCount = await query.CountAsync();
            var data = await query
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(x => new
                {
                    x.RouteTrafficId,
                    x.OriginName,
                    x.DestinationName,
                    x.DistanceKm,
                    x.EstimatedMinutes,
                    x.CongestionLevel,
                    x.CongestionStatus,
                    x.RiskScore,
                    x.CreatedDateTime,
                })
                .ToListAsync();

            // Stored as UTC → mark Kind so JSON carries "Z" and the browser shows local time
            var items = data.Select(x => new
            {
                x.RouteTrafficId, x.OriginName, x.DestinationName, x.DistanceKm, x.EstimatedMinutes,
                x.CongestionLevel, x.CongestionStatus, x.RiskScore,
                CreatedDateTime = DateTime.SpecifyKind(x.CreatedDateTime, DateTimeKind.Utc),
            }).ToList();

            return new { totalCount, page, pageSize, data = items };
        }
    }
}