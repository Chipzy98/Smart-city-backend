using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using SmartcityBackend.src.Helpers;
using SmartcityBackend.src.Services.Interfaces;

namespace SmartcityBackend.src.Functions
{
    public class DashboardFunctions
    {
        private readonly ILogger<DashboardFunctions> logger;
        private readonly IDashboardService dashboardService;
        private readonly ITokenService tokenService;

        public DashboardFunctions(
            ILogger<DashboardFunctions> logger,
            IDashboardService dashboardService,
            ITokenService tokenService)
        {
            this.logger = logger;
            this.dashboardService = dashboardService;
            this.tokenService = tokenService;
        }

        // GET /api/smartcity/dashboard/admin  (admin only)
        [Function(nameof(GetAdminDashboard))]
        public async Task<IActionResult> GetAdminDashboard(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "smartcity/dashboard/admin")]
            HttpRequest req)
        {
            var auth = await tokenService.AuthorizeAsync(req, SmartCityTaskIds.SmartCityAdminDashboard, SmartCityTaskActions.VIEW);
            if (!auth.Success)
            {
                logger.LogInformation("{methodName} denied. {message}", nameof(GetAdminDashboard), auth.Message);
                return AuthResults.Deny(auth);
            }

            return new OkObjectResult(await dashboardService.GetAdminDashboardAsync());
        }

        // GET /api/smartcity/dashboard/manager  (manager + admin)
        [Function(nameof(GetManagerDashboard))]
        public async Task<IActionResult> GetManagerDashboard(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "smartcity/dashboard/manager")]
            HttpRequest req)
        {
            var auth = await tokenService.AuthorizeAsync(req, SmartCityTaskIds.SmartCityManagerDashboard, SmartCityTaskActions.VIEW);
            if (!auth.Success)
            {
                logger.LogInformation("{methodName} denied. {message}", nameof(GetManagerDashboard), auth.Message);
                return AuthResults.Deny(auth);
            }

            return new OkObjectResult(await dashboardService.GetManagerDashboardAsync());
        }

        // GET /api/smartcity/dashboard/user  (personal dashboard, every role)
        [Function(nameof(GetUserDashboard))]
        public async Task<IActionResult> GetUserDashboard(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "smartcity/dashboard/user")]
            HttpRequest req)
        {
            var auth = await tokenService.AuthorizeAsync(req, SmartCityTaskIds.SmartCityDashboard, SmartCityTaskActions.VIEW);
            if (!auth.Success)
            {
                logger.LogInformation("{methodName} denied. {message}", nameof(GetUserDashboard), auth.Message);
                return AuthResults.Deny(auth);
            }

            return new OkObjectResult(await dashboardService.GetUserDashboardAsync(auth.UserId!.Value));
        }

        // GET /api/smartcity/dashboard/trends?days=7
        [Function(nameof(GetDashboardTrends))]
        public async Task<IActionResult> GetDashboardTrends(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "smartcity/dashboard/trends")]
            HttpRequest req)
        {
            var auth = await tokenService.AuthorizeAsync(req, SmartCityTaskIds.SmartCityDashboard, SmartCityTaskActions.VIEW);
            if (!auth.Success) return AuthResults.Deny(auth);

            var days = AuthResults.QueryInt(req, "days", 7, 3, 30);
            return new OkObjectResult(await dashboardService.GetTrendsAsync(auth.UserId!.Value, auth.Role, days));
        }
    }
}
