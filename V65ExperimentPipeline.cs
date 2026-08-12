using System.Text.Json;

namespace 六合分析软件;

/// <summary>
/// V6.5 四模型唯一预测链：三个正式 V2 周期模型先排序，自动学习只读取这三条快照。
/// </summary>
public static class V65ExperimentPipeline
{
    private static readonly int[] Periods = { 50, 100, AISettings.AllHistoryModeValue };

    public static AutoLearningSnapshot BuildSnapshot(IReadOnlyList<DatabaseHelper.HistoryRecord> prefix,
        string issue, ModelMemoryState memory)
    {
        var engine = new V65RuleScoringEngine();
        var records = Periods.Select(period =>
        {
            V65RuleScoringEngine.PredictResultV2 result = engine.Predict(prefix, period);
            string[] ranking = result.AllScores.OrderByDescending(score => score.TotalScore)
                .Select(score => score.Zodiac).ToArray();
            return new DatabaseHelper.PredictionRecord
            {
                Issue = issue,
                ModelVersion = "V6.5",
                AnalysisPeriods = period,
                FinalRankingJson = JsonSerializer.Serialize(ranking)
            };
        }).ToArray();

        return AutoLearningSnapshotBuilder.BuildFromBasePredictions(issue, records, memory);
    }
}
