using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using 六合分析软件.MacroReasoning;

namespace 六合分析软件;

/// <summary>P6专用旁路入口；正式V6.5预测、学习状态和PredictionHistory不调用此服务。</summary>
public static class V65BaseMacroExpertSnapshotService
{
    public static readonly ImmutableArray<string> InputDependencyIds =
        ImmutableArray.Create("history-prefix:caller-supplied", "V65RuleScoringEngine", "V65BaseMacroExpertAdapter");

    public static ExpertSnapshot FreezeLive(
        ImmutableExpertSnapshotStore store,
        IReadOnlyList<DatabaseHelper.HistoryRecord> history,
        string expertId,
        long targetIssue,
        long historyCutoffIssue,
        DateTimeOffset generatedAt,
        DateTimeOffset freezeAt,
        string codeVersion)
    {
        ArgumentNullException.ThrowIfNull(store);
        ValidatePrefix(history, targetIssue, historyCutoffIssue);
        V65BaseMacroRanking result = V65BaseMacroExpertAdapter.Build(history, expertId);
        ExpertSnapshot proposed = CreateSnapshot(result, targetIssue, historyCutoffIssue, generatedAt,
            generatedAt, SnapshotOrigin.LiveFrozen, codeVersion, null);
        return store.FreezeAndAppend(proposed, freezeAt);
    }

    public static ExpertSnapshot FreezeCausalReconstruction(
        ImmutableExpertSnapshotStore store,
        IReadOnlyList<DatabaseHelper.HistoryRecord> history,
        string expertId,
        long targetIssue,
        long historyCutoffIssue,
        DateTimeOffset simulatedAsOf,
        DateTimeOffset reconstructedAt,
        DateTimeOffset freezeAt,
        string codeVersion)
    {
        ArgumentNullException.ThrowIfNull(store);
        ValidatePrefix(history, targetIssue, historyCutoffIssue);
        if (simulatedAsOf >= reconstructedAt)
            throw new InvalidDataException("历史模拟时点必须早于实际重建时间");

        V65BaseMacroRanking result = V65BaseMacroExpertAdapter.Build(history, expertId);
        var provenance = new ReconstructionProvenance(true, simulatedAsOf, reconstructedAt, codeVersion,
            historyCutoffIssue, true, ComputePrefixHash(history));
        ExpertSnapshot proposed = CreateSnapshot(result, targetIssue, historyCutoffIssue, reconstructedAt,
            reconstructedAt, SnapshotOrigin.CausalReconstruction, codeVersion, provenance);
        return store.FreezeAndAppend(proposed, freezeAt);
    }

    private static ExpertSnapshot CreateSnapshot(
        V65BaseMacroRanking result,
        long targetIssue,
        long historyCutoffIssue,
        DateTimeOffset generatedAt,
        DateTimeOffset availableAt,
        SnapshotOrigin origin,
        string codeVersion,
        ReconstructionProvenance? reconstruction)
    {
        if (string.IsNullOrWhiteSpace(codeVersion) ||
            codeVersion.Contains("unknown", StringComparison.OrdinalIgnoreCase) ||
            codeVersion.Contains("current", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("CodeVersion必须是明确、不可变的版本标识");
        return new ExpertSnapshot(result.ExpertId, targetIssue, historyCutoffIssue, generatedAt,
            result.Ranking, "", result.AlgorithmVersion, codeVersion, "", availableAt, origin,
            ImmutableArray<string>.Empty, InputDependencyIds, result.ExpertRevisionId, reconstruction);
    }

    private static void ValidatePrefix(
        IReadOnlyList<DatabaseHelper.HistoryRecord> history,
        long targetIssue,
        long historyCutoffIssue)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (targetIssue <= 0 || historyCutoffIssue <= 0 || historyCutoffIssue >= targetIssue)
            throw new InvalidDataException("HistoryCutoff必须早于TargetIssue");
        if (history.Count == 0) throw new InvalidDataException("历史前缀不能为空");

        long[] issues = history.Select(row => long.TryParse(row.Period, out long issue)
            ? issue
            : throw new InvalidDataException("历史前缀包含无效期号")).ToArray();
        if (issues.Any(issue => issue > historyCutoffIssue || issue >= targetIssue) || issues.Max() != historyCutoffIssue)
            throw new InvalidDataException("历史前缀超出HistoryCutoff或未截止到声明期号");
    }

    private static string ComputePrefixHash(IReadOnlyList<DatabaseHelper.HistoryRecord> history)
    {
        var canonical = new StringBuilder();
        foreach (DatabaseHelper.HistoryRecord row in history.OrderBy(x => long.Parse(x.Period)))
            canonical.Append(row.Period).Append('|').Append(row.SpecialZodiac).Append('|')
                .Append(row.SpecialNumber).Append('|').Append(row.OpenTime).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }
}
