namespace 六合分析软件;

public sealed record ZodiacRankItem(string Zodiac, int Rank, double Score, double Probability);

public sealed class ZodiacRankingResult
{
    public IReadOnlyList<ZodiacRankItem> Items { get; init; } = Array.Empty<ZodiacRankItem>();
    public IReadOnlyList<string> Top3 => Items.Take(3).Select(x => x.Zodiac).ToArray();
    public IReadOnlyList<string> Top6 => Items.Take(6).Select(x => x.Zodiac).ToArray();
    public int TrainingTargets { get; init; }
    public double Top3Margin { get; init; }
    public double RankConfidence { get; init; }
    public double MeanAbsoluteRankChange { get; init; }
}

/// <summary>
/// Online pairwise logistic ranker. At each historical target it learns that
/// the actual zodiac should rank above each of the other eleven zodiacs.
/// </summary>
public sealed class ZodiacRankingModel
{
    private static readonly string[] StateFeatureNames =
    {
        "state_short_cycle", "state_cold_hot", "state_missing", "state_random"
    };
    private readonly bool includeStateMissingFeature;
    private readonly double[] weights = new double[FeatureEngine.FeatureNames.Count + StateFeatureNames.Length];
    public int TrainingTargets { get; private set; }

    public ZodiacRankingModel(bool includeStateMissingFeature = true)
    {
        this.includeStateMissingFeature = includeStateMissingFeature;
    }

    public void Update(
        IReadOnlyList<ZodiacFeature> features,
        string actualZodiac,
        IReadOnlyDictionary<MarketStateKind, double>? stateProbabilities = null)
    {
        var normalized = BuildModelVectors(features, stateProbabilities, includeStateMissingFeature);
        if (!normalized.TryGetValue(actualZodiac, out var actual)) return;
        double learningRate = 0.025 / Math.Sqrt(1 + TrainingTargets / 80d);
        foreach (var candidate in normalized.Where(x => x.Key != actualZodiac))
        {
            var other = candidate.Value;
            double margin = Dot(weights, actual) - Dot(weights, other);
            double gradient = 1d - Sigmoid(margin);
            for (int i = 0; i < weights.Length; i++)
            {
                double difference = Math.Clamp(actual[i] - other[i], -5, 5);
                weights[i] = Math.Clamp(weights[i] * (1 - 0.00005) + learningRate * gradient * difference, -4, 4);
            }
        }
        TrainingTargets++;
    }

    public ZodiacRankingResult Rank(
        IReadOnlyList<ZodiacFeature> features,
        IReadOnlyDictionary<MarketStateKind, double>? stateProbabilities = null,
        ZodiacRankingResult? previous = null)
    {
        var normalized = BuildModelVectors(features, stateProbabilities, includeStateMissingFeature);
        var raw = normalized.ToDictionary(x => x.Key, x => Dot(weights, x.Value));
        double max = raw.Count == 0 ? 0 : raw.Values.Max();
        var exponentials = raw.ToDictionary(x => x.Key, x => Math.Exp(Math.Clamp((x.Value - max) / 1.5, -30, 30)));
        double total = exponentials.Values.Sum();
        var ordered = raw.OrderByDescending(x => x.Value).ThenBy(x => x.Key).ToList();
        var items = ordered.Select((x, index) => new ZodiacRankItem(
            x.Key, index + 1, x.Value, total == 0 ? 1d / Math.Max(1, raw.Count) : exponentials[x.Key] / total)).ToList();
        double top3Margin = items.Count >= 4 ? Math.Max(0, items[2].Score - items[3].Score) : 0;
        double entropy = items.Count <= 1 ? 0 : -items.Sum(x => x.Probability <= 0 ? 0 : x.Probability * Math.Log(x.Probability));
        double confidence = items.Count <= 1 ? 1 : Math.Clamp(1d - entropy / Math.Log(items.Count), 0, 1);
        double rankChange = previous == null ? 0 : MeanRankChange(items, previous.Items);
        return new ZodiacRankingResult
        {
            Items = items,
            TrainingTargets = TrainingTargets,
            Top3Margin = top3Margin,
            RankConfidence = confidence,
            MeanAbsoluteRankChange = rankChange
        };
    }

    public IReadOnlyDictionary<string, double> FeatureWeights() =>
        FeatureEngine.FeatureNames.Concat(StateFeatureNames).Select((name, index) => (name, weights[index]))
            .ToDictionary(x => x.name, x => x.Item2);

    private static Dictionary<string, double[]> BuildModelVectors(
        IReadOnlyList<ZodiacFeature> features,
        IReadOnlyDictionary<MarketStateKind, double>? stateProbabilities,
        bool includeStateMissingFeature)
    {
        var normalized = Normalize(features);
        double Probability(MarketStateKind state, double fallback = 0) =>
            stateProbabilities != null && stateProbabilities.TryGetValue(state, out double value)
                ? Math.Clamp(value, 0, 1)
                : fallback;

        int Index(string name)
        {
            for (int i = 0; i < FeatureEngine.FeatureNames.Count; i++)
                if (FeatureEngine.FeatureNames[i] == name) return i;
            throw new InvalidOperationException($"Unknown feature: {name}");
        }
        int gap1 = Index("recent_20_gap_1_repeat");
        int gap2 = Index("recent_20_gap_2_repeat");
        int repeatTrend = Index("repeat_frequency_trend");
        int shortMomentum = Index("momentum_5_vs_20");
        int longMomentum = Index("momentum_20_vs_100");
        int omission = Index("omission_ratio");
        int historical = Index("historical_rate");

        return normalized.ToDictionary(row => row.Key, row =>
        {
            var baseVector = row.Value;
            double shortSignal = (baseVector[gap1] + baseVector[gap2] + baseVector[repeatTrend]) / 3d;
            double hotColdSignal = (Math.Abs(baseVector[shortMomentum]) + Math.Abs(baseVector[longMomentum])) / 2d;
            var vector = new double[baseVector.Length + StateFeatureNames.Length];
            Array.Copy(baseVector, vector, baseVector.Length);
            vector[baseVector.Length] = Probability(MarketStateKind.ShortCycleRepeat) * shortSignal;
            vector[baseVector.Length + 1] = Probability(MarketStateKind.HotColdTransition) * hotColdSignal;
            vector[baseVector.Length + 2] = includeStateMissingFeature
                ? Probability(MarketStateKind.OmissionRelease) * baseVector[omission]
                : 0;
            vector[baseVector.Length + 3] = Probability(MarketStateKind.NormalRandom, 1) * baseVector[historical];
            return vector;
        });
    }

    private static double MeanRankChange(
        IReadOnlyList<ZodiacRankItem> current,
        IReadOnlyList<ZodiacRankItem> previous)
    {
        var previousRanks = previous.ToDictionary(x => x.Zodiac, x => x.Rank);
        var changes = current.Where(x => previousRanks.ContainsKey(x.Zodiac))
            .Select(x => Math.Abs(x.Rank - previousRanks[x.Zodiac]))
            .ToArray();
        return changes.Length == 0 ? 0 : changes.Average();
    }

    private static Dictionary<string, double[]> Normalize(IReadOnlyList<ZodiacFeature> features)
    {
        var rows = features.ToDictionary(x => x.Zodiac, x => x.ToVector());
        if (rows.Count == 0) return rows;
        int width = FeatureEngine.FeatureNames.Count;
        var means = new double[width];
        var scales = new double[width];
        for (int feature = 0; feature < width; feature++)
        {
            means[feature] = rows.Values.Average(x => x[feature]);
            scales[feature] = Math.Sqrt(rows.Values.Average(x => Math.Pow(x[feature] - means[feature], 2)));
            if (scales[feature] < 1e-9) scales[feature] = 1;
        }
        return rows.ToDictionary(x => x.Key, x => x.Value.Select((value, feature) =>
            Math.Clamp((value - means[feature]) / scales[feature], -5, 5)).ToArray());
    }

    private static double Dot(double[] left, double[] right)
    {
        double sum = 0;
        for (int i = 0; i < Math.Min(left.Length, right.Length); i++) sum += left[i] * right[i];
        return sum;
    }

    private static double Sigmoid(double value) => 1d / (1d + Math.Exp(-Math.Clamp(value, -30, 30)));
}

public static class ZodiacRankingEngine
{
    public static ZodiacRankingResult Predict(
        IReadOnlyList<DatabaseHelper.HistoryRecord> history,
        int targetIndex = int.MaxValue,
        int minimumTraining = 30)
    {
        int end = Math.Clamp(targetIndex, 0, history.Count);
        var prior = history.Take(end)
            .Where(x => !string.IsNullOrWhiteSpace(x.SpecialZodiac))
            .OrderBy(x => long.TryParse(x.Period, out long period) ? period : long.MaxValue)
            .ToList();
        var model = new ZodiacRankingModel();
        for (int target = Math.Max(1, minimumTraining); target < prior.Count; target++)
        {
            var prefix = prior.Take(target).ToList();
            var state = MarketStateEngine.Detect(prior, target);
            model.Update(FeatureEngine.BuildFeatures(prefix), prior[target].SpecialZodiac, state.Probabilities);
        }
        var currentState = MarketStateEngine.Detect(prior);
        return model.Rank(FeatureEngine.BuildFeatures(prior), currentState.Probabilities);
    }
}
