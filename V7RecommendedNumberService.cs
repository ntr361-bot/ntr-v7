namespace 六合分析软件;

public sealed record V7RecommendedNumberSelection(string Numbers, string Details, string MappingSnapshotJson);

// 保留返回结构及预测映射审计快照，停止全部号码候选、评分和推荐计算。
public static class V7RecommendedNumberService
{
    public static V7RecommendedNumberSelection Select(string targetIssue,
        IEnumerable<(string Zodiac, double Probability)> ranking,
        IReadOnlyList<DatabaseHelper.HistoryRecord> history)
    {
        DateTime targetDate = ResolveTargetDate(targetIssue, history);
        return new("", "", V65MappingService.CreateSnapshot(targetIssue, targetDate));
    }

    private static DateTime ResolveTargetDate(string targetIssue, IReadOnlyList<DatabaseHelper.HistoryRecord> history)
    {
        if (DateTime.TryParseExact(targetIssue, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out DateTime datedIssue))
            return datedIssue;
        DatabaseHelper.HistoryRecord? latest = history.FirstOrDefault();
        string value = latest?.OpenTime ?? latest?.Date ?? "";
        return DateTime.TryParse(value, out DateTime lastDraw) ? lastDraw.Date.AddDays(1) : DateTime.Today;
    }
}
