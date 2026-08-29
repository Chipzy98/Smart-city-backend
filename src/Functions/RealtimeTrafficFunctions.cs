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
        private readonly ITokenService _tokenService;

        public RealtimeTrafficFunctions(
            ILogger<RealtimeTrafficFunctions> logger,
            TomTomTrafficService tomTom,
            ITokenService tokenService)
        {
            _logger = logger;
            _tomTom = tomTom;
            _tokenService = tokenService;
        }

        /// <summary>
        /// GET /api/smartcity/traffic/realtime
        /// Returns live TomTom traffic data for all Sri Lanka key points.
        /// Also saves records to DB automatically.
        /// </summary>
        [Function(nameof(GetRealtimeTraffic))]
        public async Task<IActionResult> GetRealtimeTraffic(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get",
                Route = "smartcity/traffic/realtime")]
            HttpRequest req)
        {
            try
            {
                // Auth optional — skip if no token (allows direct browser testing)
                var token = Utility.GetAuthToken(req);
                var userId = Guid.Empty;

                if (!string.IsNullOrWhiteSpace(token))
                {
                    var auth = await _tokenService.IsAuthorizedAsync(
                        token, SmartCityTaskIds.SmartCityTraffic, SmartCityTaskActions.VIEW);
                    if (auth.Success)
                        userId = _tokenService.GetUserId(auth.Data) ?? Guid.Empty;
                }

                _logger.LogInformation("[RealtimeTraffic] Fetching TomTom data…");

                var points = await _tomTom.FetchAndSaveAsync(userId);

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
                    fetchedAt = p.FetchedAt,
                });

                return new OkObjectResult(new
                {
                    success = true,
                    count = points.Count,
                    fetchedAt = DateTime.UtcNow,
                    data = result,
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[RealtimeTraffic] Error");
                return new ObjectResult(new { error = "Failed to fetch real-time traffic." })
                { StatusCode = 500 };
            }
        }

        /// <summary>
        /// GET /api/smartcity/traffic/realtime/point?lat=6.93&lng=79.84
        /// Single point flow fetch — for map click real-time lookup.
        /// </summary>
        [Function(nameof(GetPointTraffic))]
        public async Task<IActionResult> GetPointTraffic(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get",
                Route = "smartcity/traffic/realtime/point")]
            HttpRequest req)
        {
            try
            {
                if (!double.TryParse(req.Query["lat"], out var lat) ||
                    !double.TryParse(req.Query["lng"], out var lng))
                    return new BadRequestObjectResult("lat and lng query params required.");

                var point = await _tomTom.FetchFlowAsync(lat, lng);

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
                return new ObjectResult(new { error = "Failed to fetch point traffic." })
                { StatusCode = 500 };
            }
        }
    }
}