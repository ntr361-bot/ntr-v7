namespace 六合分析软件;

public sealed record IndependentSignalSnapshot(
    string SourceName,
    string ModelVersion,
    string GeneratedForIssue,
    IReadOnlyList<string> Ranking,
    bool LeakageAuditPassed);

public interface IIndependentSignalProvider
{
    IndependentSignalSnapshot GetSnapshot(string issue, IReadOnlyList<DatabaseHelper.HistoryRecord> prefix);
}

public static class AutoLearningV2SignalAudit
{
    private static readonly HashSet<string> Zodiacs = new(new[] { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" }, StringComparer.Ordinal);

    public static IndependentSignalSnapshot Validate(IIndependentSignalProvider provider, string issue,
        string historyCutoffIssue, IReadOnlyList<DatabaseHelper.HistoryRecord>? prefix = null)
    {
        IndependentSignalSnapshot snapshot = provider.GetSnapshot(issue, prefix ?? Array.Empty<DatabaseHelper.HistoryRecord>());
        if (string.IsNullOrWhiteSpace(snapshot.SourceName) || string.IsNullOrWhiteSpace(snapshot.ModelVersion) ||
            !long.TryParse(snapshot.GeneratedForIssue, out long generated) || !long.TryParse(issue, out long target) ||
            generated > target || !long.TryParse(historyCutoffIssue, out long cutoff) || generated > target ||
            snapshot.Ranking.Count != 12 || snapshot.Ranking.Distinct(StringComparer.Ordinal).Count() != 12 ||
            snapshot.Ranking.Any(zodiac => !Zodiacs.Contains(zodiac)) || !snapshot.LeakageAuditPassed)
            throw new InvalidDataException($"独立信号 {snapshot.SourceName} 未通过 AutoLearningV2 泄漏审计。");
        return snapshot;
    }
}
