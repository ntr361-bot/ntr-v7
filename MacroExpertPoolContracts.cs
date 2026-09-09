using System.Collections.Immutable;

namespace 六合分析软件.MacroReasoning;

// Declarations only; no experts are registered or enabled by this file.
public enum ExpertAuditStatus { Unknown, Pending, Passed, Failed }
public enum SnapshotOrigin { LiveFrozen, CausalReconstruction }
public enum ExpertModelType { Base, Composite, Meta }
// Reserved for a subsequent hypothesis vocabulary version; V1's seven types stay unchanged.
public enum ExpertHypothesisExtension { ExpertPerformanceShift, ExpertWrapperDegradation,
    CommonExpertFailure, ExpertFamilyBias, ExpertDiversityOpportunity }
public sealed record ExpertRegistration
{
    public required string ExpertId { get; init; }
    public required string ExpertRevisionId { get; init; }
    public required string DisplayName { get; init; }
    public required string ModelFamily { get; init; }
    public required ExpertModelType ModelType { get; init; }
    public required string AlgorithmVersion { get; init; }
    public required string CodeVersion { get; init; }
    public bool IsBaseExpert { get; init; }
    public bool IsMetaExpert { get; init; }
    public bool IsExperimental { get; init; }
    public int? DependencyDepth { get; init; }
    public bool DerivedFromExpertOutputs { get; init; }
    public bool UsesSharedMemory { get; init; }
    public bool UsesSameTargetBaseSnapshots { get; init; }
    public ImmutableArray<string> ParentExpertIds { get; init; } = ImmutableArray<string>.Empty;
    public ImmutableArray<string> InputDependencyIds { get; init; } = ImmutableArray<string>.Empty;
    public bool EligibleForMacro { get; init; } = false;
    public bool Enabled { get; init; } = false;
    public required string PredictionSnapshotType { get; init; }
    public bool HasFullRanking12 { get; init; }
    public ExpertAuditStatus LeakageAuditStatus { get; init; } = ExpertAuditStatus.Unknown;
    public ExpertAuditStatus SnapshotIntegrityStatus { get; init; } = ExpertAuditStatus.Unknown;
    public required DateTimeOffset RegisteredAt { get; init; }
    public required DateTimeOffset EffectiveFrom { get; init; }
    public required string DependencyEvidenceReference { get; init; }
}
public sealed record ExpertRegistrySnapshot(string Version, DateTimeOffset AsOf,
    ImmutableArray<ExpertRegistration> Experts, string PayloadHash);
public sealed record ExpertPoolSnapshot(long TargetIssue, DateTimeOffset AsOf, string RegistryVersion,
    ImmutableArray<string> EligibleExpertIds, ImmutableArray<string> AvailableExpertIds,
    ImmutableArray<string> IncludedExpertIds, ImmutableArray<string> MissingExpertIds,
    ImmutableArray<string> ExcludedExpertIds, ImmutableDictionary<string,ImmutableArray<string>> ExclusionReasons,
    ImmutableDictionary<string,string> IncludedSnapshotHashes, HistoricalEvaluationMode EvaluationMode,
    ImmutableDictionary<string,string> ExpertRevisionIds);
public sealed record ExpertObservation(string ExpertId, string FamilyId, string DependencyGroup,
    ImmutableArray<ObservationFact> WindowMetrics, double? GlobalReliability, double? RecentReliability,
    double? ContextReliability, double? RescueRate, double? HarmRate, double? NetImpact,
    double? MeanRank, double? DiversityValue, double? DependencyPenalty,
    double? RankingChangeRate, double? Top6UniqueContribution, double? DiversityContribution,
    int UniqueRescue, int UniqueRescueOpportunities, ImmutableArray<string> ReferenceExpertIds,
    string ContextDefinitionVersion, string ExpertRevisionId, MarginalContributionStats MarginalContribution,
    SignedReliability SignedReliability, ExpertState State);
public sealed record ExpertPairDependency(string LeftExpertId, string RightExpertId, bool ParentDependency,
    double? SharedInputRatio, ImmutableDictionary<int,double?> RankingCorrelation,
    double? Top6Overlap, double? ResidualDiversity, double? DependencyPenalty, double? RedundancyPenalty,
    int CommonSamples, string EstimatorVersion);
public sealed record ExpertDependencySnapshot(ImmutableArray<ExpertPairDependency> Pairs,
    ImmutableDictionary<string,ImmutableArray<string>> DependencyGroups,
    double? EffectiveIndependentExpertCount, string EstimatorVersion, IssueRange SourceIssues,
    ImmutableArray<ExpertDependencyEdge> MetaDependencyEdges);
public sealed record FamilyReliability(string FamilyId, ImmutableArray<string> ExpertIds,
    ReliabilityStats Reliability, string AggregationVersion);
// Research option, not the default gating strategy.
public sealed record FamilyWeightProposal(ImmutableDictionary<string,double> FamilyWeights,
    ImmutableDictionary<string,ImmutableDictionary<string,double>> WithinFamilyWeights, string StrategyVersion);
public sealed record CommonFailureEvent(long Issue, string ActualZodiac,
    ImmutableDictionary<string,int> ActualRanks, ImmutableArray<string> FailedExpertIds,
    ImmutableArray<string> RescuingExpertIds, ImmutableArray<string> FailedDependencyGroups,
    string ReferenceSetVersion, DateTimeOffset ObservedAt);
public interface IExpertRegistry { ExpertRegistrySnapshot ReadAsOf(DateTimeOffset asOf); }
public interface IExpertPoolResolver { ExpertPoolSnapshot Resolve(ExpertRegistrySnapshot registry,
    long targetIssue, DateTimeOffset asOf, ImmutableArray<ExpertSnapshot> snapshots, HistoricalEvaluationMode mode); }
public interface IExpertDependencyAnalyzer { ExpertDependencySnapshot Analyze(PrefixContext prefix); }
public interface ICommonFailureDetection { CommonFailureEvent Evaluate(ExpertPoolSnapshot frozenPool,
    ImmutableArray<ExpertSnapshot> frozenSnapshots, ClosedResult actual); }
