using System.Collections.Immutable;

namespace 六合分析软件.MacroReasoning;

/// <summary>
/// P14 pure sidecar rank aggregation. It consumes frozen views and approved weights;
/// it never calculates weights, modifies expert scores, persists state or calls production.
/// </summary>
public sealed class MacroGatingModel : IMacroGatingModel
{
    private const double Tolerance=1e-9;
    private static readonly ImmutableArray<string> ZodiacOrder=
        ImmutableArray.Create("鼠","牛","虎","兔","龙","蛇","马","羊","猴","鸡","狗","猪");
    private static readonly HashSet<string> ZodiacSet=ZodiacOrder.ToHashSet(StringComparer.Ordinal);

    public ImmutableArray<string> Rank(ImmutableArray<ExpertSnapshot> experts,
        ImmutableDictionary<string,double> weights,ImmutableArray<ExpertDecision> decisions,
        ImmutableArray<ExpertCounterView> counterViews)
    {
        if(experts.IsDefault||decisions.IsDefault||counterViews.IsDefault||weights is null)
            throw new InvalidDataException("P14输入不可为default/null");
        if(experts.Length==0)throw new InvalidDataException("P14空专家池不能生成排名");

        Dictionary<string,ExpertSnapshot> snapshotById=Unique(experts,x=>x.ExpertId,"专家快照");
        Dictionary<string,ExpertDecision> decisionById=Unique(decisions,x=>x.ExpertId,"专家决策");
        string[] included=snapshotById.Keys.OrderBy(x=>x,StringComparer.Ordinal).ToArray();
        if(!included.SequenceEqual(decisionById.Keys.OrderBy(x=>x,StringComparer.Ordinal),StringComparer.Ordinal)
            ||!included.SequenceEqual(weights.Keys.OrderBy(x=>x,StringComparer.Ordinal),StringComparer.Ordinal))
            throw new InvalidDataException("P14快照、决策与权重支持集必须完全一致");

        long issue=experts[0].TargetIssue;
        if(issue<=0||experts.Any(x=>x.TargetIssue!=issue||x.HistoryCutoffIssue>=issue))
            throw new InvalidDataException("P14专家期号或历史截止无效");

        foreach(ExpertSnapshot snapshot in experts)
        {
            ValidateRanking(snapshot.Ranking,$"专家 {snapshot.ExpertId}");
            if(string.IsNullOrWhiteSpace(snapshot.ExpertId)||string.IsNullOrWhiteSpace(snapshot.ExpertRevisionId)
                ||string.IsNullOrWhiteSpace(snapshot.PayloadHash))throw new InvalidDataException("P14专家身份、Revision或哈希缺失");
            ExpertDecision decision=decisionById[snapshot.ExpertId];
            ValidateDecision(snapshot,decision,weights[snapshot.ExpertId],issue);
        }

        if(weights.Values.Any(x=>!double.IsFinite(x)||x<0)||Math.Abs(weights.Values.Sum()-1)>Tolerance)
            throw new InvalidDataException("P14权重必须有限、非负且合计为1");

        string[] counterIds=decisions.Where(x=>x.Mode==ExpertActionMode.COUNTER).Select(x=>x.ExpertId)
            .OrderBy(x=>x,StringComparer.Ordinal).ToArray();
        Dictionary<string,ExpertCounterView> counterById=Unique(counterViews,x=>x.ExpertId,"CounterView");
        if(!counterIds.SequenceEqual(counterById.Keys.OrderBy(x=>x,StringComparer.Ordinal),StringComparer.Ordinal))
            throw new InvalidDataException("P14 COUNTER专家与显式CounterView必须一一对应");

        var scores=ZodiacOrder.ToDictionary(x=>x,_=>0d,StringComparer.Ordinal);
        foreach(string expertId in included)
        {
            ExpertSnapshot snapshot=snapshotById[expertId];
            ExpertDecision decision=decisionById[expertId];
            ImmutableArray<string> ranking=decision.Mode switch
            {
                ExpertActionMode.FOLLOW=>snapshot.Ranking,
                ExpertActionMode.IGNORE=>ImmutableArray<string>.Empty,
                ExpertActionMode.COUNTER=>ValidateCounter(snapshot,decision,counterById[expertId]),
                _=>throw new InvalidDataException("P14未知专家模式")
            };
            if(decision.Mode==ExpertActionMode.IGNORE)continue;
            double weight=weights[expertId];
            for(int index=0;index<ranking.Length;index++)scores[ranking[index]]+=weight*(12-index);
        }

        return scores.OrderByDescending(x=>x.Value)
            .ThenBy(x=>ZodiacOrder.IndexOf(x.Key)).Select(x=>x.Key).ToImmutableArray();
    }

    private static void ValidateDecision(ExpertSnapshot snapshot,ExpertDecision decision,double weight,long issue)
    {
        if(decision.ExpertId!=snapshot.ExpertId||decision.ExpertRevisionId!=snapshot.ExpertRevisionId)
            throw new InvalidDataException("P14决策Revision与冻结快照不一致");
        if(decision.ModeSelection.TargetIssue!=issue||decision.ModeSelection.HistoryCutoff!=snapshot.HistoryCutoffIssue
            ||snapshot.GeneratedAt>decision.ModeSelection.AsOf||snapshot.AvailableAt>decision.ModeSelection.AsOf
            ||decision.ModeSelection.SelectedAt<snapshot.AvailableAt||decision.ModeSelection.SelectedAt>decision.ModeSelection.AsOf
            ||string.IsNullOrWhiteSpace(decision.ModeSelection.ModePolicyVersion)
            ||string.IsNullOrWhiteSpace(decision.ModeSelection.EvidenceHash))
            throw new InvalidDataException("P14模式选择不是目标期前冻结的同一期决策");
        double[] values={decision.RawProposedWeight,decision.ProposedWeight,decision.AppliedWeight,decision.Confidence,weight};
        if(values.Any(x=>!double.IsFinite(x)||x<0)||decision.Confidence>1
            ||Math.Abs(decision.AppliedWeight-weight)>Tolerance)
            throw new InvalidDataException("P14只接受一致的非负AppliedWeight");
        if(decision.Mode==ExpertActionMode.IGNORE&&Math.Abs(weight)>Tolerance)
            throw new InvalidDataException("P14 IGNORE专家的AppliedWeight必须为0");
        if(decision.Mode!=ExpertActionMode.COUNTER
            &&(decision.ModeSelection.CounterTransformId is not null||decision.ModeSelection.TransformVersion is not null))
            throw new InvalidDataException("P14非COUNTER模式不得声明反向变换");
    }

    private static ImmutableArray<string> ValidateCounter(ExpertSnapshot snapshot,ExpertDecision decision,ExpertCounterView view)
    {
        if(view.ExpertId!=snapshot.ExpertId||view.ExpertRevisionId!=snapshot.ExpertRevisionId
            ||view.TargetIssue!=snapshot.TargetIssue||view.OriginalSnapshotHash!=snapshot.PayloadHash
            ||!view.OriginalRanking.SequenceEqual(snapshot.Ranking)
            ||view.CounterTransformId!=decision.ModeSelection.CounterTransformId
            ||view.TransformVersion!=decision.ModeSelection.TransformVersion
            ||view.CounterTransformId!=CounterTransformDefinition.CounterTransformId
            ||view.TransformVersion!=CounterTransformDefinition.Version
            ||string.IsNullOrWhiteSpace(view.ViewHash)||string.IsNullOrWhiteSpace(view.ValidationEvidenceId)
            ||view.CreatedAt<snapshot.AvailableAt||view.CreatedAt>decision.ModeSelection.SelectedAt)
            throw new InvalidDataException("P14 CounterView身份、来源、版本或可用时间无效");
        ValidateRanking(view.OriginalRanking,"CounterView原排名");
        ValidateRanking(view.CounterRanking,"CounterView反向排名");
        if(!view.CounterRanking.SequenceEqual(view.OriginalRanking.Reverse()))
            throw new InvalidDataException("P14 CounterView不符合full12-rank-reversal-v1");
        return view.CounterRanking;
    }

    private static void ValidateRanking(ImmutableArray<string> ranking,string source)
    {
        if(ranking.IsDefault||ranking.Length!=12||ranking.Any(string.IsNullOrWhiteSpace)
            ||ranking.Distinct(StringComparer.Ordinal).Count()!=12||!ZodiacSet.SetEquals(ranking))
            throw new InvalidDataException($"P14 {source}不是12生肖无重无漏完整排名");
    }

    private static Dictionary<string,T> Unique<T>(IEnumerable<T> values,Func<T,string> id,string source)
    {
        var result=new Dictionary<string,T>(StringComparer.Ordinal);
        foreach(T value in values)
        {
            string key=id(value);
            if(string.IsNullOrWhiteSpace(key)||!result.TryAdd(key,value))throw new InvalidDataException($"P14 {source}存在空ID或重复ID");
        }
        return result;
    }
}
