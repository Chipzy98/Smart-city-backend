using Microsoft.IdentityModel.Tokens;
using SmartcityBackend.src.Services.Interfaces;
using SmartcityBackend.src.ViewModels;
using System.IdentityModel.Tokens.Jwt;
using System.Text;

namespace SmartcityBackend.src.Services
{
    public class TokenService : ITokenService
    {
        private readonly string _jwtSecret;

        public TokenService()
        {
            _jwtSecret = AuthService.ResolveJwtSecret();
        }

        public async Task<TokenAuthorizationResult> IsAuthorizedAsync(
            string? token,
            Guid taskId,
            Guid action)
        {
            await Task.CompletedTask;

            if (string.IsNullOrWhiteSpace(token))
                return Fail("No token provided.");

            try
            {
                var jwt = ValidateToken(token);
                if (jwt == null)
                    return Fail("Invalid or expired token.");

                var userId = GetUserId(jwt);
                var role = jwt.Claims.FirstOrDefault(c => c.Type == "role")?.Value ?? string.Empty;

                if (userId == null)
                    return Fail("Token has no user id.");

                // Role based permission check (TaskId + Action → allowed roles)
                if (!SmartCityPermissions.IsAllowed(taskId, action, role))
                {
                    return new TokenAuthorizationResult
                    {
                        Success = false,
                        Forbidden = true,
                        Message = $"Role '{role}' is not allowed to perform this action.",
                        UserId = userId,
                        Role = role,
                    };
                }

                return new TokenAuthorizationResult
                {
                    Success = true,
                    Message = "Authorized.",
                    Data = jwt,
                    UserId = userId,
                    Role = role,
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
                return null;

            var sub = jwt.Claims.FirstOrDefault(c =>
                          c.Type == JwtRegisteredClaimNames.Sub ||
                          c.Type == "userId" ||
                          c.Type == "nameid")?.Value;

            return Guid.TryParse(sub, out var userId) ? userId : null;
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
                ValidateIssuer = true,
                ValidIssuer = AuthService.JwtIssuer,
                ValidateAudience = true,
                ValidAudience = AuthService.JwtAudience,
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
