namespace 六合分析软件;

public sealed record WeightScheme(string Name, double ShortWeight, double MediumWeight, double LongWeight);

public sealed class OptimizationCandidate
{
    public string Name { get; init; } = "";
    public double ShortWeight { get; init; }
    public double MediumWeight { get; init; }
    public double LongWeight { get; init; }
    public double Top6HitRate { get; init; }
    public double AverageOmission { get; init; }
    public double Score { get; init; }
}

public sealed class OptimizationResult
{
    public List<OptimizationCandidate> Candidates { get; } = new();
    public OptimizationCandidate? Best => Candidates.OrderByDescending(x => x.Score).FirstOrDefault();
}

public static class AutoOptimizeEngine
{
    public static OptimizationResult Optimize(IReadOnlyList<DatabaseHelper.HistoryRecord> history, int validationPeriods = 30)
    {
        var draws = Normalize(history);
        var schemes = new[]
        {
            new WeightScheme("A", .50, .30, .20),
            new WeightScheme("B", .40, .40, .20),
            new WeightScheme("C", .30, .30, .40)
        };
        var result = new OptimizationResult();
        int start = Math.Max(3, draws.Count - Math.Max(1, validationPeriods));
        foreach (var scheme in schemes)
        {
            int hits = 0, count = 0, omissionTotal = 0;
            for (int i = start; i < draws.Count; i++)
            {
                var prefix = draws.Take(i).ToList();
                var shortResult = ShortTermEngine.Predict(prefix);
                var mediumResult = MediumTermEngine.Predict(prefix);
                var longResult = LongTermEngine.Predict(prefix);
                var combined = new[] { shortResult, mediumResult, longResult }
                    .SelectMany((r, index) => r.Probabilities.Select(p => (p.Key, Value: p.Value * (index == 0 ? scheme.ShortWeight : index == 1 ? scheme.MediumWeight : scheme.LongWeight))))
                    .GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.Sum(x => x.Value));
                var ranked = combined.OrderByDescending(x => x.Value).ThenBy(x => x.Key).Select(x => x.Key).ToList();
                if (ranked.Take(6).Contains(draws[i].SpecialZodiac)) hits++;
                var actualFeature = FeatureEngine.BuildFeatures(prefix, 0).FirstOrDefault(x => x.Zodiac == draws[i].SpecialZodiac);
                omissionTotal += actualFeature?.CurrentOmission ?? 0;
                count++;
            }
            double hitRate = count == 0 ? 0 : hits / (double)count;
            double avgOmission = count == 0 ? 0 : omissionTotal / (double)count;
            result.Candidates.Add(new OptimizationCandidate { Name = scheme.Name, ShortWeight = scheme.ShortWeight,
                MediumWeight = scheme.MediumWeight, LongWeight = scheme.LongWeight, Top6HitRate = hitRate,
                AverageOmission = avgOmission, Score = hitRate - avgOmission / 1000 });
        }
        return result;
    }

    private static List<DatabaseHelper.HistoryRecord> Normalize(IReadOnlyList<DatabaseHelper.HistoryRecord> history)
    {
        var list = history.Where(x => !string.IsNullOrWhiteSpace(x.SpecialZodiac)).ToList();
        if (list.All(x => int.TryParse(x.Period, out _))) return list.OrderBy(x => int.Parse(x.Period)).ToList();
        return list.AsEnumerable().Reverse().ToList();
    }
}
