namespace 六合分析软件;

public sealed class ColorPredictionResult
{
    public string Excluded { get; init; } = "";
    public string Main { get; init; } = "";
    public string Defense { get; init; } = "";
    public Dictionary<string, double> Probabilities { get; init; } = new();
    public Dictionary<string, int> CurrentOmission { get; init; } = new();
    public Dictionary<string, IReadOnlyDictionary<string, double>> FeatureSignals { get; init; } = new();
    public ColorLearningWeights Weights { get; init; } = ColorLearningWeights.Default;
}

public static class ColorEngine
{
    private static readonly string[] Colors = { "红", "蓝", "绿" };
    private static readonly HashSet<int> Red = new() { 1, 4, 7, 10, 12, 15, 18, 21, 23, 26, 29, 30, 33, 36, 39, 42, 45, 48 };
    private static readonly HashSet<int> Blue = new() { 2, 5, 8, 11, 13, 16, 19, 22, 25, 28, 31, 34, 37, 40, 43, 46, 49 };

    public static ColorPredictionResult Predict(IReadOnlyList<DatabaseHelper.HistoryRecord> history,
        ColorLearningWeights? weights = null)
    {
        ColorLearningWeights activeWeights = ColorAutoLearningEngine.Normalize(weights ?? ColorLearningWeights.Default);
        var draws = history.Where(x => int.TryParse(x.SpecialNumber, out _)).ToList();
        var recent = draws.TakeLast(Math.Min(50, draws.Count)).ToList();
        var counts = Colors.ToDictionary(c => c, c => recent.Count(x => ColorOf(x.SpecialNumber) == c));
        var omissions = Colors.ToDictionary(c => c, c => CurrentOmission(draws, c));
        var transitions = Colors.ToDictionary(c => c, c => TransitionRate(recent, c));
        var signals = Colors.ToDictionary(c => c, c => (IReadOnlyDictionary<string, double>)
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["frequency"] = recent.Count == 0 ? 0 : counts[c] / (double)recent.Count,
                ["transition"] = transitions[c],
                ["omission"] = Math.Min(omissions[c], 20) / 20d
            });
        var raw = Colors.ToDictionary(c => c, c =>
            signals[c]["frequency"] * activeWeights.Frequency +
            signals[c]["transition"] * activeWeights.Transition +
            signals[c]["omission"] * activeWeights.Omission);
        double total = raw.Values.Sum();
        var probabilities = raw.ToDictionary(x => x.Key, x => total == 0 ? 1d / 3 : x.Value / total);
        var ranked = probabilities.OrderByDescending(x => x.Value).ThenBy(x => x.Key).Select(x => x.Key).ToList();
        return new ColorPredictionResult
        {
            Excluded = ranked.Last(), Main = ranked[0], Defense = ranked[1], Probabilities = probabilities,
            CurrentOmission = omissions, FeatureSignals = signals, Weights = activeWeights
        };
    }

    private static double TransitionRate(IReadOnlyList<DatabaseHelper.HistoryRecord> draws, string color)
    {
        if (draws.Count < 2) return 0;
        int hits = 0;
        for (int i = 1; i < draws.Count; i++) if (ColorOf(draws[i - 1].SpecialNumber) != color && ColorOf(draws[i].SpecialNumber) == color) hits++;
        return hits / (double)(draws.Count - 1);
    }

    private static int CurrentOmission(IReadOnlyList<DatabaseHelper.HistoryRecord> draws, string color)
    {
        int count = 0;
        for (int i = draws.Count - 1; i >= 0 && ColorOf(draws[i].SpecialNumber) != color; i--) count++;
        return count;
    }

    public static string ColorOf(string number)
    {
        if (!int.TryParse(number, out int n)) return "";
        if (Red.Contains(n)) return "红";
        if (Blue.Contains(n)) return "蓝";
        return "绿";
    }
}
