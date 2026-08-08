namespace 六合分析软件;

public enum MarketStateKind
{
    NormalRandom,
    ShortCycleRepeat,
    HotColdTransition,
    OmissionRelease
}

public sealed class MarketStateResult
{
    public MarketStateKind PrimaryState { get; init; }
    public Dictionary<MarketStateKind, double> Probabilities { get; init; } = new();
    public double Confidence { get; init; }
    public List<string> Evidence { get; init; } = new();
}

/// <summary>
/// Describes the observable state of a historical prefix. It does not use the
/// target draw and does not itself predict a zodiac.
/// </summary>
public static class MarketStateEngine
{
    public static MarketStateResult Detect(
        IReadOnlyList<DatabaseHelper.HistoryRecord> history, int targetIndex = int.MaxValue)
    {
        int end = Math.Clamp(targetIndex, 0, history.Count);
        var prior = history.Take(end)
            .Where(x => !string.IsNullOrWhiteSpace(x.SpecialZodiac))
            .OrderBy(x => long.TryParse(x.Period, out long period) ? period : long.MaxValue)
            .ToList();
        var features = FeatureEngine.BuildFeatures(prior, 100).ToList();
        if (features.Count == 0) return EmptyResult();

        double repeat = features.Average(x => x.Recent20Gap1RepeatCount + x.Recent20Gap2RepeatCount)
            + Math.Max(0, features.Average(x => x.RepeatFrequencyTrend));
        double momentumSpread = StandardDeviation(features.Select(x => x.Momentum5Vs20))
            + StandardDeviation(features.Select(x => x.Momentum10Vs50));
        double directionChange = features.Count(x => Math.Sign(x.Momentum5Vs20) != Math.Sign(x.Momentum20Vs100)) / 12d;
        double omission = features.OrderByDescending(x => x.OmissionRatio).Take(3).Average(x => Math.Max(0, x.OmissionRatio - 1));

        var raw = new Dictionary<MarketStateKind, double>
        {
            [MarketStateKind.ShortCycleRepeat] = 0.35 + repeat * 0.55,
            [MarketStateKind.HotColdTransition] = 0.35 + momentumSpread * 8 + directionChange,
            [MarketStateKind.OmissionRelease] = 0.35 + omission * 0.8,
        };
        double abnormal = raw.Values.Max();
        raw[MarketStateKind.NormalRandom] = 1.25 - Math.Min(0.9, Math.Max(0, abnormal - 0.5));
        var probabilities = Softmax(raw);
        var primary = probabilities.OrderByDescending(x => x.Value).ThenBy(x => x.Key).First();
        return new MarketStateResult
        {
            PrimaryState = primary.Key,
            Confidence = primary.Value,
            Probabilities = probabilities,
            Evidence = new List<string>
            {
                $"短周期重复强度={repeat:F3}",
                $"冷热动量离散={momentumSpread:F3}，方向转换占比={directionChange:P1}",
                $"高遗漏释放强度={omission:F3}",
                $"历史边界={prior.Count}期"
            }
        };
    }

    private static MarketStateResult EmptyResult() => new()
    {
        PrimaryState = MarketStateKind.NormalRandom,
        Confidence = 1,
        Probabilities = Enum.GetValues<MarketStateKind>().ToDictionary(x => x, x => x == MarketStateKind.NormalRandom ? 1d : 0d),
        Evidence = new List<string> { "没有可用历史数据" }
    };

    private static Dictionary<MarketStateKind, double> Softmax(Dictionary<MarketStateKind, double> raw)
    {
        double max = raw.Values.Max();
        var exp = raw.ToDictionary(x => x.Key, x => Math.Exp(Math.Clamp(x.Value - max, -30, 30)));
        double total = exp.Values.Sum();
        return exp.ToDictionary(x => x.Key, x => total == 0 ? 0.25 : x.Value / total);
    }

    private static double StandardDeviation(IEnumerable<double> source)
    {
        var values = source.ToArray();
        if (values.Length == 0) return 0;
        double mean = values.Average();
        return Math.Sqrt(values.Average(x => Math.Pow(x - mean, 2)));
    }
}
