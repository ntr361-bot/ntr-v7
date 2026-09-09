using System.Collections.Immutable;

namespace 六合分析软件.MacroReasoning;

/// <summary>P11 independent critic. It audits a proposal but never mutates it.</summary>
public sealed class MacroReasoningCritic : IMacroReasoningCritic
{
    private const double CautionCap=.01;
    private const double PassCap=.05;
    private const double Tolerance=1e-9;
    private static readonly ImmutableArray<string> Checks=ImmutableArray.Create(
        "SAMPLE_SIZE","RECENT_WINDOW_BIAS","MODEL_STREAK_CHASING","COUNTER_EVIDENCE_BALANCE",
        "LONG_TERM_CONFLICT","ACTION_MAGNITUDE","ENVIRONMENT_SIMILARITY","RANDOM_FLUCTUATION",
        "RANK_IMPACT_PREVIEW","FUTURE_LEAKAGE");

    public CriticResult Review(PrefixContext prefix,HypothesisSet hypotheses,EvidenceSearch support,
        EvidenceSearch counter,ActionProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(hypotheses);
        ArgumentNullException.ThrowIfNull(support);
        ArgumentNullException.ThrowIfNull(counter);
        ArgumentNullException.ThrowIfNull(proposal);
        var hard=new SortedSet<string>(StringComparer.Ordinal);
        var caution=new SortedSet<string>(StringComparer.Ordinal);

        if(prefix.TargetIssue<=0||prefix.CutoffIssue>=prefix.TargetIssue||prefix.Memory.LastTrainingIssue>=prefix.TargetIssue
            ||prefix.Memory.AvailableAt>prefix.AsOf
            ||prefix.PastResults.Any(x=>x.Issue>=prefix.TargetIssue||x.AvailableAt>prefix.AsOf))hard.Add("FUTURE_OR_TARGET_DATA");

        var ids=hypotheses.Candidates.Select(x=>x.HypothesisId).ToHashSet(StringComparer.Ordinal);
        if(ids.Count==0||ids.Count!=hypotheses.Candidates.Length
            ||!hypotheses.Candidates.Any(x=>x.Type==HypothesisType.RandomFluctuation)
            ||!hypotheses.Candidates.Any(x=>x.Type==HypothesisType.NoMeaningfulChange))hard.Add("INVALID_HYPOTHESIS_SET");
        if(!support.Completed)hard.Add("SUPPORT_SEARCH_INCOMPLETE");
        if(!counter.Completed)hard.Add("COUNTER_SEARCH_INCOMPLETE");
        if(support.Items.Any(x=>!ids.Contains(x.HypothesisId))||counter.Items.Any(x=>!ids.Contains(x.HypothesisId)))hard.Add("UNKNOWN_HYPOTHESIS_EVIDENCE");

        if(!ValidWeights(prefix,proposal))hard.Add("INVALID_WEIGHT_PROPOSAL");
        if(proposal.ActionMagnitude>PassCap+Tolerance)hard.Add("ACTION_MAGNITUDE_EXCEEDS_HARD_CAP");

        if(support.Items.Length==0)caution.Add("NO_SUPPORTING_EVIDENCE");
        if(support.Items.Any(x=>x.EffectiveSamples<20))caution.Add("SMALL_EFFECTIVE_SAMPLE");
        if(support.Items.Length>0&&support.Items.All(x=>x.Window<=20))caution.Add("SHORT_WINDOW_ONLY");
        if(support.Items.Any(x=>x.SignalName.StartsWith("Top6Rate:",StringComparison.Ordinal)&&x.Window<=20))
            caution.Add("RECENT_MODEL_PERFORMANCE_ONLY");
        double supportStrength=Strength(support.Items);
        double counterStrength=Strength(counter.Items);
        if(counterStrength+Tolerance>=supportStrength&&counter.Items.Length>0)caution.Add("COUNTER_EVIDENCE_NOT_WEAKER");
        if(counter.Items.Any(x=>x.Window>=50))caution.Add("LONG_WINDOW_COUNTER_EVIDENCE");
        if(proposal.ActionMagnitude>CautionCap+Tolerance)caution.Add("ACTION_REQUIRES_CAUTION_CAP");
        if(!hypotheses.Candidates.Any(x=>x.Type==HypothesisType.RandomFluctuation))hard.Add("RANDOM_FLUCTUATION_NOT_CONSIDERED");
        // The frozen P11 contract does not yet carry these P14 preview inputs. Record the
        // unavailable checks instead of pretending that they passed.
        caution.Add("ENVIRONMENT_SIMILARITY_UNAVAILABLE");
        caution.Add("RANK_IMPACT_PREVIEW_UNAVAILABLE");

        if(hard.Count>0)return new CriticResult(CriticVerdict.Reject,hard.ToImmutableArray(),Checks,0);
        if(caution.Count>0)return new CriticResult(CriticVerdict.Caution,caution.ToImmutableArray(),Checks,CautionCap);
        return new CriticResult(CriticVerdict.Pass,ImmutableArray.Create("ALL_CHECKS_PASSED"),Checks,PassCap);
    }

    private static bool ValidWeights(PrefixContext prefix,ActionProposal proposal)
    {
        string[] expected=prefix.ExpertPool.IncludedExpertIds.OrderBy(x=>x,StringComparer.Ordinal).ToArray();
        if(!proposal.Before.Keys.OrderBy(x=>x,StringComparer.Ordinal).SequenceEqual(expected,StringComparer.Ordinal)
            ||!proposal.Proposed.Keys.OrderBy(x=>x,StringComparer.Ordinal).SequenceEqual(expected,StringComparer.Ordinal))return false;
        if(proposal.Before.Values.Any(x=>!double.IsFinite(x)||x<0)||proposal.Proposed.Values.Any(x=>!double.IsFinite(x)||x<0))return false;
        if(Math.Abs(proposal.Before.Values.Sum()-1)>Tolerance||Math.Abs(proposal.Proposed.Values.Sum()-1)>Tolerance)return false;
        double magnitude=.5*expected.Sum(id=>Math.Abs(proposal.Proposed[id]-proposal.Before[id]));
        return double.IsFinite(proposal.ActionMagnitude)&&proposal.ActionMagnitude>=0&&Math.Abs(magnitude-proposal.ActionMagnitude)<=Tolerance;
    }

    internal static double Strength(IEnumerable<Evidence> evidence)=>evidence.Sum(x=>
        Math.Clamp(x.Reliability,0,1)*(Math.Abs(x.EffectSize)/(1+Math.Abs(x.EffectSize))));
}
