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
        private readonly SmartCityDbContext _db;
        private readonly string _jwtSecret;
        private readonly int _tokenExpiryHours = 24;

        public AuthService(SmartCityDbContext db)
        {
            _db = db;
            _jwtSecret = Environment.GetEnvironmentVariable("JWT_SECRET")
                         ?? "SmartCity_Dev_Secret_Key_2024_ChangeInProd!";
        }

        // ── Login ────────────────────────────────────────────────────────────

        public async Task<AuthResponseVM?> LoginAsync(LoginRequestVM request)
        {
            var user = await _db.SmartUser
                .FirstOrDefaultAsync(u => u.Email == request.Email && u.IsActive);

            if (user == null)
                return null;

            // BCrypt verify
            bool validPassword = BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash);
            if (!validPassword)
                return null;

            return BuildResponse(user);
        }

        // ── Register ─────────────────────────────────────────────────────────

        public async Task<AuthResponseVM?> RegisterAsync(RegisterRequestVM request)
        {
            // Duplicate email check
            bool exists = await _db.SmartUser
                .AnyAsync(u => u.Email == request.Email);

            if (exists)
                return null;

            // Validate role
            var role = request.Role.ToLower();
            if (role != "user" && role != "manager" && role != "admin")
                role = "user";

            var user = new SmartUser
            {
                UserId = Guid.NewGuid(),
                Name = request.Name.Trim(),
                Email = request.Email.Trim().ToLower(),
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password, workFactor: 11),
                Role = role,
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
                claims: claims,
                expires: DateTime.UtcNow.AddHours(_tokenExpiryHours),
                signingCredentials: creds
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }
    }
}