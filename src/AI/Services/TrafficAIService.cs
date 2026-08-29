using SmartcityBackend.src.AI.Interfaces;
using SmartcityBackend.src.AI.Models;
using System.Text;
using System.Text.Json;

namespace SmartcityBackend.src.AI.Services;

public class TrafficAIService : ITrafficAIService
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;

    private const string ApiUrl = "https://api.anthropic.com/v1/messages";
    private const string Model  = "claude-sonnet-4-6";

    // IHttpClientFactory DI walata — Program.cs ta AddHttpClient("Anthropic") add karanna
    public TrafficAIService(IHttpClientFactory httpClientFactory)
    {
        _httpClient = httpClientFactory.CreateClient("Anthropic");
        _apiKey     = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
                      ?? throw new InvalidOperationException("ANTHROPIC_API_KEY not set.");
    }

    public TrafficAIOutput Predict(TrafficAIInput input)
        => PredictAsync(input).GetAwaiter().GetResult();

    // ─────────────────────────────────────────────────────────────
    private async Task<TrafficAIOutput> PredictAsync(TrafficAIInput input)
    {
        var weatherLabel = (int)input.Weather switch
        {
            0 => "Clear", 1 => "Cloudy", 2 => "Rainy", 3 => "Stormy", _ => "Unknown"
        };

        var dayLabel = (int)input.Day switch
        {
            0 => "Sunday", 1 => "Monday", 2 => "Tuesday", 3 => "Wednesday",
            4 => "Thursday", 5 => "Friday", 6 => "Saturday", _ => "Weekday"
        };

        var prompt = $$"""
            You are a smart city traffic analysis AI.
            Predict the congestion level (0–100) for the following real-time conditions.

            - Hour of day  : {{input.Hour}}:00
            - Day of week  : {{dayLabel}}
            - Weather      : {{weatherLabel}}
            - Vehicle count: {{input.VehicleCount}}

            Reply ONLY with a JSON object, no markdown, no explanation:
            {
              "predictedCongestionLevel": <float 0-100>,
              "status": "<Low Congestion | Moderate Congestion | High Congestion>",
              "suggestion": "<one clear, practical tip for drivers>"
            }
            """;

        try
        {
            var text = await CallClaudeAsync(prompt, maxTokens: 200);

            using var doc  = JsonDocument.Parse(CleanJson(text));
            var root       = doc.RootElement;
            var level      = Math.Clamp(root.GetProperty("predictedCongestionLevel").GetSingle(), 0f, 100f);

            return new TrafficAIOutput
            {
                PredictedCongestionLevel = level,
                Status     = root.GetProperty("status").GetString()     ?? StatusFromLevel(level),
                Suggestion = root.GetProperty("suggestion").GetString() ?? "Drive safely."
            };
        }
        catch
        {
            // Fallback: original mock formula
            var level = Math.Clamp(
                (input.VehicleCount * 0.08f) + (input.Hour * 1.5f) + (input.Weather * 5),
                0f, 100f);

            return new TrafficAIOutput
            {
                PredictedCongestionLevel = level,
                Status     = StatusFromLevel(level),
                Suggestion = level >= 75 ? "Use an alternative route."
                           : level >= 40 ? "Drive carefully. Moderate traffic expected."
                           : "Route is clear."
            };
        }
    }

    // ─────────────────────────────────────────────────────────────
    private async Task<string> CallClaudeAsync(string prompt, int maxTokens)
    {
        var body = JsonSerializer.Serialize(new
        {
            model    = Model,
            max_tokens = maxTokens,
            messages = new[] { new { role = "user", content = prompt } }
        });

        var req = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
        req.Headers.Add("x-api-key",          _apiKey);
        req.Headers.Add("anthropic-version",  "2023-06-01");
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");

        var res  = await _httpClient.SendAsync(req);
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

    private static string StatusFromLevel(float level)
        => level >= 75 ? "High Congestion" : level >= 40 ? "Moderate Congestion" : "Low Congestion";
}
