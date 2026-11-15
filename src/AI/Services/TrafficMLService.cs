using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.ML;
using Microsoft.ML.Data;
using SmartcityBackend.src.Data;
using SmartcityBackend.src.Entities;
using SmartcityBackend.src.Helpers;
using System.Text.Json;

namespace SmartcityBackend.src.AI.Services
{
    /// <summary>
    /// Real machine-learning model for traffic congestion (ML.NET FastTree regression).
    ///
    /// Dataset : smart_TrafficData (TomTom live samples collected every 30 min + CSV imports)
    /// Label   : CongestionLevel (0–100)
    /// Features: hour (sin/cos), day of week, weekend, rush hour, weather, temperature,
    ///           rain, latitude, longitude, free-flow speed of the road.
    /// Output  : model zip + metrics JSON in AppConfig.ModelsDir, run history in smart_MLModelRun.
    /// </summary>
    public class TrafficMLService
    {
        public const string ModelName = "traffic-congestion";
        public const string Algorithm = "ML.NET FastTree regression";
        public const int MinRows = 100;
        private const int MaxRows = 100_000;

        public static readonly string[] FeatureColumns =
        {
            nameof(TrafficModelInput.HourSin), nameof(TrafficModelInput.HourCos),
            nameof(TrafficModelInput.DayOfWeek), nameof(TrafficModelInput.IsWeekend),
            nameof(TrafficModelInput.IsRushHour), nameof(TrafficModelInput.Weather),
            nameof(TrafficModelInput.Temperature), nameof(TrafficModelInput.Precipitation),
            nameof(TrafficModelInput.Latitude), nameof(TrafficModelInput.Longitude),
            nameof(TrafficModelInput.FreeFlowSpeed),
        };

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<TrafficMLService> _logger;
        private readonly MLContext _ml = new(seed: 42);
        private readonly SemaphoreSlim _trainLock = new(1, 1);
        private readonly object _predictLock = new();

        private ITransformer? _model;
        private PredictionEngine<TrafficModelInput, TrafficModelOutput>? _engine;
        private TrafficModelMetrics? _metrics;
        private bool _loadAttempted;

        private static string ModelPath => Path.Combine(AppConfig.ModelsDir, "traffic_model.zip");
        private static string MetricsPath => Path.Combine(AppConfig.ModelsDir, "traffic_model.json");

        public TrafficMLService(IServiceScopeFactory scopeFactory, ILogger<TrafficMLService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        public bool IsTrained
        {
            get { EnsureLoaded(); return _engine != null; }
        }

        public TrafficModelMetrics? Metrics
        {
            get { EnsureLoaded(); return _metrics; }
        }

        // ── Prediction ─────────────────────────────────────────────────────

        /// <summary>Returns predicted congestion 0–100, or null when no model is trained yet.</summary>
        public float? Predict(TrafficModelInput input)
        {
            EnsureLoaded();
            if (_engine == null) return null;

            lock (_predictLock) // PredictionEngine is not thread-safe
            {
                var score = _engine.Predict(input).Score;
                return float.IsFinite(score) ? Math.Clamp(score, 0f, 100f) : null;
            }
        }

        // ── Training ───────────────────────────────────────────────────────

        public async Task<TrafficTrainResult> TrainAsync(Guid trainedByUserId, CancellationToken ct = default)
        {
            if (!await _trainLock.WaitAsync(0, ct))
                return new TrafficTrainResult { Trained = false, Message = "Training is already running." };

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<SmartCityDbContext>();

                var rows = await db.SmartTrafficData
                    .Where(x => x.IsActive && x.CongestionLevel >= 0 && x.CongestionLevel <= 100)
                    .OrderByDescending(x => x.RecordedAt)
                    .Take(MaxRows)
                    .Select(x => new
                    {
                        x.Latitude, x.Longitude, x.CongestionLevel, x.RecordedAt,
                        x.LocalHour, x.LocalDayOfWeek, x.Weather, x.Temperature,
                        x.PrecipitationMm, x.FreeFlowSpeedKmh,
                    })
                    .ToListAsync(ct);

                if (rows.Count < MinRows)
                {
                    return new TrafficTrainResult
                    {
                        Trained = false,
                        Rows = rows.Count,
                        Message = $"Need at least {MinRows} traffic samples to train (have {rows.Count}). " +
                                  "Let the collector run or upload a traffic CSV on the AI Training page.",
                    };
                }

                var samples = rows.Select(r =>
                {
                    var local = AppTime.ToLocal(r.RecordedAt);
                    var input = TrafficModelInput.Create(
                        r.LocalHour ?? local.Hour,
                        r.LocalDayOfWeek ?? (int)local.DayOfWeek,
                        r.Weather ?? 0,
                        r.Temperature ?? 28f,
                        r.PrecipitationMm ?? 0f,
                        (double)r.Latitude,
                        (double)r.Longitude,
                        r.FreeFlowSpeedKmh ?? 50f);
                    input.Label = r.CongestionLevel;
                    return input;
                }).ToList();

                var data = _ml.Data.LoadFromEnumerable(samples);
                var split = _ml.Data.TrainTestSplit(data, testFraction: 0.2, seed: 42);

                var pipeline = _ml.Transforms.Concatenate("Features", FeatureColumns)
                    .Append(_ml.Regression.Trainers.FastTree(
                        labelColumnName: "Label",
                        featureColumnName: "Features",
                        numberOfLeaves: 20,
                        numberOfTrees: 200,
                        minimumExampleCountPerLeaf: 5,
                        learningRate: 0.1));

                // 1) Evaluate on held-out 20 % test set
                var evalModel = pipeline.Fit(split.TrainSet);
                var testPredictions = evalModel.Transform(split.TestSet);
                var m = _ml.Regression.Evaluate(testPredictions, labelColumnName: "Label", scoreColumnName: "Score");

                var trainLabels = _ml.Data.CreateEnumerable<TrafficModelInput>(split.TrainSet, reuseRowObject: false)
                    .Select(x => x.Label).ToList();
                var testLabels = _ml.Data.CreateEnumerable<TrafficModelInput>(split.TestSet, reuseRowObject: false)
                    .Select(x => x.Label).ToList();
                var mean = trainLabels.Count > 0 ? trainLabels.Average() : 0f;
                var baselineMae = testLabels.Count > 0 ? testLabels.Average(l => Math.Abs(l - mean)) : 0;

                // 2) Final model trained on ALL data
                var finalModel = pipeline.Fit(data);

                var tmp = ModelPath + ".tmp";
                _ml.Model.Save(finalModel, data.Schema, tmp);
                File.Move(tmp, ModelPath, overwrite: true);

                var metrics = new TrafficModelMetrics
                {
                    ModelName = ModelName,
                    Algorithm = Algorithm,
                    TrainedAt = DateTime.UtcNow,
                    Rows = samples.Count,
                    TrainRows = trainLabels.Count,
                    TestRows = testLabels.Count,
                    MeanAbsoluteError = Round(m.MeanAbsoluteError),
                    RootMeanSquaredError = Round(m.RootMeanSquaredError),
                    RSquared = Round(m.RSquared),
                    BaselineMeanAbsoluteError = Round(baselineMae),
                    Features = FeatureColumns,
                };
                await File.WriteAllTextAsync(MetricsPath, JsonSerializer.Serialize(metrics), ct);

                db.SmartMLModelRun.Add(new SmartMLModelRun
                {
                    ModelRunId = Guid.NewGuid(),
                    ModelName = ModelName,
                    Algorithm = Algorithm,
                    RowCount = metrics.Rows,
                    TrainRows = metrics.TrainRows,
                    TestRows = metrics.TestRows,
                    MeanAbsoluteError = metrics.MeanAbsoluteError,
                    RootMeanSquaredError = metrics.RootMeanSquaredError,
                    RSquared = metrics.RSquared,
                    TrainedAt = metrics.TrainedAt,
                    TrainedByUserId = trainedByUserId,
                    IsActive = true,
                });
                await db.SaveChangesAsync(ct);

                lock (_predictLock)
                {
                    _model = finalModel;
                    _engine = _ml.Model.CreatePredictionEngine<TrafficModelInput, TrafficModelOutput>(finalModel);
                    _metrics = metrics;
                    _loadAttempted = true;
                }

                _logger.LogInformation(
                    "[TrafficML] Trained on {Rows} rows. MAE={Mae} RMSE={Rmse} R2={R2}",
                    metrics.Rows, metrics.MeanAbsoluteError, metrics.RootMeanSquaredError, metrics.RSquared);

                return new TrafficTrainResult
                {
                    Trained = true,
                    Rows = metrics.Rows,
                    Message = $"Model trained on {metrics.Rows} samples.",
                    Metrics = metrics,
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[TrafficML] Training failed.");
                return new TrafficTrainResult { Trained = false, Message = $"Training failed: {ex.Message}" };
            }
            finally
            {
                _trainLock.Release();
            }
        }

        // ── Loading ────────────────────────────────────────────────────────

        private void EnsureLoaded()
        {
            if (_loadAttempted) return;

            lock (_predictLock)
            {
                if (_loadAttempted) return;
                _loadAttempted = true;

                try
                {
                    if (!File.Exists(ModelPath)) return;

                    _model = _ml.Model.Load(ModelPath, out _);
                    _engine = _ml.Model.CreatePredictionEngine<TrafficModelInput, TrafficModelOutput>(_model);

                    if (File.Exists(MetricsPath))
                        _metrics = JsonSerializer.Deserialize<TrafficModelMetrics>(File.ReadAllText(MetricsPath));

                    _logger.LogInformation("[TrafficML] Loaded model from {Path}", ModelPath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[TrafficML] Could not load saved model.");
                    _model = null;
                    _engine = null;
                }
            }
        }

        private static double Round(double v) => double.IsFinite(v) ? Math.Round(v, 3) : 0;
    }

    // ── ML.NET schema classes ──────────────────────────────────────────────

    public class TrafficModelInput
    {
        public float HourSin { get; set; }
        public float HourCos { get; set; }
        public float DayOfWeek { get; set; }
        public float IsWeekend { get; set; }
        public float IsRushHour { get; set; }
        public float Weather { get; set; }
        public float Temperature { get; set; }
        public float Precipitation { get; set; }
        public float Latitude { get; set; }
        public float Longitude { get; set; }
        public float FreeFlowSpeed { get; set; }

        /// <summary>Congestion level 0–100 (training label).</summary>
        public float Label { get; set; }

        /// <summary>Hour is encoded with sin/cos so 23:00 and 00:00 are "close" to each other.</summary>
        public static TrafficModelInput Create(
            int hour, int dayOfWeek, int weather, float temperature, float precipitation,
            double latitude, double longitude, float freeFlowSpeed)
        {
            var angle = 2 * Math.PI * hour / 24.0;
            return new TrafficModelInput
            {
                HourSin = (float)Math.Sin(angle),
                HourCos = (float)Math.Cos(angle),
                DayOfWeek = dayOfWeek,
                IsWeekend = AppTime.IsWeekend(dayOfWeek) ? 1f : 0f,
                IsRushHour = AppTime.IsRushHour(hour) ? 1f : 0f,
                Weather = Math.Clamp(weather, 0, 3),
                Temperature = temperature,
                Precipitation = Math.Max(0f, precipitation),
                Latitude = (float)latitude,
                Longitude = (float)longitude,
                FreeFlowSpeed = freeFlowSpeed > 0 ? freeFlowSpeed : 50f,
            };
        }
    }

    public class TrafficModelOutput
    {
        [ColumnName("Score")]
        public float Score { get; set; }
    }

    public class TrafficModelMetrics
    {
        public string ModelName { get; set; } = string.Empty;
        public string Algorithm { get; set; } = string.Empty;
        public DateTime TrainedAt { get; set; }
        public int Rows { get; set; }
        public int TrainRows { get; set; }
        public int TestRows { get; set; }
        public double MeanAbsoluteError { get; set; }
        public double RootMeanSquaredError { get; set; }
        public double RSquared { get; set; }

        /// <summary>MAE of always predicting the training mean — the model should beat this.</summary>
        public double BaselineMeanAbsoluteError { get; set; }

        public string[] Features { get; set; } = Array.Empty<string>();
    }

    public class TrafficTrainResult
    {
        public bool Trained { get; set; }
        public int Rows { get; set; }
        public string Message { get; set; } = string.Empty;
        public TrafficModelMetrics? Metrics { get; set; }
    }
}
