using System.Collections.Immutable;

namespace 六合分析软件.MacroReasoning;

/// <summary>P13 sidecar decision freezer. It does not rank zodiac signs or write production state.</summary>
public sealed class MacroDecisionEngine : IMacroDecisionEngine
{
    private const string PolicyVersion="p13-decision-v1";
    private const double MinimumConfidence=.60;
    private const double Tolerance=1e-9;

    public MacroDecision Decide(long issue,HypothesisSet hypotheses,EvidenceSearch support,EvidenceSearch counter,
        ActionProposal proposal,CriticResult critic,ConfidenceResult confidence)
    {
        ArgumentNullException.ThrowIfNull(hypotheses);
        ArgumentNullException.ThrowIfNull(support);
        ArgumentNullException.ThrowIfNull(counter);
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(critic);
        ArgumentNullException.ThrowIfNull(confidence);
        Validate(issue,hypotheses,support,counter,proposal,critic,confidence);

        (ImmutableArray<string> selected,ImmutableArray<string> rejected,double actionable,double nullStrength)=
            Classify(hypotheses,support,counter);

        if(critic.Result==CriticVerdict.Reject)
            return Frozen(issue,selected,rejected,DecisionType.Reject,proposal,confidence,critic,proposal.Before,0,
                "CRITIC_REJECT|"+string.Join(",",critic.Reasons));
        if(hypotheses.Candidates.Length==0)
            return Hold(issue,selected,rejected,proposal,confidence,critic,"NO_HYPOTHESES");
        if(!hypotheses.Candidates.Any(x=>x.Type==HypothesisType.RandomFluctuation)
            ||!hypotheses.Candidates.Any(x=>x.Type==HypothesisType.NoMeaningfulChange))
            return Hold(issue,selected,rejected,proposal,confidence,critic,"NULL_ALTERNATIVES_MISSING");
        if(!support.Completed||!counter.Completed)
            return Hold(issue,selected,rejected,proposal,confidence,critic,"EVIDENCE_SEARCH_INCOMPLETE");
        if(actionable<=0||selected.Length==0)
            return Hold(issue,selected,rejected,proposal,confidence,critic,"NO_ACTIONABLE_HYPOTHESIS");
        if(nullStrength+Tolerance>=actionable)
            return Hold(issue,selected,rejected,proposal,confidence,critic,"NULL_EXPLANATION_NOT_WEAKER");
        if(!double.IsFinite(confidence.Value)||confidence.Value+Tolerance<MinimumConfidence)
            return Hold(issue,selected,rejected,proposal,confidence,critic,"CONFIDENCE_BELOW_0.60");
        if(proposal.ActionMagnitude<=Tolerance||critic.MaximumMagnitude<=Tolerance)
            return Hold(issue,selected,rejected,proposal,confidence,critic,"NO_EFFECTIVE_ACTION");

        double cap=Math.Min(critic.MaximumMagnitude,critic.Result==CriticVerdict.Caution?.01:.05);
        double factor=Math.Min(1,cap/proposal.ActionMagnitude);
        ImmutableDictionary<string,double> applied=proposal.Before.ToImmutableDictionary(x=>x.Key,
            x=>x.Value+factor*(proposal.Proposed[x.Key]-x.Value),StringComparer.Ordinal);
        double magnitude=.5*applied.Keys.Sum(id=>Math.Abs(applied[id]-proposal.Before[id]));
        string reason=$"{PolicyVersion}|APPLY|Critic={critic.Result}|Confidence={confidence.Value:F4}|Selected={string.Join(',',selected)}";
        return Frozen(issue,selected,rejected,DecisionType.Apply,proposal,confidence,critic,applied,magnitude,reason);
    }

    private static MacroDecision Hold(long issue,ImmutableArray<string> selected,ImmutableArray<string> rejected,
        ActionProposal proposal,ConfidenceResult confidence,CriticResult critic,string reason)=>
        Frozen(issue,selected,rejected,DecisionType.Hold,proposal,confidence,critic,proposal.Before,0,$"{PolicyVersion}|HOLD|{reason}");

    private static MacroDecision Frozen(long issue,ImmutableArray<string> selected,ImmutableArray<string> rejected,
        DecisionType type,ActionProposal proposal,ConfidenceResult confidence,CriticResult critic,
        ImmutableDictionary<string,double> applied,double magnitude,string reason)=>
        new(issue,selected,rejected,type,proposal.Before,proposal.Proposed,applied,magnitude,confidence,reason,critic);

    private static (ImmutableArray<string> Selected,ImmutableArray<string> Rejected,double Actionable,double NullStrength)
        Classify(HypothesisSet hypotheses,EvidenceSearch support,EvidenceSearch counter)
    {
        var selected=ImmutableArray.CreateBuilder<string>();
        var rejected=ImmutableArray.CreateBuilder<string>();
        double actionable=0,nullStrength=0;
        foreach(Hypothesis hypothesis in hypotheses.Candidates.OrderBy(x=>x.HypothesisId,StringComparer.Ordinal))
        {
            double positive=MacroReasoningCritic.Strength(support.Items.Where(x=>x.HypothesisId==hypothesis.HypothesisId));
            double negative=MacroReasoningCritic.Strength(counter.Items.Where(x=>x.HypothesisId==hypothesis.HypothesisId));
            double net=Math.Max(0,positive-negative);
            bool isNull=hypothesis.Type is HypothesisType.RandomFluctuation or HypothesisType.NoMeaningfulChange;
            if(positive>negative+Tolerance)selected.Add(hypothesis.HypothesisId);
            else if(negative>positive+Tolerance)rejected.Add(hypothesis.HypothesisId);
            if(isNull)nullStrength=Math.Max(nullStrength,net); else actionable=Math.Max(actionable,net);
        }
        return (selected.ToImmutable(),rejected.ToImmutable(),actionable,nullStrength);
    }

    private static void Validate(long issue,HypothesisSet hypotheses,EvidenceSearch support,EvidenceSearch counter,
        ActionProposal proposal,CriticResult critic,ConfidenceResult confidence)
    {
        if(issue<=0)throw new InvalidDataException("P13目标期号无效");
        if(hypotheses.Candidates.Any(x=>x.CreatedIssue!=issue)
            ||hypotheses.Candidates.Select(x=>x.HypothesisId).Distinct(StringComparer.Ordinal).Count()!=hypotheses.Candidates.Length)
            throw new InvalidDataException("P13假设集合与目标期不一致");
        var ids=hypotheses.Candidates.Select(x=>x.HypothesisId).ToHashSet(StringComparer.Ordinal);
        if(support.Items.Any(x=>!ids.Contains(x.HypothesisId))||counter.Items.Any(x=>!ids.Contains(x.HypothesisId)))
            throw new InvalidDataException("P13证据引用未知假设");
        string[] beforeKeys=proposal.Before.Keys.OrderBy(x=>x,StringComparer.Ordinal).ToArray();
        string[] proposedKeys=proposal.Proposed.Keys.OrderBy(x=>x,StringComparer.Ordinal).ToArray();
        if(beforeKeys.Length==0||!beforeKeys.SequenceEqual(proposedKeys,StringComparer.Ordinal)
            ||proposal.Before.Values.Any(x=>!double.IsFinite(x)||x<0)||proposal.Proposed.Values.Any(x=>!double.IsFinite(x)||x<0)
            ||Math.Abs(proposal.Before.Values.Sum()-1)>Tolerance||Math.Abs(proposal.Proposed.Values.Sum()-1)>Tolerance)
            throw new InvalidDataException("P13权重提议无效");
        double expected=.5*beforeKeys.Sum(id=>Math.Abs(proposal.Proposed[id]-proposal.Before[id]));
        if(!double.IsFinite(proposal.ActionMagnitude)||proposal.ActionMagnitude<0||Math.Abs(expected-proposal.ActionMagnitude)>Tolerance)
            throw new InvalidDataException("P13动作幅度与权重提议不一致");
        if(!double.IsFinite(confidence.Value)||confidence.Value<0||confidence.Value>1)
            throw new InvalidDataException("P13置信评分无效");
        if(!double.IsFinite(critic.MaximumMagnitude)||critic.MaximumMagnitude<0)
            throw new InvalidDataException("P13 Critic幅度上限无效");
    }
}
