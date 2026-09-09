using System.Collections.Immutable;

namespace 六合分析软件;

public sealed record IntegratedV7MacroRankedZodiac(
    string Zodiac,
    double Score,
    bool ShortForbidden,
    int Rank);

public sealed record IntegratedV7MacroRanking(
    ImmutableArray<string> Ranking,
    ImmutableArray<IntegratedV7MacroRankedZodiac> Items);

/// <summary>
/// Integrated-V7 的 Macro 旁路适配器。正式 V7 继续保留 ShortForbidden 硬过滤；
/// 本适配器只把被过滤项按原始底层分数放到完整排名尾部。
/// </summary>
public static class IntegratedV7MacroExpertAdapter
{
    private static readonly string[] Zodiac =
        { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };

    public static IntegratedV7MacroRanking Build(IReadOnlyList<DatabaseHelper.HistoryRecord> history)
    {
        if (history is null) throw new ArgumentNullException(nameof(history));
        ZodiacFeature[] features = FeatureEngine.BuildFeatures(history, 0).ToArray();
        if (features.Length != 12 || features.Select(x => x.Zodiac).Distinct(StringComparer.Ordinal).Count() != 12 ||
            features.Any(x => !Zodiac.Contains(x.Zodiac, StringComparer.Ordinal)))
            throw new InvalidDataException("Integrated-V7 Macro适配需要完整12生肖特征");

        var scored = features.Select(feature => new
        {
            Feature = feature,
            Score = 0.55 * (feature.Recent10Count + feature.Recent20Count * 0.5 + feature.Recent50Count * 0.2) +
                    0.45 * Math.Min(feature.CurrentOmission, feature.AverageOmission * 2 + 1) +
                    0.10 * feature.ShortCycleRepeatCount
        }).ToArray();

        var ordered = scored.Where(x => !x.Feature.ShortForbidden)
            .OrderByDescending(x => x.Score).ThenBy(x => x.Feature.Zodiac, StringComparer.Ordinal)
            .Concat(scored.Where(x => x.Feature.ShortForbidden)
                .OrderByDescending(x => x.Score).ThenBy(x => x.Feature.Zodiac, StringComparer.Ordinal))
            .Select((x, index) => new IntegratedV7MacroRankedZodiac(
                x.Feature.Zodiac, x.Score, x.Feature.ShortForbidden, index + 1))
            .ToImmutableArray();

        ImmutableArray<string> ranking = ordered.Select(x => x.Zodiac).ToImmutableArray();
        if (ranking.Length != 12 || ranking.Distinct(StringComparer.Ordinal).Count() != 12)
            throw new InvalidDataException("Integrated-V7 Macro适配结果不是完整12生肖排名");
        return new IntegratedV7MacroRanking(ranking, ordered);
    }
}
