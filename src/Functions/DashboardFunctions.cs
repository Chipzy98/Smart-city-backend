using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
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

        [Function(nameof(GetAdminDashboard))]
        public async Task<IActionResult> GetAdminDashboard(
            [HttpTrigger(AuthorizationLevel.Function, "get", Route = "smartcity/dashboard/admin")]
        HttpRequest req)
        {
            var token = Utility.GetAuthToken(req);

            var authorized = await tokenService.IsAuthorizedAsync(
                token,
                SmartCityTaskIds.SmartCityDashboard,
                SmartCityTaskActions.VIEW);

            if (!authorized.Success)
            {
                logger.LogInformation("{methodName} unauthorized. {message}", nameof(GetAdminDashboard), authorized.Message);
                return new UnauthorizedResult();
            }

            var data = await dashboardService.GetAdminDashboardAsync();
            return new OkObjectResult(data);
        }

        [Function(nameof(GetManagerDashboard))]
        public async Task<IActionResult> GetManagerDashboard(
            [HttpTrigger(AuthorizationLevel.Function, "get", Route = "smartcity/dashboard/manager")]
        HttpRequest req)
        {
            var token = Utility.GetAuthToken(req);

            var authorized = await tokenService.IsAuthorizedAsync(
                token,
                SmartCityTaskIds.SmartCityDashboard,
                SmartCityTaskActions.VIEW);

            if (!authorized.Success)
            {
                logger.LogInformation("{methodName} unauthorized. {message}", nameof(GetManagerDashboard), authorized.Message);
                return new UnauthorizedResult();
            }

            var data = await dashboardService.GetManagerDashboardAsync();
            return new OkObjectResult(data);
        }

        [Function(nameof(GetUserDashboard))]
        public async Task<IActionResult> GetUserDashboard(
            [HttpTrigger(AuthorizationLevel.Function, "get", Route = "smartcity/dashboard/user")]
        HttpRequest req)
        {
            var token = Utility.GetAuthToken(req);

            var authorized = await tokenService.IsAuthorizedAsync(
                token,
                SmartCityTaskIds.SmartCityDashboard,
                SmartCityTaskActions.VIEW);

            if (!authorized.Success)
            {
                logger.LogInformation("{methodName} unauthorized. {message}", nameof(GetUserDashboard), authorized.Message);
                return new UnauthorizedResult();
            }

            var userId = tokenService.GetUserId(authorized.Data);

            if (userId == null)
            {
                return new UnauthorizedResult();
            }

            var data = await dashboardService.GetUserDashboardAsync(userId.Value);
            return new OkObjectResult(data);
        }
    }
}
