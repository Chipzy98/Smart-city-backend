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
    .ConfigureServices((context, services) =>
    {
        var connectionString =
            Environment.GetEnvironmentVariable("DefaultConnection");

        services.AddDbContext<SmartCityDbContext>(options =>
            options.UseSqlServer(connectionString));

        // ── HttpClient for Claude API ──────────────────────────────
        services.AddHttpClient("Anthropic", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        // ── Main Services ──────────────────────────────────────────
        services.AddScoped<ITrafficService, TrafficService>();
        services.AddScoped<IWasteService, WasteService>();
        services.AddScoped<IEnergyService, EnergyService>();
        services.AddScoped<IDashboardService, DashboardService>();

        // ── AI Services (Claude-powered) ───────────────────────────
        services.AddSingleton<ITrafficAIService, TrafficAIService>();
        services.AddSingleton<IEnergyAIService, EnergyAIService>();
        services.AddSingleton<IWasteAIService, WasteAIService>();

        // ── Authentication / Authorization ─────────────────────────
        services.AddScoped<ITokenService, TokenService>();
    })
    .Build();

host.Run();