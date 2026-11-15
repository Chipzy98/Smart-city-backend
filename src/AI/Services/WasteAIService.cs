using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using SmartcityBackend.src.AI.Interfaces;
using SmartcityBackend.src.AI.Models;
using SmartcityBackend.src.Helpers;
using System.Text;
using System.Text.Json;

namespace SmartcityBackend.src.AI.Services;

public class WasteAIService : IWasteAIService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<WasteAIService> _logger;
    private readonly string _apiKey;

    private const string ApiUrl = "https://api.anthropic.com/v1/messages";
    private static string Model => AppConfig.AnthropicModel;
    private const string ApiVersion = "2023-06-01";
    private const int MaxTokens = 200;
    private const int RetryCount = 2;

    private static readonly Dictionary<string, float> Co2Table = new(StringComparer.OrdinalIgnoreCase)
    {
        { "recyclable", 1.8f },
        { "organic",    0.7f },
        { "hazardous",  0.4f },
        { "general",    0.05f }
    };

    private static string TrainingFile => AppConfig.TrainingFile("waste");

    public WasteAIService(IHttpClientFactory httpClientFactory, ILogger<WasteAIService> logger)
    {
        _httpClient = httpClientFactory.CreateClient("Anthropic");
        _logger = logger;
        _apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
                      ?? string.Empty; // missing key → calls fail → rule-based fallback
    }

    public WasteAIOutput Classify(WasteAIInput input)
        => ClassifyAsync(input, CancellationToken.None).GetAwaiter().GetResult();

    public async Task<WasteAIOutput> ClassifyAsync(WasteAIInput input, CancellationToken ct = default)
    {
        var hint = ExtractHint(input.ImageUrl);
        var isUrl = Uri.TryCreate(input.ImageUrl, UriKind.Absolute, out var uri)
                    && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        var hasUpload = !string.IsNullOrWhiteSpace(input.ImageBase64)
                        && SupportedMediaTypes.Contains(input.MediaType ?? string.Empty);

        var trainingContext = LoadTrainingContext();

        for (var attempt = 0; attempt <= RetryCount && !string.IsNullOrWhiteSpace(_apiKey); attempt++) // no key → straight to fallback
        {
            try
            {
                // Uploaded image → base64 vision, URL → URL vision, otherwise text hint only
                object imageSource = hasUpload
                    ? new { type = "base64", media_type = input.MediaType, data = input.ImageBase64 }
                    : new { type = "url", url = input.ImageUrl };

                string text = hasUpload || isUrl
                    ? await CallClaudeVisionAsync(imageSource, trainingContext, MaxTokens, ct)
                    : await CallClaudeTextAsync(hint, trainingContext, MaxTokens, ct);

                using var doc = JsonDocument.Parse(CleanJson(text));
                var root = doc.RootElement;

                var category = NormaliseCategory(root.GetProperty("category").GetString() ?? "general");
                var confidence = Math.Clamp(root.GetProperty("confidence").GetSingle(), 0f, 1f);
                var co2Saved = Co2Table.TryGetValue(category, out var co2) ? co2 : 0.05f;

                _logger.LogInformation("[WasteAI] {Cat} conf={Conf:P0} vision={Vision}", category, confidence, hasUpload || isUrl);
                return new WasteAIOutput { Category = category, Confidence = confidence, Co2Saved = co2Saved };
            }
            catch (Exception ex) when (attempt < RetryCount)
            {
                _logger.LogWarning(ex, "[WasteAI] Attempt {A} failed, retrying…", attempt + 1);
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[WasteAI] All attempts failed — fallback.");
            }
        }

        return FallbackClassify(input.ImageUrl);
    }

    // ── Vision call ───────────────────────────────────────────────────────

    private static readonly HashSet<string> SupportedMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/gif", "image/webp"
    };

    private async Task<string> CallClaudeVisionAsync(
        object imageSource, string trainingContext, int maxTokens, CancellationToken ct)
    {
        var systemPrompt =
            "You are a waste classification AI for a smart city recycling programme.\n" +
            "Classify the waste item in the image into exactly one category:\n" +
            "  recyclable — plastics, glass, metals, paper, cardboard\n" +
            "  organic    — food scraps, garden waste, biodegradables\n" +
            "  hazardous  — batteries, chemicals, electronics, medical waste\n" +
            "  general    — non-recyclable mixed waste\n" +
            trainingContext +
            "Reply ONLY with JSON:\n" +
            "{ \"category\": \"<category>\", \"confidence\": <0.0-1.0> }";

        var messages = new[]
        {
            new
            {
                role    = "user",
                content = new object[]
                {
                    new { type = "image", source = imageSource },
                    new { type = "text",  text   = "Classify this waste item." }
                }
            }
        };

        var body = System.Text.Json.JsonSerializer.Serialize(new
        {
            model = Model,
            max_tokens = maxTokens,
            system = systemPrompt,
            messages
        });

        return await SendRequest(body, ct);
    }

    // ── Text-only call ────────────────────────────────────────────────────

    private async Task<string> CallClaudeTextAsync(
        string hint, string trainingContext, int maxTokens, CancellationToken ct)
    {
        var prompt =
            "You are a waste classification AI for a smart city recycling programme.\n" +
            $"Item hint: \"{hint}\"\n\n" +
            "Categories:\n" +
            "  recyclable — plastics, glass, metals, paper, cardboard\n" +
            "  organic    — food scraps, garden waste, biodegradables\n" +
            "  hazardous  — batteries, chemicals, electronics, medical waste\n" +
            "  general    — non-recyclable mixed waste\n\n" +
            trainingContext +
            "Reply ONLY with JSON:\n" +
            "{ \"category\": \"<category>\", \"confidence\": <0.0-1.0> }";

        var body = System.Text.Json.JsonSerializer.Serialize(new
        {
            model = Model,
            max_tokens = maxTokens,
            messages = new[] { new { role = "user", content = prompt } }
        });

        return await SendRequest(body, ct);
    }

    private async Task<string> SendRequest(string body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
        req.Headers.Add("x-api-key", _apiKey);
        req.Headers.Add("anthropic-version", ApiVersion);
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");

        var res = await _httpClient.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString() ?? "{}";
    }

    // ── Training context ──────────────────────────────────────────────────

    private static string LoadTrainingContext()
    {
        try
        {
            if (!File.Exists(TrainingFile)) return string.Empty;

            var ctx = JsonConvert.DeserializeObject<dynamic>(File.ReadAllText(TrainingFile));
            var rows = ctx?.Rows as Newtonsoft.Json.Linq.JArray;
            if (rows == null || rows.Count == 0) return string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine("\n=== Historical Waste Examples (reference) ===");
            foreach (var row in rows.Take(8)) sb.AppendLine(string.Join(", ", row));
            sb.AppendLine();
            return sb.ToString();
        }
        catch { return string.Empty; }
    }

    // ── Fallback ──────────────────────────────────────────────────────────

    private static WasteAIOutput FallbackClassify(string imageUrl)
    {
        var url = imageUrl.ToLowerInvariant();

        if (ContainsAny(url, "plastic", "bottle", "paper", "cardboard", "metal", "glass", "can", "tin", "recycle"))
            return new WasteAIOutput { Category = "recyclable", Confidence = 0.60f, Co2Saved = Co2Table["recyclable"] };
        if (ContainsAny(url, "banana", "peel", "food", "organic", "fruit", "veg", "garden", "compost", "leaf"))
            return new WasteAIOutput { Category = "organic", Confidence = 0.60f, Co2Saved = Co2Table["organic"] };
        if (ContainsAny(url, "battery", "chemical", "electronic", "phone", "circuit", "bulb", "medical", "paint"))
            return new WasteAIOutput { Category = "hazardous", Confidence = 0.60f, Co2Saved = Co2Table["hazardous"] };
        return new WasteAIOutput { Category = "general", Confidence = 0.40f, Co2Saved = Co2Table["general"] };
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static string ExtractHint(string imageUrl)
    {
        if (Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri))
            return Path.GetFileNameWithoutExtension(uri.AbsolutePath);
        return imageUrl;
    }

    private static string NormaliseCategory(string raw)
    {
        raw = raw.Trim().ToLowerInvariant();
        return raw is "recyclable" or "organic" or "hazardous" or "general" ? raw : "general";
    }

    private static string CleanJson(string text)
    {
        text = text.Trim();
        if (text.StartsWith("```")) { var l = text.Split('\n', 2); text = l.Length > 1 ? l[1] : text; }
        if (text.EndsWith("```")) text = text[..text.LastIndexOf("```")];
        return text.Trim();
    }

    private static bool ContainsAny(string src, params string[] words)
        => words.Any(src.Contains);
}