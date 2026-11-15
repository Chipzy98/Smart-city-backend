using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using SmartcityBackend.src.Helpers;
using SmartcityBackend.src.Services;
using SmartcityBackend.src.Services.Interfaces;

namespace SmartcityBackend.src.Functions
{
    public class ClimateFunctions
    {
        private readonly ILogger<ClimateFunctions> _logger;
        private readonly ClimateService _climate;
        private readonly CarbonService _carbon;
        private readonly ITokenService _tokenService;

        public ClimateFunctions(
            ILogger<ClimateFunctions> logger,
            ClimateService climate,
            CarbonService carbon,
            ITokenService tokenService)
        {
            _logger = logger;
            _climate = climate;
            _carbon = carbon;
            _tokenService = tokenService;
        }

        // GET /api/smartcity/climate/overview — all cities: weather, AQI, alerts
        [Function(nameof(GetClimateOverview))]
        public async Task<IActionResult> GetClimateOverview(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "smartcity/climate/overview")]
            HttpRequest req)
        {
            var auth = await _tokenService.AuthorizeAsync(req, SmartCityTaskIds.SmartCityClimate, SmartCityTaskActions.VIEW);
            if (!auth.Success) return AuthResults.Deny(auth);

            try
            {
                return new OkObjectResult(await _climate.GetOverviewAsync(req.HttpContext.RequestAborted));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Climate] Overview failed");
                return new ObjectResult(new { error = "Climate data is unavailable right now." }) { StatusCode = 502 };
            }
        }

        // GET /api/smartcity/climate/forecast?lat=6.93&lng=79.86&name=Colombo — 7-day forecast
        [Function(nameof(GetClimateForecast))]
        public async Task<IActionResult> GetClimateForecast(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "smartcity/climate/forecast")]
            HttpRequest req)
        {
            var auth = await _tokenService.AuthorizeAsync(req, SmartCityTaskIds.SmartCityClimate, SmartCityTaskActions.VIEW);
            if (!auth.Success) return AuthResults.Deny(auth);

            var lat = AuthResults.QueryDouble(req, "lat");
            var lng = AuthResults.QueryDouble(req, "lng");
            if (lat is null or < -90 or > 90 || lng is null or < -180 or > 180)
                return new BadRequestObjectResult(new { error = "Valid lat and lng query params are required." });

            try
            {
                var name = req.Query["name"].FirstOrDefault();
                return new OkObjectResult(await _climate.GetForecastAsync(lat.Value, lng.Value, name, req.HttpContext.RequestAborted));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Climate] Forecast failed");
                return new ObjectResult(new { error = "Forecast is unavailable right now." }) { StatusCode = 502 };
            }
        }

        // GET /api/smartcity/climate/current?lat=&lng= — current weather (Energy auto-fill)
        [Function(nameof(GetClimateCurrent))]
        public async Task<IActionResult> GetClimateCurrent(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "smartcity/climate/current")]
            HttpRequest req)
        {
            var auth = await _tokenService.AuthorizeAsync(req, SmartCityTaskIds.SmartCityClimate, SmartCityTaskActions.VIEW);
            if (!auth.Success) return AuthResults.Deny(auth);

            var lat = AuthResults.QueryDouble(req, "lat") ?? 6.9271;   // default Colombo
            var lng = AuthResults.QueryDouble(req, "lng") ?? 79.8612;

            try
            {
                var current = await _climate.GetCurrentAsync(lat, lng, req.HttpContext.RequestAborted);
                return current == null
                    ? new ObjectResult(new { error = "No current weather data." }) { StatusCode = 502 }
                    : new OkObjectResult(current);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Climate] Current failed");
                return new ObjectResult(new { error = "Weather is unavailable right now." }) { StatusCode = 502 };
            }
        }

        // GET /api/smartcity/climate/carbon — personal carbon footprint & sustainability score
        [Function(nameof(GetCarbonFootprint))]
        public async Task<IActionResult> GetCarbonFootprint(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "smartcity/climate/carbon")]
            HttpRequest req)
        {
            var auth = await _tokenService.AuthorizeAsync(req, SmartCityTaskIds.SmartCityClimate, SmartCityTaskActions.VIEW);
            if (!auth.Success) return AuthResults.Deny(auth);

            return new OkObjectResult(await _carbon.GetFootprintAsync(auth.UserId!.Value, req.HttpContext.RequestAborted));
        }
    }
}
