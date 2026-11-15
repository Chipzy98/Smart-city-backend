using System.Text;
using System.Text.Json;

namespace SmartcityBackend.src.Helpers
{
    /// <summary>
    /// Small shared helper for Claude Messages API calls (text prompt → text reply).
    /// Used by newer services so the HTTP code is not duplicated again.
    /// </summary>
    public static class ClaudeApiClient
    {
        private const string ApiUrl = "https://api.anthropic.com/v1/messages";
        private const string ApiVersion = "2023-06-01";

        public static async Task<string> SendPromptAsync(
            HttpClient http, string prompt, int maxTokens, CancellationToken ct = default)
        {
            var apiKey = AppConfig.AnthropicApiKey;
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("ANTHROPIC_API_KEY not set.");

            var body = JsonSerializer.Serialize(new
            {
                model = AppConfig.AnthropicModel,
                max_tokens = maxTokens,
                messages = new[] { new { role = "user", content = prompt } }
            });

            using var req = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
            req.Headers.Add("x-api-key", apiKey);
            req.Headers.Add("anthropic-version", ApiVersion);
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");

            var res = await http.SendAsync(req, ct);
            res.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            return doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString() ?? "{}";
        }

        /// <summary>Strips ``` fences that the model sometimes adds around JSON.</summary>
        public static string CleanJson(string text)
        {
            text = text.Trim();
            if (text.StartsWith("```")) { var l = text.Split('\n', 2); text = l.Length > 1 ? l[1] : text; }
            if (text.EndsWith("```")) text = text[..text.LastIndexOf("```")];
            return text.Trim();
        }
    }
}
