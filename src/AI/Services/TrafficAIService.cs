using Microsoft.Extensions.Logging;
using SmartcityBackend.src.AI.Interfaces;
using SmartcityBackend.src.AI.Models;
using System.Text;
using System.Text.Json;

namespace SmartcityBackend.src.AI.Services;

/// <summary>
/// Claude-powered traffic congestion prediction service.
/// Uses the Anthropic Messages API to analyse real-time conditions
/// and return a congestion score (0–100), a human-readable status,
/// and a practical driver suggestion.
/// Falls back to a deterministic formula when the API is unavailable.
/// </summary>
public class TrafficAIService : ITrafficAIService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<TrafficAIService> _logger;
    private readonly string _apiKey;

    private const string ApiUrl = "https://api.anthropic.com/v1/messages";
    private const string Model = "claude-sonnet-4-6";
    private const string ApiVersion = "2023-06-01";
    private const int MaxTokens = 250;
    private const int RetryCount = 2;

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public TrafficAIService(IHttpClientFactory httpClientFactory, ILogger<TrafficAIService> logger)
    {
        _httpClient = httpClientFactory.CreateClient("Anthropic");
        _logger = logger;
        _apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
                      ?? throw new InvalidOperationException("ANTHROPIC_API_KEY environment variable is not set.");
    }

    // ── Public interface ────────────────────────────────────────────────────

    public TrafficAIOutput Predict(TrafficAIInput input)
        => PredictAsync(input, CancellationToken.None).GetAwaiter().GetResult();

    public async Task<TrafficAIOutput> PredictAsync(TrafficAIInput input, CancellationToken ct = default)
    {
        var weatherLabel = (int)input.Weather switch
        {
            0 => "Clear",
            1 => "Cloudy",
            2 => "Rainy",
            3 => "Stormy",
            _ => "Unknown"
        };

        var dayLabel = (int)input.Day switch
        {
            0 => "Sunday",
            1 => "Monday",
            2 => "Tuesday",
            3 => "Wednesday",
            4 => "Thursday",
            5 => "Friday",
            6 => "Saturday",
            _ => "Weekday"
        };

        var isRushHour = (input.Hour is >= 7 and <= 9) || (input.Hour is >= 17 and <= 19);
        var isWeekend = input.Day is 0 or 6;

        var prompt =
            "You are a smart city traffic analysis AI. Your job is to predict road congestion\n" +
            "based on real-time sensor data and return structured JSON only — no markdown, no explanation.\n\n" +
            "=== Current Conditions ===\n" +
            $"- Time of day    : {input.Hour:00}:00\n" +
            $"- Day of week    : {dayLabel}\n" +
            $"- Rush hour      : {(isRushHour ? "Yes" : "No")}\n" +
            $"- Weekend        : {(isWeekend ? "Yes" : "No")}\n" +
            $"- Weather        : {weatherLabel}\n" +
            $"- Vehicle count  : {input.VehicleCount}\n\n" +
            "=== Instructions ===\n" +
            "1. Predict a congestion level from 0 (no traffic) to 100 (gridlock).\n" +
            "2. Classify status as exactly one of: \"Low Congestion\", \"Moderate Congestion\", \"High Congestion\".\n" +
            "3. Write one short, practical tip for drivers (max 15 words).\n\n" +
            "Respond with ONLY this JSON object:\n" +
            "{\n" +
            "  \"predictedCongestionLevel\": <float 0-100>,\n" +
            "  \"status\": \"<Low Congestion | Moderate Congestion | High Congestion>\",\n" +
            "  \"suggestion\": \"<driver tip>\"\n" +
            "}";

        for (var attempt = 0; attempt <= RetryCount; attempt++)
        {
            try
            {
                var text = await CallClaudeAsync(prompt, MaxTokens, ct);
                using var doc = JsonDocument.Parse(CleanJson(text));
                var root = doc.RootElement;

                var level = Math.Clamp(
                    root.GetProperty("predictedCongestionLevel").GetSingle(),
                    0f, 100f);

                _logger.LogInformation(
                    "[TrafficAI] Prediction OK — level={Level}, status={Status}",
                    level, root.GetProperty("status").GetString());

                return new TrafficAIOutput
                {
                    PredictedCongestionLevel = level,
                    Status = root.GetProperty("status").GetString() ?? StatusFromLevel(level),
                    Suggestion = root.GetProperty("suggestion").GetString() ?? DefaultSuggestion(level)
                };
            }
            catch (Exception ex) when (attempt < RetryCount)
            {
                _logger.LogWarning(ex, "[TrafficAI] Attempt {Attempt} failed, retrying…", attempt + 1);
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct); // exponential back-off
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[TrafficAI] All attempts failed — using deterministic fallback.");
            }
        }

        return FallbackPredict(input);
    }

    // ── Fallback (deterministic) ────────────────────────────────────────────

    private static TrafficAIOutput FallbackPredict(TrafficAIInput input)
    {
        var isRushHour = (input.Hour is >= 7 and <= 9) || (input.Hour is >= 17 and <= 19);
        var level = Math.Clamp(
            (input.VehicleCount * 0.08f)
            + (isRushHour ? 20f : 0f)
            + (input.Hour * 1.2f)
            + (input.Weather * 6f),
            0f, 100f);

        return new TrafficAIOutput
        {
            PredictedCongestionLevel = level,
            Status = StatusFromLevel(level),
            Suggestion = DefaultSuggestion(level)
        };
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
        // Strip optional ```json ... ``` fences
        if (text.StartsWith("```"))
        {
            var lines = text.Split('\n', 2);
            text = lines.Length > 1 ? lines[1] : text;
        }
        if (text.EndsWith("```"))
            text = text[..text.LastIndexOf("```")];
        return text.Trim();
    }

    private static string StatusFromLevel(float level)
        => level >= 75 ? "High Congestion"
         : level >= 40 ? "Moderate Congestion"
         : "Low Congestion";

    private static string DefaultSuggestion(float level)
        => level >= 75 ? "Heavy traffic ahead — consider an alternate route or delay your trip."
         : level >= 40 ? "Moderate traffic — allow extra travel time and drive carefully."
         : "Roads are clear — safe to proceed on your normal route.";
}