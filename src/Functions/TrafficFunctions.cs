using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using SmartcityBackend.src.Helpers;
using SmartcityBackend.src.Services;
using SmartcityBackend.src.Services.Interfaces;
using SmartcityBackend.src.ViewModels;

namespace SmartcityBackend.src.Functions
{
    public class TrafficFunctions
    {
        private readonly ILogger<TrafficFunctions> _logger;
        private readonly ITrafficService _trafficService;
        private readonly TrafficForecastService _forecastService;
        private readonly ITokenService _tokenService;

        public TrafficFunctions(
            ILogger<TrafficFunctions> logger,
            ITrafficService trafficService,
            TrafficForecastService forecastService,
            ITokenService tokenService)
        {
            _logger = logger;
            _trafficService = trafficService;
            _forecastService = forecastService;
            _tokenService = tokenService;
        }

        // GET /api/smartcity/traffic/summary?q=&page=1&pagesize=10
        [Function(nameof(GetTrafficSummary))]
        public async Task<IActionResult> GetTrafficSummary(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "smartcity/traffic/summary")]
            HttpRequest req)
        {
            var auth = await _tokenService.AuthorizeAsync(req, SmartCityTaskIds.SmartCityTraffic, SmartCityTaskActions.VIEW);
            if (!auth.Success) return AuthResults.Deny(auth);

            var data = await _trafficService.GetTrafficSummary(
                auth.UserId!.Value,
                AuthResults.QueryInt(req, "status", 0),
                req.Query["q"].FirstOrDefault() ?? "",
                AuthResults.QueryInt(req, "page", 1, 1),
                AuthResults.QueryInt(req, "pagesize", 10, 1, 100));
            return new OkObjectResult(data);
        }

        // POST /api/smartcity/traffic/predict   ("what-if" scenario)
        [Function(nameof(PredictTraffic))]
        public async Task<IActionResult> PredictTraffic(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "smartcity/traffic/predict")]
            HttpRequest req)
        {
            var auth = await _tokenService.AuthorizeAsync(req, SmartCityTaskIds.SmartCityTraffic, SmartCityTaskActions.CREATE);
            if (!auth.Success) return AuthResults.Deny(auth);

            var body = await new StreamReader(req.Body).ReadToEndAsync();
            var request = JsonConvert.DeserializeObject<TrafficPredictionRequestVM>(body);
            if (request == null) return new BadRequestObjectResult(new { error = "Invalid request body." });

            var result = await _trafficService.PredictTrafficAsync(request);
            return new OkObjectResult(result);
        }

        // POST /api/smartcity/traffic/forecast   (hybrid ML hourly forecast for a location)
        [Function(nameof(ForecastTraffic))]
        public async Task<IActionResult> ForecastTraffic(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "smartcity/traffic/forecast")]
            HttpRequest req)
        {
            var auth = await _tokenService.AuthorizeAsync(req, SmartCityTaskIds.SmartCityTraffic, SmartCityTaskActions.CREATE);
            if (!auth.Success) return AuthResults.Deny(auth);

            var body = await new StreamReader(req.Body).ReadToEndAsync();
            var request = JsonConvert.DeserializeObject<TrafficForecastRequestVM>(body);
            if (request == null) return new BadRequestObjectResult(new { error = "Invalid request body." });

            if (request.Latitude is < 5.5 or > 10.1 || request.Longitude is < 79.3 or > 82.2)
                return new BadRequestObjectResult(new { error = "Please choose a location inside Sri Lanka." });

            try
            {
                var result = await _forecastService.ForecastAsync(auth.UserId!.Value, request, req.HttpContext.RequestAborted);
                return new OkObjectResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[TrafficForecast] Error");
                return new ObjectResult(new { error = "Traffic forecast failed." }) { StatusCode = 500 };
            }
        }

        // POST /api/smartcity/traffic/route
        [Function(nameof(AnalyseRoute))]
        public async Task<IActionResult> AnalyseRoute(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "smartcity/traffic/route")]
            HttpRequest req)
        {
            var auth = await _tokenService.AuthorizeAsync(req, SmartCityTaskIds.SmartCityTraffic, SmartCityTaskActions.CREATE);
            if (!auth.Success) return AuthResults.Deny(auth);

            var body = await new StreamReader(req.Body).ReadToEndAsync();
            var request = JsonConvert.DeserializeObject<RouteTrafficRequestVM>(body);
            if (request == null) return new BadRequestObjectResult(new { error = "Invalid request body." });

            if (string.IsNullOrWhiteSpace(request.OriginName) ||
                string.IsNullOrWhiteSpace(request.DestinationName))
                return new BadRequestObjectResult(new { error = "Origin and destination names are required." });

            var result = await _trafficService.AnalyseRouteAsync(auth.UserId!.Value, request);
            return new OkObjectResult(result);
        }

        // GET /api/smartcity/traffic/route/live?oLat=&oLng=&dLat=&dLng=
        // Map route planner: real road path + traffic-aware time from Google (404 → frontend uses OSRM).
        [Function(nameof(GetLiveRoute))]
        public async Task<IActionResult> GetLiveRoute(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "smartcity/traffic/route/live")]
            HttpRequest req)
        {
            var auth = await _tokenService.AuthorizeAsync(req, SmartCityTaskIds.SmartCityTraffic, SmartCityTaskActions.VIEW);
            if (!auth.Success) return AuthResults.Deny(auth);

            static double? D(HttpRequest r, string k) =>
                double.TryParse(r.Query[k].FirstOrDefault(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;

            var oLat = D(req, "oLat"); var oLng = D(req, "oLng"); var dLat = D(req, "dLat"); var dLng = D(req, "dLng");
            if (oLat == null || oLng == null || dLat == null || dLng == null)
                return new BadRequestObjectResult(new { error = "oLat, oLng, dLat and dLng are required." });

            var route = await _trafficService.GetLiveRouteAsync(oLat.Value, oLng.Value, dLat.Value, dLng.Value);
            return route == null
                ? new NotFoundObjectResult(new { error = "Live route unavailable (Google not configured, daily limit reached or no route)." })
                : new OkObjectResult(route);
        }

        // GET /api/smartcity/traffic/route/history?page=1&pagesize=10
        [Function(nameof(GetRouteHistory))]
        public async Task<IActionResult> GetRouteHistory(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "smartcity/traffic/route/history")]
            HttpRequest req)
        {
            var auth = await _tokenService.AuthorizeAsync(req, SmartCityTaskIds.SmartCityTraffic, SmartCityTaskActions.VIEW);
            if (!auth.Success) return AuthResults.Deny(auth);

            var data = await _trafficService.GetMyRouteHistoryAsync(
                auth.UserId!.Value,
                AuthResults.QueryInt(req, "page", 1, 1),
                AuthResults.QueryInt(req, "pagesize", 10, 1, 100));
            return new OkObjectResult(data);
        }
    }
}
