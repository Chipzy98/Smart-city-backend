using Microsoft.ML;
using Microsoft.ML.Data;

// ══════════════════════════════════════════════════════════════
//  SmartCity — Energy Bill Prediction Model Trainer
//  Output : energy_model.zip
//  Place  : SmartcityBackend/bin/Debug/net8.0/AI/MLModels/
// ══════════════════════════════════════════════════════════════

var mlContext = new MLContext(seed: 42);

// ── 1. Sample training data ───────────────────────────────────
//  UsageUnits  : monthly kWh consumed
//  Temperature : avg indoor °C
//  Occupants   : number of people
//  Bill        : monthly electricity bill in USD (label)
//
//  Approximate formula used to generate realistic labels:
//    Bill ≈ UsageUnits * 0.13  (US avg rate)
//         + Temperature * 0.8  (cooling/heating cost factor)
//         + Occupants   * 4    (appliance usage per person)

var trainingData = new List<EnergyData>
{
    // Low usage — small household
    new(80f,  22f, 1,  26f), new(90f,  23f, 1,  28f), new(75f,  21f, 1,  24f),
    new(100f, 24f, 2,  38f), new(110f, 25f, 2,  41f), new(95f,  22f, 2,  36f),

    // Medium usage — average household
    new(150f, 26f, 2,  55f), new(160f, 27f, 3,  63f), new(170f, 28f, 3,  67f),
    new(155f, 25f, 2,  57f), new(145f, 26f, 3,  60f), new(165f, 29f, 4,  70f),

    // High usage — large household / hot climate
    new(200f, 30f, 3,  82f), new(220f, 32f, 4,  93f), new(250f, 33f, 4, 103f),
    new(210f, 31f, 3,  87f), new(240f, 34f, 5, 106f), new(260f, 35f, 5, 113f),

    // Very high usage — summer peak
    new(300f, 36f, 4, 126f), new(320f, 37f, 5, 136f), new(350f, 38f, 5, 147f),
    new(280f, 35f, 4, 118f), new(310f, 36f, 4, 130f),

    // Cold climate — heating bills
    new(180f, 18f, 2,  63f), new(200f, 16f, 3,  74f), new(220f, 15f, 4,  83f),
    new(160f, 19f, 2,  58f), new(190f, 17f, 3,  70f),

    // Large family
    new(230f, 28f, 5,  98f), new(260f, 30f, 6, 112f), new(280f, 31f, 6, 120f),
    new(240f, 29f, 5, 102f), new(270f, 32f, 7, 120f),

    // Extra variety
    new(120f, 24f, 2,  44f), new(130f, 25f, 2,  48f), new(140f, 26f, 3,  55f),
    new(175f, 27f, 3,  67f), new(185f, 28f, 4,  74f), new(195f, 29f, 4,  79f),
    new(215f, 30f, 4,  89f), new(235f, 31f, 5,  99f), new(255f, 33f, 5, 108f),
    new(275f, 34f, 5, 117f), new(295f, 35f, 6, 127f), new(315f, 36f, 6, 133f),
};

var dataView = mlContext.Data.LoadFromEnumerable(trainingData);

// ── 2. Pipeline ───────────────────────────────────────────────
var pipeline = mlContext.Transforms
    .Concatenate("Features", nameof(EnergyData.UsageUnits),
                              nameof(EnergyData.Temperature),
                              nameof(EnergyData.Occupants))
    .Append(mlContext.Transforms.NormalizeMinMax("Features"))
    .Append(mlContext.Regression.Trainers.FastTree(
        labelColumnName:   nameof(EnergyData.Bill),
        featureColumnName: "Features",
        numberOfLeaves:    20,
        numberOfTrees:     100,
        minimumExampleCountPerLeaf: 2));

// ── 3. Train ──────────────────────────────────────────────────
Console.WriteLine("Training energy model...");
var model = pipeline.Fit(dataView);

// ── 4. Quick evaluation ───────────────────────────────────────
var predictions = model.Transform(dataView);
var metrics     = mlContext.Regression.Evaluate(
    predictions,
    labelColumnName: nameof(EnergyData.Bill));

Console.WriteLine($"  R²  : {metrics.RSquared:F4}");
Console.WriteLine($"  MAE : {metrics.MeanAbsoluteError:F2}");
Console.WriteLine($"  RMSE: {metrics.RootMeanSquaredError:F2}");

// ── 5. Save ───────────────────────────────────────────────────
var outputDir  = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
var outputPath = Path.Combine(outputDir, "energy_model.zip");
Directory.CreateDirectory(outputDir);
mlContext.Model.Save(model, dataView.Schema, outputPath);
Console.WriteLine($"Saved → {outputPath}");


// ── Data class ────────────────────────────────────────────────
public class EnergyData(float usageUnits, float temperature, int occupants, float bill)
{
    public float UsageUnits  { get; set; } = usageUnits;
    public float Temperature { get; set; } = temperature;
    public float Occupants   { get; set; } = occupants;

    [ColumnName("Score")]
    public float Bill        { get; set; } = bill;
}
