using System.Collections.Immutable;
using 六合分析软件;
using 六合分析软件.MacroReasoning;

public static class MacroP21P25Tests
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
        Check(center.List().Single().ExperimentId=="exp-1"&&center.Ask("exp-1",1,"为什么没有调权").Contains("HOLD"),"P25 experiment center model integrates list and ask");
        using var form=new MacroExperimentCenterForm(center);
        Check(form.Text.Contains("实验中心")&&form.Controls.Count>0,"P25 independent experiment/ask-model window exists");
        var parsed = WebsiteLearningParser.Parse("256期六肖中特【猴龙蛇羊虎牛】", "六肖", "sha256");
        Check(parsed.Issue == 256 && parsed.Zodiacs.SequenceEqual(new[]{"猴","龙","蛇","羊","虎","牛"}) && parsed.SourceHash == "sha256", "P25 website source parser extracts issue and zodiac set");
        var isolated = WebsiteLearningParser.Parse("256期六肖中特【猴龙蛇羊虎牛】255期六肖中特【鼠兔狗猪】", "六肖", "sha256");
        Check(isolated.Zodiacs.SequenceEqual(new[]{"猴","龙","蛇","羊","虎","牛"}), "P25 parser isolates the first issue block from prior issues");
        var target = ParseExpectedIssue("257期六肖中特【兔鸡牛】256期六肖中特【猴龙蛇羊虎牛】255期六肖中特【鼠狗猪】", 256);
        Check(target.Issue == 256 && target.Zodiacs.SequenceEqual(new[]{"猴","龙","蛇","羊","虎","牛"}), "P25 parser selects only the requested issue block");
        var placeholder = ParseExpectedIssue("256期六生肖【猴龙蛇羊虎牛】开：<span>？00</span>255期六生肖【鼠狗猪】开：猪44", 256);
        Check(placeholder.Zodiacs.SequenceEqual(new[]{"猴","龙","蛇","羊","虎","牛"}), "P25 parser accepts an unrevealed target issue placeholder");
        Reject(()=>ParseExpectedIssue("256期六生肖【猴龙蛇羊虎牛】开：<span>猪44</span>255期六生肖【鼠狗】", 256), "P25 parser rejects a revealed target issue result");
        Reject(()=>ParseExpectedIssue("256期【大双大单小单】开：？00 255期六生肖【猪鼠马】开：猪44", 256), "P25 parser cannot import zodiac values from a prior issue");
        Reject(()=>ParseExpectedIssue("257期六生肖【兔鸡牛】", 256), "P25 parser rejects a missing target issue");
        Check(WebsiteLearningParser.IsPostResult("256期六肖【猴龙蛇羊虎牛】开：蛇02"), "P25 website parser rejects revealed result");
        var issueBlocks = ParseAllIssueBlocks("256期六肖【猴龙蛇羊虎牛】开：？00 255期六肖【鼠兔狗猪】开：猪44");
        Check(issueBlocks.Count == 2, "P25 crawler parser archives each website issue separately");
        Check(ReadIssue(issueBlocks.Single(x => ReadIssue(x) == 256)) == 256 &&
              ReadZodiacs(issueBlocks.Single(x => ReadIssue(x) == 256)).SequenceEqual(new[]{"猴","龙","蛇","羊","虎","牛"}) &&
              ReadWebsiteResult(issueBlocks.Single(x => ReadIssue(x) == 256)) is null,
              "P25 crawler parser keeps an unrevealed issue independent from older results");
        Check(ReadWebsiteResult(issueBlocks.Single(x => ReadIssue(x) == 255)) == "猪" &&
              ReadZodiacs(issueBlocks.Single(x => ReadIssue(x) == 255)).SequenceEqual(new[]{"鼠","兔","狗","猪"}),
              "P25 crawler parser settles only the revealed issue from its own block");
        Check(V7PredictionHistoryService.IsV7DisplayedModel("P25-Web", 25), "P25 website record is visible in intelligent ledger");
        Check(V7PredictionHistoryService.FormatModelName("P25-Web") == "P25网站资料", "P25 website model has readable ledger name");
        Console.WriteLine("P21_P25_SMOKE_PASS"); return 0;
    }
    static WebsiteParsedSignal ParseExpectedIssue(string text,int issue)
    {
        var method=typeof(WebsiteLearningParser).GetMethod("Parse",[typeof(string),typeof(string),typeof(string),typeof(int)]);
        if(method is null)throw new Exception("P25 target-issue parser API is missing");
        try{return (WebsiteParsedSignal)method.Invoke(null,[text,"test","sha256",issue])!;}
        catch(System.Reflection.TargetInvocationException e)when(e.InnerException is not null){throw e.InnerException;}
    }
    static IReadOnlyList<object> ParseAllIssueBlocks(string text)
    {
        var method=typeof(WebsiteLearningParser).GetMethod("ParseAll",[typeof(string),typeof(string),typeof(string)]);
        if(method is null)throw new Exception("P25 crawler parser ParseAll API is missing");
        return ((System.Collections.IEnumerable)method.Invoke(null,[text,"test","sha256"])!).Cast<object>().ToArray();
    }
    static int ReadIssue(object value)=>(int)(value.GetType().GetProperty("Issue")?.GetValue(value) ?? throw new Exception("P25 issue snapshot lacks Issue"));
    static IReadOnlyList<string> ReadZodiacs(object value)=>(IReadOnlyList<string>)(value.GetType().GetProperty("Zodiacs")?.GetValue(value) ?? throw new Exception("P25 issue snapshot lacks Zodiacs"));
    static string? ReadWebsiteResult(object value)=>(string?)value.GetType().GetProperty("WebsiteResultZodiac")?.GetValue(value);
    sealed class FakeExplanationSource : IMacroExplanationSource
    {
        public MacroExplanationRecord? Read(string experiment,long issue)=>new(experiment,issue,DecisionType.Hold,.42,"证据不足",["RandomFluctuation"],["长期窗口未确认"],CriticVerdict.Caution,["样本不足"],ImmutableDictionary<string,double>.Empty,ImmutableDictionary<string,double>.Empty,null);
    }
    static void Check(bool ok,string s){if(!ok)throw new Exception(s);Console.WriteLine("PASS "+s);}
    static void Reject(Action a,string s){try{a();}catch(InvalidDataException){Console.WriteLine("PASS "+s);return;}throw new Exception(s);}
}
