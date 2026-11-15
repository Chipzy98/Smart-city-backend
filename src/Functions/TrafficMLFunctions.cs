using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using SmartcityBackend.src.AI.Services;
using SmartcityBackend.src.Helpers;
using SmartcityBackend.src.Services;
using SmartcityBackend.src.Services.Interfaces;

namespace SmartcityBackend.src.Functions
{
    /// <summary>
    /// Traffic ML pipeline:
    ///  • Timer every 30 min  → collect TomTom live flow + weather into smart_TrafficData
    ///  • Timer daily 02:00 SL → retrain the ML.NET model on the collected data
    ///  • HTTP endpoints for the AI Training page (status, collect now, train now)
    /// Timer schedules are UTC (Azure default). Set TRAFFIC_COLLECTOR_ENABLED=false to pause collection.
    /// </summary>
    public class TrafficMLFunctions
    {
        private readonly ILogger<TrafficMLFunctions> _logger;
        private readonly TomTomTrafficService _tomTom;
        private readonly TrafficForecastService _forecast;
        private readonly TrafficMLService _ml;
        private readonly ITokenService _tokenService;

        public TrafficMLFunctions(
            ILogger<TrafficMLFunctions> logger,
            TomTomTrafficService tomTom,
            TrafficForecastService forecast,
            TrafficMLService ml,
            ITokenService tokenService)
        {
            _logger = logger;
            _tomTom = tomTom;
            _forecast = forecast;
            _ml = ml;
            _tokenService = tokenService;
        }

        private static bool CollectorEnabled =>
            !string.Equals(Environment.GetEnvironmentVariable("TRAFFIC_COLLECTOR_ENABLED"), "false", StringComparison.OrdinalIgnoreCase);

        // ── Timers ─────────────────────────────────────────────────────────

        [Function(nameof(CollectTrafficData))]
        public async Task CollectTrafficData([TimerTrigger("0 */30 * * * *")] TimerInfo timer)
        {
            if (!CollectorEnabled || !_tomTom.IsConfigured)
            {
                _logger.LogInformation("[Collector] Skipped (disabled or no traffic API key).");
                return;
            }

            // Google's free cap is 5,000 requests / month → collect every N hours (default 2):
            // 10 roads × 12 runs/day = 120 requests/day, leaving room for forecasts and the map.
            if (_tomTom.Provider == "google")
            {
                var everyHours = int.TryParse(Environment.GetEnvironmentVariable("GOOGLE_COLLECT_EVERY_HOURS"), out var h) && h > 0 ? h : 2;
                var now = AppTime.NowLocal();
                if (now.Hour % everyHours != 0 || now.Minute >= 30)
                {
                    _logger.LogInformation("[Collector] Skipped — Google collection runs every {Hours} h.", everyHours);
                    return;
                }
            }

            var points = await _tomTom.FetchAndSaveAsync(Guid.Empty);
            await _forecast.FillActualsForPointsAsync(points);
            _logger.LogInformation("[Collector] Collected {Count} points.", points.Count);
        }

        [Function(nameof(RetrainTrafficModel))]
        public async Task RetrainTrafficModel([TimerTrigger("0 30 20 * * *")] TimerInfo timer)
        {
            var result = await _ml.TrainAsync(Guid.Empty);
            _logger.LogInformation("[TrafficML] Nightly retrain: {Trained} — {Message}", result.Trained, result.Message);
        }

        // ── HTTP ───────────────────────────────────────────────────────────

        // GET /api/smartcity/ai/traffic/model
        [Function(nameof(GetTrafficModelStatus))]
        public async Task<IActionResult> GetTrafficModelStatus(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "smartcity/ai/traffic/model")]
            HttpRequest req)
        {
            var auth = await _tokenService.AuthorizeAsync(req, SmartCityTaskIds.SmartCityAITraining, SmartCityTaskActions.VIEW);
            if (!auth.Success) return AuthResults.Deny(auth);

            return new OkObjectResult(await _forecast.GetModelStatusAsync(req.HttpContext.RequestAborted));
        }

        // POST /api/smartcity/ai/traffic/train-model
        [Function(nameof(TrainTrafficModel))]
        public async Task<IActionResult> TrainTrafficModel(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "smartcity/ai/traffic/train-model")]
            HttpRequest req)
        {
            var auth = await _tokenService.AuthorizeAsync(req, SmartCityTaskIds.SmartCityAITraining, SmartCityTaskActions.CREATE);
            if (!auth.Success) return AuthResults.Deny(auth);

            var result = await _ml.TrainAsync(auth.UserId!.Value);
            return result.Trained
                ? new OkObjectResult(result)
                : new BadRequestObjectResult(result);
        }

        // POST /api/smartcity/ai/traffic/collect
        [Function(nameof(CollectTrafficNow))]
        public async Task<IActionResult> CollectTrafficNow(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "smartcity/ai/traffic/collect")]
            HttpRequest req)
        {
            var auth = await _tokenService.AuthorizeAsync(req, SmartCityTaskIds.SmartCityAITraining, SmartCityTaskActions.CREATE);
            if (!auth.Success) return AuthResults.Deny(auth);

            if (!_tomTom.IsConfigured)
                return new BadRequestObjectResult(new { error = "No traffic API key (GOOGLE_MAPS_API_KEY or TOMTOM_API_KEY) is configured on the backend." });

            var points = await _tomTom.FetchAndSaveAsync(auth.UserId!.Value);
            await _forecast.FillActualsForPointsAsync(points);

            return new OkObjectResult(new
            {
                success = points.Count > 0,
                collected = points.Count,
                message = points.Count > 0
                    ? $"Collected {points.Count} live traffic samples."
                    : _tomTom.Provider == "google"
                        ? $"Google returned no live data. Check that the Routes API is enabled for the key and billing is on (used today: {GoogleTrafficService.UsedToday}/{GoogleTrafficService.DailyLimit})."
                        : "TomTom returned no live data for these roads (Sri Lanka is outside TomTom's real-time traffic coverage). Add GOOGLE_MAPS_API_KEY or upload a traffic CSV.",
            });
        }
    }
}
