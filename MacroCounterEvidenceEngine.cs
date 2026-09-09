using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace 六合分析软件.MacroReasoning;

/// <summary>P10 sidecar counter-evidence search. Absence of support is never silently treated as counter-evidence.</summary>
public sealed class MacroCounterEvidenceEngine : IMacroCounterEvidenceEngine
{
    private const string Version="p10-counter-v1";

    public EvidenceSearch Search(MacroObservationSnapshot observation,HypothesisSet hypotheses,EvidenceSearch support,PrefixContext prefix)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(hypotheses);
        ArgumentNullException.ThrowIfNull(support);
        ArgumentNullException.ThrowIfNull(prefix);
        Validate(observation,hypotheses,support,prefix);

        var checkedSignals=ImmutableArray.CreateBuilder<string>();
        var missing=ImmutableArray.CreateBuilder<string>();
        var items=ImmutableArray.CreateBuilder<Evidence>();

        CheckStructural(observation,Get(hypotheses,HypothesisType.ColdReturnIncreasing),"ColdReturnRate","__environment__",.05,false,
            checkedSignals,missing,items);
        CheckStructural(observation,Get(hypotheses,HypothesisType.HotPersistenceIncreasing),"ZodiacConcentration","__environment__",.02,false,
            checkedSignals,missing,items,true);
        CheckStructural(observation,Get(hypotheses,HypothesisType.TrendSignalDegrading),"TrendReliability","__missing_signal__",.05,true,
            checkedSignals,missing,items,true);
        Hypothesis repeat=Get(hypotheses,HypothesisType.RepeatRegimeIncreasing);
        foreach(string signal in new[]{"ImmediateRepeatRate","Gap1RepeatRate","Gap2RepeatRate"})
            CheckStructural(observation,repeat,signal,"__environment__",.05,false,checkedSignals,missing,items);

        Hypothesis shift=Get(hypotheses,HypothesisType.ModelPerformanceShift);
        foreach(ExpertObservation expert in observation.ExpertObservations.OrderBy(x=>x.ExpertId,StringComparer.Ordinal))
        {
            string signal=$"Top6Rate:{expert.ExpertId}";
            checkedSignals.Add($"{shift.HypothesisId}|{signal}|20/50/100");
            ObservationFact? recent=Find(expert.WindowMetrics,"Top6Rate",expert.ExpertId,20);
            ObservationFact? middle=Find(expert.WindowMetrics,"Top6Rate",expert.ExpertId,50);
            ObservationFact? baseline=Find(expert.WindowMetrics,"Top6Rate",expert.ExpertId,100);
            if(!Usable(recent)||!Usable(middle)||!Usable(baseline))
            {
                missing.Add($"{shift.HypothesisId}|{signal}@20/50/100");
                continue;
            }
            double recentDelta=recent!.Value!.Value-baseline!.Value!.Value;
            double middleDelta=middle!.Value!.Value-baseline.Value.Value;
            bool noRecentShift=Math.Abs(recentDelta)<.10;
            bool longNotConfirmed=Math.Abs(recentDelta)>=.10&&(Math.Abs(middleDelta)<.05||Math.Sign(middleDelta)!=Math.Sign(recentDelta));
            if(noRecentShift||longNotConfirmed)
                Add(items,shift,signal,50,middle.Value.Value,baseline.Value.Value,
                    noRecentShift?.10-Math.Abs(recentDelta):Math.Abs(recentDelta-middleDelta),
                    noRecentShift?"NoMaterialRecentShift":"LongWindowNotConfirmed",middle,expert.DependencyGroup,observation.SnapshotHash);
        }

        int material=support.Items.Count(e=>HypothesisTypeOf(hypotheses,e.HypothesisId) is not HypothesisType.RandomFluctuation and not HypothesisType.NoMeaningfulChange);
        IssueRange aggregateRange=CombinedRange(observation);
        foreach(Hypothesis candidate in new[]{Get(hypotheses,HypothesisType.RandomFluctuation),Get(hypotheses,HypothesisType.NoMeaningfulChange)})
        {
            checkedSignals.Add($"{candidate.HypothesisId}|MaterialStructuralShiftCount");
            if(material>0)
            {
                var source=new ObservationFact("MaterialStructuralShiftCount","__derived__",0,material,0,
                    Math.Max(1,aggregateRange.Samples),aggregateRange,Version);
                Add(items,candidate,"MaterialStructuralShiftCount",0,material,0,material,
                    "ObservedStructuralChange",source,"aggregate-structural",observation.SnapshotHash);
            }
        }

        return new EvidenceSearch(true,checkedSignals.Distinct(StringComparer.Ordinal).OrderBy(x=>x,StringComparer.Ordinal).ToImmutableArray(),
            items.OrderBy(x=>x.HypothesisId,StringComparer.Ordinal).ThenBy(x=>x.SignalName,StringComparer.Ordinal).ToImmutableArray(),
            missing.Distinct(StringComparer.Ordinal).OrderBy(x=>x,StringComparer.Ordinal).ToImmutableArray());
    }

    private static void CheckStructural(MacroObservationSnapshot observation,Hypothesis hypothesis,string signal,string expert,
        double threshold,bool decreasing,ImmutableArray<string>.Builder checkedSignals,ImmutableArray<string>.Builder missing,
        ImmutableArray<Evidence>.Builder items,bool frozenBaseline=false)
    {
        checkedSignals.Add($"{hypothesis.HypothesisId}|{signal}|20/50/100");
        ObservationFact? recent=Find(observation.Facts,signal,expert,20);
        ObservationFact? middle=Find(observation.Facts,signal,expert,50);
        ObservationFact? baseline=Find(observation.Facts,signal,expert,100);
        double? recentBase=frozenBaseline?recent?.BaselineValue:baseline?.Value;
        double? middleBase=frozenBaseline?middle?.BaselineValue:baseline?.Value;
        if(!Usable(recent)||!Usable(middle)||!recentBase.HasValue||!middleBase.HasValue)
        {
            missing.Add($"{hypothesis.HypothesisId}|{signal}@20/50/100");
            return;
        }
        double recentDelta=recent!.Value!.Value-recentBase.Value;
        double middleDelta=middle!.Value!.Value-middleBase.Value;
        bool recentSupports=decreasing?recentDelta<=-threshold:recentDelta>=threshold;
        bool middleSupports=decreasing?middleDelta<=-threshold:middleDelta>=threshold;
        if(!recentSupports||!middleSupports)
            Add(items,hypothesis,signal,50,middle.Value.Value,middleBase.Value,
                !recentSupports?Math.Abs(recentDelta):Math.Abs(recentDelta-middleDelta),
                !recentSupports?"OppositeOrStableShortWindow":"LongWindowNotConfirmed",middle,
                expert=="__environment__"?"environment-"+signal:"signal-"+signal,observation.SnapshotHash);
    }

    private static void Add(ImmutableArray<Evidence>.Builder items,Hypothesis hypothesis,string signal,int window,
        double value,double baseline,double effect,string direction,ObservationFact source,string group,string observationHash)
    {
        int samples=Math.Max(0,source.EffectiveSamples);
        double reliability=Math.Min(1,samples/100d);
        string canonical=string.Join("\n",Version,observationHash,hypothesis.HypothesisId,signal,window.ToString(CultureInfo.InvariantCulture),
            value.ToString("R",CultureInfo.InvariantCulture),baseline.ToString("R",CultureInfo.InvariantCulture),direction,
            source.SourceIssueRange.First.ToString(CultureInfo.InvariantCulture),source.SourceIssueRange.Last.ToString(CultureInfo.InvariantCulture),samples.ToString(CultureInfo.InvariantCulture));
        string id=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        items.Add(new Evidence(id,hypothesis.HypothesisId,signal,window,value,baseline,Math.Max(0,effect),reliability,direction,
            source.SourceIssueRange,samples,Version,group));
    }

    private static void Validate(MacroObservationSnapshot observation,HypothesisSet hypotheses,EvidenceSearch support,PrefixContext prefix)
    {
        if(!support.Completed)throw new InvalidDataException("P10不能在支持证据搜索未完成时运行");
        if(observation.Issue!=prefix.TargetIssue||observation.CutoffIssue!=prefix.CutoffIssue)
            throw new InvalidDataException("P10输入期号不一致");
        if(prefix.PastResults.Any(x=>x.Issue>=prefix.TargetIssue||x.AvailableAt>prefix.AsOf))
            throw new InvalidDataException("P10检测到目标期、未来期或尚不可见结果");
        var ids=hypotheses.Candidates.Select(x=>x.HypothesisId).ToHashSet(StringComparer.Ordinal);
        if(ids.Count==0||support.Items.Any(x=>!ids.Contains(x.HypothesisId)))throw new InvalidDataException("P10支持证据引用未知假设");
    }

    private static HypothesisType HypothesisTypeOf(HypothesisSet set,string id)=>set.Candidates.Single(x=>x.HypothesisId==id).Type;
    private static Hypothesis Get(HypothesisSet set,HypothesisType type)=>set.Candidates.SingleOrDefault(x=>x.Type==type)
        ??throw new InvalidDataException($"P10缺少假设 {type}");
    private static ObservationFact? Find(IEnumerable<ObservationFact> facts,string signal,string expert,int window)=>
        facts.SingleOrDefault(x=>x.SignalName==signal&&x.ExpertId==expert&&x.Window==window);
    private static bool Usable(ObservationFact? fact)=>fact is {Value:not null,EffectiveSamples:>0}&&double.IsFinite(fact.Value.Value);
    private static IssueRange CombinedRange(MacroObservationSnapshot observation)
    {
        ObservationFact[] facts=observation.Facts.Where(x=>x.EffectiveSamples>0).ToArray();
        return facts.Length==0?new IssueRange(0,0,0):new IssueRange(facts.Min(x=>x.SourceIssueRange.First),facts.Max(x=>x.SourceIssueRange.Last),facts.Max(x=>x.EffectiveSamples));
    }
}
