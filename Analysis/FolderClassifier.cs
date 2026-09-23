using System.Diagnostics;
using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Transforms.Text;

namespace SpaceMonger.Analysis;

/// <summary>
/// Local ML.NET multiclass classifier over folders. Inputs: name tokens + character trigrams of the
/// folder and its parent, and numeric content features (byte mix by file category, size, file
/// count, average file size, age, depth, location flags).
///
/// There is no public labelled dataset for "is this folder junk", so it is trained per scan on
/// weak labels: folders the high-precision <see cref="CleanupRules"/> recognise, plus anything the
/// user labelled by hand (weighted up). Its job is to generalise to folders the rules miss — e.g. an
/// app's oddly-named cache full of small temp blobs.
/// </summary>
public sealed class FolderClassifier
{
    public sealed record Sample(string Text, float[] Numeric, string? Label);

    private sealed class Row
    {
        public string Label { get; set; } = "";
        public string Text { get; set; } = "";
        [VectorType(FolderFeatures.NumericCount)] public float[] Numeric { get; set; } = [];
    }

    private sealed class Prediction
    {
        public string PredictedLabel { get; set; } = "";
        public float[] Score { get; set; } = [];
    }

    private const int MaxPerClass = 1500;
    private const int UserLabelWeight = 5;

    private readonly MLContext _ml = new(seed: 1);
    private ITransformer? _model;

    public bool IsTrained => _model != null;

    /// <summary>Human-readable training summary (sample count, classes, held-out agreement).</summary>
    public string Info { get; private set; } = "Not trained";

    public bool Train(IReadOnlyList<Sample> labelled, IReadOnlyList<Sample> userLabelled, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var rng = new Random(1);
        var rows = new List<Row>();
        foreach (var g in labelled.GroupBy(s => s.Label!))
        {
            var list = g.ToList();
            if (list.Count > MaxPerClass) list = list.OrderBy(_ => rng.Next()).Take(MaxPerClass).ToList();
            rows.AddRange(list.Select(ToRow));
        }
        for (int i = 0; i < UserLabelWeight; i++) rows.AddRange(userLabelled.Select(ToRow));

        var classes = rows.GroupBy(r => r.Label).Where(g => g.Count() >= 5).Select(g => g.Key).ToHashSet();
        rows.RemoveAll(r => !classes.Contains(r.Label));
        if (classes.Count < 2 || rows.Count < 30)
        {
            _model = null;
            Info = $"Not enough labelled folders to train ({rows.Count} samples, {classes.Count} classes)";
            return false;
        }
        ct.ThrowIfCancellationRequested();

        var data = _ml.Data.ShuffleRows(_ml.Data.LoadFromEnumerable(rows), seed: 1);
        var split = _ml.Data.TrainTestSplit(data, testFraction: 0.2, seed: 1);

        var textOptions = new TextFeaturizingEstimator.Options
        {
            CaseMode = TextNormalizingEstimator.CaseMode.Lower,
            KeepPunctuations = false,
            WordFeatureExtractor = new WordBagEstimator.Options { NgramLength = 1 },
            CharFeatureExtractor = new WordBagEstimator.Options { NgramLength = 3, UseAllLengths = false },
        };
        var pipeline = _ml.Transforms.Conversion.MapValueToKey("Label")
            .Append(_ml.Transforms.Text.FeaturizeText("TextFeatures", textOptions, nameof(Row.Text)))
            .Append(_ml.Transforms.NormalizeMinMax("NumericNorm", nameof(Row.Numeric)))
            .Append(_ml.Transforms.Concatenate("Features", "TextFeatures", "NumericNorm"))
            .Append(_ml.MulticlassClassification.Trainers.SdcaMaximumEntropy(maximumNumberOfIterations: 30))
            .Append(_ml.Transforms.Conversion.MapKeyToValue("PredictedLabel"));

        var model = pipeline.Fit(split.TrainSet);
        ct.ThrowIfCancellationRequested();
        var metrics = _ml.MulticlassClassification.Evaluate(model.Transform(split.TestSet));
        _model = model;
        Info = $"Trained on {rows.Count:N0} folders, {classes.Count} classes in {sw.Elapsed.TotalSeconds:0.0}s — " +
               $"{metrics.MacroAccuracy:P0} agreement with labels on held-out folders";
        return true;
    }

    public (string Label, float Confidence)[] Predict(IReadOnlyList<Sample> samples)
    {
        if (_model == null || samples.Count == 0) return [];
        var scored = _model.Transform(_ml.Data.LoadFromEnumerable(samples.Select(s => ToRow(s with { Label = "" }))));
        return _ml.Data.CreateEnumerable<Prediction>(scored, reuseRowObject: false)
            .Select(p => (p.PredictedLabel, p.Score.Length > 0 ? p.Score.Max() : 0f))
            .ToArray();
    }

    private static Row ToRow(Sample s) => new() { Label = s.Label ?? "", Text = s.Text, Numeric = s.Numeric };
}
