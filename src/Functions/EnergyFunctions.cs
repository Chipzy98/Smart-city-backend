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
    public class EnergyFunctions
    {
        private readonly ILogger<EnergyFunctions> logger;
        private readonly IEnergyService energyService;
        private readonly ITokenService tokenService;

        public EnergyFunctions(
            ILogger<EnergyFunctions> logger,
            IEnergyService energyService,
            ITokenService tokenService)
        {
            this.logger = logger;
            this.energyService = energyService;
            this.tokenService = tokenService;
        }

        // GET /api/smartcity/energy/my-usage?q=&page=1&pagesize=10
        [Function(nameof(GetMyEnergyUsage))]
        public async Task<IActionResult> GetMyEnergyUsage(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "smartcity/energy/my-usage")]
            HttpRequest req)
        {
            var auth = await tokenService.AuthorizeAsync(req, SmartCityTaskIds.SmartCityEnergy, SmartCityTaskActions.VIEW);
            if (!auth.Success)
            {
                logger.LogInformation("{methodName} denied. {message}", nameof(GetMyEnergyUsage), auth.Message);
                return AuthResults.Deny(auth);
            }

            var data = await energyService.GetMyEnergyUsageAsync(
                auth.UserId!.Value,
                req.Query["q"].FirstOrDefault() ?? "",
                AuthResults.QueryInt(req, "page", 1, 1),
                AuthResults.QueryInt(req, "pagesize", 10, 1, 100));
            return new OkObjectResult(data);
        }

        // POST /api/smartcity/energy/predict
        [Function(nameof(PredictEnergy))]
        public async Task<IActionResult> PredictEnergy(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "smartcity/energy/predict")]
            HttpRequest req)
        {
            var auth = await tokenService.AuthorizeAsync(req, SmartCityTaskIds.SmartCityEnergy, SmartCityTaskActions.CREATE);
            if (!auth.Success)
            {
                logger.LogInformation("{methodName} denied. {message}", nameof(PredictEnergy), auth.Message);
                return AuthResults.Deny(auth);
            }

            var body = await new StreamReader(req.Body).ReadToEndAsync();
            var request = JsonConvert.DeserializeObject<EnergyPredictionRequestVM>(body);

            if (request == null || request.UsageUnits <= 0 || request.Occupants <= 0)
                return new BadRequestObjectResult(new { error = "Usage units and occupants must be greater than zero." });

            var result = await energyService.PredictEnergyAsync(auth.UserId!.Value, request);
            return new OkObjectResult(result);
        }
    }
}
