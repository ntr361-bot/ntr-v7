using System.Collections.Immutable;
using 六合分析软件;
using 六合分析软件.MacroReasoning;

public static class MacroP21P24Tests
{
    public static int Run()
    {
        var registry=new MacroExperimentRegistry();
        registry.Append(new("exp-1","P21",HistoricalEvaluationMode.CausalReconstruction,"frozen",DateTimeOffset.UtcNow,false));
        Check(registry.Read("exp-1")!.ProductionEnabled==false,"P21 experiment defaults shadow-only");
        Reject(()=>registry.Append(new("exp-1","P21",HistoricalEvaluationMode.CausalReconstruction,"other",DateTimeOffset.UtcNow,false)),"experiment overwrite rejected");
        var plan=new MacroValidationPlan([300,600,1000],"split-v1","static-v1",6501);
        var unavailable=new MacroHistoricalValidator().Validate(plan,250,_=>throw new Exception());
        Check(unavailable.Runs.All(x=>x.Status=="INSUFFICIENT_DATA"),"P21 reports actual availability without inventing samples");
        var shadow=new MacroLiveShadowService();
        var receipt=shadow.Record(new("exp-1",2026250,"hash",DateTimeOffset.UtcNow,false));
        Check(!receipt.ProductionApplied&&shadow.Read("exp-1",2026250) is not null,"P22 stores shadow output without production apply");
        Reject(()=>shadow.Record(receipt with {AuditHash="changed"}),"shadow overwrite rejected");
        var source=new FakeExplanationSource();
        var explain=new ModelExplanationService(source);
        Check(explain.Explain("exp-1",1,"为什么没有调权").Contains("HOLD"),"P23 explanation comes from stored audit");
        Check(explain.Explain("exp-1",1,"未知问题").Contains("不支持"),"P23 refuses invented explanation");
        var emptyRun=new RunIdentity("empty","v1","v1",new string('a',64),"test",1);
        var emptyStore=new MacroReasoningAuditStore(Path.Combine(Path.GetTempPath(),"macro-explain-"+Guid.NewGuid()+".db"),emptyRun);
        Check(new StoredMacroExplanationSource(emptyStore).Read("empty",1) is null,"P23 stored-audit source reports missing honestly");
        var assistant=new MacroModelAssistant(explain);
        Check(assistant.Ask("exp-1",1,"为什么没有调权").Contains("HOLD"),"P24 assistant is read-only audit query");
        var center=new MacroExperimentCenterModel(registry,shadow,assistant);
        Check(center.List().Single().ExperimentId=="exp-1"&&center.Ask("exp-1",1,"为什么没有调权").Contains("HOLD"),"experiment center model integrates list and ask");
        using var form=new MacroExperimentCenterForm(center);
        Check(form.Text.Contains("实验中心")&&form.Controls.Count>0,"independent experiment/ask-model window exists");
        Check(!V7PredictionHistoryService.IsV7DisplayedModel("P25-Web", 25), "removed P25 web model is hidden");
        Console.WriteLine("P21_P24_SMOKE_PASS"); return 0;
    }
    sealed class FakeExplanationSource : IMacroExplanationSource
    {
        public MacroExplanationRecord? Read(string experiment,long issue)=>new(experiment,issue,DecisionType.Hold,.42,"证据不足",["RandomFluctuation"],["长期窗口未确认"],CriticVerdict.Caution,["样本不足"],ImmutableDictionary<string,double>.Empty,ImmutableDictionary<string,double>.Empty,null);
    }
    static void Check(bool ok,string s){if(!ok)throw new Exception(s);Console.WriteLine("PASS "+s);}
    static void Reject(Action a,string s){try{a();}catch(InvalidDataException){Console.WriteLine("PASS "+s);return;}throw new Exception(s);}
}
