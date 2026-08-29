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
    public class TrafficFunctions
    {
        private readonly ILogger<TrafficFunctions> logger;
        private readonly ITrafficService trafficService;
        private readonly ITokenService tokenService;

        public TrafficFunctions(
            ILogger<TrafficFunctions> logger,
            ITrafficService trafficService,
            ITokenService tokenService)
        {
            this.logger = logger;
            this.trafficService = trafficService;
            this.tokenService = tokenService;
        }

        [Function(nameof(GetTrafficSummary))]
        public async Task<IActionResult> GetTrafficSummary(
            [HttpTrigger(AuthorizationLevel.Function, "get", Route = "smartcity/traffic/summary")]
        HttpRequest req,
            int status = 0,
            string q = "",
            int page = 1,
            int pagesize = 10)
        {
            var token = Utility.GetAuthToken(req);

            var authorized = await tokenService.IsAuthorizedAsync(
                token,
                SmartCityTaskIds.SmartCityTraffic,
                SmartCityTaskActions.VIEW);

            if (!authorized.Success)
            {
                logger.LogInformation("{methodName} unauthorized. {message}", nameof(GetTrafficSummary), authorized.Message);
                return new UnauthorizedResult();
            }

            var userId = tokenService.GetUserId(authorized.Data);

            if (userId == null)
            {
                logger.LogInformation("{methodName} UserID is null", nameof(GetTrafficSummary));
                return new UnauthorizedResult();
            }

            var data = await trafficService.GetTrafficSummary(userId.Value, status, q, page, pagesize);
            return new OkObjectResult(data);
        }

        [Function(nameof(PredictTraffic))]
        public async Task<IActionResult> PredictTraffic(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = "smartcity/traffic/predict")]
        HttpRequest req)
        {
            var token = Utility.GetAuthToken(req);

            var authorized = await tokenService.IsAuthorizedAsync(
                token,
                SmartCityTaskIds.SmartCityTraffic,
                SmartCityTaskActions.CREATE);

            if (!authorized.Success)
            {
                logger.LogInformation("{methodName} unauthorized. {message}", nameof(PredictTraffic), authorized.Message);
                return new UnauthorizedResult();
            }

            var body = await new StreamReader(req.Body).ReadToEndAsync();
            var request = JsonConvert.DeserializeObject<TrafficPredictionRequestVM>(body);

            if (request == null)
            {
                return new BadRequestObjectResult("Invalid request body.");
            }

            var result = await trafficService.PredictTrafficAsync(request);
            return new OkObjectResult(result);
        }
    }
}
