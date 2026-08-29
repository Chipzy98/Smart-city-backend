using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using SmartcityBackend.src.AI.Interfaces;
using SmartcityBackend.src.AI.Models;
using System.Text;
using System.Text.Json;

namespace SmartcityBackend.src.AI.Services;

public class TrafficAIService : ITrafficAIService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<TrafficAIService> _logger;
    private readonly string _apiKey;

    private const string ApiUrl = "https://api.anthropic.com/v1/messages";
    private const string Model = "claude-sonnet-4-6";
    private const string ApiVersion = "2023-06-01";
    private const int MaxTokens = 300;
    private const int RetryCount = 2;

    // Training data context file path
    private static readonly string TrainingFile =
        Path.Combine(AppContext.BaseDirectory, "training_data", "traffic_context.json");

    public TrafficAIService(IHttpClientFactory httpClientFactory, ILogger<TrafficAIService> logger)
    {
        _httpClient = httpClientFactory.CreateClient("Anthropic");
        _logger = logger;
        _apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
                      ?? throw new InvalidOperationException("ANTHROPIC_API_KEY not set.");
    }

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

        // Load training context if available
        var trainingContext = LoadTrainingContext();

        var prompt =
            "You are a smart city traffic analysis AI.\n" +
            "Predict the congestion level (0-100) and return ONLY JSON.\n\n" +
            "=== Current Conditions ===\n" +
            $"- Time: {input.Hour:00}:00  |  Day: {dayLabel}  |  Rush hour: {(isRushHour ? "Yes" : "No")}  |  Weekend: {(isWeekend ? "Yes" : "No")}\n" +
            $"- Weather: {weatherLabel}  |  Vehicle count: {input.VehicleCount}\n\n" +
            trainingContext +
            "Reply ONLY with this JSON (no markdown):\n" +
            "{\n" +
            "  \"predictedCongestionLevel\": <float 0-100>,\n" +
            "  \"status\": \"<Low Congestion | Moderate Congestion | High Congestion>\",\n" +
            "  \"suggestion\": \"<one practical tip for drivers>\"\n" +
            "}";

        for (var attempt = 0; attempt <= RetryCount; attempt++)
        {
            try
            {
                var text = await CallClaudeAsync(prompt, MaxTokens, ct);
                using var doc = JsonDocument.Parse(CleanJson(text));
                var root = doc.RootElement;
                var level = Math.Clamp(root.GetProperty("predictedCongestionLevel").GetSingle(), 0f, 100f);

                _logger.LogInformation("[TrafficAI] level={Level}", level);

                return new TrafficAIOutput
                {
                    PredictedCongestionLevel = level,
                    Status = root.GetProperty("status").GetString() ?? StatusFromLevel(level),
                    Suggestion = root.GetProperty("suggestion").GetString() ?? DefaultSuggestion(level)
                };
            }
            catch (Exception ex) when (attempt < RetryCount)
            {
                _logger.LogWarning(ex, "[TrafficAI] Attempt {A} failed, retrying…", attempt + 1);
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[TrafficAI] All attempts failed — fallback.");
            }
        }

        return FallbackPredict(input);
    }

    // ── Training context loader ───────────────────────────────────────────────
    private static string LoadTrainingContext()
    {
        try
        {
            if (!File.Exists(TrainingFile)) return string.Empty;

            var json = File.ReadAllText(TrainingFile);
            var ctx = JsonConvert.DeserializeObject<dynamic>(json);
            if (ctx == null) return string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine("=== Historical Training Data (use as reference) ===");
            sb.AppendLine("hour,day,weather,vehicleCount → congestionLevel");

            var rows = ctx.Rows as Newtonsoft.Json.Linq.JArray;
            var headers = ctx.Headers as Newtonsoft.Json.Linq.JArray;
            if (rows == null || headers == null) return string.Empty;

            // Take up to 10 example rows
            foreach (var row in rows.Take(10))
            {
                sb.AppendLine(string.Join(", ", row));
            }
            sb.AppendLine();

            return sb.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    // ── Claude API call ───────────────────────────────────────────────────────
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
        if (text.StartsWith("```")) { var lines = text.Split('\n', 2); text = lines.Length > 1 ? lines[1] : text; }
        if (text.EndsWith("```")) text = text[..text.LastIndexOf("```")];
        return text.Trim();
    }

    private static TrafficAIOutput FallbackPredict(TrafficAIInput input)
    {
        var isRushHour = (input.Hour is >= 7 and <= 9) || (input.Hour is >= 17 and <= 19);
        var level = Math.Clamp(
            (input.VehicleCount * 0.08f) + (isRushHour ? 20f : 0f)
            + (input.Hour * 1.2f) + (input.Weather * 6f), 0f, 100f);
        return new TrafficAIOutput
        {
            PredictedCongestionLevel = level,
            Status = StatusFromLevel(level),
            Suggestion = DefaultSuggestion(level)
        };
    }

    private static string StatusFromLevel(float l)
        => l >= 75 ? "High Congestion" : l >= 40 ? "Moderate Congestion" : "Low Congestion";

    private static string DefaultSuggestion(float l)
        => l >= 75 ? "Heavy traffic — consider alternate route or delay trip."
         : l >= 40 ? "Moderate traffic — allow extra travel time."
         : "Roads are clear — safe to proceed.";
}