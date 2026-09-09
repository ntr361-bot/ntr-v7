using System.Collections.Immutable;

namespace 六合分析软件;

public sealed record V65BaseMacroExpertDefinition(
    string ExpertId,
    string ExpertRevisionId,
    string DisplayName,
    string AlgorithmVersion,
    int AnalysisPeriods);

public sealed record V65BaseMacroRankedZodiac(string Zodiac, double Score, int Rank);

public sealed record V65BaseMacroRanking(
    string ExpertId,
    string ExpertRevisionId,
    string AlgorithmVersion,
    int AnalysisPeriods,
    ImmutableArray<string> Ranking,
    ImmutableArray<V65BaseMacroRankedZodiac> Items);

/// <summary>
/// V6.5基础规则模型的Macro旁路适配器。它只接受显式历史前缀，并复用正式评分入口；
/// 不读取数据库、PredictionHistory、ModelMemory或目标期开奖结果。
/// </summary>
public static class V65BaseMacroExpertAdapter
{
    public static readonly ImmutableArray<V65BaseMacroExpertDefinition> Definitions =
    [
        new("V65-50", "V65-50@macro-full12-f016-t016-o020-h016-p032-c000-v1", "V6.5-50 Macro FullRanking12",
            "v65-rule-full12-window50-f016-t016-o020-h016-p032-c000-v1", 50),
        new("V65-100", "V65-100@macro-full12-f024-t013-o016-h020-p027-c000-v1", "V6.5-100 Macro FullRanking12",
            "v65-rule-full12-window100-f024-t013-o016-h020-p027-c000-v1", 100),
        new("V65-All", "V65-All@macro-full12-f017-t017-o015-h017-p034-c000-v1", "V6.5-全部历史 Macro FullRanking12",
            "v65-rule-full12-window-all-f017-t017-o015-h017-p034-c000-v1", AISettings.AllHistoryModeValue)
    ];

    public static V65BaseMacroExpertDefinition DefinitionFor(string expertId) =>
        Definitions.SingleOrDefault(x => string.Equals(x.ExpertId, expertId, StringComparison.Ordinal))
        ?? throw new ArgumentOutOfRangeException(nameof(expertId), "仅支持V65-50、V65-100和V65-All基础专家");

    public static V65BaseMacroRanking Build(
        IReadOnlyList<DatabaseHelper.HistoryRecord> history,
        string expertId)
    {
        ArgumentNullException.ThrowIfNull(history);
        V65BaseMacroExpertDefinition definition = DefinitionFor(expertId);
        var result = new V65RuleScoringEngine().Predict(history, definition.AnalysisPeriods,
            V65ExperimentPipeline.GetWeightsForPeriods(definition.AnalysisPeriods));

        ImmutableArray<V65BaseMacroRankedZodiac> items = result.AllScores
            .OrderByDescending(x => x.TotalScore)
            .Select((x, index) => new V65BaseMacroRankedZodiac(x.Zodiac, x.TotalScore, index + 1))
            .ToImmutableArray();
        ImmutableArray<string> ranking = items.Select(x => x.Zodiac).ToImmutableArray();
        MacroReasoning.ExpertSnapshotIntegrity.ValidateRanking(ranking);

        return new V65BaseMacroRanking(definition.ExpertId, definition.ExpertRevisionId,
            definition.AlgorithmVersion, definition.AnalysisPeriods, ranking, items);
    }
}
