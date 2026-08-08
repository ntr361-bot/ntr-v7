namespace 六合分析软件;

public sealed class V82PredictionResult
{
    public MarketStateResult State { get; init; } = new();
    public IReadOnlyList<ZodiacRankItem> Items { get; init; } = Array.Empty<ZodiacRankItem>();
    public IReadOnlyList<string> Top3 => Items.Take(3).Select(x => x.Zodiac).ToArray();
    public IReadOnlyList<string> Top6 => Items.Take(6).Select(x => x.Zodiac).ToArray();
    public Dictionary<string, double> RoutingWeights { get; init; } = new();
}

public static class V82StateRouter
{
    public static V82PredictionResult Route(
        MarketStateResult state,
        V7PredictionResult shortResult,
        V7PredictionResult mediumResult,
        V7PredictionResult longResult,
        ZodiacRankingResult ranking)
    {
        var weights = Profile(state.PrimaryState);
        var zodiacs = ranking.Items.Select(x => x.Zodiac).Distinct().ToList();
        var rankingProbabilities = ranking.Items.ToDictionary(x => x.Zodiac, x => x.Probability);
        var raw = zodiacs.ToDictionary(zodiac => zodiac, zodiac =>
            weights["short"] * Probability(shortResult, zodiac) +
            weights["medium"] * Probability(mediumResult, zodiac) +
            weights["long"] * Probability(longResult, zodiac) +
            weights["ranking"] * rankingProbabilities.GetValueOrDefault(zodiac));
        double total = raw.Values.Sum();
        var ordered = raw.OrderByDescending(x => x.Value).ThenBy(x => x.Key).ToList();
        var items = ordered.Select((x, index) => new ZodiacRankItem(
            x.Key, index + 1, x.Value, total <= 0 ? 1d / Math.Max(1, raw.Count) : x.Value / total)).ToList();
        return new V82PredictionResult { State = state, Items = items, RoutingWeights = weights };
    }

    public static Dictionary<string, double> Profile(MarketStateKind state) => state switch
    {
        MarketStateKind.ShortCycleRepeat => new() { ["short"] = 0.55, ["medium"] = 0.15, ["long"] = 0.10, ["ranking"] = 0.20 },
        MarketStateKind.HotColdTransition => new() { ["short"] = 0.25, ["medium"] = 0.35, ["long"] = 0.15, ["ranking"] = 0.25 },
        MarketStateKind.OmissionRelease => new() { ["short"] = 0.10, ["medium"] = 0.25, ["long"] = 0.40, ["ranking"] = 0.25 },
        _ => new() { ["short"] = 0.20, ["medium"] = 0.30, ["long"] = 0.30, ["ranking"] = 0.20 }
    };

    private static double Probability(V7PredictionResult result, string zodiac) =>
        result.Probabilities.TryGetValue(zodiac, out double value) && double.IsFinite(value) ? value : 0;
}
