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
    public class WasteFunctions
    {
        private readonly ILogger<WasteFunctions> logger;
        private readonly IWasteService wasteService;
        private readonly ITokenService tokenService;

        public WasteFunctions(
            ILogger<WasteFunctions> logger,
            IWasteService wasteService,
            ITokenService tokenService)
        {
            this.logger = logger;
            this.wasteService = wasteService;
            this.tokenService = tokenService;
        }

        [Function(nameof(GetMyWasteRecords))]
        public async Task<IActionResult> GetMyWasteRecords(
            [HttpTrigger(AuthorizationLevel.Function, "get", Route = "smartcity/waste/my-records")]
        HttpRequest req,
            string q = "",
            int page = 1,
            int pagesize = 10)
        {
            var token = Utility.GetAuthToken(req);

            var authorized = await tokenService.IsAuthorizedAsync(
                token,
                SmartCityTaskIds.SmartCityWaste,
                SmartCityTaskActions.VIEW);

            if (!authorized.Success)
            {
                logger.LogInformation("{methodName} unauthorized. {message}", nameof(GetMyWasteRecords), authorized.Message);
                return new UnauthorizedResult();
            }

            var userId = tokenService.GetUserId(authorized.Data);

            if (userId == null)
            {
                return new UnauthorizedResult();
            }

            var data = await wasteService.GetMyWasteRecordsAsync(userId.Value, q, page, pagesize);
            return new OkObjectResult(data);
        }

        [Function(nameof(ClassifyWaste))]
        public async Task<IActionResult> ClassifyWaste(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = "smartcity/waste/classify")]
        HttpRequest req)
        {
            var token = Utility.GetAuthToken(req);

            var authorized = await tokenService.IsAuthorizedAsync(
                token,
                SmartCityTaskIds.SmartCityWaste,
                SmartCityTaskActions.CREATE);

            if (!authorized.Success)
            {
                logger.LogInformation("{methodName} unauthorized. {message}", nameof(ClassifyWaste), authorized.Message);
                return new UnauthorizedResult();
            }

            var userId = tokenService.GetUserId(authorized.Data);

            if (userId == null)
            {
                return new UnauthorizedResult();
            }

            var body = await new StreamReader(req.Body).ReadToEndAsync();
            var request = JsonConvert.DeserializeObject<WasteClassifyRequestVM>(body);

            if (request == null)
            {
                return new BadRequestObjectResult("Invalid request body.");
            }

            var result = await wasteService.ClassifyWasteAsync(userId.Value, request);
            return new OkObjectResult(result);
        }
    }
}
