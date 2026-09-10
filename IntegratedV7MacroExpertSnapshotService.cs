using System.Collections.Immutable;
using 六合分析软件.MacroReasoning;

namespace 六合分析软件;

/// <summary>P6专用旁路入口；正式预测和PredictionHistory不调用此服务。</summary>
public static class IntegratedV7MacroExpertSnapshotService
{
    public const string ExpertId = "Integrated-V7";
    public const string ExpertRevisionId = "Integrated-V7@macro-full12-f055-o045-v1";
    public const string AlgorithmVersion = "integrated-v7-macro-full12-f055-o045-v1";
    public static readonly ImmutableArray<string> InputDependencyIds =
        ImmutableArray.Create("history.db", "FeatureEngine", "IntegratedV7MacroExpertAdapter");

    public static ExpertSnapshot FreezeLive(
        ImmutableExpertSnapshotStore store,
        IReadOnlyList<DatabaseHelper.HistoryRecord> history,
        long targetIssue,
        long historyCutoffIssue,
        DateTimeOffset generatedAt,
        DateTimeOffset freezeAt,
        string expertRevisionId,
        string codeVersion)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(history);
        if (targetIssue <= 0 || historyCutoffIssue >= targetIssue)
            throw new InvalidDataException("HistoryCutoff必须早于TargetIssue");
        if (history.Count == 0) throw new InvalidDataException("历史前缀不能为空");

        long[] issues = history.Select(row => long.TryParse(row.Period, out long issue)
            ? issue
            : throw new InvalidDataException("历史前缀包含无效期号")).ToArray();
        if (issues.Any(issue => issue > historyCutoffIssue || issue >= targetIssue) || issues.Max() != historyCutoffIssue)
            throw new InvalidDataException("历史前缀超出HistoryCutoff或未截止到声明期号");

        IntegratedV7MacroRanking result = IntegratedV7MacroExpertAdapter.Build(history);
        ExpertSnapshot proposed = new(
            ExpertId,
            targetIssue,
            historyCutoffIssue,
            generatedAt,
            result.Ranking,
            "",
            AlgorithmVersion,
            codeVersion,
            "",
            generatedAt,
            SnapshotOrigin.LiveFrozen,
            ImmutableArray<string>.Empty,
            InputDependencyIds,
            expertRevisionId,
            null);
        return store.FreezeAndAppend(proposed, freezeAt);
    }

    /// <summary>Research-only reconstruction; keeps the actual rebuild time rather than falsifying a live timestamp.</summary>
    public static ExpertSnapshot FreezeCausalReconstruction(
        ImmutableExpertSnapshotStore store, IReadOnlyList<DatabaseHelper.HistoryRecord> history,
        long targetIssue, long historyCutoffIssue, DateTimeOffset simulatedAsOf,
        DateTimeOffset reconstructedAt, DateTimeOffset freezeAt, string codeVersion)
    {
        if (simulatedAsOf >= reconstructedAt) throw new InvalidDataException("历史模拟时点必须早于实际重建时间");
        // Reuse prefix validation without producing a permanent live snapshot.
        if (history.Count == 0 || history.Select(x=>long.Parse(x.Period)).Any(x=>x>historyCutoffIssue||x>=targetIssue)
            || long.Parse(history[^1].Period)!=historyCutoffIssue) throw new InvalidDataException("历史前缀超出截止期");
        var ranking=IntegratedV7MacroExpertAdapter.Build(history);
        string prefixHash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            string.Join("\n",history.Select(x=>$"{x.Period}|{x.SpecialZodiac}|{x.SpecialNumber}")))));
        var proposed=new ExpertSnapshot(ExpertId,targetIssue,historyCutoffIssue,reconstructedAt,ranking.Ranking,"",AlgorithmVersion,
            codeVersion,"",reconstructedAt,SnapshotOrigin.CausalReconstruction,ImmutableArray<string>.Empty,InputDependencyIds,
            ExpertRevisionId,new ReconstructionProvenance(true,simulatedAsOf,reconstructedAt,codeVersion,historyCutoffIssue,true,prefixHash));
        return store.FreezeAndAppend(proposed,freezeAt);
    }
}
