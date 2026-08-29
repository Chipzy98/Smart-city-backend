using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using SmartcityBackend.src.Helpers;
using SmartcityBackend.src.Services.Interfaces;
using SmartcityBackend.src.ViewModels;

namespace SmartcityBackend.src.Functions
{
    public class TrafficFunctions
    {
        private readonly ILogger<TrafficFunctions> _logger;
        private readonly ITrafficService _trafficService;
        private readonly ITokenService _tokenService;

        public TrafficFunctions(
            ILogger<TrafficFunctions> logger,
            ITrafficService trafficService,
            ITokenService tokenService)
        {
            _logger = logger;
            _trafficService = trafficService;
            _tokenService = tokenService;
        }

        // GET /api/smartcity/traffic/summary
        [Function(nameof(GetTrafficSummary))]
        public async Task<IActionResult> GetTrafficSummary(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "smartcity/traffic/summary")]
            HttpRequest req,
            int status = 0, string q = "", int page = 1, int pagesize = 10)
        {
            var auth = await AuthorizeAsync(req, SmartCityTaskActions.VIEW);
            if (!auth.success) return new UnauthorizedResult();

            var data = await _trafficService.GetTrafficSummary(
                auth.userId, status, q, page, pagesize);
            return new OkObjectResult(data);
        }

        // POST /api/smartcity/traffic/predict
        [Function(nameof(PredictTraffic))]
        public async Task<IActionResult> PredictTraffic(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "smartcity/traffic/predict")]
            HttpRequest req)
        {
            var auth = await AuthorizeAsync(req, SmartCityTaskActions.CREATE);
            if (!auth.success) return new UnauthorizedResult();

            var body = await new StreamReader(req.Body).ReadToEndAsync();
            var request = JsonConvert.DeserializeObject<TrafficPredictionRequestVM>(body);
            if (request == null) return new BadRequestObjectResult("Invalid request body.");

            var result = await _trafficService.PredictTrafficAsync(request);
            return new OkObjectResult(result);
        }

        // POST /api/smartcity/traffic/route
        [Function(nameof(AnalyseRoute))]
        public async Task<IActionResult> AnalyseRoute(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "smartcity/traffic/route")]
            HttpRequest req)
        {
            var auth = await AuthorizeAsync(req, SmartCityTaskActions.CREATE);
            if (!auth.success) return new UnauthorizedResult();

            var body = await new StreamReader(req.Body).ReadToEndAsync();
            var request = JsonConvert.DeserializeObject<RouteTrafficRequestVM>(body);
            if (request == null) return new BadRequestObjectResult("Invalid request body.");

            if (string.IsNullOrWhiteSpace(request.OriginName) ||
                string.IsNullOrWhiteSpace(request.DestinationName))
                return new BadRequestObjectResult("Origin and destination names are required.");

            var result = await _trafficService.AnalyseRouteAsync(auth.userId, request);
            return new OkObjectResult(result);
        }

        // GET /api/smartcity/traffic/route/history
        [Function(nameof(GetRouteHistory))]
        public async Task<IActionResult> GetRouteHistory(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "smartcity/traffic/route/history")]
            HttpRequest req,
            int page = 1, int pagesize = 10)
        {
            var auth = await AuthorizeAsync(req, SmartCityTaskActions.VIEW);
            if (!auth.success) return new UnauthorizedResult();

            var data = await _trafficService.GetMyRouteHistoryAsync(auth.userId, page, pagesize);
            return new OkObjectResult(data);
        }

        // ── Auth helper ───────────────────────────────────────────────────────
        private async Task<(bool success, Guid userId)> AuthorizeAsync(HttpRequest req, Guid action)
        {
            var token = Utility.GetAuthToken(req);
            var auth = await _tokenService.IsAuthorizedAsync(
                token, SmartCityTaskIds.SmartCityTraffic, action);

            if (!auth.Success) return (false, Guid.Empty);

            var userId = _tokenService.GetUserId(auth.Data);
            return userId.HasValue ? (true, userId.Value) : (false, Guid.Empty);
        }
    }
}