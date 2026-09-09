using System.Collections.Immutable;

namespace 六合分析软件.MacroReasoning;

/// <summary>P15 final sidecar safety layer. It can only keep, shrink or cancel a P13 action.</summary>
public sealed class MacroUncertaintyController : IMacroUncertaintyController
{
    private const string Version="p15-uncertainty-v1";
    private const double MinimumConfidence=.60;
    private const double CautionCap=.01;
    private const double PassCap=.05;
    private const double Tolerance=1e-9;

    public MacroDecision Constrain(MacroDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        Validate(decision);

        if(decision.DecisionType is DecisionType.Hold or DecisionType.Reject)
            return decision;

        if(decision.CriticResult.Result==CriticVerdict.Reject)
            return Cancel(decision,DecisionType.Reject,"CRITIC_REJECT_VETO");

        if(decision.Confidence.Value+Tolerance<MinimumConfidence)
            return Cancel(decision,DecisionType.Hold,"CONFIDENCE_BELOW_0.60");

        double verdictCap=decision.CriticResult.Result==CriticVerdict.Caution?CautionCap:PassCap;
        double allowed=Math.Min(decision.ActionMagnitude,
            decision.Confidence.Value*Math.Min(verdictCap,decision.CriticResult.MaximumMagnitude));
        if(allowed<=Tolerance)return Cancel(decision,DecisionType.Hold,"ZERO_ALLOWED_MAGNITUDE");

        double factor=Math.Min(1,allowed/decision.ActionMagnitude);
        ImmutableDictionary<string,double> applied=decision.ExpertWeightsBefore.ToImmutableDictionary(
            x=>x.Key,x=>x.Value+factor*(decision.ExpertWeightsApplied[x.Key]-x.Value),StringComparer.Ordinal);
        double magnitude=Magnitude(decision.ExpertWeightsBefore,applied);
        string reason=$"{decision.ReasonSummary}|{Version}|KEEP_OR_SHRINK|Confidence={decision.Confidence.Value:F4}|Cap={allowed:F6}";
        return decision with { ExpertWeightsApplied=applied,ActionMagnitude=magnitude,ReasonSummary=reason };
    }

    private static MacroDecision Cancel(MacroDecision decision,DecisionType type,string reason)=>decision with
    {
        DecisionType=type,
        ExpertWeightsApplied=decision.ExpertWeightsBefore,
        ActionMagnitude=0,
        ReasonSummary=$"{decision.ReasonSummary}|{Version}|{type.ToString().ToUpperInvariant()}|{reason}"
    };

    private static void Validate(MacroDecision decision)
    {
        if(decision.Issue<=0)throw new InvalidDataException("P15目标期无效");
        string[] before=decision.ExpertWeightsBefore.Keys.OrderBy(x=>x,StringComparer.Ordinal).ToArray();
        string[] proposed=decision.ExpertWeightsProposed.Keys.OrderBy(x=>x,StringComparer.Ordinal).ToArray();
        string[] applied=decision.ExpertWeightsApplied.Keys.OrderBy(x=>x,StringComparer.Ordinal).ToArray();
        if(before.Length==0||!before.SequenceEqual(proposed,StringComparer.Ordinal)||!before.SequenceEqual(applied,StringComparer.Ordinal))
            throw new InvalidDataException("P15权重支持集不一致");
        foreach(ImmutableDictionary<string,double> set in new[]{decision.ExpertWeightsBefore,decision.ExpertWeightsProposed,decision.ExpertWeightsApplied})
            if(set.Values.Any(x=>!double.IsFinite(x)||x<0)||Math.Abs(set.Values.Sum()-1)>Tolerance)
                throw new InvalidDataException("P15权重必须有限、非负且和为1");

        foreach(string id in before)
        {
            double proposedMove=decision.ExpertWeightsProposed[id]-decision.ExpertWeightsBefore[id];
            double appliedMove=decision.ExpertWeightsApplied[id]-decision.ExpertWeightsBefore[id];
            if(Math.Abs(appliedMove)>Math.Abs(proposedMove)+Tolerance
                ||Math.Abs(appliedMove)>Tolerance&&Math.Sign(appliedMove)!=Math.Sign(proposedMove))
                throw new InvalidDataException("P15输入动作已超出或反转Proposal方向");
        }

        double expected=Magnitude(decision.ExpertWeightsBefore,decision.ExpertWeightsApplied);
        if(!double.IsFinite(decision.ActionMagnitude)||decision.ActionMagnitude<0
            ||Math.Abs(expected-decision.ActionMagnitude)>Tolerance)
            throw new InvalidDataException("P15动作幅度与Applied权重不一致");
        if(!double.IsFinite(decision.Confidence.Value)||decision.Confidence.Value<0||decision.Confidence.Value>1)
            throw new InvalidDataException("P15置信评分无效");
        if(!double.IsFinite(decision.CriticResult.MaximumMagnitude)||decision.CriticResult.MaximumMagnitude<0
            ||decision.CriticResult.MaximumMagnitude>PassCap+Tolerance)
            throw new InvalidDataException("P15 Critic上限无效");
        if((decision.DecisionType is DecisionType.Hold or DecisionType.Reject)
            &&(expected>Tolerance||decision.ActionMagnitude>Tolerance))
            throw new InvalidDataException("P15 HOLD/REJECT不能携带已执行动作");
    }

    private static double Magnitude(ImmutableDictionary<string,double> before,ImmutableDictionary<string,double> after)=>
        .5*before.Keys.Sum(id=>Math.Abs(after[id]-before[id]));
}
