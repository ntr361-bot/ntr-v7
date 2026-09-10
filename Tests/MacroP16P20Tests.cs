using System.Collections.Immutable;
using System.Text.Json;
using 六合分析软件;
using 六合分析软件.MacroReasoning;

public static class MacroP16P20Tests
{
    public static int Run()
    {
        string dir=Path.Combine(Path.GetTempPath(),"macro-p20-synthetic-"+Guid.NewGuid().ToString("N"));
        var start=new DateTimeOffset(2025,9,20,0,0,0,TimeSpan.Zero);
        var registry=new VersionedExpertRegistry(Path.Combine(dir,"registry.db"));
        CurrentExpertCatalog.Seed(registry,start.AddDays(-10),"p20-synthetic-tests");
        var registered=registry.ReadAsOf(start);
        var selected=registered.Experts.Where(x=>x.ExpertRevisionId.Contains("@macro-full12",StringComparison.Ordinal)).ToImmutableArray();
        var view=registered with { Experts=selected.Select(x=>x with {Enabled=true,EligibleForMacro=true}).ToImmutableArray() };
        string[] z={"鼠","牛","虎","兔","龙","蛇","马","羊","猴","鸡","狗","猪"};
        long Issue(int i) { var date=start.AddDays(i); return date.Year*1000+date.DayOfYear; }
        var history=Enumerable.Range(0,112).Select(i=>new DatabaseHelper.HistoryRecord {
            Period=Issue(i).ToString(), SpecialZodiac=z[(i*5+i/7)%12],SpecialNumber="01",
            OpenTime=start.AddDays(i).AddHours(21).ToString("yyyy-MM-dd HH:mm:ss") }).ToArray();
        var results=Enumerable.Range(0,112).Select(i=>new ClosedResult(Issue(i),history[i].SpecialZodiac,
            start.AddDays(i).AddHours(21),start.AddDays(i).AddHours(21),"synthetic-"+i)).ToImmutableArray();
        var snapshotStore=new ImmutableExpertSnapshotStore(Path.Combine(dir,"snapshots.db"),registry);
        var frames=ImmutableArray.CreateBuilder<MacroEvaluationFrame>();
        for(int i=100;i<112;i++)
        {
            var asOf=start.AddDays(i).AddHours(20); var prefix=history.Take(i).ToArray();
            var snapshots=V65BaseMacroExpertAdapter.Definitions.Select(d=>V65BaseMacroExpertSnapshotService.FreezeLive(
                snapshotStore,prefix,d.ExpertId,Issue(i),Issue(i-1),asOf,asOf,"p20-synthetic-tests")).Append(
                IntegratedV7MacroExpertSnapshotService.FreezeLive(snapshotStore,prefix,Issue(i),Issue(i-1),asOf,asOf,
                    IntegratedV7MacroExpertSnapshotService.ExpertRevisionId,"p20-synthetic-tests")).ToImmutableArray();
            var pool=new ExpertPoolResolver().Resolve(view,Issue(i),asOf,snapshots,HistoricalEvaluationMode.HistoricalAvailability);
            Check(pool.IncludedExpertIds.Length==4,"synthetic pool full12 x4");
            snapshotStore.FreezePool(pool,asOf);
            frames.Add(new(Issue(i),Issue(i-1),asOf,snapshots,pool,results[i]));
        }
        var input=frames.ToImmutable();
        var weights=selected.ToImmutableDictionary(x=>x.ExpertId,_=>.25);
        string split=JsonSerializer.Serialize(new MacroEvaluationSplit(Issue(103),Issue(107),Issue(111)));
        var run=new RunIdentity("p20-synthetic","v1","p20-v1",MacroWalkForwardEvaluator.ParametersHash(weights,"hold-control-v1",split,
            HistoricalEvaluationMode.HistoricalAvailability),"p20-synthetic-tests",6501);
        var auditStore=new MacroReasoningAuditStore(Path.Combine(dir,"reasoning.db"),run);
        string Formal()=>JsonSerializer.Serialize(new {V65=V65ExperimentPipeline.RunBaseModels(history.Take(100).ToArray(),Issue(100).ToString()),
            V7=V7Engine.Predict(history.Take(100).ToArray()).Top6});
        string before=Formal();
        var evaluator=new MacroWalkForwardEvaluator(registry,auditStore,input,results.Take(100).ToImmutableArray(),weights,"hold-control-v1");
        var metrics=evaluator.Evaluate(run,input.Select(x=>x.Issue).ToImmutableArray(),split);
        var audits=auditStore.Predictions();
        Check(audits.Length==12&&evaluator.Rows.Length==12,"P7-P20 predicts and appends 12 synthetic issues");
        Check(audits.All(MacroReasoningAuditStore.Verify),"audit hash survives database roundtrip");
        Check(audits.All(a=>a.Counterfactual.NoActionRanking.SequenceEqual(a.Counterfactual.AppliedRanking)),"HOLD retains ranking");
        Check(audits[0].UsedMemoryVersion==0&&audits[1].UsedMemoryVersion>0,"reveal-reflect-memory precedes next prediction");
        Check(auditStore.LatestMemory().Version>12,"ten-draw maturity appends separate reflection, including year boundary");
        Check(evaluator.SplitMetrics.Values.All(x=>x.Denominators["Predictions"]==4),"training/validation/holdout isolated");
        Check(metrics.Denominators["PendingHypotheses"]>0,"unmatured hypotheses remain Pending");
        Check(new MacroReasoningMemory(auditStore).ReadAsOf(Issue(100),input[0].AsOf).Version==0,"historical AsOf cannot read later memory");
        var first=audits[0]; auditStore.AppendPrediction(first);
        Check(auditStore.Predictions().Length==12,"exact prediction retry idempotent");
        Reject(()=>auditStore.AppendPrediction(first with {AuditHash="bad"}),"tampered hash rejected");
        Reject(()=>auditStore.AppendPrediction(MacroReasoningAuditStore.Seal(first with {UsedMemoryVersion=999})),"conflicting append rejected");
        Reject(()=>new MacroReflectionEngine().Reflect(first,[],MacroReasoningMemory.Empty,first.CreatedAt),"reflection before outcome rejected");
        Reject(()=>evaluator.Evaluate(run,input.Select(x=>x.Issue).ToImmutableArray(),split),"future trained memory cannot seed replay");
        Check(before==Formal(),"official V65/V7 prediction unchanged");
        Check(registry.ReadAsOf(input[^1].AsOf).Experts.All(x=>!x.Enabled&&!x.EligibleForMacro),"physical expert registry stays disabled");
        var againStore=new MacroReasoningAuditStore(Path.Combine(dir,"reproduce.db"),run);
        var again=new MacroWalkForwardEvaluator(registry,againStore,input,results.Take(100).ToImmutableArray(),weights,"hold-control-v1");
        again.Evaluate(run,input.Select(x=>x.Issue).ToImmutableArray(),split);
        Check(audits.Select(x=>x.AuditHash).SequenceEqual(againStore.Predictions().Select(x=>x.AuditHash)),"same inputs reproduce audit hashes");
        Check(auditStore.LatestMemory().MemoryHash==againStore.LatestMemory().MemoryHash,"reasoning memory deterministic");
        Console.WriteLine("P16_P20_SYNTHETIC_PASS; no real history database or long backtest used");
        return 0;
    }
    static void Check(bool ok,string name){if(!ok)throw new InvalidOperationException(name);Console.WriteLine("PASS "+name);}
    static void Reject(Action action,string name){try{action();}catch(InvalidDataException){Console.WriteLine("PASS "+name);return;}throw new InvalidOperationException(name);}
}
