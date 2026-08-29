using Microsoft.Extensions.Logging;
using SmartcityBackend.src.AI.Interfaces;
using SmartcityBackend.src.AI.Models;
using System.Text;
using System.Text.Json;

namespace SmartcityBackend.src.AI.Services;

/// <summary>
/// Claude-powered waste classification service.
/// Analyses an image URL (passed as a text description to Claude) to
/// classify waste into one of four categories, estimate confidence, and
/// calculate the CO₂ saving from correct disposal.
///
/// When a public image URL is supplied, the service sends it to Claude
/// as a vision message; when only a filename hint is available it falls
/// back to the text-only prompt.
///
/// Falls back to keyword matching when the API is unavailable.
/// </summary>
public class WasteAIService : IWasteAIService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<WasteAIService> _logger;
    private readonly string _apiKey;

    private const string ApiUrl = "https://api.anthropic.com/v1/messages";
    private const string Model = "claude-sonnet-4-6";
    private const string ApiVersion = "2023-06-01";
    private const int MaxTokens = 200;
    private const int RetryCount = 2;

    /// <summary>
    /// Reference CO₂ savings (kg) per item correctly sorted.
    /// Source: life-cycle analysis averages used across EU waste frameworks.
    /// </summary>
    private static readonly Dictionary<string, float> Co2Table = new(StringComparer.OrdinalIgnoreCase)
    {
        { "recyclable", 1.8f },   // avg across paper, plastic, metal, glass
        { "organic",    0.7f },   // compost vs landfill methane
        { "hazardous",  0.4f },   // avoided leachate / incineration penalty
        { "general",    0.05f }   // minimal saving; should be minimised
    };

    public WasteAIService(IHttpClientFactory httpClientFactory, ILogger<WasteAIService> logger)
    {
        _httpClient = httpClientFactory.CreateClient("Anthropic");
        _logger = logger;
        _apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
                      ?? throw new InvalidOperationException("ANTHROPIC_API_KEY environment variable is not set.");
    }

    // ── Public interface ────────────────────────────────────────────────────

    public WasteAIOutput Classify(WasteAIInput input)
        => ClassifyAsync(input, CancellationToken.None).GetAwaiter().GetResult();

    public async Task<WasteAIOutput> ClassifyAsync(WasteAIInput input, CancellationToken ct = default)
    {
        var hint = ExtractFilenameHint(input.ImageUrl);
        var isUrl = Uri.TryCreate(input.ImageUrl, UriKind.Absolute, out var uri)
                    && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

        for (var attempt = 0; attempt <= RetryCount; attempt++)
        {
            try
            {
                string text;

                if (isUrl)
                {
                    // ── Vision path: send image directly to Claude ──────────
                    text = await CallClaudeVisionAsync(input.ImageUrl, MaxTokens, ct);
                }
                else
                {
                    // ── Text-only path: describe the item by filename ────────
                    text = await CallClaudeTextAsync(hint, MaxTokens, ct);
                }

                using var doc = JsonDocument.Parse(CleanJson(text));
                var root = doc.RootElement;

                var category = NormaliseCategory(root.GetProperty("category").GetString() ?? "general");
                var confidence = Math.Clamp(root.GetProperty("confidence").GetSingle(), 0f, 1f);
                var co2Saved = Co2Table.TryGetValue(category, out var co2) ? co2 : 0.05f;

                _logger.LogInformation(
                    "[WasteAI] Classified as '{Category}' (conf={Conf:P0}, CO₂={Co2} kg)",
                    category, confidence, co2Saved);

                return new WasteAIOutput
                {
                    Category = category,
                    Confidence = confidence,
                    Co2Saved = co2Saved
                };
            }
            catch (Exception ex) when (attempt < RetryCount)
            {
                _logger.LogWarning(ex, "[WasteAI] Attempt {Attempt} failed, retrying…", attempt + 1);
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[WasteAI] All attempts failed — using keyword fallback.");
            }
        }

        return FallbackClassify(input.ImageUrl);
    }

    // ── Vision call (image URL → Claude vision) ─────────────────────────────

    private async Task<string> CallClaudeVisionAsync(string imageUrl, int maxTokens, CancellationToken ct)
    {
        var systemInstructions = """
            You are a waste classification AI for a smart city recycling programme.
            You will receive an image of a waste item.
            Classify it into exactly one of these four categories:
              "recyclable" — plastics, glass, metals, paper, cardboard
              "organic"    — food scraps, garden waste, biodegradables
              "hazardous"  — batteries, chemicals, electronics, medical waste
              "general"    — non-recyclable mixed or residual waste

            Reply with ONLY this JSON object (no markdown, no explanation):
            {
              "category"  : "<recyclable | organic | hazardous | general>",
              "confidence": <float 0.0–1.0>
            }
            """;

        // Build a vision message: image_url block + text instruction
        var messages = new[]
        {
            new
            {
                role    = "user",
                content = new object[]
                {
                    new { type = "image", source = new { type = "url", url = imageUrl } },
                    new { type = "text",  text   = "Classify this waste item." }
                }
            }
        };

        var body = JsonSerializer.Serialize(new
        {
            model = Model,
            max_tokens = maxTokens,
            system = systemInstructions,
            messages
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

    // ── Text-only call (filename hint → Claude text) ─────────────────────────

    private async Task<string> CallClaudeTextAsync(string hint, int maxTokens, CancellationToken ct)
    {
        var prompt =
            "You are a waste classification AI for a smart city recycling programme.\n" +
            "Classify the waste item described below into exactly one category.\n\n" +
            $"Item description / filename hint: \"{hint}\"\n\n" +
            "Categories:\n" +
            "  \"recyclable\" — plastics, glass, metals, paper, cardboard\n" +
            "  \"organic\"    — food scraps, garden waste, biodegradables\n" +
            "  \"hazardous\"  — batteries, chemicals, electronics, medical waste\n" +
            "  \"general\"    — non-recyclable mixed or residual waste\n\n" +
            "Reply with ONLY this JSON object (no markdown, no explanation):\n" +
            "{\n" +
            "  \"category\"  : \"<recyclable | organic | hazardous | general>\",\n" +
            "  \"confidence\": <float 0.0-1.0>\n" +
            "}";

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

    // ── Fallback (keyword matching) ──────────────────────────────────────────

    private static WasteAIOutput FallbackClassify(string imageUrl)
    {
        var url = imageUrl.ToLowerInvariant();

        if (ContainsAny(url, "plastic", "bottle", "paper", "cardboard", "metal", "glass", "can", "tin", "recycle"))
            return new WasteAIOutput { Category = "recyclable", Confidence = 0.90f, Co2Saved = Co2Table["recyclable"] };

        if (ContainsAny(url, "banana", "peel", "food", "organic", "fruit", "veg", "garden", "compost", "leaf", "grass"))
            return new WasteAIOutput { Category = "organic", Confidence = 0.87f, Co2Saved = Co2Table["organic"] };

        if (ContainsAny(url, "battery", "chemical", "electronic", "phone", "circuit", "bulb", "medical", "needle", "paint"))
            return new WasteAIOutput { Category = "hazardous", Confidence = 0.85f, Co2Saved = Co2Table["hazardous"] };

        return new WasteAIOutput { Category = "general", Confidence = 0.70f, Co2Saved = Co2Table["general"] };
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string ExtractFilenameHint(string imageUrl)
    {
        if (Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri))
            return Path.GetFileNameWithoutExtension(uri.AbsolutePath);
        return imageUrl;
    }

    private static string NormaliseCategory(string raw)
    {
        raw = raw.Trim().ToLowerInvariant();
        return raw is "recyclable" or "organic" or "hazardous" or "general"
            ? raw
            : "general";
    }

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

    private static bool ContainsAny(string source, params string[] keywords)
        => keywords.Any(source.Contains);
}