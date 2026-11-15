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

        // GET /api/smartcity/waste/my-records?q=&page=1&pagesize=10
        [Function(nameof(GetMyWasteRecords))]
        public async Task<IActionResult> GetMyWasteRecords(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "smartcity/waste/my-records")]
            HttpRequest req)
        {
            var auth = await tokenService.AuthorizeAsync(req, SmartCityTaskIds.SmartCityWaste, SmartCityTaskActions.VIEW);
            if (!auth.Success)
            {
                logger.LogInformation("{methodName} denied. {message}", nameof(GetMyWasteRecords), auth.Message);
                return AuthResults.Deny(auth);
            }

            var data = await wasteService.GetMyWasteRecordsAsync(
                auth.UserId!.Value,
                req.Query["q"].FirstOrDefault() ?? "",
                AuthResults.QueryInt(req, "page", 1, 1),
                AuthResults.QueryInt(req, "pagesize", 10, 1, 100));
            return new OkObjectResult(data);
        }

        // POST /api/smartcity/waste/classify   { imageUrl, imageBase64?, mediaType? }
        [Function(nameof(ClassifyWaste))]
        public async Task<IActionResult> ClassifyWaste(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "smartcity/waste/classify")]
            HttpRequest req)
        {
            var auth = await tokenService.AuthorizeAsync(req, SmartCityTaskIds.SmartCityWaste, SmartCityTaskActions.CREATE);
            if (!auth.Success)
            {
                logger.LogInformation("{methodName} denied. {message}", nameof(ClassifyWaste), auth.Message);
                return AuthResults.Deny(auth);
            }

            var body = await new StreamReader(req.Body).ReadToEndAsync();
            var request = JsonConvert.DeserializeObject<WasteClassifyRequestVM>(body);

            if (request == null || (string.IsNullOrWhiteSpace(request.ImageUrl) && string.IsNullOrWhiteSpace(request.ImageBase64)))
                return new BadRequestObjectResult(new { error = "An image URL or uploaded image is required." });

            try
            {
                var result = await wasteService.ClassifyWasteAsync(auth.UserId!.Value, request);
                return new OkObjectResult(result);
            }
            catch (ArgumentException ex)
            {
                return new BadRequestObjectResult(new { error = ex.Message });
            }
        }
    }
}
