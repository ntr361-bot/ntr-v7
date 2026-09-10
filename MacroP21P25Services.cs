using System.Collections.Immutable;

namespace 六合分析软件.MacroReasoning;

public sealed record MacroExperimentRegistration(string ExperimentId,string Phase,HistoricalEvaluationMode Mode,
    string FrozenParametersHash,DateTimeOffset RegisteredAt,bool ProductionEnabled);
public sealed class MacroExperimentRegistry
{
    private readonly Dictionary<string,MacroExperimentRegistration> items=new(StringComparer.Ordinal);
    public void Append(MacroExperimentRegistration value)
    {
        if(string.IsNullOrWhiteSpace(value.ExperimentId)||value.ProductionEnabled)throw new InvalidDataException("Research registration must be named and shadow-only");
        if(items.TryGetValue(value.ExperimentId,out var old)) { if(old!=value)throw new InvalidDataException("Experiment registry is append-only"); return; }
        items.Add(value.ExperimentId,value);
    }
    public MacroExperimentRegistration? Read(string id)=>items.GetValueOrDefault(id);
    public ImmutableArray<MacroExperimentRegistration> All()=>items.Values.OrderBy(x=>x.RegisteredAt).ToImmutableArray();
}

public sealed record MacroValidationPlan(ImmutableArray<int> Windows,string FrozenSplitDefinition,string StaticBlendRegistration,int Seed);
public sealed record MacroValidationRun(int RequestedSamples,int AvailableSamples,string Status,ReasoningMetrics? Metrics,string Explanation);
public sealed record MacroValidationReport(ImmutableArray<MacroValidationRun> Runs,string DefinitionVersion);
public sealed class MacroHistoricalValidator
{
    public MacroValidationReport Validate(MacroValidationPlan plan,int availableSamples,Func<int,ReasoningMetrics> frozenRunner)
    {
        if(plan.Windows.IsEmpty||plan.Windows.Any(x=>x<=0)||plan.Windows.Distinct().Count()!=plan.Windows.Length||string.IsNullOrWhiteSpace(plan.FrozenSplitDefinition))
            throw new InvalidDataException("Validation plan must be preregistered");
        var runs=plan.Windows.Select(n=>availableSamples<n
            ?new MacroValidationRun(n,availableSamples,"INSUFFICIENT_DATA",null,"只报告实际可用样本，不补造快照")
            :new MacroValidationRun(n,n,"COMPLETED",frozenRunner(n),"由冻结P20 runner生成")).ToImmutableArray();
        return new(runs,"p21-window-validation-v1");
    }
}

public sealed record MacroShadowReceipt(string ExperimentId,long Issue,string AuditHash,DateTimeOffset RecordedAt,bool ProductionApplied);
public sealed class MacroLiveShadowService
{
    private readonly Dictionary<(string,long),MacroShadowReceipt> rows=[];
    public MacroShadowReceipt Record(MacroShadowReceipt value)
    {
        if(value.ProductionApplied||string.IsNullOrWhiteSpace(value.AuditHash))throw new InvalidDataException("Live Shadow cannot apply production output");
        var key=(value.ExperimentId,value.Issue);
        if(rows.TryGetValue(key,out var old)){if(old!=value)throw new InvalidDataException("Shadow receipt is immutable");return old;}
        rows.Add(key,value);return value;
    }
    public MacroShadowReceipt? Read(string experiment,long issue)=>rows.GetValueOrDefault((experiment,issue));
}

public sealed record MacroExplanationRecord(string ExperimentId,long Issue,DecisionType Decision,double Confidence,string Reason,
    ImmutableArray<string> Hypotheses,ImmutableArray<string> CounterEvidence,CriticVerdict Critic,
    ImmutableArray<string> CriticReasons,ImmutableDictionary<string,double> WeightsBefore,
    ImmutableDictionary<string,double> WeightsApplied,OutcomeType? Outcome);
public interface IMacroExplanationSource { MacroExplanationRecord? Read(string experiment,long issue); }
public sealed class StoredMacroExplanationSource(MacroReasoningAuditStore store) : IMacroExplanationSource
{
    public MacroExplanationRecord? Read(string experiment,long issue)
    {
        var a=store.Read(experiment,issue); if(a is null)return null;
        return new(experiment,issue,a.Decision.DecisionType,a.Decision.Confidence.Value,a.Decision.ReasonSummary,
            a.Decision.SelectedHypothesisIds,a.CounterEvidence.Items.Select(x=>$"{x.SignalName}:{x.Direction}").ToImmutableArray(),
            a.Decision.CriticResult.Result,a.Decision.CriticResult.Reasons,a.Decision.ExpertWeightsBefore,a.Decision.ExpertWeightsApplied,null);
    }
}
public sealed class ModelExplanationService(IMacroExplanationSource source) : IModelExplanationService
{
    public string Explain(string experimentId,long issue,string question)
    {
        var row=source.Read(experimentId,issue); if(row is null)return "没有找到该期的冻结审计记录。";
        if(question.Contains("没有调权")||question.Contains("为什么")&&row.Decision==DecisionType.Hold)
            return $"决策=HOLD，置信度={row.Confidence:P0}；原因：{row.Reason}；Critic={row.Critic}（{string.Join("、",row.CriticReasons)}）；反证：{string.Join("、",row.CounterEvidence)}。";
        if(question.Contains("假设"))return "当期假设："+string.Join("、",row.Hypotheses);
        if(question.Contains("反证"))return "当期反证："+string.Join("、",row.CounterEvidence);
        if(question.Contains("权重"))return $"调整前：{Format(row.WeightsBefore)}；实际：{Format(row.WeightsApplied)}。";
        if(question.Contains("结果")||question.Contains("错误"))return row.Outcome is null?"该期尚未开奖或尚未反思。":"开奖后归因："+row.Outcome;
        return "该问题目前不支持；解释服务不会脱离审计记录编造理由。";
    }
    static string Format(ImmutableDictionary<string,double> x)=>string.Join("，",x.OrderBy(v=>v.Key).Select(v=>$"{v.Key}={v.Value:P1}"));
}
public sealed class MacroModelAssistant(IModelExplanationService explanation)
{
    public string Ask(string experiment,long issue,string question)=>explanation.Explain(experiment,issue,question);
}
public sealed class MacroExperimentCenterModel(MacroExperimentRegistry registry,MacroLiveShadowService shadow,MacroModelAssistant assistant)
{
    public ImmutableArray<MacroExperimentRegistration> List()=>registry.All();
    public MacroShadowReceipt? Shadow(string experiment,long issue)=>shadow.Read(experiment,issue);
    public string Ask(string experiment,long issue,string question)=>assistant.Ask(experiment,issue,question);
}
