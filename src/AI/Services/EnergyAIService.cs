using SmartcityBackend.src.AI.Interfaces;
using SmartcityBackend.src.AI.Models;
using System.Text;
using System.Text.Json;

namespace SmartcityBackend.src.AI.Services;

public class EnergyAIService : IEnergyAIService
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;

    private const string ApiUrl = "https://api.anthropic.com/v1/messages";
    private const string Model  = "claude-sonnet-4-6";

    public EnergyAIService(IHttpClientFactory httpClientFactory)
    {
        _httpClient = httpClientFactory.CreateClient("Anthropic");
        _apiKey     = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
                      ?? throw new InvalidOperationException("ANTHROPIC_API_KEY not set.");
    }

    public EnergyAIOutput Predict(EnergyAIInput input)
        => PredictAsync(input).GetAwaiter().GetResult();

    // ─────────────────────────────────────────────────────────────
    private async Task<EnergyAIOutput> PredictAsync(EnergyAIInput input)
    {
        var prompt = $$"""
            You are a smart city energy management AI.
            Estimate the monthly electricity bill and give a personalized saving tip.

            Household data:
            - Monthly usage  : {{input.UsageUnits}} kWh
            - Avg temperature: {{input.Temperature}}°C
            - Occupants      : {{input.Occupants}}

            Typical rate: ~$0.13 per kWh (US average).

            Reply ONLY with a JSON object, no markdown, no explanation:
            {
              "predictedBill": <float, USD>,
              "currency": "USD",
              "tip": "<one specific, actionable energy-saving recommendation>"
            }
            """;

        try
        {
            var text = await CallClaudeAsync(prompt, maxTokens: 250);

            using var doc = JsonDocument.Parse(CleanJson(text));
            var root      = doc.RootElement;

            return new EnergyAIOutput
            {
                PredictedBill = root.GetProperty("predictedBill").GetSingle(),
                Currency      = root.GetProperty("currency").GetString() ?? "USD",
                Tip           = root.GetProperty("tip").GetString()      ?? "Monitor your usage regularly."
            };
        }
        catch
        {
            // Fallback: original mock formula
            var bill = (input.UsageUnits * 0.35f) + (input.Temperature * 1.2f) + (input.Occupants * 3);
            var tip  = input.UsageUnits > 180
                ? "High energy usage detected. Reduce AC and heavy appliance usage."
                : input.Temperature > 32
                    ? "Temperature is high. Optimize cooling settings."
                    : "Energy usage is normal.";

            return new EnergyAIOutput { PredictedBill = bill, Currency = "USD", Tip = tip };
        }
    }

    // ─────────────────────────────────────────────────────────────
    private async Task<string> CallClaudeAsync(string prompt, int maxTokens)
    {
        var body = JsonSerializer.Serialize(new
        {
            model      = Model,
            max_tokens = maxTokens,
            messages   = new[] { new { role = "user", content = prompt } }
        });

        var req = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
        req.Headers.Add("x-api-key",         _apiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");

        var res = await _httpClient.SendAsync(req);
        res.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString() ?? "{}";
    }

    private static string CleanJson(string text)
    {
        text = text.Trim();
        if (text.StartsWith("```")) text = text.Split('\n', 2).Last();
        if (text.EndsWith("```"))   text = text[..text.LastIndexOf("```")];
        return text.Trim();
    }
}
