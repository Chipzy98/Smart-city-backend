using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using SmartcityBackend.src.Helpers;
using SmartcityBackend.src.Services.Interfaces;

namespace SmartcityBackend.src.Functions
{
    /// <summary>
    /// Receives CSV training data from the frontend and stores it so the
    /// AI services can use it as few-shot examples in their Claude prompts.
    /// No ML.NET model is re-trained — instead the data is persisted to a
    /// JSON file and injected into the relevant prompt as context rows,
    /// which makes Claude's predictions more accurate for YOUR city's data.
    /// </summary>
    public class AITrainingFunctions
    {
        private readonly ILogger<AITrainingFunctions> _logger;
        private readonly ITokenService _tokenService;

        // Where training context files are stored on disk
        private static readonly string TrainingDir =
            Path.Combine(AppContext.BaseDirectory, "training_data");

        public AITrainingFunctions(
            ILogger<AITrainingFunctions> logger,
            ITokenService tokenService)
        {
            _logger = logger;
            _tokenService = tokenService;
            Directory.CreateDirectory(TrainingDir);
        }

        // POST /api/smartcity/ai/train
        [Function(nameof(TrainAIModule))]
        public async Task<IActionResult> TrainAIModule(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "smartcity/ai/train")]
            HttpRequest req)
        {
            // Auth check
            var token = Utility.GetAuthToken(req);
            var auth = await _tokenService.IsAuthorizedAsync(
                token, SmartCityTaskIds.SmartCityDashboard, SmartCityTaskActions.CREATE);

            if (!auth.Success)
            {
                _logger.LogWarning("[AITraining] Unauthorized attempt.");
                return new UnauthorizedResult();
            }

            try
            {
                var body = await new StreamReader(req.Body).ReadToEndAsync();
                var payload = JsonConvert.DeserializeObject<TrainingPayload>(body);

                if (payload == null || string.IsNullOrWhiteSpace(payload.Module))
                    return new BadRequestObjectResult(new { error = "Invalid payload." });

                var allowedModules = new[] { "traffic", "energy", "waste" };
                if (!allowedModules.Contains(payload.Module.ToLower()))
                    return new BadRequestObjectResult(new { error = "Module must be traffic, energy, or waste." });

                if (payload.Data == null || payload.Data.Count == 0)
                    return new BadRequestObjectResult(new { error = "No data rows provided." });

                // Persist to JSON file (read back in AI service prompts)
                var filePath = Path.Combine(TrainingDir, $"{payload.Module}_context.json");
                var toSave = new TrainingContext
                {
                    Module = payload.Module,
                    Headers = payload.Headers,
                    Rows = payload.Data.Take(200).ToList(), // max 200 rows as context
                    UpdatedAt = DateTime.UtcNow
                };

                await File.WriteAllTextAsync(
                    filePath,
                    JsonConvert.SerializeObject(toSave, Formatting.Indented));

                _logger.LogInformation(
                    "[AITraining] {Module} trained with {Rows} rows.",
                    payload.Module, payload.Data.Count);

                return new OkObjectResult(new
                {
                    success = true,
                    module = payload.Module,
                    rows = payload.Data.Count,
                    message = $"{payload.Data.Count} rows saved as training context for {payload.Module} AI."
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AITraining] Error processing training data.");
                return new ObjectResult(new { error = "Training failed." }) { StatusCode = 500 };
            }
        }

        // GET /api/smartcity/ai/train/status — check what training data exists
        [Function(nameof(GetTrainingStatus))]
        public async Task<IActionResult> GetTrainingStatus(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "smartcity/ai/train/status")]
            HttpRequest req)
        {
            await Task.CompletedTask;

            var modules = new[] { "traffic", "energy", "waste" };
            var statuses = modules.Select(m =>
            {
                var path = Path.Combine(TrainingDir, $"{m}_context.json");
                var exists = File.Exists(path);
                DateTime? updated = null;
                int rows = 0;

                if (exists)
                {
                    try
                    {
                        var ctx = JsonConvert.DeserializeObject<TrainingContext>(
                            File.ReadAllText(path));
                        updated = ctx?.UpdatedAt;
                        rows = ctx?.Rows?.Count ?? 0;
                    }
                    catch { /* ignore */ }
                }

                return new { module = m, trained = exists, rows, lastUpdated = updated };
            });

            return new OkObjectResult(statuses);
        }
    }

    // ── DTOs ─────────────────────────────────────────────────────────────────

    public class TrainingPayload
    {
        public string Module { get; set; } = string.Empty;
        public List<string> Headers { get; set; } = new();
        public List<List<string>> Data { get; set; } = new();
    }

    public class TrainingContext
    {
        public string Module { get; set; } = string.Empty;
        public List<string> Headers { get; set; } = new();
        public List<List<string>> Rows { get; set; } = new();
        public DateTime UpdatedAt { get; set; }
    }
}