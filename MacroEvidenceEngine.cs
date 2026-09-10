using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace 六合分析软件.MacroReasoning;

/// <summary>P9 sidecar supporting-evidence search. It never selects or scores an action.</summary>
public sealed class MacroEvidenceEngine : IMacroEvidenceEngine
{
    private const string Version="p9-evidence-v1";
    private const double HotThreshold=.02;
    private const double TrendThreshold=.05;
    private const double RepeatThreshold=.05;
    private const double ModelShiftThreshold=.10;

    public EvidenceSearch Search(MacroObservationSnapshot observation,HypothesisSet hypotheses,PrefixContext prefix)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(hypotheses);
        ArgumentNullException.ThrowIfNull(prefix);
        Validate(observation,hypotheses,prefix);

        var checkedSignals=ImmutableArray.CreateBuilder<string>();
        var missing=ImmutableArray.CreateBuilder<string>();
        var items=ImmutableArray.CreateBuilder<Evidence>();
        int evaluated=0,material=0;

        Hypothesis cold=Get(hypotheses,HypothesisType.ColdReturnIncreasing);
        TryDelta(observation,cold,"ColdReturnRate","__environment__",20,100,.05,less:false,"cold-return",
            checkedSignals,missing,items,ref evaluated,ref material);

        Hypothesis hot=Get(hypotheses,HypothesisType.HotPersistenceIncreasing);
        TryDelta(observation,hot,"ZodiacConcentration","__environment__",20,100,HotThreshold,less:false,"environment-concentration",
            checkedSignals,missing,items,ref evaluated,ref material,useFrozenBaseline:true);

        Hypothesis trend=Get(hypotheses,HypothesisType.TrendSignalDegrading);
        TryDelta(observation,trend,"TrendReliability","__missing_signal__",20,100,TrendThreshold,less:true,"trend-signal",
            checkedSignals,missing,items,ref evaluated,ref material,useFrozenBaseline:true);

        Hypothesis repeat=Get(hypotheses,HypothesisType.RepeatRegimeIncreasing);
        foreach(string signal in new[]{"ImmediateRepeatRate","Gap1RepeatRate","Gap2RepeatRate"})
            TryDelta(observation,repeat,signal,"__environment__",20,100,RepeatThreshold,less:false,"repeat-signals",
                checkedSignals,missing,items,ref evaluated,ref material);

        Hypothesis shift=Get(hypotheses,HypothesisType.ModelPerformanceShift);
        foreach(ExpertObservation expert in observation.ExpertObservations.OrderBy(x=>x.ExpertId,StringComparer.Ordinal))
        {
            string checkedId=$"{shift.HypothesisId}|Top6Rate:{expert.ExpertId}|20vs100";
            checkedSignals.Add(checkedId);
            ObservationFact? recent=Find(expert.WindowMetrics,"Top6Rate",expert.ExpertId,20);
            ObservationFact? baseline=Find(expert.WindowMetrics,"Top6Rate",expert.ExpertId,100);
            if(!Usable(recent)||!Usable(baseline))
            {
                missing.Add($"{shift.HypothesisId}|Top6Rate:{expert.ExpertId}@20/100");
                continue;
            }
            evaluated++;
            double delta=recent!.Value!.Value-baseline!.Value!.Value;
            if(Math.Abs(delta)+1e-12<ModelShiftThreshold)continue;
            material++;
            Add(items,shift,$"Top6Rate:{expert.ExpertId}",20,recent.Value.Value,baseline.Value.Value,Math.Abs(delta),
                delta>0?"ExpertImproving":"ExpertDegrading",recent,expert.DependencyGroup,observation.SnapshotHash);
        }

        if(evaluated>=2&&material==0)
        {
            IssueRange range=CombinedRange(observation);
            int samples=Math.Max(1,range.Samples);
            foreach(Hypothesis candidate in new[]{Get(hypotheses,HypothesisType.RandomFluctuation),Get(hypotheses,HypothesisType.NoMeaningfulChange)})
            {
                checkedSignals.Add($"{candidate.HypothesisId}|MaterialShiftCount");
                Add(items,candidate,"MaterialShiftCount",0,0,1,1,candidate.Type==HypothesisType.RandomFluctuation?"RandomPlausible":"StablePlausible",
                    new ObservationFact("MaterialShiftCount","__derived__",0,0,1,samples,range,Version),"aggregate-structural",observation.SnapshotHash);
            }
        }

        return new EvidenceSearch(true,checkedSignals.Distinct(StringComparer.Ordinal).OrderBy(x=>x,StringComparer.Ordinal).ToImmutableArray(),
            items.OrderBy(x=>x.HypothesisId,StringComparer.Ordinal).ThenBy(x=>x.SignalName,StringComparer.Ordinal).ToImmutableArray(),
            missing.Distinct(StringComparer.Ordinal).OrderBy(x=>x,StringComparer.Ordinal).ToImmutableArray());
    }

    private static void TryDelta(MacroObservationSnapshot observation,Hypothesis hypothesis,string signal,string expert,
        int recentWindow,int baselineWindow,double threshold,bool less,string group,
        ImmutableArray<string>.Builder checkedSignals,ImmutableArray<string>.Builder missing,
        ImmutableArray<Evidence>.Builder items,ref int evaluated,ref int material,bool useFrozenBaseline=false)
    {
        checkedSignals.Add($"{hypothesis.HypothesisId}|{signal}|{recentWindow}vs{baselineWindow}");
        ObservationFact? recent=Find(observation.Facts,signal,expert,recentWindow);
        ObservationFact? baseline=Find(observation.Facts,signal,expert,baselineWindow);
        double? baselineValue=useFrozenBaseline?recent?.BaselineValue:baseline?.Value;
        if(!Usable(recent)||!baselineValue.HasValue||!double.IsFinite(baselineValue.Value))
        {
            missing.Add($"{hypothesis.HypothesisId}|{signal}@{recentWindow}");
            return;
        }
        evaluated++;
        double delta=recent!.Value!.Value-baselineValue.Value;
        bool supports=less?delta<=-threshold:delta>=threshold;
        if(!supports)return;
        material++;
        Add(items,hypothesis,signal,recentWindow,recent.Value.Value,baselineValue.Value,Math.Abs(delta),
            less?"Decreasing":"Increasing",recent,group,observation.SnapshotHash);
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
        items.Add(new Evidence(id,hypothesis.HypothesisId,signal,window,value,baseline,effect,reliability,direction,
            source.SourceIssueRange,samples,Version,group));
    }

    private static void Validate(MacroObservationSnapshot observation,HypothesisSet hypotheses,PrefixContext prefix)
    {
        if(observation.Issue!=prefix.TargetIssue||observation.CutoffIssue!=prefix.CutoffIssue
            ||observation.ExpertPool.TargetIssue!=prefix.TargetIssue)throw new InvalidDataException("P9输入期号不一致");
        if(prefix.PastResults.Any(x=>x.Issue>=prefix.TargetIssue||x.AvailableAt>prefix.AsOf))
            throw new InvalidDataException("P9检测到目标期、未来期或尚不可见结果");
        if(hypotheses.Candidates.Length==0||hypotheses.Candidates.Select(x=>x.HypothesisId).Distinct(StringComparer.Ordinal).Count()!=hypotheses.Candidates.Length
            ||hypotheses.Candidates.Any(x=>x.CreatedIssue!=prefix.TargetIssue))throw new InvalidDataException("P9假设集合无效");
        _=Get(hypotheses,HypothesisType.RandomFluctuation);
        _=Get(hypotheses,HypothesisType.NoMeaningfulChange);
    }

    private static Hypothesis Get(HypothesisSet set,HypothesisType type)=>set.Candidates.SingleOrDefault(x=>x.Type==type)
        ??throw new InvalidDataException($"P9缺少假设 {type}");
    private static ObservationFact? Find(IEnumerable<ObservationFact> facts,string signal,string expert,int window)=>
        facts.SingleOrDefault(x=>x.SignalName==signal&&x.ExpertId==expert&&x.Window==window);
    private static bool Usable(ObservationFact? fact)=>fact is {Value:not null,EffectiveSamples:>0}&&double.IsFinite(fact.Value.Value);
    private static IssueRange CombinedRange(MacroObservationSnapshot observation)
    {
        // MaterialShiftCount summarizes revealed historical signals, not current-target ranking overlap.
        ObservationFact[] facts=observation.Facts.Where(x=>x.EffectiveSamples>0 && x.SourceIssueRange.Last<=observation.CutoffIssue).ToArray();
        if(facts.Length==0)return new IssueRange(0,0,0);
        return new IssueRange(facts.Min(x=>x.SourceIssueRange.First),facts.Max(x=>x.SourceIssueRange.Last),facts.Max(x=>x.EffectiveSamples));
    }
}
