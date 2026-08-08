using System.Text.Json;

namespace 六合分析软件;

public sealed class V7PredictionResult
{
    public string Engine { get; init; } = "";
    public int Window { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.Now;
    public List<ZodiacFeature> Features { get; init; } = new();
    public List<string> Top3 { get; init; } = new();
    public List<string> Top6 { get; init; } = new();
    public Dictionary<string, double> Probabilities { get; init; } = new();
}

public static class ShortTermEngine
{
    public static V7PredictionResult Predict(IReadOnlyList<DatabaseHelper.HistoryRecord> history)
    {
        var features = FeatureEngine.BuildFeatures(history, 50).ToList();
        return Build("ShortTermEngine", 50, features, 1.0, 0.25);
    }

    public static string SaveResult(V7PredictionResult result, string? directory = null) => Save(result, directory ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "V7Models", "ShortTerm"));
    private static V7PredictionResult Build(string name, int window, List<ZodiacFeature> features, double frequencyWeight, double omissionWeight) => EngineScoring.Build(name, window, features, frequencyWeight, omissionWeight);
    private static string Save(V7PredictionResult result, string directory) => EngineScoring.Save(result, directory);
}

public static class MediumTermEngine
{
    public static V7PredictionResult Predict(IReadOnlyList<DatabaseHelper.HistoryRecord> history)
    {
        var features = FeatureEngine.BuildFeatures(history, 100).ToList();
        return EngineScoring.Build("MediumTermEngine", 100, features, 0.8, 0.35);
    }

    public static string SaveResult(V7PredictionResult result, string? directory = null) => EngineScoring.Save(result, directory ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "V7Models", "MediumTerm"));
}

public static class LongTermEngine
{
    public static V7PredictionResult Predict(IReadOnlyList<DatabaseHelper.HistoryRecord> history)
    {
        var features = FeatureEngine.BuildFeatures(history, 0).ToList();
        return EngineScoring.Build("LongTermEngine", 0, features, 0.55, 0.45);
    }

    public static string SaveResult(V7PredictionResult result, string? directory = null) => EngineScoring.Save(result, directory ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "V7Models", "LongTerm"));
}

internal static class EngineScoring
{
    public static V7PredictionResult Build(string name, int window, List<ZodiacFeature> features, double frequencyWeight, double omissionWeight)
    {
        // short_forbidden is a hard candidate filter, not a weight reduction.
        var candidates = features.Where(x => !x.ShortForbidden).ToList();
        var raw = candidates.ToDictionary(x => x.Zodiac, x =>
            frequencyWeight * (x.Recent10Count + x.Recent20Count * 0.5 + x.Recent50Count * 0.2) +
            omissionWeight * Math.Min(x.CurrentOmission, x.AverageOmission * 2 + 1) +
            0.1 * x.ShortCycleRepeatCount);
        double total = raw.Values.Sum(v => Math.Max(0, v));
        var probabilities = raw.ToDictionary(x => x.Key, x => total <= 0 ? 0d : Math.Max(0, x.Value) / total);
        var ranked = probabilities.OrderByDescending(x => x.Value).ThenBy(x => x.Key).Select(x => x.Key).ToList();
        return new V7PredictionResult
        {
            Engine = name, Window = window, Features = features,
            Probabilities = probabilities, Top3 = ranked.Take(3).ToList(), Top6 = ranked.Take(6).ToList()
        };
    }

    public static string Save(V7PredictionResult result, string directory)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"{DateTime.Now:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }
}
