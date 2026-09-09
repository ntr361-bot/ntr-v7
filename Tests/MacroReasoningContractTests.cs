using System.Collections.Immutable;
using 六合分析软件.MacroReasoning;

public static class MacroReasoningContractTests
{
    // Executable contract checks. P9-P13 behavior is covered by MacroReasoningP9P13Tests;
    // the remaining roadmap cases below belong to later stages.
    public static int Run()
    {
        if(Enum.GetValues<HypothesisType>().Length!=7 ||
            !Enum.IsDefined(HypothesisType.RandomFluctuation) || !Enum.IsDefined(HypothesisType.NoMeaningfulChange))
            throw new Exception("Hypothesis vocabulary must include the two null explanations");
        var before = ImmutableDictionary<string,double>.Empty.Add("V7",0.25);
        var after = before.SetItem("V7",0.26);
        if(before["V7"]!=0.25 || after["V7"]!=0.26) throw new Exception("Immutable weights changed in place");
        if(typeof(MacroReasoningAudit).GetProperty("ActualZodiac") is not null ||
            typeof(MacroReflection).GetProperty("HypothesisAssessments") is null ||
            typeof(MacroReflection).GetProperty("DecisionCorrect") is null)
            throw new Exception("Prediction audit and later assessments must remain separate");
        if(typeof(CounterfactualSnapshot).GetProperty("ProposedRanking") is null ||
            typeof(CounterfactualSnapshot).GetProperty("NoActionRanking") is null)
            throw new Exception("Critic evaluation needs the frozen rejected proposal");
        var registration = new ExpertRegistration { ExpertId="future-experiment", ExpertRevisionId="future-experiment@test-v1", DisplayName="未来实验",
            ModelFamily="Research", ModelType=ExpertModelType.Meta, AlgorithmVersion="test-v1",CodeVersion="test",
            PredictionSnapshotType="Frozen12",RegisteredAt=DateTimeOffset.UnixEpoch,
            EffectiveFrom=DateTimeOffset.UnixEpoch,DependencyEvidenceReference="fixture",IsExperimental=true };
        if(registration.EligibleForMacro || registration.Enabled || registration.LeakageAuditStatus!=ExpertAuditStatus.Unknown)
            throw new Exception("New experimental registrations must not enable themselves");
        var nWeights = Enumerable.Range(1,9).ToImmutableDictionary(i=>"expert-"+i,i=>1d/9);
        if(nWeights.Count!=9 || Math.Abs(nWeights.Values.Sum()-1)>1e-12 ||
            typeof(IMacroGatingModel).GetMethod("Rank")!.GetParameters()[1].ParameterType!=typeof(ImmutableDictionary<string,double>))
            throw new Exception("Gating contract must support arbitrary expert identifiers");
        if(typeof(MacroReasoningAudit).GetProperty("ExpertPool") is null ||
            typeof(ExpertPoolSnapshot).GetProperty("MissingExpertIds") is null ||
            typeof(ExpertDependencySnapshot).GetProperty("EffectiveIndependentExpertCount") is null)
            throw new Exception("Pool eligibility and dependency evidence must be auditable");
        if(Enum.GetValues<ExpertActionMode>().Length!=3 || default(ExpertActionMode)!=ExpertActionMode.IGNORE ||
            CounterTransformDefinition.Version!="full12-rank-reversal-v1") throw new Exception("Explicit action and counter transform declaration missing");
        if(typeof(ExpertSnapshot).GetProperty("ExpertRevisionId") is null || typeof(ExpertRegistration).GetProperty("DependencyDepth") is null ||
            typeof(MacroReflection).GetProperty("CommonSuccesses") is null) throw new Exception("Revision, dependency and reflection contracts incomplete");
        if(typeof(ExpertPoolSnapshot).GetProperty("EvaluationMode")!.PropertyType!=typeof(HistoricalEvaluationMode) ||
            !Enum.IsDefined(BaselineType.BestStaticBlend)) throw new Exception("Historical comparison contracts incomplete");
        if(typeof(ExpertCounterView).GetProperty("CounterTransformId") is null ||
            typeof(CounterValidationEvidence).GetProperty("TransformVersion") is null ||
            typeof(CounterValidationEvidence).GetProperty("InferencePlan") is null)
            throw new Exception("Transform identity and inference preregistration must accompany evidence");
        if(typeof(ExpertDecision).GetProperty("ModeSelection")?.PropertyType!=typeof(ExpertModeSelection) ||
            typeof(ExpertModeSelection).GetProperty("HistoryCutoff") is null)
            throw new Exception("Mode selection requires frozen prefix provenance");
        if(typeof(BaselineContract).GetProperty("StaticBlendSearch") is null ||
            typeof(StaticBlendSearchRegistration).GetProperty("RegularizationPolicy") is null ||
            typeof(MarginalContributionStats).GetProperty("ControlEnsembleId") is null ||
            typeof(MarginalContributionStats).GetProperty("CandidateEnsembleId") is null)
            throw new Exception("Static search and contextual marginal comparison contracts required");
        Console.WriteLine("PASS 13 contract checks; P9-P13 have a separate executable acceptance suite; no COUNTER transform or statistics executed.");
        return 0;
    }

    public static readonly (string Name, string Stage, string Expected)[] PendingAcceptance =
    {
        ("TEST04_LowConfidenceCap", "P12/P15", "Magnitude <= frozen confidence cap; CAUTION <= .01"),
        ("TEST06_DeterministicReasoning", "P17", "Same frozen prefix, versions, time and seed => same audit payload"),
        ("TEST07_NoTargetLeakage", "P7/P20", "Perturb target/future outcomes => unchanged Observe(N)"),
        ("TEST08_ReflectionAfterReveal", "P18", "Before AvailableAt => reject reflection"),
        ("TEST09_PredictionBeforeReflection", "P17/P18", "Missing frozen pre-draw audit => reject"),
        ("TEST10_SeparateAssessments", "P18", "Hypothesis correct with harmful action remains separately represented"),
        ("TEST12_CompleteImmutableAudit", "P17/P19", "Missing fields rejected; reflection cannot modify prediction hash")
    };
    public static readonly string[] PendingDynamicPoolAcceptance =
    {
        "Unregistered or audit-failed expert cannot enter Included, regardless of performance",
        "Qualified poor-performing expert remains eligible; no score threshold exclusion",
        "Missing snapshot never substituted; incomplete 11-zodiac snapshot excluded",
        "Parent cycle, unknown version and unavailable parent provenance rejected",
        "N=1,6,9 weight vectors sum to one and keys exactly match Included",
        "Registry created after AsOf cannot enter historical LiveFrozen pool",
        "CausalReconstruction stays in its own experiment and cannot masquerade as LiveFrozen",
        "UniqueRescue rejects empty or missing reference set and records opportunity denominator",
        "CommonFailure uses frozen dependency groups and only revealed prior outcomes",
        "Adding duplicate experts cannot increase confidence as independent evidence; estimator missing data yields unknown"
    };
    public static readonly string[] PendingStatisticalAcceptance =
    {
        "Counter Top6 > 50% alone cannot pass validation; full reversal equals Original Bottom6",
        "Algebraic MeanRank improvement alone cannot pass; require unseen original ActualRank deviation from 6.5 and preregistered stability",
        "Sequence inference respects frozen block bootstrap/rolling-block permutation plan, null model and multiplicity policy; no naive IID substitution",
        "Unseen Top3/MRR/rank evidence, contextual MarginalNetImpact and Live Shadow are required; mechanical reversal is not independent evidence",
        "Transform identity and version bind view and validation; a different transform cannot inherit full-reversal validation",
        "Changing target or future outcomes leaves Mode and selected transform unchanged; selection freezes before prediction and evaluation follows reveal",
        "BestStaticBlend rejects absent or late search preregistration; search space, grid, regularization and training interval cannot change after freeze",
        "Marginal comparison IDs must match the frozen ensemble pair; contributions from different pairs cannot be presented as absolute expert value"
    };
    public static readonly string[] PendingDecisionSemanticsAcceptance =
    {
        "Same ExpertId with different Revision statistics remain separately identifiable",
        "Incomplete ranking rejects CounterView; full ranking transformation follows declared version",
        "Negative or nonfinite weights rejected in every mode; IGNORE applied weight is zero",
        "Negative SignedReliability alone never authorizes COUNTER",
        "Missing BACKTEST/HOLDOUT/LIVE_SHADOW evidence or future-dated evidence rejects COUNTER",
        "Mode change with unchanged numeric weight still requires Critic and counter validation",
        "MarginalRescue/Harm compare frozen ensemble with and without e; net equals difference",
        "Current CommonSuccess/CommonFailure absent before reveal; reflection cannot rewrite audit",
        "SharedHistory and ConsumesExpertRanking have distinct edge semantics and revision evidence",
        "EffectiveIndependentExpertCount never directly generates weights",
        "HistoricalAvailability and reconstruction results cannot merge; reconstruction provenance required",
        "BestStaticBlend selection uses training only; validation and holdout weights remain frozen",
        "Duplicate FOLLOW/COUNTER use of same revision rejected; all IGNORE has defined HOLD behavior",
        "Decision audit modes, revisions, counter views and weights agree and remain immutable"
    };
}
