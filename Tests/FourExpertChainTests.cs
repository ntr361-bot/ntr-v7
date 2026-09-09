using System.Collections.Immutable;
using System.Text.Json;
using 六合分析软件;
using 六合分析软件.MacroReasoning;

public static class FourExpertChainTests
{
    public static int Run()
    {
        var at = new DateTimeOffset(2026,9,9,0,0,0,TimeSpan.Zero);
        var asOf = at.AddHours(2);
        string dir = Path.Combine(Path.GetTempPath(),"four-expert-chain-"+Guid.NewGuid().ToString("N"));
        var registry = new VersionedExpertRegistry(Path.Combine(dir,"registry.db"));
        CurrentExpertCatalog.Seed(registry,at,"54d94b6-joint-test");
        var registered = registry.ReadAsOf(asOf);
        var selected = registered.Experts.Where(x=>x.ExpertRevisionId.Contains("@macro-full12",StringComparison.Ordinal)).ToImmutableArray();
        Check(selected.Length==4 && selected.All(x=>!x.Enabled&&!x.EligibleForMacro),"正式目录四个新修订保持关闭");
        var history=Enumerable.Range(0,140).Select(i=>new DatabaseHelper.HistoryRecord {
            Period=(2026001+i).ToString(),SpecialZodiac=Zodiac[(i*5+i/7)%12],SpecialNumber="01",
            OpenTime=new DateTime(2026,1,1).AddDays(i).ToString("yyyy-MM-dd HH:mm:ss")}).ToArray();
        string Formal()
        {
            var v7=V7Engine.Predict(history);
            return JsonSerializer.Serialize(new { V65=V65ExperimentPipeline.RunBaseModels(history,"2026141"),
                V7=new {v7.Engine,v7.Window,v7.Features,v7.Top3,v7.Top6,v7.Probabilities} });
        }
        string before=Formal();
        var store=new ImmutableExpertSnapshotStore(Path.Combine(dir,"snapshots.db"),registry);
        var snapshots=V65BaseMacroExpertAdapter.Definitions.Select(d=>V65BaseMacroExpertSnapshotService.FreezeLive(
            store,history,d.ExpertId,2026141,2026140,at.AddHours(1),asOf,"54d94b6-joint-test")).Append(
            IntegratedV7MacroExpertSnapshotService.FreezeLive(store,history,2026141,2026140,at.AddHours(1),asOf,
                IntegratedV7MacroExpertSnapshotService.ExpertRevisionId,"54d94b6-joint-test")).ToImmutableArray();
        // Only this in-memory test view enables the selected revisions; registry storage is unchanged.
        var testRegistry=registered with {Experts=selected.Select(x=>x with {Enabled=true,EligibleForMacro=true}).ToImmutableArray()};
        var resolver=new ExpertPoolResolver();
        var pool=resolver.Resolve(testRegistry,2026141,asOf,snapshots,HistoricalEvaluationMode.HistoricalAvailability);
        Check(pool.IncludedExpertIds.Length==4&&pool.MissingExpertIds.IsEmpty,"P6绑定四份同目标期新Revision快照");
        store.FreezePool(pool,asOf);
        Check(store.ReadPool(2026141,asOf,pool.EvaluationMode,pool.RegistryVersion)!=null,"P6冻结Pool持久化");
        var memory=new ReasoningMemorySnapshot(0,0,at,ImmutableDictionary<string,ReliabilityStats>.Empty,
            ImmutableDictionary<string,ReliabilityStats>.Empty,ImmutableDictionary<string,ReliabilityStats>.Empty,
            ImmutableDictionary<string,ReliabilityStats>.Empty,ImmutableDictionary<string,ReliabilityStats>.Empty,
            ImmutableArray<ConfidenceCalibrationStats>.Empty,new string('a',64));
        var results=history.Select((h,i)=>new ClosedResult(long.Parse(h.Period),h.SpecialZodiac,
            at.AddDays(-141+i),at.AddDays(-141+i),new string('b',64))).ToImmutableArray();
        var prefix=new PrefixContext(new RunIdentity("four-expert-technical-test","v1","p6-p14",new string('c',64),"54d94b6",6501),
            2026141,2026140,asOf,results,snapshots,new string('d',64),memory,pool);
        var observer=new MacroObservationEngine(registry);
        var observation=observer.Observe(prefix);
        Check(observation.ExpertObservations.Length==4,"P7观察四个真实适配器输出");
        var hypotheses=new MacroHypothesisEngine().Build(observation,memory);
        var support=new MacroEvidenceEngine().Search(observation,hypotheses,prefix);
        var counter=new MacroCounterEvidenceEngine().Search(observation,hypotheses,support,prefix);
        var weights=pool.IncludedExpertIds.ToImmutableDictionary(x=>x,_=>.25);
        var proposed=weights.SetItem("Integrated-V7",.26).SetItem("V65-100",.24);
        var action=new ActionProposal(weights,proposed,.01,"technical-test-proposal-not-learned");
        var critic=new MacroReasoningCritic().Review(prefix,hypotheses,support,counter,action);
        var confidence=new MacroConfidenceEngine().Estimate(hypotheses,support,counter,critic,memory);
        var decision=new MacroDecisionEngine().Decide(prefix.TargetIssue,hypotheses,support,counter,action,critic,confidence);
        Check(decision.DecisionType!=DecisionType.Apply&&Same(weights,decision.ExpertWeightsApplied),"P8-P13缺少历史专家成绩时保守HOLD/REJECT并保持权重");
        var decisions=snapshots.Select(s=>new ExpertDecision(s.ExpertId,s.ExpertRevisionId,ExpertActionMode.FOLLOW,
            null,null,null,null,null,null,null,proposed[s.ExpertId],proposed[s.ExpertId],decision.ExpertWeightsApplied[s.ExpertId],confidence.Value,
            ImmutableArray.Create(decision.ReasonSummary),ImmutableArray<string>.Empty,
            new ExpertModeSelection(prefix.TargetIssue,prefix.CutoffIssue,asOf,asOf,"joint-follow-v1",observation.SnapshotHash,null,null))).ToImmutableArray();
        var gating=new MacroGatingModel();
        var ranking=gating.Rank(snapshots,decision.ExpertWeightsApplied,decisions,ImmutableArray<ExpertCounterView>.Empty);
        Check(ranking.Length==12&&ranking.Distinct().Count()==12,"P14生成完整12生肖旁路排名");
        Check(ranking.SequenceEqual(gating.Rank(snapshots,weights,decisions,ImmutableArray<ExpertCounterView>.Empty)),"HOLD保留NoAction排名");
        var missing=resolver.Resolve(testRegistry,prefix.TargetIssue,asOf,snapshots.RemoveAt(0),pool.EvaluationMode);
        Check(missing.MissingExpertIds.Contains(snapshots[0].ExpertId)&&missing.IncludedExpertIds.Length==3,"缺少快照标为Missing且不补位");
        Reject(()=>observer.Observe(prefix with {ExpertSnapshots=snapshots.RemoveAt(0)}),"已冻结四专家Pool缺快照被拒绝");
        var bad=prefix with {PastResults=results.Add(new ClosedResult(prefix.TargetIssue,"鼠",at,at,"bad"))};
        var veto=new MacroReasoningCritic().Review(bad,hypotheses,support,counter,action);
        var rejected=new MacroDecisionEngine().Decide(prefix.TargetIssue,hypotheses,support,counter,action,veto,confidence);
        Check(veto.Result==CriticVerdict.Reject&&rejected.DecisionType==DecisionType.Reject&&Same(weights,rejected.ExpertWeightsApplied),"目标期开奖结果污染触发Critic否决且不调权");
        Check(before==Formal(),"联合链执行前后正式V65和V7完整输出不变");
        Check(registry.ReadAsOf(asOf).Experts.All(x=>!x.Enabled&&!x.EligibleForMacro),"临时试验未启用持久化专家");
        bool grouped=observation.Dependencies.DependencyGroups.Values.Any(g=>new[]{"V65-50","V65-100","V65-All"}.All(g.Contains));
        var edges=observation.Dependencies.MetaDependencyEdges;
        Check(edges.Length==6 && edges.Count(e=>e.DependencyType==MetaDependencyType.SharedHistory)==3 &&
            edges.Count(e=>e.DependencyType==MetaDependencyType.SharedFeatures)==3,
            "三个新V65修订两两登记共享历史和特征定义");
        Check(selected.Where(x=>x.ExpertId.StartsWith("V65-",StringComparison.Ordinal)).All(x=>x.DependencyDepth==0),
            "共享关系不增加消费DAG深度");
        Check(registry.ReadDependencies().Where(e=>e.FromExpertId=="V65-Auto").All(e=>!e.ToExpertRevisionId.Contains("@macro-full12",StringComparison.Ordinal)),
            "旧Auto消费边未改绑Macro修订");
        Console.WriteLine("V65_NEW_REVISION_SHARED_GROUP="+grouped);
        Console.WriteLine("TECHNICAL_CHAIN_PASS; FULL_ACCEPTANCE="+(grouped?"PASS":"CONDITIONAL: missing new revision dependency edges"));
        return grouped?0:2;
    }
    private static readonly string[] Zodiac={"鼠","牛","虎","兔","龙","蛇","马","羊","猴","鸡","狗","猪"};
    private static bool Same(ImmutableDictionary<string,double> a,ImmutableDictionary<string,double> b)=>a.OrderBy(x=>x.Key).SequenceEqual(b.OrderBy(x=>x.Key));
    private static void Check(bool ok,string message){if(!ok)throw new InvalidOperationException(message);Console.WriteLine("PASS "+message);}
    private static void Reject(Action action,string message){try{action();}catch(InvalidDataException){Console.WriteLine("PASS "+message);return;}throw new InvalidOperationException(message);}
}
