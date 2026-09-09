using System.Collections.Immutable;

namespace 六合分析软件.MacroReasoning;

// Schema only. No transforms, score formulas, activation logic or production implementation.
public enum ExpertActionMode { IGNORE, FOLLOW, COUNTER }
public enum ExpertState { NeutralExpert, PositiveExpert, ConditionalExpert, PotentialNegativeExpert, ValidatedNegativeExpert }
public enum MetaDependencyType { SharedHistory, SharedFeatures, SharedRawSignals, ConsumesExpertRanking,
    ConsumesExpertScore, ConsumesMetaOutput, SharedLearningMemory }
public enum HistoricalEvaluationMode { HistoricalAvailability, CausalReconstruction }
public enum CounterValidationStage { Unvalidated, BACKTEST, HOLDOUT, LIVE_SHADOW, Validated }
public enum BaselineType { BestSingleExpert, EqualWeightEnsemble, BestStaticBlend, ThreeExpertLearning,
    FourExpertLearning, NExpertLearning, DependencyAwareNExpert, MacroReasoning, OriginalExpert, CounterExpert }
public static class CounterTransformDefinition
{
    // One candidate, not an exhaustive list or an implemented transformation.
    public const string CounterTransformId = "full12-rank-reversal";
    public const string Version = "full12-rank-reversal-v1";
    public const string Formula = "CounterRank = 13 - OriginalRank";
}
// Reserved sequence-aware inference designs only; no IID test or estimator is implemented.
public enum SequenceInferenceMethod { BlockBootstrap, RollingBlockPermutation }
public sealed record CounterInferencePlan(string PlanId, SequenceInferenceMethod Method,
    string BlockPolicy, int? BlockLength, int Replicates, int Seed, double RandomRankCenter,
    string TestStatistic, string NullModel, string SignificanceAndMultiplicityPolicy,
    string StabilityCriterion, IssueRange EvaluationIssues, string Split,
    DateTimeOffset PreregisteredAt, string PlanHash);
// Frozen before prediction N, using only information available before N. Evaluation belongs to reflection.
public sealed record ExpertModeSelection(long TargetIssue, long HistoryCutoff, DateTimeOffset AsOf,
    DateTimeOffset SelectedAt, string ModePolicyVersion, string EvidenceHash,
    string? CounterTransformId, string? TransformVersion);
// Search design is preregistered before search; selected parameters freeze before validation/holdout.
public sealed record StaticBlendSearchRegistration(string RegistrationId, IssueRange TrainingIssues,
    string SearchSpace, string GranularityPolicy, string RegularizationPolicy, string Objective,
    string SearchBudget, string MissingExpertPolicy, DateTimeOffset PreregisteredAt, string RegistrationHash);
// Negative reliability is diagnostic potential, never a negative ensemble weight or activation permission.
public sealed record SignedReliability(double? Value, int Samples, IssueRange SourceIssues,
    string EstimatorVersion, string ContextId);
public sealed record ExpertDecision(string ExpertId, string ExpertRevisionId, ExpertActionMode Mode,
    double? GlobalReliability, double? RecentReliability, double? ContextReliability,
    double? CounterReliability, double? DependencyPenalty, double? RedundancyPenalty, double? DiversityValue,
    double RawProposedWeight, double ProposedWeight, double AppliedWeight, double Confidence,
    ImmutableArray<string> ReasonCodes, ImmutableArray<string> EvidenceReferences,
    ExpertModeSelection ModeSelection);
public sealed record ExpertCounterView(string ExpertId, string ExpertRevisionId, long TargetIssue,
    string OriginalSnapshotHash, ImmutableArray<string> OriginalRanking, ImmutableArray<string> CounterRanking,
    string CounterTransformId, string TransformVersion, DateTimeOffset CreatedAt, string ViewHash, string ValidationEvidenceId);
public sealed record ExpertDependencyEdge(string FromExpertId, string ToExpertId,
    string FromExpertRevisionId, string ToExpertRevisionId, MetaDependencyType DependencyType,
    string EvidenceReference, string Version, ImmutableArray<string> SharedInputIds);
public sealed record ReconstructionProvenance(bool Reconstructed, DateTimeOffset SimulatedAsOf,
    DateTimeOffset ReconstructedAt, string ReconstructionCodeVersion, long HistoryCutoff,
    bool MemoryRebuiltFromScratch, string TrainingPrefixHash);
// Contextual to these frozen ensembles, never an absolute property transferable to another blend.
public sealed record MarginalContributionStats(string ExpertId, string ExpertRevisionId,
    int MarginalRescue, int MarginalHarm, int MarginalNetImpact, int CommonSamples,
    string ControlEnsembleId, string CandidateEnsembleId, ExpertActionMode CandidateMode,
    string FrozenComparisonPolicyVersion, HistoricalEvaluationMode EvaluationMode, string Split);
public sealed record ConsensusReliability(int Evaluated, int CommonSuccess, int CommonFailure,
    double? Value, string DependencyGroup, string ContextId, IssueRange SourceIssues, string EstimatorVersion);
// Only previously revealed consensus outcomes may contribute to prediction-time fields.
public sealed record ConsensusObservation(long TargetIssue, string DependencyGroup,
    double? ConsensusRisk, double? DependencyGroupConsensus,
    ConsensusReliability HistoricalConsensusReliability);
public sealed record CommonSuccessEvent(long Issue, string ActualZodiac,
    ImmutableDictionary<string,int> ActualRanks, ImmutableArray<string> SuccessfulExpertIds,
    ImmutableArray<string> SuccessfulDependencyGroups, string ReferenceSetVersion, DateTimeOffset ObservedAt);
// Full reversal: Counter Top6 equals Original Bottom6 (complement of Original Top6).
// Counter MeanRank = 13 - Original MeanRank on identical samples: algebra, not efficacy evidence.
// Neither Counter Top6 > .5 nor mechanically improved MeanRank independently validates COUNTER.
public sealed record CounterBaselineMetrics(double? Top3, double? Top6, double? MRR, double? MeanRank,
    double? MedianRank, int MaxTop3Miss, int MaxTop6Miss, int Samples);
public sealed record CounterValidationEvidence(string EvidenceId, string ExpertId, string ExpertRevisionId,
    CounterValidationStage Stage, ImmutableDictionary<int,CounterBaselineMetrics> OriginalWindowMetrics,
    ImmutableDictionary<int,CounterBaselineMetrics> CounterWindowMetrics,
    string HoldoutReportId, string LiveShadowReportId, int CounterRescue, int CounterHarm,
    DateTimeOffset AvailableAt, string ThresholdPolicyVersion, HistoricalEvaluationMode EvaluationMode,
    ImmutableArray<string> CheckResults, string CounterTransformId, string TransformVersion,
    CounterInferencePlan InferencePlan);
public sealed record BaselineContract(BaselineType Type, string ExperimentId, HistoricalEvaluationMode Mode,
    IssueRange TrainingIssues, string FrozenSplitId, DateTimeOffset ParametersFrozenAt,
    ImmutableDictionary<string,string> ExpertRevisionIds, ImmutableDictionary<string,double> FrozenWeights,
    string SelectionPolicyVersion, string ParametersHash,
    StaticBlendSearchRegistration? StaticBlendSearch);
public sealed record ExpertPoolDecisionAudit(ImmutableArray<string> IncludedExperts,
    ImmutableDictionary<string,string> ExpertRevisionIds, ImmutableDictionary<string,ExpertActionMode> ExpertModes,
    ImmutableDictionary<string,double> ExpertWeightsBefore, ImmutableDictionary<string,double> ExpertWeightsProposed,
    ImmutableDictionary<string,double> ExpertWeightsApplied, ImmutableArray<ExpertDecision> Decisions,
    ImmutableDictionary<string,ImmutableArray<string>> DependencyGroups,
    ImmutableArray<ExpertDependencyEdge> MetaDependencyEdges, double? EffectiveIndependentExpertCount,
    ImmutableArray<ExpertCounterView> CounterViewsUsed, ImmutableArray<string> ExcludedCounterCandidates,
    ImmutableDictionary<string,ImmutableArray<string>> ExclusionReasons,
    ImmutableArray<ConsensusObservation> ConsensusObservations);
public interface IExpertCounterViewFactory { ExpertCounterView Create(ExpertSnapshot snapshot, CounterValidationEvidence evidence); }
public interface IExpertDecisionPolicy { ImmutableArray<ExpertDecision> Propose(PrefixContext prefix, HypothesisSet hypotheses,
    EvidenceSearch support, EvidenceSearch counter, CriticResult critic, ConfidenceResult confidence); }
public interface IMarginalContributionEvaluator { MarginalContributionStats Evaluate(string frozenComparisonId); }
public interface ICommonSuccessDetection { CommonSuccessEvent Evaluate(ExpertPoolSnapshot frozenPool,
    ImmutableArray<ExpertSnapshot> snapshots, ClosedResult actual); }
