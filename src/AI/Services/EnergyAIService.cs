using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using SmartcityBackend.src.AI.Interfaces;
using SmartcityBackend.src.AI.Models;
using System.Text;
using System.Text.Json;

namespace SmartcityBackend.src.AI.Services;

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
    private const float RatePerKwh = 0.135f;

    private static readonly string TrainingFile =
        Path.Combine(AppContext.BaseDirectory, "training_data", "energy_context.json");

    public EnergyAIService(IHttpClientFactory httpClientFactory, ILogger<EnergyAIService> logger)
    {
        _httpClient = httpClientFactory.CreateClient("Anthropic");
        _logger = logger;
        _apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
                      ?? throw new InvalidOperationException("ANTHROPIC_API_KEY not set.");
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

        var naiveBill = Math.Round(input.UsageUnits * RatePerKwh, 2);
        var trainingContext = LoadTrainingContext();

        var prompt =
            "You are a smart city energy management AI.\n" +
            "Estimate the monthly electricity bill and give one specific saving tip.\n" +
            "Reply ONLY with JSON — no markdown.\n\n" +
            "=== Household Data ===\n" +
            $"- Monthly usage : {input.UsageUnits} kWh ({usageCategory})\n" +
            $"- Temperature   : {input.Temperature}°C ({heatContext})\n" +
            $"- Occupants     : {input.Occupants}\n" +
            $"- Baseline bill : ${naiveBill} USD at $0.135/kWh\n\n" +
            trainingContext +
            "Adjust the bill up to ±30% from baseline based on temperature and occupants.\n" +
            "Give ONE specific tip for this household's situation.\n\n" +
            "{\n" +
            "  \"predictedBill\": <float USD>,\n" +
            "  \"currency\"     : \"USD\",\n" +
            "  \"tip\"          : \"<specific saving tip>\"\n" +
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

                _logger.LogInformation("[EnergyAI] bill={Bill} USD", bill);
                return new EnergyAIOutput { PredictedBill = bill, Currency = "USD", Tip = tip };
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

        return FallbackPredict(input);
    }

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

    private static EnergyAIOutput FallbackPredict(EnergyAIInput input)
    {
        var tempPenalty = input.Temperature > 25
            ? 1f + (input.Temperature - 25f) * 0.005f
            : input.Temperature < 15
                ? 1f + (15f - input.Temperature) * 0.005f
                : 1f;
        var bill = (float)Math.Round(input.UsageUnits * RatePerKwh * tempPenalty + input.Occupants * 2.5f, 2);
        return new EnergyAIOutput { PredictedBill = bill, Currency = "USD", Tip = FallbackTip(input) };
    }

    private static string FallbackTip(EnergyAIInput input)
    {
        if (input.UsageUnits > 400) return "Usage is very high — audit high-wattage appliances.";
        if (input.Temperature > 30) return "Set A/C to 24–26°C and use ceiling fans to cut cooling costs.";
        if (input.Temperature < 10) return "Seal draughts around windows to cut heating bills by up to 20%.";
        return "Your usage looks healthy — consider a smart meter to track real-time consumption.";
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