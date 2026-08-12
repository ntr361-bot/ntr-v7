using System.Text.Json;

namespace 六合分析软件;

public sealed record V65ExperimentScoreboardRow(
    string Group,
    string ModelName,
    int Samples,
    double Top3HitRate,
    double Top6HitRate,
    double AverageRank,
    double Recent20Top3HitRate,
    double Recent20Top6HitRate,
    double Recent50Top3HitRate,
    double Recent50Top6HitRate,
    int MaximumTop6Misses,
    int CurrentTop6Misses,
    string Status);

/// <summary>
/// 只读取已开奖预测记录，分别汇总 V6.5 四模型实验与独立智能预测模型的表现。
/// </summary>
public static class V65ExperimentScoreboardService
{
    private sealed record Definition(string Group, string Name, Func<DatabaseHelper.PredictionRecord, bool> Matches);

    private static readonly Definition[] Definitions =
    {
        new("V6.5四模型实验", "V6.5-50期", row => IsV65(row) && row.AnalysisPeriods == 50),
        new("V6.5四模型实验", "V6.5-100期", row => IsV65(row) && row.AnalysisPeriods == 100),
        new("V6.5四模型实验", "V6.5-全部历史", row => IsV65(row) && row.AnalysisPeriods == AISettings.AllHistoryModeValue),
        new("V6.5四模型实验", "V6.5-自动学习", row => row.ModelVersion == "V6.5 AutoLearning"),
        new("智能预测模型", "智能预测-短期", row => row.ModelVersion == "V7 ShortTerm"),
        new("智能预测模型", "智能预测-中期", row => row.ModelVersion == "V7 MediumTerm"),
        new("智能预测模型", "智能预测-长期", row => row.ModelVersion == "V7 LongTerm"),
        new("智能预测模型", "智能预测-ML", row => row.ModelVersion == "V7 ML LightGBM"),
        new("智能预测模型", "智能预测-自动学习", row => row.ModelVersion == "V7 AutoLearning")
    };

    public static IReadOnlyList<V65ExperimentScoreboardRow> Build(
        IReadOnlyList<DatabaseHelper.PredictionRecord> records)
    {
        var drafts = Definitions.Select(definition => BuildDraft(definition, records)).ToList();
        var output = new List<V65ExperimentScoreboardRow>(drafts.Count);
        foreach (var group in drafts.GroupBy(draft => draft.Definition.Group))
        {
            double bestRecent50Top6 = group.Max(draft => draft.Recent50Top6);
            double bestAverageRank = group.Where(draft => draft.Samples > 0).Select(draft => draft.AverageRank)
                .DefaultIfEmpty(double.PositiveInfinity).Min();
            foreach (Draft draft in group)
            {
                string status = draft.Samples < 30 ? "观察" :
                    draft.CurrentTop6Misses >= 8 ? "暂停" :
                    NearlyEqual(draft.Recent50Top6, bestRecent50Top6) && NearlyEqual(draft.AverageRank, bestAverageRank)
                        ? "领先" : "观察";
                output.Add(new V65ExperimentScoreboardRow(draft.Definition.Group, draft.Definition.Name,
                    draft.Samples, Rate(draft.Ranks, 3), Rate(draft.Ranks, 6), draft.AverageRank,
                    Rate(draft.Ranks.TakeLast(20), 3), Rate(draft.Ranks.TakeLast(20), 6),
                    Rate(draft.Ranks.TakeLast(50), 3), Rate(draft.Ranks.TakeLast(50), 6),
                    draft.MaximumTop6Misses, draft.CurrentTop6Misses, status));
            }
        }
        return output;
    }

    public static IReadOnlyList<V65ExperimentScoreboardRow> Load() => Build(DatabaseHelper.GetPredictionHistory(int.MaxValue));

    private static Draft BuildDraft(Definition definition, IReadOnlyList<DatabaseHelper.PredictionRecord> records)
    {
        int[] ranks = records.Where(definition.Matches)
            .Select(row => (Row: row, Rank: ActualRank(row)))
            .Where(item => item.Rank is >= 1 and <= 12)
            .OrderBy(item => IssueNumber(item.Row.Issue)).ThenBy(item => item.Row.Id)
            .Select(item => item.Rank).ToArray();
        int maximum = 0;
        int current = 0;
        foreach (int rank in ranks)
        {
            current = rank <= 6 ? 0 : current + 1;
            maximum = Math.Max(maximum, current);
        }
        return new Draft(definition, ranks, ranks.Length == 0 ? 0 : ranks.Average(), maximum, current,
            Rate(ranks.TakeLast(50), 6));
    }

    private static int ActualRank(DatabaseHelper.PredictionRecord row)
    {
        if (row.ActualRank is >= 1 and <= 12) return row.ActualRank;
        if (string.IsNullOrWhiteSpace(row.ActualZodiac) || string.IsNullOrWhiteSpace(row.FinalRankingJson)) return 0;
        try
        {
            string[] ranking = JsonSerializer.Deserialize<string[]>(row.FinalRankingJson) ?? Array.Empty<string>();
            return Array.IndexOf(ranking, row.ActualZodiac) + 1;
        }
        catch { return 0; }
    }

    private static bool IsV65(DatabaseHelper.PredictionRecord row) => row.ModelVersion == "V6.5";
    private static long IssueNumber(string issue) => long.TryParse(issue, out long value) ? value : long.MaxValue;
    private static double Rate(IEnumerable<int> ranks, int cutoff)
    {
        int[] values = ranks.ToArray();
        return values.Length == 0 ? 0 : values.Count(rank => rank <= cutoff) / (double)values.Length;
    }
    private static bool NearlyEqual(double left, double right) => Math.Abs(left - right) < 0.000000001;

    private sealed record Draft(Definition Definition, int[] Ranks, double AverageRank,
        int MaximumTop6Misses, int CurrentTop6Misses, double Recent50Top6);
}
