using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using SmartcityBackend.src.Helpers;
using SmartcityBackend.src.Services.Interfaces;
using SmartcityBackend.src.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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

        [Function(nameof(GetMyEnergyUsage))]
        public async Task<IActionResult> GetMyEnergyUsage(
            [HttpTrigger(AuthorizationLevel.Function, "get", Route = "smartcity/energy/my-usage")]
        HttpRequest req,
            string q = "",
            int page = 1,
            int pagesize = 10)
        {
            var token = Utility.GetAuthToken(req);

            var authorized = await tokenService.IsAuthorizedAsync(
                token,
                SmartCityTaskIds.SmartCityEnergy,
                SmartCityTaskActions.VIEW);

            if (!authorized.Success)
            {
                logger.LogInformation("{methodName} unauthorized. {message}", nameof(GetMyEnergyUsage), authorized.Message);
                return new UnauthorizedResult();
            }

            var userId = tokenService.GetUserId(authorized.Data);

            if (userId == null)
            {
                return new UnauthorizedResult();
            }

            var data = await energyService.GetMyEnergyUsageAsync(userId.Value, q, page, pagesize);
            return new OkObjectResult(data);
        }

        [Function(nameof(PredictEnergy))]
        public async Task<IActionResult> PredictEnergy(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = "smartcity/energy/predict")]
        HttpRequest req)
        {
            var token = Utility.GetAuthToken(req);

            var authorized = await tokenService.IsAuthorizedAsync(
                token,
                SmartCityTaskIds.SmartCityEnergy,
                SmartCityTaskActions.CREATE);

            if (!authorized.Success)
            {
                logger.LogInformation("{methodName} unauthorized. {message}", nameof(PredictEnergy), authorized.Message);
                return new UnauthorizedResult();
            }

            var userId = tokenService.GetUserId(authorized.Data);

            if (userId == null)
            {
                return new UnauthorizedResult();
            }

            var body = await new StreamReader(req.Body).ReadToEndAsync();
            var request = JsonConvert.DeserializeObject<EnergyPredictionRequestVM>(body);

            if (request == null)
            {
                return new BadRequestObjectResult("Invalid request body.");
            }

            var result = await energyService.PredictEnergyAsync(userId.Value, request);
            return new OkObjectResult(result);
        }
    }
}
