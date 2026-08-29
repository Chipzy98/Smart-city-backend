using Microsoft.IdentityModel.Tokens;
using SmartcityBackend.src.Services.Interfaces;
using SmartcityBackend.src.ViewModels;
using System.IdentityModel.Tokens.Jwt;
using System.Text;

namespace SmartcityBackend.src.Services
{
    public class TokenService : ITokenService
    {
        // JWT Secret — local.settings.json හි "JWT_SECRET" key add කරන්න
        private readonly string _jwtSecret;

        public TokenService()
        {
            _jwtSecret = Environment.GetEnvironmentVariable("JWT_SECRET")
                         ?? "SmartCity_Dev_Secret_Key_2024_ChangeInProd!";
        }

        public async Task<TokenAuthorizationResult> IsAuthorizedAsync(
            string? token,
            Guid taskId,
            Guid action)
        {
            await Task.CompletedTask;

            if (string.IsNullOrWhiteSpace(token))
            {
                return Fail("No token provided.");
            }

            try
            {
                var principal = ValidateToken(token);

                if (principal == null)
                    return Fail("Invalid or expired token.");

                // Token valid නම් authorized — Task/Action level check
                // production හි DB permission check add කරන්න
                return new TokenAuthorizationResult
                {
                    Success = true,
                    Message = "Authorized.",
                    Data = principal   // GetUserId() will read from this
                };
            }
            catch (Exception ex)
            {
                return Fail($"Token validation failed: {ex.Message}");
            }
        }

        public Guid? GetUserId(object? data)
        {
            if (data is not JwtSecurityToken jwt)
            {
                // Try to re-parse if passed as ClaimsPrincipal or string
                return null;
            }

            // Standard JWT "sub" claim or custom "userId" claim
            var sub = jwt.Claims.FirstOrDefault(c =>
                          c.Type == JwtRegisteredClaimNames.Sub ||
                          c.Type == "userId" ||
                          c.Type == "nameid")?.Value;

            if (Guid.TryParse(sub, out var userId))
                return userId;

            return null;
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        private JwtSecurityToken? ValidateToken(string token)
        {
            var handler = new JwtSecurityTokenHandler();
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtSecret));

            var parameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = key,
                ValidateIssuer = false,   // set true + ValidIssuer in prod
                ValidateAudience = false,   // set true + ValidAudience in prod
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(1)
            };

            handler.ValidateToken(token, parameters, out var validated);
            return validated as JwtSecurityToken;
        }

        private static TokenAuthorizationResult Fail(string msg) =>
            new() { Success = false, Message = msg, Data = null };
    }
}