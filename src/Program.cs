using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SmartcityBackend.src.AI.Interfaces;
using SmartcityBackend.src.AI.Services;
using SmartcityBackend.src.Data;
using SmartcityBackend.src.Services;
using SmartcityBackend.src.Services.Interfaces;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
    .ConfigureServices((_, services) =>
    {
        services.AddDbContext<SmartCityDbContext>(o =>
            o.UseSqlServer(Environment.GetEnvironmentVariable("DefaultConnection")));

        // ── HTTP Clients ──────────────────────────────────────────────────
        services.AddHttpClient("Anthropic", c =>
        {
            c.BaseAddress = new Uri("https://api.anthropic.com");
            c.Timeout = TimeSpan.FromSeconds(45);
        });

        services.AddHttpClient("TomTom", c =>
        {
            c.BaseAddress = new Uri("https://api.tomtom.com");
            c.Timeout = TimeSpan.FromSeconds(15);
        });

        // ── Domain Services ───────────────────────────────────────────────
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<ITrafficService, TrafficService>();
        services.AddScoped<IWasteService, WasteService>();
        services.AddScoped<IEnergyService, EnergyService>();
        services.AddScoped<IDashboardService, DashboardService>();
        services.AddScoped<TomTomTrafficService>();   // ← NEW

        // ── AI Services ───────────────────────────────────────────────────
        services.AddSingleton<ITrafficAIService, TrafficAIService>();
        services.AddSingleton<IRouteTrafficAIService, RouteTrafficAIService>();
        services.AddSingleton<IEnergyAIService, EnergyAIService>();
        services.AddSingleton<IWasteAIService, WasteAIService>();

        // ── Auth ──────────────────────────────────────────────────────────
        services.AddScoped<ITokenService, TokenService>();
    })
    .Build();

host.Run();