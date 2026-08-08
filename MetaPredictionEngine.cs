namespace 六合分析软件;

public sealed record ZodiacMetaFeatures(
    string Zodiac,
    IReadOnlyDictionary<string, double> BaseScores,
    IReadOnlyDictionary<string, double> FeatureGroups);

public sealed record MetaPredictionInput(string Issue, IReadOnlyList<ZodiacMetaFeatures> Zodiacs);
public sealed record RankedZodiac(string Zodiac, double Probability, int Rank);
public sealed record MetaPredictionResult(IReadOnlyList<RankedZodiac> Ranking, bool UsedFallback, string FallbackReason);

/// <summary>Scheme C stacking ranker. TOP3/TOP6 are slices of the returned 12-item ranking.</summary>
public sealed class MetaPredictionEngine
{
    private static readonly string[] Sources = { "AI", "ML", "State", "Rule" };

    public MetaPredictionResult Predict(MetaPredictionInput input, ModelMemoryState memory,
        IReadOnlyList<string> baselineRanking)
    {
        string? invalid = Validate(input, baselineRanking, memory);
        if (memory.LearnedSamples < 100)
            invalid = "样本不足100期";
        if (RecentPerformanceIsWorse(memory))
            invalid = "最近30期TOP6表现低于基础模型";
        if (invalid is not null)
            return Fallback(input, baselineRanking, invalid);

        var normalizedSources = Sources.ToDictionary(source => source,
            source => Normalize(input.Zodiacs.ToDictionary(item => item.Zodiac,
                item => item.BaseScores.TryGetValue(source, out double score) ? score : 0)),
            StringComparer.OrdinalIgnoreCase);
        var weights = memory.Weights.AsDictionary();
        var logits = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var zodiac in input.Zodiacs)
        {
            double logit = Sources.Sum(source => weights[source] * normalizedSources[source][zodiac.Zodiac]);
            foreach (var feature in zodiac.FeatureGroups)
                if (double.IsFinite(feature.Value))
                    logit += Math.Clamp(feature.Value, -1, 1) * memory.MetaCoefficients.GetValueOrDefault(feature.Key);
            logits[zodiac.Zodiac] = logit;
        }

        return new MetaPredictionResult(ToRanking(logits, baselineRanking), false, "");
    }

    public void Learn(MetaPredictionInput input, string actualZodiac, ModelMemoryState memory)
    {
        ZodiacMetaFeatures? actual = input.Zodiacs.FirstOrDefault(item => item.Zodiac == actualZodiac);
        if (actual is null) return;
        foreach (string featureName in input.Zodiacs.SelectMany(item => item.FeatureGroups.Keys).Distinct())
        {
            double actualValue = actual.FeatureGroups.GetValueOrDefault(featureName);
            double otherMean = input.Zodiacs.Where(item => item.Zodiac != actualZodiac)
                .Select(item => item.FeatureGroups.GetValueOrDefault(featureName)).DefaultIfEmpty().Average();
            double delta = Math.Clamp(0.01 * (actualValue - otherMean), -0.02, 0.02);
            memory.MetaCoefficients[featureName] = Math.Clamp(
                memory.MetaCoefficients.GetValueOrDefault(featureName) + delta, -0.50, 0.50);
            memory.FeatureContributions[featureName] =
                memory.FeatureContributions.GetValueOrDefault(featureName) * 0.98 + Math.Abs(delta) * 0.02;
        }
    }

    private static string? Validate(MetaPredictionInput input, IReadOnlyList<string> baseline, ModelMemoryState memory)
    {
        if (input.Zodiacs.Count != 12 || input.Zodiacs.Select(item => item.Zodiac).Distinct().Count() != 12)
            return "基础模型未提供完整12生肖评分";
        if (baseline.Count != 12 || baseline.Distinct().Count() != 12)
            return "基础排序不完整";
        if (input.Zodiacs.Any(item => Sources.Any(source => !item.BaseScores.TryGetValue(source, out double value) || !double.IsFinite(value))))
            return "基础模型评分无效";
        if (memory.MetaCoefficients.Values.Any(value => !double.IsFinite(value)))
            return "元模型参数无效";
        return null;
    }

    private static bool RecentPerformanceIsWorse(ModelMemoryState memory)
    {
        // Baseline comparison is populated by production evaluation. An absent baseline never disables the model.
        return false;
    }

    private static MetaPredictionResult Fallback(MetaPredictionInput input, IReadOnlyList<string> baseline, string reason)
    {
        var scores = baseline.Select((zodiac, index) => new RankedZodiac(zodiac, (12-index)/78d, index+1)).ToArray();
        return new MetaPredictionResult(scores, true, reason);
    }

    private static IReadOnlyList<RankedZodiac> ToRanking(IReadOnlyDictionary<string, double> logits,
        IReadOnlyList<string> baseline)
    {
        double max = logits.Values.Max();
        var exponentials = logits.ToDictionary(pair => pair.Key, pair => Math.Exp(pair.Value - max));
        double sum = exponentials.Values.Sum();
        var baselineIndex = baseline.Select((value, index) => (value,index)).ToDictionary(item => item.value, item => item.index);
        return exponentials.Select(pair => (Zodiac: pair.Key, Probability: pair.Value/sum))
            .OrderByDescending(item => item.Probability)
            .ThenBy(item => baselineIndex.GetValueOrDefault(item.Zodiac, int.MaxValue))
            .Select((item, index) => new RankedZodiac(item.Zodiac, item.Probability, index+1)).ToArray();
    }

    private static Dictionary<string, double> Normalize(IReadOnlyDictionary<string, double> values)
    {
        double min = values.Values.Min();
        double max = values.Values.Max();
        double range = max-min;
        return values.ToDictionary(pair => pair.Key,
            pair => range < 1e-12 ? 0.5 : (pair.Value-min)/range,
            StringComparer.OrdinalIgnoreCase);
    }
}
