using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using SmartcityBackend.src.AI.Interfaces;
using SmartcityBackend.src.AI.Models;
using SmartcityBackend.src.Helpers;
using System.Text;
using System.Text.Json;

namespace SmartcityBackend.src.AI.Services;

public class EnergyAIService : IEnergyAIService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<EnergyAIService> _logger;
    private readonly string _apiKey;

    private const string ApiUrl = "https://api.anthropic.com/v1/messages";
    private static string Model => AppConfig.AnthropicModel;
    private const string ApiVersion = "2023-06-01";
    private const int MaxTokens = 300;
    private const int RetryCount = 2;

    private static string TrainingFile => AppConfig.TrainingFile("energy");

    public EnergyAIService(IHttpClientFactory httpClientFactory, ILogger<EnergyAIService> logger)
    {
        _httpClient = httpClientFactory.CreateClient("Anthropic");
        _logger = logger;
        _apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
                      ?? string.Empty; // missing key → calls fail → rule-based fallback
    }

    public EnergyAIOutput Predict(EnergyAIInput input)
        => PredictAsync(input, CancellationToken.None).GetAwaiter().GetResult();

    public async Task<EnergyAIOutput> PredictAsync(EnergyAIInput input, CancellationToken ct = default)
    {
        var usageCategory = input.UsageUnits switch
        {
            < 100 => "very low",
            < 250 => "average",
            < 450 => "above average",
            _ => "high"
        };

        var heatContext = input.Temperature switch
        {
            > 35 => "extremely hot — high A/C demand",
            > 28 => "warm — moderate cooling load",
            > 20 => "comfortable",
            > 10 => "cool — some heating required",
            _ => "cold — high heating demand"
        };

        // The bill always comes from the official CEB tariff (exact, in LKR); Claude only writes the tip.
        var billedUnits = CebTariff.AdjustForTemperature(input.UsageUnits, input.Temperature);
        var bill = CebTariff.Calculate(billedUnits);
        var trainingContext = LoadTrainingContext();

        var prompt =
            "You are a smart city energy management AI for households in Sri Lanka.\n" +
            "Give one specific electricity saving tip. Reply ONLY with JSON — no markdown.\n\n" +
            "=== Household Data ===\n" +
            $"- Monthly usage : {input.UsageUnits} kWh ({usageCategory})\n" +
            $"- Temperature   : {input.Temperature}°C ({heatContext})\n" +
            $"- Occupants     : {input.Occupants}\n" +
            $"- Monthly bill  : Rs. {bill.Total:N2} on {bill.Units} kWh ({CebTariff.TariffName})\n" +
            "- Tariff blocks : 0–60 kWh is cheapest; above 180 kWh every extra unit costs Rs. 100 and\n" +
            "                  the whole bill moves to Rs. 32.50/kWh with a Rs. 2,500 fixed charge.\n\n" +
            trainingContext +
            "If the household is just above a block limit (60, 90, 120 or 180 kWh), say how many units\n" +
            "to cut to drop below it and roughly how many rupees that saves. Use Rs. for money.\n\n" +
            "{\n" +
            "  \"tip\": \"<specific saving tip>\"\n" +
            "}";

        for (var attempt = 0; attempt <= RetryCount && !string.IsNullOrWhiteSpace(_apiKey); attempt++) // no key → straight to fallback
        {
            try
            {
                var text = await CallClaudeAsync(prompt, MaxTokens, ct);
                using var doc = JsonDocument.Parse(CleanJson(text));
                var tip = doc.RootElement.GetProperty("tip").GetString() ?? FallbackTip(input, bill);

                _logger.LogInformation("[EnergyAI] bill=Rs.{Bill} for {Units} kWh", bill.Total, bill.Units);
                return ToOutput(bill, tip);
            }
            catch (Exception ex) when (attempt < RetryCount)
            {
                _logger.LogWarning(ex, "[EnergyAI] Attempt {A} failed, retrying…", attempt + 1);
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[EnergyAI] All attempts failed — fallback.");
            }
        }

        return ToOutput(bill, FallbackTip(input, bill));
    }

    private static EnergyAIOutput ToOutput(CebTariff.Bill bill, string tip) => new()
    {
        PredictedBill = (float)bill.Total,
        Currency      = CebTariff.Currency,
        Tip           = tip,
        BilledUnits   = (float)bill.Units,
        EnergyCharge  = (float)bill.EnergyCharge,
        FixedCharge   = (float)bill.FixedCharge,
        Sscl          = (float)bill.Sscl,
        Tariff        = CebTariff.TariffName,
    };

    // ── Training context ──────────────────────────────────────────────────

    private static string LoadTrainingContext()
    {
        try
        {
            if (!File.Exists(TrainingFile)) return string.Empty;

            var ctx = JsonConvert.DeserializeObject<dynamic>(File.ReadAllText(TrainingFile));
            if (ctx == null) return string.Empty;

            var rows = ctx.Rows as Newtonsoft.Json.Linq.JArray;
            if (rows == null || rows.Count == 0) return string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine("=== Historical Energy Data (reference) ===");
            sb.AppendLine("usageKwh, tempC, occupants → bill");
            foreach (var row in rows.Take(8)) sb.AppendLine(string.Join(", ", row));
            sb.AppendLine();
            return sb.ToString();
        }
        catch { return string.Empty; }
    }

    // ── Fallback ──────────────────────────────────────────────────────────

    private static string FallbackTip(EnergyAIInput input, CebTariff.Bill bill)
    {
        // Just above a tariff block limit → cutting a few units saves a lot of rupees.
        foreach (var limit in new[] { 60.0, 90.0, 120.0, 180.0 })
        {
            var over = bill.Units - limit;
            if (over > 0 && over <= 20)
            {
                var saving = bill.Total - CebTariff.Calculate(limit).Total;
                return $"You are {over:0} kWh above the {limit:0}-unit block. Cutting {over:0} units a month " +
                       $"would lower the bill by about Rs. {saving:N0}. Switch off idle appliances and limit A/C hours.";
            }
        }
        if (bill.Units > 180) return "Above 180 units every extra unit costs Rs. 100 — audit the A/C, water heater and other high-wattage appliances.";
        if (input.Temperature > 30) return "Set the A/C to 24–26°C and use ceiling fans to cut cooling costs.";
        if (input.Temperature < 15) return "Use heaters only in occupied rooms and close doors to keep the heat in.";
        return "Your usage looks healthy — keep it under 60 units to stay in the cheapest CEB block.";
    }

    // ── Claude API call ───────────────────────────────────────────────────

    private async Task<string> CallClaudeAsync(string prompt, int maxTokens, CancellationToken ct)
    {
        var body = System.Text.Json.JsonSerializer.Serialize(new
        {
            model = Model,
            max_tokens = maxTokens,
            messages = new[] { new { role = "user", content = prompt } }
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
        req.Headers.Add("x-api-key", _apiKey);
        req.Headers.Add("anthropic-version", ApiVersion);
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");

        var res = await _httpClient.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString() ?? "{}";
    }

    private static string CleanJson(string text)
    {
        text = text.Trim();
        if (text.StartsWith("```")) { var l = text.Split('\n', 2); text = l.Length > 1 ? l[1] : text; }
        if (text.EndsWith("```")) text = text[..text.LastIndexOf("```")];
        return text.Trim();
    }
}