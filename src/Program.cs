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

        services.AddHttpClient("Anthropic", c =>
        {
            c.BaseAddress = new Uri("https://api.anthropic.com");
            c.Timeout = TimeSpan.FromSeconds(45);
        });

        // ── Domain services ───────────────────────────────────────────────
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<ITrafficService, TrafficService>();
        services.AddScoped<IWasteService, WasteService>();
        services.AddScoped<IEnergyService, EnergyService>();
        services.AddScoped<IDashboardService, DashboardService>();

        // ── AI services ───────────────────────────────────────────────────
        services.AddSingleton<ITrafficAIService, TrafficAIService>();
        services.AddSingleton<IRouteTrafficAIService, RouteTrafficAIService>();  // NEW
        services.AddSingleton<IEnergyAIService, EnergyAIService>();
        services.AddSingleton<IWasteAIService, WasteAIService>();

        // ── Auth ──────────────────────────────────────────────────────────
        services.AddScoped<ITokenService, TokenService>();
    })
    .Build();

host.Run();