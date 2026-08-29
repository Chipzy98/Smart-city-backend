using SmartcityBackend.src.AI.Interfaces;
using SmartcityBackend.src.AI.Models;
using System.Text;
using System.Text.Json;

namespace SmartcityBackend.src.AI.Services;

public class WasteAIService : IWasteAIService
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;

    private const string ApiUrl = "https://api.anthropic.com/v1/messages";
    private const string Model  = "claude-sonnet-4-6";

    public WasteAIService(IHttpClientFactory httpClientFactory)
    {
        _httpClient = httpClientFactory.CreateClient("Anthropic");
        _apiKey     = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
                      ?? throw new InvalidOperationException("ANTHROPIC_API_KEY not set.");
    }

    public WasteAIOutput Classify(WasteAIInput input)
        => ClassifyAsync(input).GetAwaiter().GetResult();

    // ─────────────────────────────────────────────────────────────
    private async Task<WasteAIOutput> ClassifyAsync(WasteAIInput input)
    {
        // URL eken filename hint ganna
        var hint = Uri.TryCreate(input.ImageUrl, UriKind.Absolute, out var uri)
            ? Path.GetFileNameWithoutExtension(uri.AbsolutePath)
            : input.ImageUrl;

        var prompt = $$"""
            You are a smart city waste classification AI.
            Classify the waste item based on the image URL/filename provided.

            Image URL     : {{input.ImageUrl}}
            Filename hint : {{hint}}

            Categories:
            - "recyclable" -> plastics, glass, metals, paper, cardboard
            - "organic"    -> food scraps, garden waste, biodegradables
            - "hazardous"  -> batteries, chemicals, electronics, medical waste
            - "general"    -> non-recyclable mixed waste

            Reply ONLY with a JSON object, no markdown, no explanation:
            {
              "category"  : "<recyclable | organic | hazardous | general>",
              "confidence": <float 0.0-1.0>,
              "co2Saved"  : <float, estimated kg CO2 saved by proper disposal>
            }
            """;

        try
        {
            var text = await CallClaudeAsync(prompt, maxTokens: 150);

            using var doc = JsonDocument.Parse(CleanJson(text));
            var root      = doc.RootElement;

            return new WasteAIOutput
            {
                Category   = root.GetProperty("category").GetString()   ?? "general",
                Confidence = Math.Clamp(root.GetProperty("confidence").GetSingle(), 0f, 1f),
                Co2Saved   = Math.Max(0f, root.GetProperty("co2Saved").GetSingle())
            };
        }
        catch
        {
            // Fallback: original keyword matching
            var url = input.ImageUrl.ToLower();

            if (url.Contains("plastic") || url.Contains("bottle") || url.Contains("paper") || url.Contains("metal") || url.Contains("glass"))
                return new WasteAIOutput { Category = "recyclable", Confidence = 0.92f, Co2Saved = 1.5f };

            if (url.Contains("banana") || url.Contains("food") || url.Contains("organic") || url.Contains("garden"))
                return new WasteAIOutput { Category = "organic", Confidence = 0.88f, Co2Saved = 0.8f };

            if (url.Contains("battery") || url.Contains("chemical") || url.Contains("electronic"))
                return new WasteAIOutput { Category = "hazardous", Confidence = 0.84f, Co2Saved = 0.3f };

            return new WasteAIOutput { Category = "general", Confidence = 0.75f, Co2Saved = 0.1f };
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
