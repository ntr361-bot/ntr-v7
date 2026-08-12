namespace 六合分析软件;

public sealed record V65ExperimentBacktestResult(IReadOnlyList<ModelScoreResult> Models, int MinimumTrainingPeriods);

/// <summary>
/// 严格按正式 V6.5 四模型链做滚动验证；不包含智能预测历史或单因子竞争模型。
/// </summary>
public static class V65ExperimentBacktestService
{
    private static readonly string[] ModelNames = { "V6.5-50期", "V6.5-100期", "V6.5-全部历史", "V6.5-自动学习" };

    public static V65ExperimentBacktestResult Run(IReadOnlyList<DatabaseHelper.HistoryRecord> records,
        int minimumTrainingPeriods = 100)
    {
        var chronological = AutoLearningTrainer.Normalize(records);
        if (minimumTrainingPeriods < 10) throw new ArgumentOutOfRangeException(nameof(minimumTrainingPeriods));
        var predictions = ModelNames.ToDictionary(name => name, _ => new List<BacktestPredictionRecord>());
        var memory = new ModelMemoryState();

        for (int target = minimumTrainingPeriods; target < chronological.Count; target++)
        {
            DatabaseHelper.HistoryRecord actual = chronological[target];
            var prefix = chronological.Take(target).ToArray();
            IReadOnlyList<V65ExperimentPipeline.BaseModelPrediction> baseModels =
                V65ExperimentPipeline.RunBaseModels(prefix, actual.Period);

            Add(predictions[ModelNames[0]], actual, baseModels.Single(model => model.AnalysisPeriods == 50).Result.Top3,
                baseModels.Single(model => model.AnalysisPeriods == 50).Result.Top6);
            Add(predictions[ModelNames[1]], actual, baseModels.Single(model => model.AnalysisPeriods == 100).Result.Top3,
                baseModels.Single(model => model.AnalysisPeriods == 100).Result.Top6);
            Add(predictions[ModelNames[2]], actual, baseModels.Single(model => model.AnalysisPeriods == AISettings.AllHistoryModeValue).Result.Top3,
                baseModels.Single(model => model.AnalysisPeriods == AISettings.AllHistoryModeValue).Result.Top6);

            AutoLearningSnapshot auto = V65ExperimentPipeline.BuildSnapshot(prefix, actual.Period, memory);
            string[] autoRanking = auto.Result.Ranking.Select(item => item.Zodiac).ToArray();
            Add(predictions[ModelNames[3]], actual, autoRanking.Take(3), autoRanking.Take(6));
            AutoLearningTrainer.LearnOne(auto.Input, autoRanking, actual.SpecialZodiac, memory);
        }

        return new V65ExperimentBacktestResult(predictions.Select(pair => ToScore(pair.Key, pair.Value)).ToArray(),
            minimumTrainingPeriods);
    }

    private static void Add(List<BacktestPredictionRecord> records, DatabaseHelper.HistoryRecord actual,
        IEnumerable<string> top3, IEnumerable<string> top6)
    {
        string[] first = top3.ToArray();
        string[] six = top6.ToArray();
        records.Add(new BacktestPredictionRecord
        {
            Period = actual.Period,
            ActualZodiac = actual.SpecialZodiac,
            Top3 = first.ToList(),
            Top6 = six.ToList(),
            Top3Hit = first.Contains(actual.SpecialZodiac),
            Top6Hit = six.Contains(actual.SpecialZodiac)
        });
    }

    private static ModelScoreResult ToScore(string name, List<BacktestPredictionRecord> records)
    {
        int maximumMisses = 0;
        int misses = 0;
        foreach (BacktestPredictionRecord record in records)
        {
            misses = record.Top6Hit ? 0 : misses + 1;
            maximumMisses = Math.Max(maximumMisses, misses);
        }
        int count = records.Count;
        int top3Hits = records.Count(record => record.Top3Hit);
        int top6Hits = records.Count(record => record.Top6Hit);
        double top3 = count == 0 ? 0 : top3Hits / (double)count;
        double top6 = count == 0 ? 0 : top6Hits / (double)count;
        return new ModelScoreResult
        {
            ModelName = name,
            TotalTests = count,
            Top3Hits = top3Hits,
            Top6Hits = top6Hits,
            Top3HitRate = top3,
            Top6HitRate = top6,
            MaxConsecutiveMisses = maximumMisses,
            StabilityScore = top3 * .60 + top6 * .40,
            CombinedScore = top3 * .60 + top6 * .40,
            StabilityGrade = top6 >= .50 ? "A" : top6 >= .40 ? "B" : "C",
            Records = records
        };
    }
}
