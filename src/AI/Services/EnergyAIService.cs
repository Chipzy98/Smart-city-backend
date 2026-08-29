using Microsoft.Extensions.Logging;
using SmartcityBackend.src.AI.Interfaces;
using SmartcityBackend.src.AI.Models;
using System.Text;
using System.Text.Json;

namespace SmartcityBackend.src.AI.Services;

/// <summary>
/// Claude-powered energy bill prediction and efficiency advisory service.
/// Estimates the monthly electricity bill from usage data and returns
/// a personalised, context-aware saving tip.
/// Falls back to a rule-based formula when the API is unavailable.
/// </summary>
public class EnergyAIService : IEnergyAIService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<EnergyAIService> _logger;
    private readonly string _apiKey;

    private const string ApiUrl = "https://api.anthropic.com/v1/messages";
    private const string Model = "claude-sonnet-4-6";
    private const string ApiVersion = "2023-06-01";
    private const int MaxTokens = 300;
    private const int RetryCount = 2;

    // US average residential rate as of 2024 (EIA)
    private const float RatePerKwh = 0.135f;

    public EnergyAIService(IHttpClientFactory httpClientFactory, ILogger<EnergyAIService> logger)
    {
        _httpClient = httpClientFactory.CreateClient("Anthropic");
        _logger = logger;
        _apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
                      ?? throw new InvalidOperationException("ANTHROPIC_API_KEY environment variable is not set.");
    }

    // ── Public interface ────────────────────────────────────────────────────

    public EnergyAIOutput Predict(EnergyAIInput input)
        => PredictAsync(input, CancellationToken.None).GetAwaiter().GetResult();

    public async Task<EnergyAIOutput> PredictAsync(EnergyAIInput input, CancellationToken ct = default)
    {
        var usageCategory = input.UsageUnits switch
        {
            < 100 => "very low (energy-efficient household)",
            < 250 => "average",
            < 450 => "above average",
            _ => "high (review heavy appliance usage)"
        };

        var heatContext = input.Temperature switch
        {
            > 35 => "extremely hot — high A/C demand expected",
            > 28 => "warm — moderate cooling load",
            > 20 => "comfortable — minimal heating or cooling needed",
            > 10 => "cool — some heating required",
            _ => "cold — high heating demand expected"
        };

        var naiveBill = Math.Round(input.UsageUnits * RatePerKwh, 2);

        var prompt =
            "You are a smart city energy management AI. Estimate a household's monthly electricity\n" +
            "bill and give one highly specific, actionable saving tip.\n" +
            "Respond with ONLY the JSON object below — no markdown, no extra text.\n\n" +
            "=== Household Profile ===\n" +
            $"- Monthly usage      : {input.UsageUnits} kWh  ({usageCategory})\n" +
            $"- Outside temperature: {input.Temperature}°C   ({heatContext})\n" +
            $"- Number of occupants: {input.Occupants}\n" +
            $"- Baseline estimate  : ${naiveBill} USD (at ${RatePerKwh}/kWh)\n\n" +
            "=== Instructions ===\n" +
            "1. Adjust the bill estimate for temperature-driven load and occupant count.\n" +
            "   The adjusted figure may be up to 30 % above or below the baseline.\n" +
            "2. Write ONE specific tip tailored to the usage category and temperature.\n" +
            "   - High usage + heat → cooling optimisation tips.\n" +
            "   - High usage + cold → heating efficiency tips.\n" +
            "   - Low usage         → maintenance or smart-device tips.\n" +
            "3. Keep the tip under 20 words.\n\n" +
            "{\n" +
            "  \"predictedBill\": <float, USD>,\n" +
            "  \"currency\"     : \"USD\",\n" +
            "  \"tip\"          : \"<specific saving recommendation>\"\n" +
            "}";

        for (var attempt = 0; attempt <= RetryCount; attempt++)
        {
            try
            {
                var text = await CallClaudeAsync(prompt, MaxTokens, ct);
                using var doc = JsonDocument.Parse(CleanJson(text));
                var root = doc.RootElement;

                var bill = Math.Max(0f, root.GetProperty("predictedBill").GetSingle());
                var tip = root.GetProperty("tip").GetString() ?? FallbackTip(input);

                _logger.LogInformation(
                    "[EnergyAI] Prediction OK — bill={Bill} USD", bill);

                return new EnergyAIOutput
                {
                    PredictedBill = bill,
                    Currency = root.GetProperty("currency").GetString() ?? "USD",
                    Tip = tip
                };
            }
            catch (Exception ex) when (attempt < RetryCount)
            {
                _logger.LogWarning(ex, "[EnergyAI] Attempt {Attempt} failed, retrying…", attempt + 1);
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[EnergyAI] All attempts failed — using deterministic fallback.");
            }
        }

        return FallbackPredict(input);
    }

    // ── Fallback ────────────────────────────────────────────────────────────

    private static EnergyAIOutput FallbackPredict(EnergyAIInput input)
    {
        // Temperature penalty: each °C above 25 or below 15 adds ~0.5 % to the bill.
        var tempPenalty = input.Temperature > 25
            ? 1f + (input.Temperature - 25f) * 0.005f
            : input.Temperature < 15
                ? 1f + (15f - input.Temperature) * 0.005f
                : 1f;

        var bill = (float)Math.Round(input.UsageUnits * RatePerKwh * tempPenalty + input.Occupants * 2.5f, 2);

        return new EnergyAIOutput
        {
            PredictedBill = bill,
            Currency = "USD",
            Tip = FallbackTip(input)
        };
    }

    private static string FallbackTip(EnergyAIInput input)
    {
        if (input.UsageUnits > 400)
            return "Usage is very high — audit high-wattage appliances and check for phantom loads.";
        if (input.UsageUnits > 250)
            return "Above-average usage detected — set your thermostat 2°C closer to ambient temperature.";
        if (input.Temperature > 30)
            return "Use ceiling fans with A/C set to 24–26°C to reduce cooling costs by up to 15%.";
        if (input.Temperature < 10)
            return "Seal draughts around windows and doors to cut heating bills by up to 20%.";
        return "Your energy usage looks healthy — consider a smart meter to track real-time consumption.";
    }

    // ── Claude HTTP call ────────────────────────────────────────────────────

    private async Task<string> CallClaudeAsync(string prompt, int maxTokens, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
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
        return doc.RootElement
                   .GetProperty("content")[0]
                   .GetProperty("text")
                   .GetString() ?? "{}";
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static string CleanJson(string text)
    {
        text = text.Trim();
        if (text.StartsWith("```"))
        {
            var lines = text.Split('\n', 2);
            text = lines.Length > 1 ? lines[1] : text;
        }
        if (text.EndsWith("```"))
            text = text[..text.LastIndexOf("```")];
        return text.Trim();
    }
}