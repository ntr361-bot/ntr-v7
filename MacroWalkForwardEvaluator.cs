using System.Collections.Immutable;
using System.Text.Json;

namespace 六合分析软件.MacroReasoning;

/// <summary>Input rows are frozen before evaluation. Actual is never passed to an action policy.</summary>
public sealed record MacroEvaluationFrame(long Issue, long CutoffIssue, DateTimeOffset AsOf,
    ImmutableArray<ExpertSnapshot> Snapshots, ExpertPoolSnapshot Pool, ClosedResult Actual);
public sealed record MacroEvaluationSplit(long TrainingEnd, long ValidationEnd, long HoldoutEnd);
public sealed record MacroEvaluationRow(long Issue, string Split, int BaseRank, int NoActionRank, int ProposedRank,
    int MacroRank, DecisionType Decision, double Confidence, bool Rescue, bool Harm, string AuditHash);

/// <summary>P20 orchestration only. Policy is explicitly frozen; no production calls or parameter search.</summary>
public sealed class MacroWalkForwardEvaluator : IMacroWalkForwardEvaluator
{
    private readonly VersionedExpertRegistry registry;
    private readonly MacroReasoningAuditStore store;
    private readonly ImmutableArray<MacroEvaluationFrame> frames;
    private readonly ImmutableArray<ClosedResult> warmup;
    private readonly ImmutableDictionary<string,double> control;
    private readonly Func<PrefixContext,MacroObservationSnapshot,HypothesisSet,EvidenceSearch,EvidenceSearch,ActionProposal> policy;
    private readonly string policyVersion;
    public ImmutableArray<MacroEvaluationRow> Rows { get; private set; } = [];
    public ImmutableDictionary<string,ReasoningMetrics> SplitMetrics { get; private set; } = ImmutableDictionary<string,ReasoningMetrics>.Empty;
    public MacroWalkForwardEvaluator(VersionedExpertRegistry registry, MacroReasoningAuditStore store,
        ImmutableArray<MacroEvaluationFrame> frames, ImmutableArray<ClosedResult> warmup,
        ImmutableDictionary<string,double> control, string policyVersion,
        Func<PrefixContext,MacroObservationSnapshot,HypothesisSet,EvidenceSearch,EvidenceSearch,ActionProposal>? policy = null)
    {
        this.registry=registry; this.store=store; this.frames=frames; this.warmup=warmup; this.control=control; this.policyVersion=policyVersion;
        // No implicit "recent winner" weighting. The stock control is explicitly no-action.
        this.policy=policy??((p,o,h,s,c)=>new ActionProposal(control,control,0,policyVersion));
    }
    public static string ParametersHash(ImmutableDictionary<string,double> weights,string policyVersion,string split,HistoricalEvaluationMode mode)
        => MacroRecordCodec.Hash(new { weights,policyVersion,split,mode,Runner="p20-v1",Reflection=MacroReflectionEngine.Version });

    public ReasoningMetrics Evaluate(RunIdentity run, ImmutableArray<long> issues, string frozenSplitDefinition)
    {
        var split=JsonSerializer.Deserialize<MacroEvaluationSplit>(frozenSplitDefinition)??throw new InvalidDataException("Missing frozen split");
        if(frames.IsEmpty||issues.IsEmpty||!issues.SequenceEqual(issues.Order())||issues.Distinct().Count()!=issues.Length
            ||!issues.SequenceEqual(frames.Select(x=>x.Issue))||frames.Select(x=>x.Pool.EvaluationMode).Distinct().Count()!=1
            ||split.TrainingEnd>=split.ValidationEnd||split.ValidationEnd>=split.HoldoutEnd||issues[^1]>split.HoldoutEnd)
            throw new InvalidDataException("Invalid chronological issue set/split or mixed historical modes");
        if(run.ExperimentId!=store.ExperimentId||run.ParametersHash!=ParametersHash(control,policyVersion,frozenSplitDefinition,frames[0].Pool.EvaluationMode))
            throw new InvalidDataException("Frozen run parameters mismatch");
        if(store.Predictions().Length!=0||store.LatestMemory().Version!=0)throw new InvalidDataException("Walk-forward requires a fresh isolated experiment; no reuse of future memory");
        if(warmup.Any(x=>x.Issue>=issues[0]||x.AvailableAt>frames[0].AsOf)||warmup.GroupBy(x=>x.Issue).Any(x=>x.Count()!=1))throw new InvalidDataException("Invalid warm-up prefix");
        var sourceResults=warmup.Concat(frames.Select(x=>x.Actual)).ToImmutableArray();
        if(sourceResults.GroupBy(x=>x.Issue).Any(g=>g.Count()!=1))throw new InvalidDataException("Duplicate outcomes");
        foreach(var f in frames)
            if(f.Issue!=f.Actual.Issue||f.CutoffIssue>=f.Issue||f.Actual.OpenedAt<=f.AsOf||f.Actual.AvailableAt<f.Actual.OpenedAt
                ||f.Pool.AsOf!=f.AsOf||!f.Pool.IncludedExpertIds.Order().SequenceEqual(control.Keys.Order()))
                throw new InvalidDataException("Frame chronology or fixed comparison pool invalid");
        if(frames.Zip(frames.Skip(1)).Any(p=>p.First.AsOf>=p.Second.AsOf))throw new InvalidDataException("AsOf must advance");
        var memory=new MacroReasoningMemory(store); var reflectionEngine=new MacroReflectionEngine();
        var audits=new List<MacroReasoningAudit>(); var allSnapshots=ImmutableArray<ExpertSnapshot>.Empty;
        var reflected=new HashSet<string>(StringComparer.Ordinal); var outcomes=new Dictionary<long,MacroReflection>();
        var rows=ImmutableArray.CreateBuilder<MacroEvaluationRow>();
        void Reveal(DateTimeOffset time,long nextIssue)
        {
            var available=sourceResults.Where(x=>x.AvailableAt<=time&&x.Issue<nextIssue).OrderBy(x=>x.Issue).ToImmutableArray();
            foreach(var a in audits)
            {
                if(!available.Any(x=>x.Issue==a.Issue))continue;
                bool mature=available.Count(x=>x.Issue>=a.Issue)>=10;
                string key=a.AuditHash+(mature?"|mature":"|outcome"); if(!reflected.Add(key))continue;
                var result=reflectionEngine.Reflect(a,available,store.LatestMemory(),time);
                result=reflectionEngine.WithConsensus(result,a,allSnapshots);
                memory.Append(result,result.BeforeMemoryVersion); outcomes[a.Issue]=result;
            }
        }
        foreach(var frame in frames)
        {
            // Reveal/learn only events actually available before this prediction.
            Reveal(frame.AsOf,frame.Issue);
            var past=sourceResults.Where(x=>x.Issue<frame.Issue&&x.AvailableAt<=frame.AsOf).OrderBy(x=>x.Issue).ToImmutableArray();
            if(past.IsEmpty||past[^1].Issue!=frame.CutoffIssue)throw new InvalidDataException("Declared cutoff does not match available prefix");
            allSnapshots=allSnapshots.AddRange(frame.Snapshots);
            var prefix=new PrefixContext(run,frame.Issue,frame.CutoffIssue,frame.AsOf,past,allSnapshots,
                MacroRecordCodec.Hash(new { Past=past,Hashes=allSnapshots.Select(x=>x.PayloadHash).ToArray() }),memory.ReadAsOf(frame.Issue,frame.AsOf),frame.Pool);
            var observation=new MacroObservationEngine(registry).Observe(prefix);
            var hypotheses=new MacroHypothesisEngine().Build(observation,prefix.Memory);
            var support=new MacroEvidenceEngine().Search(observation,hypotheses,prefix);
            var counter=new MacroCounterEvidenceEngine().Search(observation,hypotheses,support,prefix);
            var proposal=policy(prefix,observation,hypotheses,support,counter);
            if(proposal.RuleVersion!=policyVersion)throw new InvalidDataException("Unregistered action policy");
            var critic=new MacroReasoningCritic().Review(prefix,hypotheses,support,counter,proposal);
            var confidence=new MacroConfidenceEngine().Estimate(hypotheses,support,counter,critic,prefix.Memory);
            var decision=new MacroUncertaintyController().Constrain(new MacroDecisionEngine().Decide(frame.Issue,hypotheses,support,counter,proposal,critic,confidence));
            var cf=new MacroCounterfactualEngine().Evaluate(frame.Snapshots,decision,control);
            var expertDecisions=MacroCounterfactualEngine.FollowDecisions(frame.Snapshots,decision.ExpertWeightsApplied)
                .Select(x=>x with {RawProposedWeight=proposal.Proposed[x.ExpertId],ProposedWeight=proposal.Proposed[x.ExpertId],Confidence=confidence.Value,
                    ReasonCodes=ImmutableArray.Create(decision.ReasonSummary)}).ToImmutableArray();
            var expertAudit=new ExpertPoolDecisionAudit(frame.Pool.IncludedExpertIds,frame.Pool.ExpertRevisionIds,
                expertDecisions.ToImmutableDictionary(x=>x.ExpertId,x=>x.Mode),proposal.Before,proposal.Proposed,decision.ExpertWeightsApplied,
                expertDecisions,observation.Dependencies.DependencyGroups,observation.Dependencies.MetaDependencyEdges,
                observation.Dependencies.EffectiveIndependentExpertCount,[],[],ImmutableDictionary<string,ImmutableArray<string>>.Empty,[]);
            var audit=MacroReasoningAuditStore.Seal(new MacroReasoningAudit(run,frame.Issue,frame.CutoffIssue,frame.AsOf,prefix.Memory.Version,
                observation,hypotheses,support,counter,proposal,decision,cf,"",frame.Pool,observation.Dependencies,expertAudit));
            store.AppendPrediction(audit); audits.Add(audit);
        }
        Reveal(frames.Max(x=>x.Actual.AvailableAt),long.MaxValue);
        foreach(var a in audits)
        {
            var outcome=outcomes[a.Issue]; string label=a.Issue<=split.TrainingEnd?"Training":a.Issue<=split.ValidationEnd?"Validation":"Holdout";
            rows.Add(new(a.Issue,label,MacroReflectionEngine.Rank(a.Counterfactual.BaseRanking,outcome.Actual.ActualZodiac),
                MacroReflectionEngine.Rank(a.Counterfactual.NoActionRanking,outcome.Actual.ActualZodiac),MacroReflectionEngine.Rank(a.Counterfactual.ProposedRanking,outcome.Actual.ActualZodiac),
                MacroReflectionEngine.Rank(a.Counterfactual.AppliedRanking,outcome.Actual.ActualZodiac),a.Decision.DecisionType,a.Decision.Confidence.Value,outcome.Rescue,outcome.Harm,a.AuditHash));
        }
        Rows=rows.ToImmutable();
        SplitMetrics=new[]{"Training","Validation","Holdout"}.ToImmutableDictionary(x=>x,x=>Metrics(Rows.Where(r=>r.Split==x).ToArray(),outcomes,x));
        return Metrics(Rows.ToArray(),outcomes,"All-descriptive-not-independent-holdout");
    }
    private static ReasoningMetrics Metrics(MacroEvaluationRow[] rows,Dictionary<long,MacroReflection> reflections,string split)
    {
        int n=rows.Length; double? Rate(int hits)=>n==0?null:hits/(double)n;
        var outcomes=rows.Select(r=>reflections[r.Issue]).ToArray();
        var mature=outcomes.SelectMany(x=>x.HypothesisAssessments).Where(x=>x.HypothesisCorrect is Assessment.Correct or Assessment.Incorrect).ToArray();
        var decisions=outcomes.Where(x=>x.DecisionCorrect is Assessment.Correct or Assessment.Incorrect).ToArray();
        var high=auditable(rows,outcomes,.60,true); var low=auditable(rows,outcomes,.60,false);
        var sorted=rows.Select(x=>x.MacroRank).Order().ToArray();
        var values=ImmutableDictionary<string,double?>.Empty.Add("Top1",Rate(rows.Count(x=>x.MacroRank==1)))
            .Add("Top3",Rate(rows.Count(x=>x.MacroRank<=3))).Add("Top6",Rate(rows.Count(x=>x.MacroRank<=6)))
            .Add("MRR",n==0?null:rows.Average(x=>1d/x.MacroRank)).Add("MeanRank",n==0?null:rows.Average(x=>(double)x.MacroRank))
            .Add("MedianRank",n==0?null:(sorted[(n-1)/2]+sorted[n/2])/2d)
            .Add("ControlTop3",Rate(rows.Count(x=>x.BaseRank<=3))).Add("ControlTop6",Rate(rows.Count(x=>x.BaseRank<=6)))
            .Add("Rescue",rows.Count(x=>x.Rescue)).Add("Harm",rows.Count(x=>x.Harm))
            .Add("HoldRate",Rate(rows.Count(x=>x.Decision==DecisionType.Hold))).Add("ApplyRate",Rate(rows.Count(x=>x.Decision==DecisionType.Apply)))
            .Add("RejectRate",Rate(rows.Count(x=>x.Decision==DecisionType.Reject)))
            .Add("HypothesisAccuracy",mature.Length==0?null:mature.Count(x=>x.HypothesisCorrect==Assessment.Correct)/(double)mature.Length)
            .Add("DecisionAccuracy",decisions.Length==0?null:decisions.Count(x=>x.DecisionCorrect==Assessment.Correct)/(double)decisions.Length)
            .Add("HighConfidenceAccuracy",Accuracy(high)).Add("LowConfidenceAccuracy",Accuracy(low))
            .Add("OverconfidenceRate",CalibrationRate(rows,outcomes,true)).Add("UnderconfidenceRate",CalibrationRate(rows,outcomes,false))
            .Add("CriticPreventedHarm",outcomes.Count(x=>x.CriticPreventedHarm)).Add("CriticMissedRescue",outcomes.Count(x=>x.CriticMissedRescue))
            .Add("MaxTop3Miss",MaxMiss(rows,3)).Add("MaxTop6Miss",MaxMiss(rows,6));
        return new(values,ImmutableDictionary<string,int>.Empty.Add("Predictions",n).Add("DecidableHypotheses",mature.Length)
            .Add("PendingHypotheses",outcomes.Sum(x=>x.HypothesisAssessments.Count(h=>h.HypothesisCorrect==Assessment.Pending)))
            .Add("DecidableDecisions",decisions.Length).Add("HighConfidenceDecisions",high.Length).Add("LowConfidenceDecisions",low.Length),split,"p20-v1");
    }
    private static (double Confidence, Assessment Result)[] auditable(MacroEvaluationRow[] rows, MacroReflection[] outcomes,double boundary,bool high)
        => rows.Zip(outcomes).Where(x=>x.Second.DecisionCorrect is Assessment.Correct or Assessment.Incorrect && (high?x.First.Confidence>=boundary:x.First.Confidence<boundary))
            .Select(x=>(x.First.Confidence,x.Second.DecisionCorrect)).ToArray();
    private static double? Accuracy((double Confidence, Assessment Result)[] rows)=>rows.Length==0?null:rows.Count(x=>x.Result==Assessment.Correct)/(double)rows.Length;
    private static double? CalibrationRate(MacroEvaluationRow[] rows,MacroReflection[] outcomes,bool over)
    {
        var values=rows.Zip(outcomes).Where(x=>x.Second.DecisionCorrect is Assessment.Correct or Assessment.Incorrect).ToArray();
        if(values.Length==0)return null;
        return values.Count(x=>over ? x.First.Confidence>=.60&&x.Second.DecisionCorrect==Assessment.Incorrect
            : x.First.Confidence<.60&&x.Second.DecisionCorrect==Assessment.Correct)/(double)values.Length;
    }
    private static int MaxMiss(MacroEvaluationRow[] rows,int top) { int max=0,current=0; foreach(var row in rows){current=row.MacroRank<=top?0:current+1;max=Math.Max(max,current);}return max; }
}
