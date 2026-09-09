using System.Collections.Immutable;
using 六合分析软件.MacroReasoning;

namespace 六合分析软件;

public static class CurrentExpertCatalog
{
    public static void Seed(VersionedExpertRegistry registry,DateTimeOffset registeredAt,string codeVersion)
    {
        ExpertRegistration[] registrations=
        {
            R("V65-50","V65-50@v65-rule-current","V6.5-50","V65",ExpertModelType.Base,"unknown",codeVersion,true,false,false,null,false,false,false,[],["history.db","V65RuleScoringEngine"],"V65ExperimentPipeline.cs; AIEngine.cs"),
            R("V65-100","V65-100@v65-rule-current","V6.5-100","V65",ExpertModelType.Base,"unknown",codeVersion,true,false,false,null,false,false,false,[],["history.db","V65RuleScoringEngine"],"V65ExperimentPipeline.cs; AIEngine.cs"),
            R("V65-All","V65-All@v65-rule-current","V6.5-全部历史","V65",ExpertModelType.Base,"unknown",codeVersion,true,false,false,null,false,false,false,[],["history.db","V65RuleScoringEngine"],"V65ExperimentPipeline.cs; AIEngine.cs"),
            R("Integrated-V7","Integrated-V7@f055-o045-v1","整合V7","V7",ExpertModelType.Composite,"frequency-0.55-omission-0.45",codeVersion,true,false,false,null,false,false,false,[],["history.db","FeatureEngine"],"V7PredictionEngines.cs:V7Engine.Predict/EngineScoring.Build"),
            R("V65-Auto","V65-Auto@meta-current","V6.5自动学习","V65-Meta",ExpertModelType.Meta,"unknown",codeVersion,false,true,false,null,true,false,true,["V65-50","V65-100","V65-All","Integrated-V7"],["PredictionHistory","ModelMemory:v65-auto"],"V7PredictionHistoryService.cs:SaveAutoLearning; AutoLearningSnapshotBuilder.cs:BuildFromBasePredictions"),
            R("V7-Auto","V7-Auto@historical-meta-current","V7自动学习","V7-Meta",ExpertModelType.Meta,"unknown",codeVersion,false,true,false,null,false,false,false,[],["history.db","FeatureEngine","MarketStateEngine","ModelMemory:intelligent-history"],"V7PredictionHistoryService.cs:SaveIntelligentAutoLearning; AutoLearningEvaluation.cs:HistoricalMetaSnapshotBuilder")
        };
        foreach(var r in registrations)registry.AppendRevision(r with{RegisteredAt=registeredAt,EffectiveFrom=registeredAt});
        var byId=registrations.ToDictionary(x=>x.ExpertId,x=>x.ExpertRevisionId,StringComparer.Ordinal);
        void Consume(string source,string typeEvidence)=>registry.AppendDependency(E("V65-Auto",source,MetaDependencyType.ConsumesExpertRanking,typeEvidence));
        Consume("V65-50","AutoLearningSnapshotBuilder.BuildFromBasePredictions读取50期完整排名");
        Consume("V65-100","AutoLearningSnapshotBuilder.BuildFromBasePredictions读取100期完整排名");
        Consume("V65-All","AutoLearningSnapshotBuilder.BuildFromBasePredictions读取全部历史完整排名");
        Consume("Integrated-V7","SaveAutoLearning将V7Engine完整排名传入BuildFromBasePredictions");
        Shared("Integrated-V7","V7-Auto",MetaDependencyType.SharedHistory,"两者读取同一history前缀");
        Shared("Integrated-V7","V7-Auto",MetaDependencyType.SharedFeatures,"两者调用FeatureEngine；V7-Auto另含MarketStateEngine");
        Shared("V65-50","V65-100",MetaDependencyType.SharedHistory,"同一期V65ExperimentPipeline历史前缀，不同窗口");
        Shared("V65-50","V65-All",MetaDependencyType.SharedHistory,"同一期V65ExperimentPipeline历史前缀，不同窗口");
        Shared("V65-100","V65-All",MetaDependencyType.SharedHistory,"同一期V65ExperimentPipeline历史前缀，不同窗口");
        ExpertDependencyEdge E(string from,string to,MetaDependencyType type,string evidence)=>new(from,to,byId[from],byId[to],type,evidence,"p5-source-audit-v1",ImmutableArray<string>.Empty);
        void Shared(string left,string right,MetaDependencyType type,string evidence){if(string.CompareOrdinal(byId[left],byId[right])>0)(left,right)=(right,left);registry.AppendDependency(E(left,right,type,evidence));}
    }

    private static ExpertRegistration R(string id,string revision,string display,string family,ExpertModelType type,string algorithm,string code,bool baseExpert,bool meta,bool experimental,int? depth,bool derived,bool sharedMemory,bool sameSnapshots,string[] parents,string[] inputs,string evidence)=>new(){ExpertId=id,ExpertRevisionId=revision,DisplayName=display,ModelFamily=family,ModelType=type,AlgorithmVersion=algorithm,CodeVersion=code,IsBaseExpert=baseExpert,IsMetaExpert=meta,IsExperimental=experimental,DependencyDepth=depth,DerivedFromExpertOutputs=derived,UsesSharedMemory=sharedMemory,UsesSameTargetBaseSnapshots=sameSnapshots,ParentExpertIds=parents.ToImmutableArray(),InputDependencyIds=inputs.ToImmutableArray(),EligibleForMacro=false,Enabled=false,PredictionSnapshotType="FullRanking12",HasFullRanking12=true,LeakageAuditStatus=ExpertAuditStatus.Unknown,SnapshotIntegrityStatus=ExpertAuditStatus.Unknown,RegisteredAt=default,EffectiveFrom=default,DependencyEvidenceReference=evidence};
}
