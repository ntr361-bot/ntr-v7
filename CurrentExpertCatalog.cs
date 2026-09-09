using System.Collections.Immutable;
using 六合分析软件.MacroReasoning;

namespace 六合分析软件;

public static class CurrentExpertCatalog
{
    public static void Seed(VersionedExpertRegistry registry,DateTimeOffset registeredAt,string codeVersion)
    {
        V65BaseMacroExpertDefinition v6550=V65BaseMacroExpertAdapter.DefinitionFor("V65-50");
        V65BaseMacroExpertDefinition v65100=V65BaseMacroExpertAdapter.DefinitionFor("V65-100");
        V65BaseMacroExpertDefinition v65All=V65BaseMacroExpertAdapter.DefinitionFor("V65-All");
        ExpertRegistration[] registrations=
        {
            R("V65-50","V65-50@v65-rule-current","V6.5-50","V65",ExpertModelType.Base,"unknown",codeVersion,true,false,false,null,false,false,false,[],["history.db","V65RuleScoringEngine"],"V65ExperimentPipeline.cs; AIEngine.cs"),
            R("V65-100","V65-100@v65-rule-current","V6.5-100","V65",ExpertModelType.Base,"unknown",codeVersion,true,false,false,null,false,false,false,[],["history.db","V65RuleScoringEngine"],"V65ExperimentPipeline.cs; AIEngine.cs"),
            R("V65-All","V65-All@v65-rule-current","V6.5-全部历史","V65",ExpertModelType.Base,"unknown",codeVersion,true,false,false,null,false,false,false,[],["history.db","V65RuleScoringEngine"],"V65ExperimentPipeline.cs; AIEngine.cs"),
            R(v6550.ExpertId,v6550.ExpertRevisionId,v6550.DisplayName,"V65",ExpertModelType.Base,v6550.AlgorithmVersion,codeVersion,true,false,false,0,false,false,false,[],V65BaseMacroExpertSnapshotService.InputDependencyIds.ToArray(),"V65BaseMacroExpertAdapter.cs; V65BaseMacroExpertSnapshotService.cs; V65ExperimentPipeline.cs; ZodiacPredictEngineV2.cs") with { LeakageAuditStatus=ExpertAuditStatus.Passed,SnapshotIntegrityStatus=ExpertAuditStatus.Passed },
            R(v65100.ExpertId,v65100.ExpertRevisionId,v65100.DisplayName,"V65",ExpertModelType.Base,v65100.AlgorithmVersion,codeVersion,true,false,false,0,false,false,false,[],V65BaseMacroExpertSnapshotService.InputDependencyIds.ToArray(),"V65BaseMacroExpertAdapter.cs; V65BaseMacroExpertSnapshotService.cs; V65ExperimentPipeline.cs; ZodiacPredictEngineV2.cs") with { LeakageAuditStatus=ExpertAuditStatus.Passed,SnapshotIntegrityStatus=ExpertAuditStatus.Passed },
            R(v65All.ExpertId,v65All.ExpertRevisionId,v65All.DisplayName,"V65",ExpertModelType.Base,v65All.AlgorithmVersion,codeVersion,true,false,false,0,false,false,false,[],V65BaseMacroExpertSnapshotService.InputDependencyIds.ToArray(),"V65BaseMacroExpertAdapter.cs; V65BaseMacroExpertSnapshotService.cs; V65ExperimentPipeline.cs; ZodiacPredictEngineV2.cs") with { LeakageAuditStatus=ExpertAuditStatus.Passed,SnapshotIntegrityStatus=ExpertAuditStatus.Passed },
            R("Integrated-V7","Integrated-V7@f055-o045-v1","整合V7","V7",ExpertModelType.Composite,"frequency-0.55-omission-0.45",codeVersion,true,false,false,null,false,false,false,[],["history.db","FeatureEngine"],"V7PredictionEngines.cs:V7Engine.Predict/EngineScoring.Build"),
            R("Integrated-V7",IntegratedV7MacroExpertSnapshotService.ExpertRevisionId,"整合V7 Macro FullRanking12","V7",ExpertModelType.Composite,IntegratedV7MacroExpertSnapshotService.AlgorithmVersion,codeVersion,true,false,false,null,false,false,false,[],IntegratedV7MacroExpertSnapshotService.InputDependencyIds.ToArray(),"IntegratedV7MacroExpertAdapter.cs; IntegratedV7MacroExpertSnapshotService.cs") with { LeakageAuditStatus=ExpertAuditStatus.Passed,SnapshotIntegrityStatus=ExpertAuditStatus.Passed },
            R("V65-Auto","V65-Auto@meta-current","V6.5自动学习","V65-Meta",ExpertModelType.Meta,"unknown",codeVersion,false,true,false,null,true,false,true,["V65-50","V65-100","V65-All","Integrated-V7"],["PredictionHistory","ModelMemory:v65-auto"],"V7PredictionHistoryService.cs:SaveAutoLearning; AutoLearningSnapshotBuilder.cs:BuildFromBasePredictions"),
            R("V7-Auto","V7-Auto@historical-meta-current","V7自动学习","V7-Meta",ExpertModelType.Meta,"unknown",codeVersion,false,true,false,null,false,false,false,[],["history.db","FeatureEngine","MarketStateEngine","ModelMemory:intelligent-history"],"V7PredictionHistoryService.cs:SaveIntelligentAutoLearning; AutoLearningEvaluation.cs:HistoricalMetaSnapshotBuilder")
        };
        foreach(var r in registrations)registry.AppendRevision(r with{RegisteredAt=registeredAt,EffectiveFrom=registeredAt});
        // Existing production dependencies remain bound to the original production revision.
        // The Macro FullRanking12 revision is append-only and has no production consumers.
        var byId=registrations.GroupBy(x=>x.ExpertId,StringComparer.Ordinal)
            .ToDictionary(x=>x.Key,x=>x.First().ExpertRevisionId,StringComparer.Ordinal);
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
        // Append relationships for the Macro revisions explicitly; never retarget old consumer edges.
        var macroBases = V65BaseMacroExpertAdapter.Definitions;
        for (int i = 0; i < macroBases.Length; i++)
        for (int j = i + 1; j < macroBases.Length; j++)
        {
            var left = macroBases[i];
            var right = macroBases[j];
            if (string.CompareOrdinal(left.ExpertRevisionId, right.ExpertRevisionId) > 0)
                (left, right) = (right, left);
            registry.AppendDependency(new ExpertDependencyEdge(left.ExpertId, right.ExpertId,
                left.ExpertRevisionId, right.ExpertRevisionId, MetaDependencyType.SharedHistory,
                "V65BaseMacroExpertAdapter.Build: caller-supplied history; V65RuleScoringEngine.Predict: windowed prefixes of shared history",
                "v65-macro-dependencies-v1", ImmutableArray.Create("history-prefix:caller-supplied")));
            registry.AppendDependency(new ExpertDependencyEdge(left.ExpertId, right.ExpertId,
                left.ExpertRevisionId, right.ExpertRevisionId, MetaDependencyType.SharedFeatures,
                "ZodiacPredictEngineV2.cs: CalculateZodiacScoreV2/ApplyEightZodiacRule share feature definitions; window values and weights differ",
                "v65-macro-dependencies-v1", ImmutableArray.Create("V65RuleScoringEngine")));
        }
        ExpertDependencyEdge E(string from,string to,MetaDependencyType type,string evidence)=>new(from,to,byId[from],byId[to],type,evidence,"p5-source-audit-v1",ImmutableArray<string>.Empty);
        void Shared(string left,string right,MetaDependencyType type,string evidence){if(string.CompareOrdinal(byId[left],byId[right])>0)(left,right)=(right,left);registry.AppendDependency(E(left,right,type,evidence));}
    }

    private static ExpertRegistration R(string id,string revision,string display,string family,ExpertModelType type,string algorithm,string code,bool baseExpert,bool meta,bool experimental,int? depth,bool derived,bool sharedMemory,bool sameSnapshots,string[] parents,string[] inputs,string evidence)=>new(){ExpertId=id,ExpertRevisionId=revision,DisplayName=display,ModelFamily=family,ModelType=type,AlgorithmVersion=algorithm,CodeVersion=code,IsBaseExpert=baseExpert,IsMetaExpert=meta,IsExperimental=experimental,DependencyDepth=depth,DerivedFromExpertOutputs=derived,UsesSharedMemory=sharedMemory,UsesSameTargetBaseSnapshots=sameSnapshots,ParentExpertIds=parents.ToImmutableArray(),InputDependencyIds=inputs.ToImmutableArray(),EligibleForMacro=false,Enabled=false,PredictionSnapshotType="FullRanking12",HasFullRanking12=true,LeakageAuditStatus=ExpertAuditStatus.Unknown,SnapshotIntegrityStatus=ExpertAuditStatus.Unknown,RegisteredAt=default,EffectiveFrom=default,DependencyEvidenceReference=evidence};
}
