using System.Text.Json;

namespace 六合分析软件;

public enum MlModelKind
{
    LightGbmStyle,
    XgBoostStyle
}

public sealed record MlZodiacFeatures(
    string Zodiac,
    int Recent5Count,
    int CurrentOmission,
    double[] Values)
{
    public double[] ToVector() => Values;
    public int Gap1RepeatCount => Values.Length > 15 ? (int)Values[15] : 0;
    public int Gap2RepeatCount => Values.Length > 16 ? (int)Values[16] : 0;
    public int Count50 => Values.Length > 7 ? (int)Values[7] : 0;
    public int Count100 => Values.Length > 8 ? (int)Values[8] : 0;
    public double ColorTrend => Values.Length > 40 ? Values[40] : 0;
}

public sealed record MlZodiacProbability(string Zodiac, double Probability, MlModelKind Model);

public sealed record MlBacktestPrediction(
    int TargetIndex,
    string TargetPeriod,
    string ActualZodiac,
    IReadOnlyList<MlZodiacProbability> Probabilities,
    bool Top3Hit,
    bool Top6Hit,
    int TrainingCount);

public sealed class MlBacktestReport
{
    public MlModelKind Model { get; init; }
    public int Warmup { get; init; }
    public List<MlBacktestPrediction> Predictions { get; } = new();
    public double Top3HitRate => Predictions.Count == 0 ? 0 : Predictions.Count(x => x.Top3Hit) / (double)Predictions.Count;
    public double Top6HitRate => Predictions.Count == 0 ? 0 : Predictions.Count(x => x.Top6Hit) / (double)Predictions.Count;
    public int MaximumConsecutiveMisses { get; internal set; }
    public int MaximumOmission { get; internal set; }
    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
}

/// <summary>
/// Dependency-free local experiment for V6.3. The scorer uses gradient-boosted
/// decision stumps with LightGBM/XGBoost-style regularisation. It intentionally
/// does not replace the existing V6.3 rule model until its rolling backtest is
/// reviewed. A native provider can implement the same feature contract later.
/// </summary>
public static class MachineLearningPredictionService
{
    public static IReadOnlyList<string> FeatureNames => FeatureEngine.FeatureNames;
    private static readonly string[] Zodiacs =
        { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };

    public static IReadOnlyList<MlZodiacProbability> Predict(
        IReadOnlyList<DatabaseHelper.HistoryRecord> records,
        int minimumTraining = 30,
        MlModelKind model = MlModelKind.LightGbmStyle)
    {
        var chronological = Normalize(records);
        if (chronological.Count < 2) return Array.Empty<MlZodiacProbability>();
        int target = chronological.Count;
        var output = new List<MlZodiacProbability>(Zodiacs.Length);
        foreach (var zodiac in Zodiacs)
        {
            var training = BuildSamples(chronological, Math.Max(1, Math.Min(target - 1, minimumTraining)), target, zodiac);
            var features = BuildFeatures(chronological, target, zodiac).ToVector();
            output.Add(new MlZodiacProbability(zodiac, FitAndPredict(training, features, model), model));
        }
        return output.OrderByDescending(x => x.Probability).ThenBy(x => x.Zodiac).ToList();
    }

    public static MlBacktestReport RollingBacktest(
        IReadOnlyList<DatabaseHelper.HistoryRecord> records,
        int warmup = 50,
        int minimumTraining = 30,
        MlModelKind model = MlModelKind.LightGbmStyle)
    {
        var chronological = Normalize(records);
        var report = new MlBacktestReport { Model = model, Warmup = warmup };
        int misses = 0;
        for (int target = Math.Max(1, warmup); target < chronological.Count; target++)
        {
            var probabilities = new List<MlZodiacProbability>();
            foreach (var zodiac in Zodiacs)
            {
                var training = BuildSamples(chronological, Math.Max(1, Math.Min(target - 1, minimumTraining)), target, zodiac);
                probabilities.Add(new MlZodiacProbability(zodiac,
                    FitAndPredict(training, BuildFeatures(chronological, target, zodiac).ToVector(), model), model));
            }
            var ranked = probabilities.OrderByDescending(x => x.Probability).ThenBy(x => x.Zodiac).ToList();
            string actual = chronological[target].SpecialZodiac ?? "";
            bool top3 = ranked.Take(3).Any(x => x.Zodiac == actual);
            bool top6 = ranked.Take(6).Any(x => x.Zodiac == actual);
            misses = top6 ? 0 : misses + 1;
            report.MaximumConsecutiveMisses = Math.Max(report.MaximumConsecutiveMisses, misses);
            report.MaximumOmission = Math.Max(report.MaximumOmission,
                BuildFeatures(chronological, target, actual).CurrentOmission);
            report.Predictions.Add(new MlBacktestPrediction(target, chronological[target].Period,
                actual, ranked, top3, top6, Math.Max(0, target - Math.Max(1, Math.Min(target - 1, minimumTraining)))));
        }
        return report;
    }

    public static string SaveReport(MlBacktestReport report, string? directory = null)
    {
        directory ??= Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "ml-backtest");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"{DateTime.Now:yyyyMMdd-HHmmss}-{report.Model}.json");
        File.WriteAllText(path, report.ToJson());
        return path;
    }

    public static MlZodiacFeatures BuildFeatures(
        IReadOnlyList<DatabaseHelper.HistoryRecord> records, int targetIndex, string zodiac)
    {
        int end = Math.Clamp(targetIndex, 0, records.Count);
        var prior = records.Take(end).Where(x => !string.IsNullOrWhiteSpace(x.SpecialZodiac)).ToList();
        var feature = FeatureEngine.BuildFeature(prior, zodiac);
        if (feature == null) return new MlZodiacFeatures(zodiac, 0, prior.Count, new double[FeatureNames.Count]);
        return new MlZodiacFeatures(zodiac, feature.Recent5Count, feature.CurrentOmission, feature.ToVector());
    }

    private static List<(double[] Features, double Label)> BuildSamples(
        IReadOnlyList<DatabaseHelper.HistoryRecord> records, int start, int end, string zodiac)
    {
        var samples = new List<(double[], double)>();
        for (int i = start; i < end && i < records.Count; i++)
        {
            var f = BuildFeatures(records, i, zodiac);
            samples.Add((f.ToVector(), records[i].SpecialZodiac == zodiac ? 1d : 0d));
        }
        return samples;
    }

    private static double FitAndPredict(List<(double[] Features, double Label)> samples, double[] input, MlModelKind kind)
    {
        if (samples.Count == 0) return 0.5;
        double positive = samples.Sum(x => x.Label);
        double baseScore = Math.Log((positive + 1) / (samples.Count - positive + 1));
        int rounds = kind == MlModelKind.LightGbmStyle ? 12 : 18;
        double learningRate = kind == MlModelKind.LightGbmStyle ? 0.08 : 0.05;
        var stumps = new List<(int Feature, double Threshold, double Left, double Right)>();
        var score = samples.Select(_ => baseScore).ToArray();
        for (int round = 0; round < rounds; round++)
        {
            var residual = samples.Select((x, i) => x.Label - Sigmoid(score[i])).ToArray();
            var selected = SelectBestSplit(samples, residual);
            if (selected == null) break;
            var best = selected.Value;
            stumps.Add(best);
            for (int i = 0; i < samples.Count; i++)
                score[i] += learningRate * (samples[i].Features[best.Feature] <= best.Threshold ? best.Left : best.Right);
        }
        double prediction = baseScore;
        foreach (var stump in stumps)
            prediction += learningRate * (input[stump.Feature] <= stump.Threshold ? stump.Left : stump.Right);
        return Sigmoid(prediction);
    }

    public static string SelectBestSplitFeature(IReadOnlyList<double[]> features, IReadOnlyList<double> labels)
    {
        if (features.Count == 0 || features.Count != labels.Count) return "";
        if (features.Any(x => x.Length != FeatureNames.Count)) return "";
        var samples = features.Select((x, i) => (Features: x, Label: labels[i])).ToList();
        double positive = labels.Sum();
        double baseScore = Math.Log((positive + 1) / (labels.Count - positive + 1));
        var residual = labels.Select(label => label - Sigmoid(baseScore)).ToArray();
        var selected = SelectBestSplit(samples, residual);
        return selected == null ? "" : FeatureNames[selected.Value.Feature];
    }

    private static (int Feature, double Threshold, double Left, double Right)? SelectBestSplit(
        List<(double[] Features, double Label)> samples, double[] residual)
    {
        double bestGain = double.NegativeInfinity;
        (int Feature, double Threshold, double Left, double Right) best = default;
        for (int f = 0; f < samples[0].Features.Length; f++)
        {
            double threshold = samples.Select(x => x.Features[f]).OrderBy(x => x).ElementAt(samples.Count / 2);
            var left = Enumerable.Range(0, samples.Count).Where(i => samples[i].Features[f] <= threshold).ToArray();
            var right = Enumerable.Range(0, samples.Count).Where(i => samples[i].Features[f] > threshold).ToArray();
            if (left.Length == 0 || right.Length == 0) continue;
            double lv = left.Average(i => residual[i]), rv = right.Average(i => residual[i]);
            double gain = left.Length * lv * lv + right.Length * rv * rv;
            if (gain > bestGain)
            {
                bestGain = gain;
                best = (f, threshold, lv, rv);
            }
        }
        return double.IsNegativeInfinity(bestGain) ? null : best;
    }

    private static double Sigmoid(double x) => 1d / (1d + Math.Exp(-Math.Clamp(x, -30, 30)));

    private static List<DatabaseHelper.HistoryRecord> Normalize(IReadOnlyList<DatabaseHelper.HistoryRecord> records)
    {
        var list = records.Where(x => !string.IsNullOrWhiteSpace(x.SpecialZodiac)).ToList();
        if (list.All(x => int.TryParse(x.Period, out _))) return list.OrderBy(x => int.Parse(x.Period)).ToList();
        return list.AsEnumerable().Reverse().ToList();
    }
}
