using Microsoft.ML;
using Microsoft.ML.Data;

// ══════════════════════════════════════════════════════════════
//  SmartCity — Traffic Congestion Model Trainer
//  Output : traffic_model.zip
//  Place  : SmartcityBackend/bin/Debug/net8.0/AI/MLModels/
// ══════════════════════════════════════════════════════════════

var mlContext = new MLContext(seed: 42);

// ── 1. Sample training data ───────────────────────────────────
//  Hour  : 0–23
//  Day   : 0=Sun … 6=Sat
//  Weather: 0=Clear, 1=Cloudy, 2=Rainy, 3=Stormy
//  VehicleCount : vehicles on road
//  CongestionLevel : 0–100 (label)

var trainingData = new List<TrafficData>
{
    // Morning rush (weekdays)
    new(8,  1, 0,  850, 78f), new(8,  2, 0,  900, 82f), new(8,  3, 0,  870, 79f),
    new(8,  4, 0,  820, 75f), new(8,  5, 0,  780, 70f), new(9,  1, 0,  750, 68f),
    new(9,  2, 1,  700, 65f), new(9,  3, 2,  680, 69f), new(9,  4, 0,  720, 66f),

    // Evening rush
    new(17, 1, 0,  920, 85f), new(17, 2, 0,  960, 88f), new(17, 3, 1,  880, 81f),
    new(18, 1, 0,  900, 84f), new(18, 2, 2,  850, 83f), new(18, 3, 0,  870, 82f),
    new(18, 4, 0,  910, 85f), new(18, 5, 1,  800, 76f),

    // Midday moderate
    new(12, 1, 0,  500, 45f), new(12, 2, 0,  520, 47f), new(12, 3, 1,  480, 46f),
    new(13, 1, 0,  510, 44f), new(13, 4, 0,  490, 42f), new(14, 2, 2,  460, 50f),
    new(14, 3, 0,  470, 43f), new(15, 5, 0,  530, 48f),

    // Night / early morning (low)
    new(2,  1, 0,   80,  5f), new(3,  2, 0,   60,  4f), new(4,  3, 0,   50,  3f),
    new(23, 5, 0,  150, 12f), new(0,  6, 0,  100,  8f), new(1,  0, 0,   70,  5f),
    new(5,  1, 0,   90,  7f), new(6,  2, 0,  200, 18f),

    // Weekend (lower rush)
    new(10, 0, 0,  400, 35f), new(11, 6, 0,  450, 38f), new(14, 0, 1,  380, 34f),
    new(15, 6, 0,  420, 37f), new(16, 0, 2,  390, 40f),

    // Rainy / stormy penalty
    new(8,  1, 2,  900, 88f), new(17, 2, 3,  850, 91f), new(12, 3, 2,  500, 55f),
    new(9,  4, 3,  700, 76f), new(18, 5, 2,  870, 87f),

    // Extra variety
    new(7,  1, 0,  600, 55f), new(7,  2, 1,  580, 54f), new(19, 1, 0,  650, 58f),
    new(20, 3, 0,  400, 36f), new(21, 4, 0,  300, 27f), new(22, 5, 0,  200, 18f),
    new(10, 1, 0,  450, 40f), new(11, 2, 0,  470, 42f), new(16, 3, 1,  560, 52f),
};

var dataView = mlContext.Data.LoadFromEnumerable(trainingData);

// ── 2. Pipeline ───────────────────────────────────────────────
var pipeline = mlContext.Transforms
    .Concatenate("Features", nameof(TrafficData.Hour),
                              nameof(TrafficData.Day),
                              nameof(TrafficData.Weather),
                              nameof(TrafficData.VehicleCount))
    .Append(mlContext.Transforms.NormalizeMinMax("Features"))
    .Append(mlContext.Regression.Trainers.FastTree(
        labelColumnName:   nameof(TrafficData.CongestionLevel),
        featureColumnName: "Features",
        numberOfLeaves:    20,
        numberOfTrees:     100,
        minimumExampleCountPerLeaf: 2));

// ── 3. Train ──────────────────────────────────────────────────
Console.WriteLine("Training traffic model...");
var model = pipeline.Fit(dataView);

// ── 4. Quick evaluation ───────────────────────────────────────
var predictions = model.Transform(dataView);
var metrics     = mlContext.Regression.Evaluate(
    predictions,
    labelColumnName: nameof(TrafficData.CongestionLevel));

Console.WriteLine($"  R²  : {metrics.RSquared:F4}");
Console.WriteLine($"  MAE : {metrics.MeanAbsoluteError:F2}");
Console.WriteLine($"  RMSE: {metrics.RootMeanSquaredError:F2}");

// ── 5. Save ───────────────────────────────────────────────────
var outputDir  = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
var outputPath = Path.Combine(outputDir, "traffic_model.zip");
Directory.CreateDirectory(outputDir);
mlContext.Model.Save(model, dataView.Schema, outputPath);
Console.WriteLine($"Saved → {outputPath}");


// ── Data class ────────────────────────────────────────────────
public class TrafficData(float hour, float day, float weather, float vehicleCount, float congestionLevel)
{
    public float Hour             { get; set; } = hour;
    public float Day              { get; set; } = day;
    public float Weather          { get; set; } = weather;
    public float VehicleCount     { get; set; } = vehicleCount;

    [ColumnName("Score")]
    public float CongestionLevel  { get; set; } = congestionLevel;
}
