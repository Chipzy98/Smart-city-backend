using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using SmartcityBackend.src.Data;
using SmartcityBackend.src.Entities;
using SmartcityBackend.src.Helpers;
using SmartcityBackend.src.Services.Interfaces;
using System.Globalization;

namespace SmartcityBackend.src.Functions
{
    /// <summary>
    /// Receives CSV training data from the frontend.
    ///  • All modules: first 200 rows saved as few-shot context for the Claude prompts.
    ///  • Traffic: every valid row is ALSO imported into smart_TrafficData (Source = "csv")
    ///    so the ML.NET model can be trained on it ("Train model" button / nightly retrain).
    ///    A new traffic CSV replaces the previous CSV import.
    /// </summary>
    public class AITrainingFunctions
    {
        private const int MaxImportRows = 20_000;

        private readonly ILogger<AITrainingFunctions> _logger;
        private readonly ITokenService _tokenService;
        private readonly SmartCityDbContext _db;

        public AITrainingFunctions(
            ILogger<AITrainingFunctions> logger,
            ITokenService tokenService,
            SmartCityDbContext db)
        {
            _logger = logger;
            _tokenService = tokenService;
            _db = db;
        }

        // POST /api/smartcity/ai/train
        [Function(nameof(TrainAIModule))]
        public async Task<IActionResult> TrainAIModule(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "smartcity/ai/train")]
            HttpRequest req)
        {
            var auth = await _tokenService.AuthorizeAsync(req, SmartCityTaskIds.SmartCityAITraining, SmartCityTaskActions.CREATE);
            if (!auth.Success)
            {
                _logger.LogWarning("[AITraining] Denied: {Message}", auth.Message);
                return AuthResults.Deny(auth);
            }

            try
            {
                var body = await new StreamReader(req.Body).ReadToEndAsync();
                var payload = JsonConvert.DeserializeObject<TrainingPayload>(body);

                if (payload == null || string.IsNullOrWhiteSpace(payload.Module))
                    return new BadRequestObjectResult(new { error = "Invalid payload." });

                var module = payload.Module.ToLowerInvariant();
                var allowedModules = new[] { "traffic", "energy", "waste" };
                if (!allowedModules.Contains(module))
                    return new BadRequestObjectResult(new { error = "Module must be traffic, energy, or waste." });

                if (payload.Data == null || payload.Data.Count == 0)
                    return new BadRequestObjectResult(new { error = "No data rows provided." });

                // 1) Prompt context file (read back by the Claude AI services)
                var toSave = new TrainingContext
                {
                    Module = module,
                    Headers = payload.Headers,
                    Rows = payload.Data.Take(200).ToList(),
                    UpdatedAt = DateTime.UtcNow
                };
                await File.WriteAllTextAsync(
                    AppConfig.TrainingFile(module),
                    JsonConvert.SerializeObject(toSave, Formatting.Indented));

                // 2) Traffic → import into the ML dataset
                var imported = 0;
                var skipped = 0;
                if (module == "traffic")
                    (imported, skipped) = await ImportTrafficRowsAsync(payload, auth.UserId!.Value);

                _logger.LogInformation("[AITraining] {Module}: {Rows} rows, {Imported} imported to dataset.",
                    module, payload.Data.Count, imported);

                return new OkObjectResult(new
                {
                    success = true,
                    module,
                    rows = payload.Data.Count,
                    importedToDataset = imported,
                    skippedRows = skipped,
                    message = module == "traffic"
                        ? $"{payload.Data.Count} rows received. {imported} rows added to the ML dataset" +
                          (skipped > 0 ? $" ({skipped} invalid rows skipped)." : ".") +
                          " Click \"Train model\" to retrain."
                        : $"{payload.Data.Count} rows saved as training context for {module} AI."
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
            var auth = await _tokenService.AuthorizeAsync(req, SmartCityTaskIds.SmartCityAITraining, SmartCityTaskActions.VIEW);
            if (!auth.Success) return AuthResults.Deny(auth);

            var modules = new[] { "traffic", "energy", "waste" };
            var statuses = modules.Select(m =>
            {
                var path = AppConfig.TrainingFile(m);
                var exists = File.Exists(path);
                DateTime? updated = null;
                int rows = 0;

                if (exists)
                {
                    try
                    {
                        var ctx = JsonConvert.DeserializeObject<TrainingContext>(File.ReadAllText(path));
                        updated = ctx?.UpdatedAt;
                        rows = ctx?.Rows?.Count ?? 0;
                    }
                    catch { /* ignore */ }
                }

                return new { module = m, trained = exists, rows, lastUpdated = updated };
            });

            return new OkObjectResult(statuses);
        }

        // ── Traffic CSV → smart_TrafficData ─────────────────────────────────

        /// <summary>
        /// Required columns: hour, day, weather, congestionLevel
        /// Optional columns: latitude, longitude, temperature, precipitation, freeFlowSpeed, locationName
        /// </summary>
        private async Task<(int Imported, int Skipped)> ImportTrafficRowsAsync(TrainingPayload payload, Guid userId)
        {
            var idx = payload.Headers
                .Select((h, i) => (Name: h.Trim().ToLowerInvariant(), Index: i))
                .GroupBy(x => x.Name)
                .ToDictionary(g => g.Key, g => g.First().Index);

            int Col(string name) => idx.TryGetValue(name.ToLowerInvariant(), out var i) ? i : -1;

            var hourCol = Col("hour");
            var dayCol = Col("day");
            var weatherCol = Col("weather");
            var levelCol = Col("congestionLevel");
            if (hourCol < 0 || dayCol < 0 || weatherCol < 0 || levelCol < 0)
                return (0, payload.Data.Count);

            var latCol = Col("latitude");
            var lngCol = Col("longitude");
            var tempCol = Col("temperature");
            var rainCol = Col("precipitation");
            var ffCol = Col("freeFlowSpeed");
            var nameCol = Col("locationName");

            float? F(List<string> row, int col) =>
                col >= 0 && col < row.Count &&
                float.TryParse(row[col], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && float.IsFinite(v)
                    ? v : null;

            var now = DateTime.UtcNow;
            var entities = new List<SmartTrafficData>();
            var skipped = 0;

            foreach (var row in payload.Data.Take(MaxImportRows))
            {
                var hour = F(row, hourCol);
                var day = F(row, dayCol);
                var weather = F(row, weatherCol);
                var level = F(row, levelCol);

                if (hour is null or < 0 or > 23 || day is null or < 0 or > 6 ||
                    weather is null or < 0 or > 3 || level is null or < 0 or > 100)
                {
                    skipped++;
                    continue;
                }

                var lat = F(row, latCol) ?? 6.9344f;   // default Colombo Fort
                var lng = F(row, lngCol) ?? 79.8428f;

                entities.Add(new SmartTrafficData
                {
                    TrafficDataId = Guid.NewGuid(),
                    Latitude = (decimal)Math.Round(lat, 6),
                    Longitude = (decimal)Math.Round(lng, 6),
                    CongestionLevel = level.Value,
                    RecordedAt = now,
                    IsActive = true,
                    CreatedDateTime = now,
                    CreatedUserId = userId,
                    LocationName = nameCol >= 0 && nameCol < row.Count && !string.IsNullOrWhiteSpace(row[nameCol])
                        ? row[nameCol].Trim()[..Math.Min(200, row[nameCol].Trim().Length)]
                        : null,
                    Temperature = F(row, tempCol),
                    PrecipitationMm = F(row, rainCol),
                    FreeFlowSpeedKmh = F(row, ffCol),
                    Weather = (int)weather.Value,
                    LocalHour = (int)hour.Value,
                    LocalDayOfWeek = (int)day.Value,
                    Source = "csv",
                });
            }

            skipped += Math.Max(0, payload.Data.Count - MaxImportRows);
            if (entities.Count == 0) return (0, skipped);

            // Replace the previous CSV import (soft delete) so re-uploads do not duplicate data
            await _db.SmartTrafficData
                .Where(x => x.IsActive && x.Source == "csv")
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));

            _db.SmartTrafficData.AddRange(entities);
            await _db.SaveChangesAsync();
            return (entities.Count, skipped);
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
