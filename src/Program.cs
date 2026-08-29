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

        // ── Named HttpClient for the Anthropic Claude API ─────────────────
        // 30 s is comfortable for most responses; raise if you enable streaming.
        services.AddHttpClient("Anthropic", client =>
        {
            client.BaseAddress = new Uri("https://api.anthropic.com");
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        // ── Domain Services ───────────────────────────────────────────────
        services.AddScoped<ITrafficService, TrafficService>();
        services.AddScoped<IWasteService, WasteService>();
        services.AddScoped<IEnergyService, EnergyService>();
        services.AddScoped<IDashboardService, DashboardService>();

        // ── AI Services (Claude-powered) ──────────────────────────────────
        // Singleton is safe because the services hold no mutable state;
        // IHttpClientFactory and ILogger are both thread-safe.
        services.AddSingleton<ITrafficAIService, TrafficAIService>();
        services.AddSingleton<IEnergyAIService, EnergyAIService>();
        services.AddSingleton<IWasteAIService, WasteAIService>();

        // ── Authentication / Authorisation ────────────────────────────────
        services.AddScoped<ITokenService, TokenService>();
    })
    .Build();

host.Run();