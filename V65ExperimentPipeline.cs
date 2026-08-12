using System.Text.Json;

namespace 六合分析软件;

/// <summary>
/// V6.5 四模型唯一预测链：三个正式 V2 周期模型先排序，自动学习只读取这三条快照。
/// </summary>
public static class V65ExperimentPipeline
{
    private static readonly int[] Periods = { 50, 100, AISettings.AllHistoryModeValue };

    private static readonly V65RuleScoringEngine.WeightConfig Period50Weights = new()
    {
        FrequencyWeight = 0.16, RecentTrendWeight = 0.16, OmissionWeight = 0.20,
        HotColdWeight = 0.16, PeriodPatternWeight = 0.32, ConsecutiveWeight = 0
    };

    private static readonly V65RuleScoringEngine.WeightConfig Period100Weights = new()
    {
        FrequencyWeight = 0.24, RecentTrendWeight = 0.13, OmissionWeight = 0.16,
        HotColdWeight = 0.20, PeriodPatternWeight = 0.27, ConsecutiveWeight = 0
    };

    private static readonly V65RuleScoringEngine.WeightConfig AllHistoryWeights = new()
    {
        FrequencyWeight = 0.17, RecentTrendWeight = 0.17, OmissionWeight = 0.15,
        HotColdWeight = 0.17, PeriodPatternWeight = 0.34, ConsecutiveWeight = 0
    };

    public sealed record BaseModelPrediction(int AnalysisPeriods, V65RuleScoringEngine.PredictResultV2 Result);

    public static V65RuleScoringEngine.WeightConfig GetWeightsForPeriods(int periods)
    {
        V65RuleScoringEngine.WeightConfig source = periods switch
        {
            50 => Period50Weights,
            100 => Period100Weights,
            AISettings.AllHistoryModeValue => AllHistoryWeights,
            _ => throw new ArgumentOutOfRangeException(nameof(periods), "V6.5实验只支持50期、100期和全部历史。")
        };
        return new V65RuleScoringEngine.WeightConfig
        {
            FrequencyWeight = source.FrequencyWeight,
            RecentTrendWeight = source.RecentTrendWeight,
            OmissionWeight = source.OmissionWeight,
            HotColdWeight = source.HotColdWeight,
            PeriodPatternWeight = source.PeriodPatternWeight,
            ConsecutiveWeight = source.ConsecutiveWeight
        };
    }

    public static IReadOnlyList<BaseModelPrediction> RunBaseModels(
        IReadOnlyList<DatabaseHelper.HistoryRecord> prefix, string issue)
    {
        if (string.IsNullOrWhiteSpace(issue)) throw new ArgumentException("预测期号不能为空", nameof(issue));
        var engine = new V65RuleScoringEngine();
        return Periods.Select(period => new BaseModelPrediction(period,
            engine.Predict(prefix, period, GetWeightsForPeriods(period)))).ToArray();
    }

    public static AutoLearningSnapshot BuildSnapshot(IReadOnlyList<DatabaseHelper.HistoryRecord> prefix,
        string issue, ModelMemoryState memory)
    {
        var records = RunBaseModels(prefix, issue).Select(model =>
        {
            string[] ranking = model.Result.AllScores.OrderByDescending(score => score.TotalScore)
                .Select(score => score.Zodiac).ToArray();
            return new DatabaseHelper.PredictionRecord
            {
                Issue = issue,
                ModelVersion = "V6.5",
                AnalysisPeriods = model.AnalysisPeriods,
                FinalRankingJson = JsonSerializer.Serialize(ranking)
            };
        }).ToArray();

        return AutoLearningSnapshotBuilder.BuildFromBasePredictions(issue, records, memory);
    }
}
