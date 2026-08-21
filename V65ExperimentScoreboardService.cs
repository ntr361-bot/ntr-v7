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

/// <summary>成绩榜内单个模型的已开奖预测明细；仅供只读展示。</summary>
public sealed record V65ExperimentScoreboardDetailRow(
    string Issue,
    string ModelName,
    string Top3Zodiac,
    string Top6Zodiac,
    string ActualZodiac,
    int ActualRank,
    bool Top3Hit,
    bool Top6Hit,
    string PredictTime,
    string Source,
    bool IsVerified,
    bool IsLatestPrediction);

/// <summary>
/// 只读取六个每日运行模型的已开奖预测记录，分别汇总 V6.5 四模型与 V7 两模型的表现。
/// </summary>
public static class V65ExperimentScoreboardService
{
    private sealed record Definition(string Group, string Name, Func<DatabaseHelper.PredictionRecord, bool> Matches);

    private static readonly Definition[] Definitions =
    {
        new("V6.5四模型实验", "V6.5-50期", row => IsV65(row) && row.AnalysisPeriods == 50),
        new("V6.5四模型实验", "V6.5-100期", row => IsV65(row) && row.AnalysisPeriods == 100),
        new("V6.5四模型实验", "V6.5-全部历史", row => IsV65(row) &&
            (row.AnalysisPeriods == AISettings.AllHistoryModeValue || row.AnalysisPeriods > 100)),
        new("V6.5四模型实验", "V6.5-自动学习", row => row.ModelVersion == "V6.5 AutoLearning"),
        new("V7 每日模型", "整合 V7", row => row.ModelVersion == "V7"),
        new("V7 每日模型", "V7 自动学习", row => row.ModelVersion == "V7 AutoLearning")
    };

    public static IReadOnlyList<V65ExperimentScoreboardRow> Build(
        IReadOnlyList<DatabaseHelper.PredictionRecord> records)
    {
        var drafts = Definitions.Select(definition => BuildDraft(definition, records)).ToList();
        var output = new List<V65ExperimentScoreboardRow>(drafts.Count);
        foreach (var group in drafts.GroupBy(draft => draft.Definition.Group))
        {
            double bestRecent50Top6 = group.Max(draft => draft.Recent50Top6);
            double bestAverageRank = group.Where(draft => draft.Ranks.Length > 0).Select(draft => draft.AverageRank)
                .DefaultIfEmpty(double.PositiveInfinity).Min();
            foreach (Draft draft in group)
            {
                string status = draft.Ranks.Length < 30 ? "观察" :
                    draft.CurrentTop6Misses >= 8 ? "暂停" :
                    NearlyEqual(draft.Recent50Top6, bestRecent50Top6) && NearlyEqual(draft.AverageRank, bestAverageRank)
                        ? "领先" : "观察";
                output.Add(new V65ExperimentScoreboardRow(draft.Definition.Group, draft.Definition.Name,
                    draft.Ranks.Length, Rate(draft.Ranks, 3), Rate(draft.Ranks, 6), draft.AverageRank,
                    Rate(draft.Ranks.TakeLast(20), 3), Rate(draft.Ranks.TakeLast(20), 6),
                    Rate(draft.Ranks.TakeLast(50), 3), Rate(draft.Ranks.TakeLast(50), 6),
                    draft.MaximumTop6Misses, draft.CurrentTop6Misses, status));
            }
        }
        return output;
    }

    public static IReadOnlyList<V65ExperimentScoreboardRow> Load() => Build(DatabaseHelper.GetPredictionHistory(int.MaxValue));

    /// <summary>读取指定模型最近的已开奖记录，不会触碰预测历史或学习状态。</summary>
    public static IReadOnlyList<V65ExperimentScoreboardDetailRow> GetRecentVerifiedDetails(
        string modelName, IReadOnlyList<DatabaseHelper.PredictionRecord> records, int limit = 30)
    {
        Definition definition = Definitions.SingleOrDefault(item => item.Name == modelName)
            ?? throw new ArgumentException($"未知成绩榜模型：{modelName}", nameof(modelName));
        return records.Where(definition.Matches)
            .Select(row => (Row: row, Rank: ActualRank(row)))
            .Where(item => item.Rank is >= 1 and <= 12)
            .OrderByDescending(item => IssueNumber(item.Row.Issue)).ThenByDescending(item => item.Row.Id)
            .Take(Math.Max(0, limit))
            .Select(item => CreateDetail(definition.Name, item.Row, item.Rank, isLatestPrediction: false))
            .ToArray();
    }

    public static IReadOnlyList<V65ExperimentScoreboardDetailRow> LoadRecentVerifiedDetails(string modelName, int limit = 30) =>
        GetRecentVerifiedDetails(modelName, DatabaseHelper.GetPredictionHistory(int.MaxValue), limit);

    /// <summary>成绩单先显示模型的最新一期预测，再显示最多30条已开奖成绩；只读。</summary>
    public static IReadOnlyList<V65ExperimentScoreboardDetailRow> GetScorecardDetails(
        string modelName, IReadOnlyList<DatabaseHelper.PredictionRecord> records, int verifiedLimit = 30)
    {
        Definition definition = Definitions.SingleOrDefault(item => item.Name == modelName)
            ?? throw new ArgumentException($"未知成绩榜模型：{modelName}", nameof(modelName));
        DatabaseHelper.PredictionRecord[] matching = records.Where(definition.Matches).ToArray();
        DatabaseHelper.PredictionRecord? latest = matching
            .OrderByDescending(row => NewestIssueNumber(row.Issue)).ThenByDescending(row => row.Id).FirstOrDefault();
        var output = new List<V65ExperimentScoreboardDetailRow>();
        if (latest is not null)
        {
            int latestRank = ActualRank(latest);
            output.Add(CreateDetail(definition.Name, latest, latestRank, isLatestPrediction: true));
        }
        output.AddRange(matching
            .Select(row => (Row: row, Rank: ActualRank(row)))
            .Where(item => item.Rank is >= 1 and <= 12 && !ReferenceEquals(item.Row, latest))
            .OrderByDescending(item => NewestIssueNumber(item.Row.Issue)).ThenByDescending(item => item.Row.Id)
            .Take(Math.Max(0, verifiedLimit))
            .Select(item => CreateDetail(definition.Name, item.Row, item.Rank, isLatestPrediction: false)));
        return output;
    }

    public static IReadOnlyList<V65ExperimentScoreboardDetailRow> LoadScorecardDetails(string modelName, int verifiedLimit = 30) =>
        GetScorecardDetails(modelName, DatabaseHelper.GetPredictionHistory(int.MaxValue), verifiedLimit);

    /// <summary>供数据中心直接说明 V6.5 自动学习是否已完成历史预训练。</summary>
    public static string DescribeAutoLearningState(ModelMemoryState state)
    {
        if (state.LearnedSamples <= 0) return "待历史训练";
        string issue = string.IsNullOrWhiteSpace(state.LastTrainingIssue) ? "-" : state.LastTrainingIssue;
        return $"已学习·{state.LearnedSamples}样本·至{issue}";
    }

    public static string LoadAutoLearningState() => DescribeAutoLearningState(
        new ModelMemory(ExperimentModels.AutoLearning).LoadOrCreate());

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
        if (string.IsNullOrWhiteSpace(row.ActualZodiac)) return 0;
        if (!string.IsNullOrWhiteSpace(row.FinalRankingJson))
        {
            try
            {
                string[] ranking = JsonSerializer.Deserialize<string[]>(row.FinalRankingJson) ?? Array.Empty<string>();
                return Array.IndexOf(ranking, row.ActualZodiac) + 1;
            }
            catch { /* 排名快照损坏时退回命中档推算 */ }
        }
        // 没有完整排名快照的历史记录：按已开奖命中结果推算代表排名（前3→2，前6→5，未中→9）。
        if (row.HitResult == "命中") return 2;
        if (row.Top6HitResult == "命中") return 5;
        if (row.HitResult == "未命中" || row.Top6HitResult == "未命中") return 9;
        return 0;
    }

    private static bool IsV65(DatabaseHelper.PredictionRecord row) => row.ModelVersion == "V6.5";
    private static long IssueNumber(string issue) => long.TryParse(issue, out long value) ? value : long.MaxValue;
    private static long NewestIssueNumber(string issue) => long.TryParse(issue, out long value) ? value : long.MinValue;
    private static V65ExperimentScoreboardDetailRow CreateDetail(string modelName, DatabaseHelper.PredictionRecord row,
        int rank, bool isLatestPrediction) => new(
            row.Issue,
            modelName,
            row.PredictZodiac,
            row.Top6Zodiac,
            row.ActualZodiac,
            rank,
            rank is >= 1 and <= 3,
            rank is >= 1 and <= 6,
            row.PredictTime,
            row.PredictionSource,
            rank is >= 1 and <= 12,
            isLatestPrediction);
    private static double Rate(IEnumerable<int> ranks, int cutoff)
    {
        int[] values = ranks.ToArray();
        return values.Length == 0 ? 0 : values.Count(rank => rank <= cutoff) / (double)values.Length;
    }
    private static bool NearlyEqual(double left, double right) => Math.Abs(left - right) < 0.000000001;

    private sealed record Draft(Definition Definition, int[] Ranks, double AverageRank,
        int MaximumTop6Misses, int CurrentTop6Misses, double Recent50Top6);
}
