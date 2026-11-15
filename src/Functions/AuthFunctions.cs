using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using SmartcityBackend.src.Services.Interfaces;
using SmartcityBackend.src.ViewModels;

namespace SmartcityBackend.src.Functions
{
    public class AuthFunctions
    {
        private readonly ILogger<AuthFunctions> _logger;
        private readonly IAuthService _authService;

        public AuthFunctions(ILogger<AuthFunctions> logger, IAuthService authService)
        {
            _logger = logger;
            _authService = authService;
        }

        // POST /api/auth/login
        [Function(nameof(Login))]
        public async Task<IActionResult> Login(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/login")]
            HttpRequest req)
        {
            try
            {
                var body = await new StreamReader(req.Body).ReadToEndAsync();
                var request = JsonConvert.DeserializeObject<LoginRequestVM>(body);

                if (request == null || string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
                    return new BadRequestObjectResult(new { error = "Email and password are required." });

                var result = await _authService.LoginAsync(request);

                if (result == null)
                {
                    _logger.LogWarning("[Auth] Login failed for {Email}", request.Email);
                    return new UnauthorizedObjectResult(new { error = "Invalid email or password." });
                }

                _logger.LogInformation("[Auth] Login success for {Email}", request.Email);
                return new OkObjectResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Auth] Login error");
                return new ObjectResult(new { error = "Login failed." }) { StatusCode = 500 };
            }
        }

        // POST /api/auth/register
        [Function(nameof(Register))]
        public async Task<IActionResult> Register(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "auth/register")]
            HttpRequest req)
        {
            try
            {
                var body = await new StreamReader(req.Body).ReadToEndAsync();
                var request = JsonConvert.DeserializeObject<RegisterRequestVM>(body);

                if (request == null || string.IsNullOrWhiteSpace(request.Name) ||
                    string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
                    return new BadRequestObjectResult(new { error = "Name, email and password are required." });

                if (request.Password.Length < 6)
                    return new BadRequestObjectResult(new { error = "Password must be at least 6 characters." });

                var result = await _authService.RegisterAsync(request);

                if (result == null)
                {
                    return new ConflictObjectResult(new { error = "Email already registered." });
                }

                _logger.LogInformation("[Auth] Registered new user: {Email}", request.Email);
                return new OkObjectResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Auth] Register error");
                return new ObjectResult(new { error = "Registration failed." }) { StatusCode = 500 };
            }
        }
    }
}