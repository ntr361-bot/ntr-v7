using System.Collections.Immutable;
using 六合分析软件.MacroReasoning;

namespace 六合分析软件;

/// <summary>Builds research-only causal frames. It never writes PredictionHistory or uses live model memory.</summary>
public static class MacroHistoricalFrameBuilder
{
    public static (VersionedExpertRegistry Registry, ImmutableArray<MacroEvaluationFrame> Frames, ImmutableArray<ClosedResult> Warmup,
        ImmutableDictionary<string,double> Weights, RunIdentity Run, string Split) Build(IReadOnlyList<DatabaseHelper.HistoryRecord> source,int samples,string directory)
    {
        var history=source.Where(x=>long.TryParse(x.Period,out _)&&!string.IsNullOrWhiteSpace(x.SpecialZodiac))
            .OrderBy(x=>long.Parse(x.Period)).ToArray();
        if(samples<300||history.Length<samples+100)throw new InvalidDataException("真实P21至少需要目标样本数加100期历史前缀");
        Directory.CreateDirectory(directory); var now=DateTimeOffset.UtcNow;
        var registry=new VersionedExpertRegistry(Path.Combine(directory,"expert-registry.db"));
        CurrentExpertCatalog.Seed(registry,now.AddDays(-1),"macro-p21-causal-v1");
        var catalog=registry.ReadAsOf(now); var enabled=catalog.Experts.Where(x=>x.ExpertRevisionId.Contains("@macro-full12",StringComparison.Ordinal))
            .Select(x=>x with {EligibleForMacro=true,Enabled=true}).ToImmutableArray();
        if(enabled.Length!=4)throw new InvalidDataException("缺少四个已审计基础专家Revision");
        var view=catalog with {Experts=enabled}; var store=new ImmutableExpertSnapshotStore(Path.Combine(directory,"snapshots.db"),registry);
        int start=history.Length-samples; var frames=ImmutableArray.CreateBuilder<MacroEvaluationFrame>();
        DateTimeOffset At(DatabaseHelper.HistoryRecord r)=>DateTimeOffset.Parse(r.OpenTime).ToUniversalTime();
        var allResults=history.Select(r=>new ClosedResult(long.Parse(r.Period),r.SpecialZodiac,At(r),At(r),"p21-history:"+r.Period)).ToArray();
        for(int i=start;i<history.Length;i++)
        {
            var prefix=history.Take(i).ToArray(); long target=long.Parse(history[i].Period),cutoff=long.Parse(prefix[^1].Period);
            var asOf=At(history[i]).AddMinutes(-1); var rebuilt=now;
            var snapshots=V65BaseMacroExpertAdapter.Definitions.Select(d=>V65BaseMacroExpertSnapshotService.FreezeCausalReconstruction(
                store,prefix,d.ExpertId,target,cutoff,asOf,rebuilt,rebuilt,"macro-p21-causal-v1")).Append(
                IntegratedV7MacroExpertSnapshotService.FreezeCausalReconstruction(store,prefix,target,cutoff,asOf,rebuilt,rebuilt,"macro-p21-causal-v1")).ToImmutableArray();
            var pool=new ExpertPoolResolver().Resolve(view,target,asOf,snapshots,HistoricalEvaluationMode.CausalReconstruction);
            if(pool.IncludedExpertIds.Length!=4)throw new InvalidDataException($"第{target}期没有完整四专家Pool");
            store.FreezePool(pool,rebuilt); frames.Add(new(target,cutoff,asOf,snapshots,pool,allResults[i]));
        }
        var weights=enabled.ToImmutableDictionary(x=>x.ExpertId,_=>.25); int a=start+(int)(samples*.6),b=start+(int)(samples*.8);
        string split=System.Text.Json.JsonSerializer.Serialize(new MacroEvaluationSplit(long.Parse(history[a-1].Period),long.Parse(history[b-1].Period),long.Parse(history[^1].Period)));
        var run=new RunIdentity("macro-p21-causal-"+samples,"p21-v1","p20-v1",MacroWalkForwardEvaluator.ParametersHash(weights,"p21-hold-control-v1",split,HistoricalEvaluationMode.CausalReconstruction),"macro-p21-causal-v1",6501);
        return (registry,frames.ToImmutable(),allResults.Take(start).ToImmutableArray(),weights,run,split);
    }
}
