using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SmartcityBackend.src.Services.Interfaces;
using SmartcityBackend.src.ViewModels;

namespace SmartcityBackend.src.Helpers
{
    public static class AuthResults
    {
        /// <summary>401 for missing/invalid token, 403 when the role is not allowed.</summary>
        public static IActionResult Deny(TokenAuthorizationResult auth) =>
            auth.Forbidden
                ? new ObjectResult(new { error = auth.Message }) { StatusCode = StatusCodes.Status403Forbidden }
                : new UnauthorizedObjectResult(new { error = auth.Message });

        /// <summary>Reads the bearer token and runs the permission check in one call.</summary>
        public static Task<TokenAuthorizationResult> AuthorizeAsync(
            this ITokenService tokenService, HttpRequest req, Guid taskId, Guid action) =>
            tokenService.IsAuthorizedAsync(Utility.GetAuthToken(req), taskId, action);

        public static int QueryInt(HttpRequest req, string name, int fallback, int min = int.MinValue, int max = int.MaxValue)
        {
            var raw = req.Query[name].FirstOrDefault();
            var value = int.TryParse(raw, out var v) ? v : fallback;
            return Math.Clamp(value, min, max);
        }

        public static double? QueryDouble(HttpRequest req, string name)
        {
            var raw = req.Query[name].FirstOrDefault();
            return double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
        }
    }
}
