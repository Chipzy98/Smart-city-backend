using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using SmartcityBackend.src.Data;
using SmartcityBackend.src.Entities;
using SmartcityBackend.src.Services.Interfaces;
using SmartcityBackend.src.ViewModels;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace SmartcityBackend.src.Services
{
    public class AuthService : IAuthService
    {
        public const string JwtIssuer = "SmartcityBackend";
        public const string JwtAudience = "SmartcityFrontend";

        private readonly SmartCityDbContext _db;
        private readonly string _jwtSecret;
        private readonly int _tokenExpiryHours = 24;

        public AuthService(SmartCityDbContext db)
        {
            _db = db;
            _jwtSecret = ResolveJwtSecret();
        }

        /// <summary>
        /// JWT_SECRET must be set (min 32 chars). A dev-only fallback is used
        /// only when running locally (no Azure WEBSITE_INSTANCE_ID).
        /// </summary>
        public static string ResolveJwtSecret()
        {
            var secret = Environment.GetEnvironmentVariable("JWT_SECRET");
            if (!string.IsNullOrWhiteSpace(secret) && secret.Length >= 32)
                return secret;

            var runningInAzure = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID"));
            if (runningInAzure)
                throw new InvalidOperationException("JWT_SECRET (min 32 characters) must be configured.");

            return "SmartCity_Dev_Secret_Key_2024_ChangeInProd!";
        }

        // ── Login ────────────────────────────────────────────────────────────

        public async Task<AuthResponseVM?> LoginAsync(LoginRequestVM request)
        {
            var email = request.Email.Trim().ToLowerInvariant();

            var user = await _db.SmartUser
                .FirstOrDefaultAsync(u => u.Email == email && u.IsActive);

            if (user == null)
                return null;

            bool validPassword = BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash);
            if (!validPassword)
                return null;

            return BuildResponse(user);
        }

        // ── Register ─────────────────────────────────────────────────────────

        public async Task<AuthResponseVM?> RegisterAsync(RegisterRequestVM request)
        {
            var email = request.Email.Trim().ToLowerInvariant();

            bool exists = await _db.SmartUser.AnyAsync(u => u.Email == email);
            if (exists)
                return null;

            // Public self-registration always creates a normal "user".
            // Manager / admin accounts are created by the seed script (or an admin in the DB).
            var user = new SmartUser
            {
                UserId = Guid.NewGuid(),
                Name = request.Name.Trim(),
                Email = email,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password, workFactor: 11),
                Role = SmartCityPermissions.User,
                IsActive = true,
                CreatedDateTime = DateTime.UtcNow
            };

            _db.SmartUser.Add(user);
            await _db.SaveChangesAsync();

            return BuildResponse(user);
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private AuthResponseVM BuildResponse(SmartUser user)
        {
            var token = GenerateJwt(user);

            return new AuthResponseVM
            {
                Token = token,
                User = new UserVM
                {
                    Id = user.UserId,
                    Name = user.Name,
                    Email = user.Email,
                    Role = user.Role
                }
            };
        }

        private string GenerateJwt(SmartUser user)
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtSecret));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var claims = new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub,   user.UserId.ToString()),
                new Claim(JwtRegisteredClaimNames.Email, user.Email),
                new Claim("userId",                      user.UserId.ToString()),
                new Claim("name",                        user.Name),
                new Claim("role",                        user.Role),
                new Claim(JwtRegisteredClaimNames.Jti,   Guid.NewGuid().ToString())
            };

            var token = new JwtSecurityToken(
                issuer: JwtIssuer,
                audience: JwtAudience,
                claims: claims,
                expires: DateTime.UtcNow.AddHours(_tokenExpiryHours),
                signingCredentials: creds
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }
    }
}
