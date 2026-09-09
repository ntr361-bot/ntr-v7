using System.Collections.Immutable;
using 六合分析软件.MacroReasoning;

public static class MacroUncertaintyP15Tests
{
    public static int Run()
    {
        var controller=new MacroUncertaintyController();
        MacroDecision hold=Decision(DecisionType.Hold,.8,CriticVerdict.Caution,.01,.5,.5,.5,.5);
        MacroDecision held=controller.Constrain(hold);
        Check(held.DecisionType==DecisionType.Hold&&held.ExpertWeightsApplied.SequenceEqual(hold.ExpertWeightsBefore)
            &&Close(held.ActionMagnitude,0),"P15 HOLD保持零动作");

        MacroDecision reject=Decision(DecisionType.Reject,.9,CriticVerdict.Reject,0,.5,.5,.5,.5);
        Check(controller.Constrain(reject).DecisionType==DecisionType.Reject,"P15 REJECT不能被不确定性层推翻");

        MacroDecision inconsistentReject=Decision(DecisionType.Apply,.9,CriticVerdict.Reject,0,.5,.5,.51,.49);
        MacroDecision vetoed=controller.Constrain(inconsistentReject);
        Check(vetoed.DecisionType==DecisionType.Reject&&Close(vetoed.ActionMagnitude,0)
            &&vetoed.ExpertWeightsApplied.SequenceEqual(vetoed.ExpertWeightsBefore),"P15 Critic拒绝强制回到Before");

        MacroDecision low=Decision(DecisionType.Apply,.59,CriticVerdict.Pass,.05,.5,.5,.51,.49);
        MacroDecision lowHeld=controller.Constrain(low);
        Check(lowHeld.DecisionType==DecisionType.Hold&&Close(lowHeld.ActionMagnitude,0),"P15低于0.60置信度自动HOLD");

        MacroDecision caution=Decision(DecisionType.Apply,.6,CriticVerdict.Caution,.01,.5,.5,.51,.49);
        MacroDecision cautious=controller.Constrain(caution);
        Check(cautious.DecisionType==DecisionType.Apply&&Close(cautious.ActionMagnitude,.006)
            &&Close(cautious.ExpertWeightsApplied["A"],.506),"P15 CAUTION按置信度缩小且不超过1个百分点");

        MacroDecision pass=Decision(DecisionType.Apply,.75,CriticVerdict.Pass,.05,.5,.5,.54,.46);
        MacroDecision constrained=controller.Constrain(pass);
        Check(Close(constrained.ActionMagnitude,.0375)&&Close(constrained.ExpertWeightsApplied["A"],.5375),"P15 PASS受置信度乘数限制");

        MacroDecision small=Decision(DecisionType.Apply,.9,CriticVerdict.Pass,.05,.5,.5,.505,.495);
        MacroDecision unchanged=controller.Constrain(small);
        Check(Close(unchanged.ActionMagnitude,.005)&&unchanged.ExpertWeightsApplied.SequenceEqual(small.ExpertWeightsApplied),"P15已低于上限的小动作不被放大");

        Check(constrained.ExpertWeightsProposed.SequenceEqual(pass.ExpertWeightsProposed)
            &&constrained.SelectedHypothesisIds.SequenceEqual(pass.SelectedHypothesisIds),"P15不改Proposal和推理选择");
        Check(constrained.ExpertWeightsApplied.Values.All(x=>x>=0)&&Close(constrained.ExpertWeightsApplied.Values.Sum(),1),"P15输出权重非负且和为1");

        Reject(()=>controller.Constrain(hold with { ExpertWeightsApplied=Weights(.51,.49),ActionMagnitude=.01 }),"P15拒绝伪HOLD携带动作");
        Reject(()=>controller.Constrain(pass with { ActionMagnitude=.03 }),"P15拒绝动作幅度与权重不一致");
        Reject(()=>controller.Constrain(pass with { ExpertWeightsApplied=pass.ExpertWeightsApplied.Add("C",0) }),"P15拒绝支持集变化");
        Reject(()=>controller.Constrain(pass with { Confidence=pass.Confidence with { Value=double.NaN } }),"P15拒绝非法置信度");
        Reject(()=>controller.Constrain(pass with { ExpertWeightsProposed=Weights(.52,.48) }),"P15拒绝Applied超出Proposal幅度");
        Reject(()=>controller.Constrain(pass with { ExpertWeightsProposed=Weights(.46,.54) }),"P15拒绝Applied反转Proposal方向");

        Console.WriteLine("P15 UNCERTAINTY PASS");
        return 0;
    }

    private static MacroDecision Decision(DecisionType type,double confidence,CriticVerdict critic,double criticCap,
        double beforeA,double beforeB,double appliedA,double appliedB)
    {
        var before=Weights(beforeA,beforeB);
        var applied=Weights(appliedA,appliedB);
        double magnitude=.5*(Math.Abs(appliedA-beforeA)+Math.Abs(appliedB-beforeB));
        return new MacroDecision(400,ImmutableArray.Create("h1"),ImmutableArray<string>.Empty,type,before,applied,applied,magnitude,
            new ConfidenceResult(confidence,ImmutableDictionary<string,double?>.Empty,"fixture",100),"fixture",
            new CriticResult(critic,ImmutableArray.Create("fixture"),ImmutableArray.Create("fixture"),criticCap));
    }

    private static ImmutableDictionary<string,double> Weights(double a,double b)=>
        ImmutableDictionary<string,double>.Empty.WithComparers(StringComparer.Ordinal).Add("A",a).Add("B",b);
    private static bool Close(double a,double b)=>Math.Abs(a-b)<1e-9;
    private static void Reject(Action action,string name){try{action();}catch(InvalidDataException){Console.WriteLine("PASS "+name);return;}throw new InvalidOperationException("FAIL "+name);}
    private static void Check(bool condition,string name){if(!condition)throw new InvalidOperationException("FAIL "+name);Console.WriteLine("PASS "+name);}
}
