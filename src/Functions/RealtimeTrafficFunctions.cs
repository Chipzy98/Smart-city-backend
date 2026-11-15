using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using SmartcityBackend.src.Helpers;
using SmartcityBackend.src.Services;
using SmartcityBackend.src.Services.Interfaces;

namespace SmartcityBackend.src.Functions
{
    public class RealtimeTrafficFunctions
    {
        private readonly ILogger<RealtimeTrafficFunctions> _logger;
        private readonly TomTomTrafficService _tomTom;
        private readonly TrafficForecastService _forecast;
        private readonly ITokenService _tokenService;

        public RealtimeTrafficFunctions(
            ILogger<RealtimeTrafficFunctions> logger,
            TomTomTrafficService tomTom,
            TrafficForecastService forecast,
            ITokenService tokenService)
        {
            _logger = logger;
            _tomTom = tomTom;
            _forecast = forecast;
            _tokenService = tokenService;
        }

        /// <summary>
        /// GET /api/smartcity/traffic/realtime?refresh=true
        /// Latest live traffic for the monitored Sri Lanka points.
        /// Uses stored data if it is younger than 10 minutes (protects the TomTom quota);
        /// otherwise fetches TomTom, enriches with weather and saves to the dataset.
        /// refresh=false → never calls TomTom (used by the map auto-refresh).
        /// </summary>
        [Function(nameof(GetRealtimeTraffic))]
        public async Task<IActionResult> GetRealtimeTraffic(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "smartcity/traffic/realtime")]
            HttpRequest req)
        {
            var auth = await _tokenService.AuthorizeAsync(req, SmartCityTaskIds.SmartCityTraffic, SmartCityTaskActions.VIEW);
            if (!auth.Success) return AuthResults.Deny(auth);

            try
            {
                var refresh = !string.Equals(req.Query["refresh"].FirstOrDefault(), "false", StringComparison.OrdinalIgnoreCase);
                var (points, fresh) = await _tomTom.GetLatestAsync(refresh, auth.UserId!.Value, req.HttpContext.RequestAborted);

                // No live TomTom data (e.g. Sri Lanka is outside TomTom's real-time coverage)
                // → show model estimates for the current hour instead of an empty map.
                var estimated = false;
                string? estimateSource = null;
                if (points.Count == 0)
                {
                    (points, estimateSource) = await _forecast.EstimateNowAsync(req.HttpContext.RequestAborted);
                    estimated = points.Count > 0;
                }

                var result = points.Select(p => new
                {
                    locationName = p.LocationName,
                    latitude = p.Lat,
                    longitude = p.Lng,
                    currentSpeedKmh = Math.Round(p.CurrentSpeedKmh, 1),
                    freeFlowSpeedKmh = Math.Round(p.FreeFlowSpeedKmh, 1),
                    congestionLevel = Math.Round(p.CongestionLevel, 1),
                    congestionStatus = p.CongestionStatus,
                    confidence = Math.Round(p.Confidence, 2),
                    temperature = p.Temperature,
                    weather = p.Weather,
                    fetchedAt = p.FetchedAt,
                    estimated,
                });

                return new OkObjectResult(new
                {
                    success = true,
                    count = points.Count,
                    fresh,
                    estimated,
                    estimateSource,
                    provider = estimated ? null : points.FirstOrDefault()?.Provider,
                    tomTomConfigured = _tomTom.IsConfigured,
                    fetchedAt = points.Count > 0 ? points.Max(p => p.FetchedAt) : (DateTime?)null,
                    data = result,
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[RealtimeTraffic] Error");
                return new ObjectResult(new { error = "Failed to fetch real-time traffic." }) { StatusCode = 500 };
            }
        }

        /// <summary>
        /// GET /api/smartcity/traffic/realtime/point?lat=6.93&amp;lng=79.84
        /// Single point flow fetch — for map click real-time lookup.
        /// </summary>
        [Function(nameof(GetPointTraffic))]
        public async Task<IActionResult> GetPointTraffic(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "smartcity/traffic/realtime/point")]
            HttpRequest req)
        {
            var auth = await _tokenService.AuthorizeAsync(req, SmartCityTaskIds.SmartCityTraffic, SmartCityTaskActions.VIEW);
            if (!auth.Success) return AuthResults.Deny(auth);

            try
            {
                var lat = AuthResults.QueryDouble(req, "lat");
                var lng = AuthResults.QueryDouble(req, "lng");
                if (lat == null || lng == null)
                    return new BadRequestObjectResult(new { error = "lat and lng query params required." });

                var point = await _tomTom.FetchFlowAsync(lat.Value, lng.Value);

                if (point == null)
                    return new OkObjectResult(new { success = false, message = "No data for this location." });

                return new OkObjectResult(new
                {
                    success = true,
                    latitude = point.Lat,
                    longitude = point.Lng,
                    currentSpeedKmh = Math.Round(point.CurrentSpeedKmh, 1),
                    freeFlowSpeedKmh = Math.Round(point.FreeFlowSpeedKmh, 1),
                    congestionLevel = Math.Round(point.CongestionLevel, 1),
                    congestionStatus = point.CongestionStatus,
                    confidence = Math.Round(point.Confidence, 2),
                    fetchedAt = point.FetchedAt,
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[PointTraffic] Error");
                return new ObjectResult(new { error = "Failed to fetch point traffic." }) { StatusCode = 500 };
            }
        }
    }
}
